using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using MinionHumanizer.Core;

namespace MinionHumanizer.Watchers;

/// <summary>
/// 监视 Glamourer 的设计文件，检测"你在 Glamourer 里改了设计"并触发重新施加。
///
/// <para><b>为什么监视文件而不是问 Glamourer</b>：Glamourer 没有暴露"当前正在编辑哪个设计"的
/// 接口，它只提供"按 GUID 取设计"。而设计文件本身就是它保存设计的地方
/// （<c>pluginConfigs\Glamourer\designs\{Guid}.json</c>），每次保存都会重写该文件——
/// 所以文件时间戳是唯一可靠的"它变了"信号。</para>
///
/// <para><b>为什么要防抖</b>：保存设计时文件可能分几次写。看到时间戳变化就立刻读，
/// 很可能读到半截 JSON，解析失败会让宠物外观闪回"照抄我自己"。
/// 所以检测到变化后先等一段时间，直到时间戳稳定下来才宣告写入完成。</para>
///
/// <para><b>为什么要节流</b>：这是每帧调用的路径，不能每帧问文件系统。默认 0.5 秒问一次。</para>
/// </summary>
public sealed class DesignWatcher : IDisposable
{
    /// <summary>轮询频率（秒）。每帧问文件系统太浪费，0.5 秒已经足够"实时"。</summary>
    private const double PollIntervalSeconds = 0.5;

    /// <summary>连续几轮时间戳不变，才认为上一次写入已完成。</summary>
    private const int MinStableRounds = 2;

    private readonly Configuration    _configuration;
    private readonly HumanizerService _service;
    private readonly IPluginLog        _log;

    private GlamourerBridge? _glamourer;

    /// <summary>每个目标类别当前"已知"的设计文件时间戳。null = 尚未建立基线。</summary>
    private readonly Dictionary<TargetKind, DateTime?> _knownStamp = new()
    {
        [TargetKind.Minion]         = null,
        [TargetKind.ChocoboPartner] = null,
    };

    /// <summary>正在等待写入稳定的轮数（0 = 没在等待）。</summary>
    private int _pendingRounds;

    /// <summary>等待期间最后看到的时间戳。</summary>
    private DateTime? _pendingStamp;

    /// <summary>本轮检测到变化、正在等待确认的类别（null = 无）。</summary>
    private TargetKind? _pendingKind;

    /// <summary>最近一次自动同步的说明（给 UI 显示）。</summary>
    public string LastSyncNote { get; private set; } = string.Empty;

    /// <summary>最近一次自动同步的时刻。</summary>
    public DateTime LastSyncUtc { get; private set; } = DateTime.MinValue;

    private DateTime _lastPollUtc = DateTime.MinValue;

    public DesignWatcher(Configuration configuration, HumanizerService service, GlamourerBridge? glamourer, IPluginLog log)
    {
        _configuration = configuration;
        _service       = service;
        _glamourer     = glamourer;
        _log           = log;
    }

    /// <summary>由框架每帧调用。仅在"开了实时同步 + 正在用Glamourer 设计 + 有已拟人目标"时才真正工作。</summary>
    public void Tick()
    {
        if (_glamourer is null)
            return;

        if (!_configuration.Enabled || !_configuration.UseGlamourerDesign || !_configuration.LiveSyncDesign
         || !_service.AnyApplied)
        {
            ResetTracking();
            return;
        }

        var now = DateTime.UtcNow;
        if ((now - _lastPollUtc).TotalSeconds < PollIntervalSeconds)
            return;

        _lastPollUtc = now;

        try
        {
            CheckForChanges();
        }
        catch (Exception ex)
        {
            // 监视器绝不能把异常抛到每帧路径上——降级为"这一轮没检查"。
            _log.Debug(ex, "[MinionHumanizer] 实时同步检查异常（本轮跳过）。");
        }
    }

    private void CheckForChanges()
    {
        // 宠物与陆行鸟搭档各用各的设计，任一变化都要重新施加。
        var stampFor = new (TargetKind Kind, string Label)[2];
        stampFor[0] = (TargetKind.Minion,         "宠物");
        stampFor[1] = (TargetKind.ChocoboPartner, "陆行鸟搭档");

        foreach (var (kind, label) in stampFor)
        {
            var stamp = _glamourer!.GetStampUtc(_configuration.GetDesignId(kind));
            if (Poll(kind, label, stamp))
                TriggerSync(kind, label, stamp);
        }
    }

    /// <summary>
    /// 判断某个类别的设计文件是否真的变了（含防抖与基线建立）。返回 true = 确认变了。
    /// </summary>
    private bool Poll(TargetKind kind, string label, DateTime? stamp)
    {
        // 第一次见到这个类别：只建基线，不算变化。
        if (_knownStamp[kind] is null)
        {
            _knownStamp[kind] = stamp;
            return false;
        }

        // 时间戳没变：若正在等待上一次写入，说明它又变回去了——取消等待。
        if (stamp == _knownStamp[kind])
        {
            if (_pendingKind == kind)
            {
                _pendingRounds = 0;
                _pendingStamp  = null;
                _pendingKind   = null;
            }

            return false;
        }

        // ── 防抖：时间戳变了之后要连续稳定若干轮，才认为写入真的结束 ──
        if (_pendingKind != kind || _pendingStamp != stamp)
        {
            // 出现新的时间戳（或换了类别）→ 重新开始数稳定轮数。
            _pendingKind   = kind;
            _pendingStamp  = stamp;
            _pendingRounds = 1;
            return false;
        }

        _pendingRounds++;

        // 按用户配置折算需要几轮；至少 MinStableRounds 轮。
        var debounce = Math.Max(0.2, _configuration.LiveSyncDebounceSeconds);
        var needRounds = Math.Max(MinStableRounds, (int)Math.Ceiling(debounce / PollIntervalSeconds));

        if (_pendingRounds < needRounds)
            return false;

        // 确认变化：立刻用新时间戳重建基线，避免下一轮又判定为"又变了"。
        _knownStamp[kind] = stamp;
        _pendingRounds     = 0;
        _pendingStamp      = null;
        _pendingKind       = null;
        return true;
    }

    private void TriggerSync(TargetKind kind, string label, DateTime? stamp)
    {
        // 确认文件已变 → 强制丢弃解析缓存，确保不会在同一时间戳下返回旧结果。
        _glamourer?.InvalidateCache();

        _service.RequestReapply($"{label}的 Glamourer 设计已更新");

        var suffix = stamp is null ? string.Empty : $"（文件时间 {stamp.Value.ToLocalTime():HH:mm:ss}）";
        LastSyncNote = $"{label}设计已更新并自动应用{suffix}。";
        LastSyncUtc  = DateTime.UtcNow;
        _log.Information($"[MinionHumanizer] 实时同步：{LastSyncNote}");
    }

    /// <summary>功能关闭或无目标时清掉基线，等下次重新启用时不会误判为"刚改过"。</summary>
    private void ResetTracking()
    {
        _pendingRounds = 0;
        _pendingStamp  = null;
        _pendingKind   = null;

        if (_knownStamp[TargetKind.Minion] is null && _knownStamp[TargetKind.ChocoboPartner] is null)
            return;

        _knownStamp[TargetKind.Minion]         = null;
        _knownStamp[TargetKind.ChocoboPartner] = null;
    }

    public void Dispose()
    {
        ResetTracking();
        _glamourer = null;
    }
}