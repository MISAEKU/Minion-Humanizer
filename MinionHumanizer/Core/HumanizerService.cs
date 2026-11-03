using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using SceneObjectType = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.ObjectType;

namespace MinionHumanizer.Core;

/// <summary>
/// 调度层。三件事：巡检（找到自己的宠物）、施加（写成人类并重建绘制对象）、还原。
///
/// 重建绘制对象故意<b>不</b>在一个 tick 内做完：
///   DisableDraw → （等若干帧，IsReadyToDraw 为真） → EnableDraw → （等 DrawObject 出现）
/// 这样避开游戏自己正在创建 actor 的那几帧；同一帧里连着调用是崩溃的常见来源。
/// 这一段的做法参照 Brio 的 ActorRedrawService（GPL-3.0，仅借鉴思路，未复制代码）。
/// </summary>
public sealed class HumanizerService : IDisposable
{
    private readonly Configuration        _configuration;
    private readonly CompanionFinder      _finder;
    private readonly SafetyGuard          _guard;
    private readonly KindRestrictionBypass _bypass;
    private readonly IFramework           _framework;
    private readonly IObjectTable         _objectTable;
    private readonly IChatGui             _chat;

    private readonly IPluginLog           _log;
    private readonly GlamourerBridge?     _glamourer;

    private readonly Dictionary<nint, Tracked> _tracked = new();

    /// <summary>目标"从候选列表消失"的起始时刻，用于清理时的宽限期判定。</summary>
    private readonly Dictionary<nint, DateTime> _missingSince = new();

    /// <summary>清理宽限期（秒）：连续这么久没被枚举到才丢弃跟踪记录。</summary>
    private const double MissingGraceSeconds = 5.0;

    /// <summary>
    /// 重建绘制对象的超时（秒）。给得比较宽松：加载资源、跨区、切图都可能拖慢它，
    /// 而一旦判成超时就要回滚成原形，宁可多等一会儿。
    /// </summary>
    private const double RebuildTimeoutSeconds = 6.0;
    private int  _frame;
    private bool _inTick;
    private bool _disposed;
    private bool _restoreAllRequested;

    /// <summary>上一帧是否处于骑乘状态，用于检测「落骑」这一瞬间。</summary>
    private bool _wasRiding;

    /// <summary>换区后的一小段时间里提高巡检频率，直到宠物重新施加完成。</summary>
    private int  _aggressiveScanFrames;

    /// <summary>有调用方显式要求"下一帧就巡检"（换区 / 换设计 / 手动应用）。</summary>
    private bool _forceScanNextTick;

    /// <summary>还原操作开始前触发（监听方需暂停"重绘即丢弃基准"的逻辑）。</summary>
    public event Action? BeforeRestore;

    /// <summary>还原操作结束后触发。</summary>
    public event Action? AfterRestore;

    /// <summary>施加成功后的回调（姿态重播用）。由 Plugin 注入。</summary>
    public event Action? Applied;

    /// <summary>
    /// 重绘完成后回调，参数是刚处理完的目标地址。
    /// 绘制对象被换掉后旧缩放基准失效，监听方需丢弃它（换区/换装后体型异常的直接原因）。
    /// </summary>
    public event Action<nint>? Redrawn;

    /// <summary>跟踪记录被移除时的回调（姿态控制器据此清自己的字典）。</summary>
    public event Action<nint>? Untracked;

    /// <summary>
    /// 向姿态控制器询问某个目标的<b>绘制缩放基准</b>（自检报告显示用）。
    /// 由 Plugin 把 <c>PuppetController.GetDrawScaleBaseline</c> 接进来 —— 服务层不直接持有姿态层。
    /// </summary>
    public Func<nint, float?>? ScaleBaselineProbe;

    public HumanizerService(Configuration configuration, CompanionFinder finder, SafetyGuard guard, KindRestrictionBypass bypass,
        IFramework framework, IObjectTable objectTable, IChatGui chat, IPluginLog log,
        GlamourerBridge? glamourer = null)
    {
        _configuration = configuration;
        _finder        = finder;
        _guard         = guard;
        _bypass        = bypass;
        _framework     = framework;
        _objectTable   = objectTable;
        _chat          = chat;
        _log           = log;

        // 让查找器直接复用已跟踪目标的类别，<b>每帧不再重读 Owner / CompanionType</b>。
        //
        // 这是宠物能被持续识别的关键：那两个字段描述的是"原始随从状态"，
        // 我们把 actor 改成人类模型之后它们就不再可靠（重建期间甚至读不到），
        // 于是宠物会被判成"陌生随从 / 类型不符"踢出候选列表 → 原样快照被连带删除。
        // 陆行鸟搭档分支不受影响，正是因为它不依赖这两个字段。
        CompanionFinder.TrackedKindHook = address => TrackedKindOf(address);

        _glamourer     = glamourer;

        // 告诉安全校验：某个地址是否已被我们跟踪、且 EntityId 二次确认过。
        // 已确认身份的目标不再重读 Owner / CompanionType —— 那两个字段在拟人化后不再可靠，
        // 而陆行鸟分支从来不读它们，所以一直正常。详见 SafetyGuard.IsVerifiedTracked。
        _guard.IsVerifiedTracked = address => TrackedKindOf(address) is not null;
    }

    /// <summary>
    /// 已跟踪目标当前应属的类别；未跟踪、或 EntityId 已变（对象被复用）则返回 null。
    /// 指针操作集中在 unsafe 方法里，供非unsafe 的构造函数委托使用。
    /// </summary>
    private unsafe TargetKind? TrackedKindOf(nint address)
    {
        if (!_tracked.TryGetValue(address, out var t))
            return null;

        var currentId = ((GameObject*)address)->EntityId;
        return currentId == t.Snapshot.EntityId ? t.Kind : null;
    }

    public int TrackedCount => _tracked.Count;

    /// <summary>
    /// 当前被跟踪的目标清单（名字 + 类别 + 是否已拟人化）。
    /// UI 用它来显示"到底有没有在处理跟宠坐骑"——光有一个开关是看不出结果的。
    /// </summary>
    public IReadOnlyList<(string Name, TargetKind Kind, bool Applied)> DescribeTracked()
    {
        var result = new List<(string, TargetKind, bool)>(_tracked.Count);
        foreach (var tracked in _tracked.Values)
            result.Add((tracked.Name, tracked.Kind, tracked.Applied));

        result.Sort((a, b) => string.Compare(a.Item1, b.Item1, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    /// <summary>当前是否至少有一只宠物被我们改成了人形。</summary>
    public bool AnyApplied => _tracked.Values.Any(t => t.Applied);

    /// <summary>
    /// 枚举所有「已施加成功」的宠物（姿态控制用）。指针每次都经过对象表校验。
    /// 保留迭代器版本给低频调用；每帧路径请用 <see cref="CopyAppliedTo"/>（零分配）。
    /// </summary>
    public IEnumerable<CompanionHandle> EnumerateApplied()
    {
        foreach (var tracked in _tracked.Values.ToList())
        {
            if (!tracked.Applied || tracked.Busy)
                continue;

            var obj = _objectTable[tracked.ObjectIndex];
            if (obj is null || obj.Address != tracked.Address)
                continue;

            yield return new CompanionHandle(tracked.Address, tracked.ObjectIndex, tracked.Name, tracked.Kind);
        }
    }

    /// <summary>把「已施加成功」的宠物填进调用方给的缓冲（每帧调用，不产生垃圾）。</summary>
    public void CopyAppliedTo(List<CompanionHandle> buffer)
    {
        buffer.Clear();

        foreach (var tracked in _tracked.Values)
        {
            if (!tracked.Applied || tracked.Busy)
                continue;

            var obj = _objectTable[tracked.ObjectIndex];
            if (obj is null || obj.Address != tracked.Address)
                continue;

            buffer.Add(new CompanionHandle(tracked.Address, tracked.ObjectIndex, tracked.Name, tracked.Kind));
        }
    }

    /// <summary>某只宠物的跟踪记录是否还活着（姿态控制器用它清理自己的字典）。</summary>
    public bool IsTracked(nint address) => _tracked.ContainsKey(address);

    /// <summary>
    /// 请求重新施加：换了 Glamourer 设计、换了外观来源、点了「立即应用」之后必须调它。
    ///
    /// 没有它的话会出这个 bug：宠物已经是人形（ModelCharaId=0）→ 巡检判定"已经是人形，跳过"
    /// → 换了设计也不会生效，必须换图才看得到变化。
    /// </summary>
    public void RequestReapply(string reason)
    {
        var any = false;
        foreach (var tracked in _tracked.Values)
        {
            tracked.NeedsReapply = true;
            any                = true;
        }

        if (any)
            _log.Debug($"[MinionHumanizer] 请求重新施加（{reason}）。");

        // 没有已跟踪的宠物时也要确保下一次巡检会走一遍正常流程。
        _forceScanNextTick = true;
    }

    /// <summary>
    /// 强制重新捕获所有目标的"原样"快照（丢弃旧记录，下一轮巡检会以当前状态为准）。
    ///
    /// 用于从"快照已被污染"的状态恢复：先把宠物还原成原形（关闭功能），
    /// 再调用本方法重新建立正确的原样基线，之后重新开启即可正常拟人化。
    /// </summary>
    public int RefreshSnapshots()
    {
        var count = _tracked.Count;
        foreach (var address in _tracked.Keys.ToList())
            _tracked.Remove(address);

        _missingSince.Clear();
        _forceScanNextTick = true;
        LastStatus = count > 0 ? "已重建原样基线（下一轮巡检重新记录）。" : "当前没有跟踪中的目标。";
        return count;
    }

    /// <summary>最近一次动作的结果，给 UI 显示。</summary>
    public string LastStatus { get; private set; } = "尚未执行任何操作。";

    /// <summary>
    /// 换区：进入密集巡检，并要求重新施加。
    ///
    /// ⚠️ 这里<b>刻意不清空</b>跟踪记录。副本内换区时宠物往往还活着（同一实例），
    /// 清掉记录就等于毁掉了"原样快照"——之后 /mhuman revert 会把人形当原样写回，
    /// 宠物就再也变不回蝴蝶了。地址真的消失时，巡检末尾的清理会负责移除记录。
    /// </summary>
    public void OnTerritoryChanged()
    {

        // 先做一次存活校验：换区时绝大多数随从对象已经没了，
        // 留着这些死记录会挡住新宠物的跟踪（表现为"换区后偶尔就再也拟人化不了了"）。
        foreach (var address in _tracked.Keys.ToList())
        {
            var tracked = _tracked[address];
            var obj     = _objectTable[tracked.ObjectIndex];
            if (obj is null || obj.Address != address)
            {
                _tracked.Remove(address);
                continue;
            }
        }

        foreach (var tracked in _tracked.Values)
        {
            tracked.Busy = false;
            tracked.Job.BypassWindow?.Dispose();
            tracked.Job.BypassWindow = null;
            tracked.NeedsReapply       = true;
            tracked.Reverts            = 0;
            tracked.NextRevertRetryUtc = DateTime.UtcNow;
            tracked.LastApplyUtc       = DateTime.MinValue;
        }

        _aggressiveScanFrames = 600; // ~10 秒（60fps）
        _forceScanNextTick    = true;
        LastStatus = "已换区：等待宠物重新拟人化…";
        _log.Information("[MinionHumanizer] 换区，进入密集巡检并标记重新施加。");
    }

    // ────────────────────────────────────────────────────────────────
    //  巡检
    // ────────────────────────────────────────────────────────────────

    public void Tick(IFramework _)
    {
        if (_disposed || _inTick)
            return;

        _inTick = true;
        try
        {
            TickCore();
        }
        catch (Exception ex)
        {
            // 巡检里任何异常都不该让插件崩掉。记录、跳过这一帧。
            _log.Error(ex, "[MinionHumanizer] 巡检异常。");
        }
        finally
        {
            _inTick = false;
        }
    }

    private void TickCore()
    {
        ++_frame;

        // ── 落骑瞬间：强制巡检并清空退避 ──
        //
        // 骑乘期间 CompanionFinder 完全停手（宠物被安置、游戏不绘制，改它只会打架），
        // 但游戏会在这期间把模型整理回随从形态。所以落骑的那一刻必须
        // **立刻**重新施加，否则要等退避计时器（最长 30 秒）才恢复 ——
        // 用户看到的就是「下了坐骑宠物还是原形」。
        var riding = _objectTable.LocalPlayer?.CurrentMount is { IsValid: true };
        if (_wasRiding && !riding)
            OnDismounted();

        _wasRiding = riding;

        // 把短路的适用范围收窄到"确实有一只人形宠物活着"的时段。
        _bypass.HasLiveOverride = AnyApplied;

        if (!_configuration.Enabled)
        {
            if (_tracked.Count == 0)
            {
                _restoreAllRequested = false;
                return;
            }

            // 只在刚关闭的那一刻发起一次；后面的重建由 RunOnTick 自己跑完。
            if (_restoreAllRequested)
                return;

            _restoreAllRequested = true;
            RestoreAll("功能已关闭");
            return;
        }

        _restoreAllRequested = false;

        // 换区后的密集巡检窗口：每 10 帧扫一次，直到宠物重新施加完成或窗口耗尽。
        //
        // ⚠️ 条件是"仍有目标<b>尚未</b>拟人化"，不是"完全没有已拟人化的"。
        // 旧写法用 `!AnyApplied`：一旦有任何一只先被修好（比如原本就在跟踪的陆行鸟），
        // 这个条件就永久为假，密集窗口对剩下的宠物<b>完全不生效</b>——
        // 而换区恰恰是所有宠物同时被游戏重建模型、最需要密集重施的时刻。
        // 结果就是"多只宠物时，换区后要等默认巡检间隔才恢复"。
        if (_aggressiveScanFrames > 0)
        {
            _aggressiveScanFrames--;

            var pending = _tracked.Values.Any(t => !t.Applied);
            if (pending && _frame % 10 == 0)
            {
                Scan();
                _forceScanNextTick = false;
                return;
            }
        }

        if (_forceScanNextTick)
        {
            _forceScanNextTick = false;
            Scan();
            return;
        }

        if (_configuration.AutoReapply && _frame % Math.Max(1, _configuration.ReapplyIntervalFrames) == 0)
            Scan();
    }

    /// <summary>
    /// 刚下坐骑：让所有目标立刻进入可重施状态。
    /// 退避计数与退避时刻一起清零——它们是为「游戏自己短暂改回去」准备的，
    /// 而落骑是一次明确的、需要立刻响应的状态变化。
    /// </summary>
    private void OnDismounted()
    {
        foreach (var tracked in _tracked.Values)
        {
            tracked.Reverts             = 0;
            tracked.NextRevertRetryUtc  = DateTime.UtcNow;
            tracked.NeedsReapply        = true;
            tracked.LastApplyUtc        = DateTime.MinValue;
        }

        _forceScanNextTick   = true;
        _aggressiveScanFrames = Math.Max(_aggressiveScanFrames, 30);

        _log.Debug("[MinionHumanizer] 检测到落骑，立即重新施加。");
    }

    private unsafe void Scan()
    {
        var player = _objectTable.LocalPlayer;
        if (player is null || player.Address == nint.Zero)
            return;

        var playerCharacter = (Character*)player.Address;
        if (playerCharacter is null)
            return;

        var seen = new HashSet<nint>();

        foreach (var handle in _finder.Enumerate(_configuration))
        {
            seen.Add(handle.Address);

            var character = handle.Character;
            var entityId  = ((GameObject*)handle.Address)->EntityId;

            if (!_tracked.TryGetValue(handle.Address, out var tracked))
            {
                var verdict = _guard.CheckAll(handle, _configuration);
                if (!verdict.Allowed)
                {
                    if (_configuration.VerboseLog)
                        _log.Debug($"[MinionHumanizer] 跳过 {handle.Name}（{handle.Kind.DisplayName()}）：{verdict.Reason}");

                    continue;
                }

                tracked = new Tracked(handle.Address, handle.ObjectIndex, handle.Name, handle.Kind)
                {
                    Snapshot = CompanionSnapshot.Capture(character, entityId),
                };
                tracked.SettleUntilFrame = _frame + SettleFramesFor(handle.Kind, _configuration);
                _tracked[handle.Address] = tracked;
                _log.Debug($"[MinionHumanizer] 开始跟踪 {handle.Kind.DisplayName()} {handle.Name}"
                         + $"（原始 ModelCharaId={tracked.Snapshot.ModelCharaId}）。");
            }
            else if (!tracked.Snapshot.MatchesEntityId(entityId))
            {
                // 对象表地址被复用，或随从被重新召唤：丢弃旧记录重新做。
                if (!tracked.Busy)
                {
                    tracked.Snapshot          = CompanionSnapshot.Capture(character, entityId);
                    tracked.Applied           = false;
                    tracked.NeedsReapply      = false;
                    tracked.Kind              = handle.Kind;
                    tracked.Reverts           = 0;
                    tracked.NextRevertRetryUtc= DateTime.UtcNow;
                    tracked.LastApplyUtc      = DateTime.MinValue;
                    _log.Debug($"[MinionHumanizer] {handle.Name} 是新的一次召唤，已重新快照。");
                }
            }

            if (tracked.Busy)
                continue;

            var isHuman = ModelWriter.IsHumanModel(character);

            // 已经是人形且没人要求重施 → 没事干。
            if (tracked.Applied && isHuman && !tracked.NeedsReapply)
            {
                // 但还要确认<b>画出来</b>的也是人形。
                // 内存里的值是我们写的，绘制对象却是游戏造的；两者有分歧就说明
                // 上一次"成功"其实是假的——屏幕上还是宠物原形。
                // 这里只挑明确异常的情况：绘制对象存在、却不是人类模型。
                // （绘制对象为空属于重建中间态，由 Busy 挡着，不算异常。）
                var drawn = ModelWriter.DrawObjectModelType((GameObject*)handle.Address);
                if (drawn is not null && drawn != CharacterBase.ModelType.Human)
                {
                    _log.Warning($"[MinionHumanizer] {tracked.Name} 内存已是人形，但绘制对象仍是 {drawn}，重新施加。");
                    tracked.NeedsReapply = true;
                }
                else
                {
                    continue;
                }
            }

            // 游戏把模型改回去了：记一次并<b>退避重试</b>。
            //
            // ⚠️ 这里曾经写成"超过 3 次就永远放弃"——那是换区后宠物随机不再拟人化的真凶：
            // 换区时游戏会重建一次模型，必然产生几次 revert，累计到阈值后插件就彻底不管这只宠物了。
            // 正确做法是<b>越退越久地重试，永不永久放弃</b>：既避免每帧重绘的闪烁，
            // 又保证模型被改回后最终一定会被修回来。
            // ⚠️ 显式重施请求优先于退避。
            //
            // 之前这里无条件走退避 → 切设计触发的重绘过程中，游戏会短暂把模型退回非人形，
            // 这一刻命中退避分支被 continue 掉 —— 重施请求因此丢失，
            // 表现就是"切设计后宠物变回原样，必须重新开关才恢复"。
            //
            // 现在：用户主动要求的重施（切设计 / 改外观）直接跳过退避，
            // 只有"游戏自己莫名改回"才退避重试。
            //
            // ⚠️⚠️ 退避<b>到期后必须真的重施</b>（置 NeedsReapply 往下走），
            // 不能只是把下次检查时间往后推就continue —— 那样等于一次都不救，
            // 模型被改回后就永远停在原形。详见分支内的说明。
            if (tracked.Applied && !isHuman && !tracked.NeedsReapply)
            {
                if (DateTime.UtcNow < tracked.NextRevertRetryUtc)
                    continue;   // 退避未到点：本轮不动它，避免每帧重绘造成闪烁。

                ++tracked.Reverts;

                if (tracked.Reverts == Tracked.RevertWarnThreshold)
                {
                    _log.Warning($"[MinionHumanizer] {tracked.Name} 的模型被游戏反复改回（已发生 {tracked.Reverts} 次），"
                               + "正在按退避节奏自动修回（1s → 2s → 4s → 5s，之后每 5s 一次）。"
                               + "若长时间修不回，请用 /mhuman status 看「类型限制短路」是否为“已挂钩”，"
                               + "并检查同区是否有别的插件也在改这只宠物。");
                }

                // 1s / 2s / 4s … 最多 5s 一次，永不放弃。
                // 上限刻意压低：游戏在换区、上下坐骑、切换目标时都会重建一次模型，
                // 退避太久就意味着"宠物长时间停在原形"，用户看到的就是"变回去了"。
                var backoffSeconds = Math.Min(5, 1 << Math.Min(tracked.Reverts - 1, 5));
                tracked.NextRevertRetryUtc = DateTime.UtcNow.AddSeconds(backoffSeconds);

                // ⚠️⚠️ 这里<b>必须</b>把 NeedsReapply 置真，让流程真的走到 Apply。
                //
                // 旧实现在设置完退避时间后就直接 continue 了——也就是说"退避"只推迟了
                // 下一次检查时间，<b>却从来没有真正重施过</b>。注释写着"永不放弃"，
                // 实际行为是"一次都不救"：模型被游戏改回后就永远停在原形。
                //
                // 这正是「换完区她一直是原形，得手动开关面板才恢复」的成因：
                // 开关面板会走 RequestReapply（置 NeedsReapply=true），
                // 恰好绕开了这个分支，所以看起来"手动一下就好了"。
                //
                // 换区时游戏必然重建一次模型 → 必然产生 revert → 必然踩中这里。
                tracked.NeedsReapply = true;
            }

            // 模型正常（是我们写的人形）：抖动计数与退避一起清零。
            //
            // ⚠️ 只有"不需要退避重施"时才清零。退避到期后我们刚把 NeedsReapply 置真，
            // 就是为了让它走到下面真的Apply；此处若照旧清零，会把刚排好的退避立刻抹平，
            // 退避就形同虚设（会退化成每轮巡检都重施一次 → 闪烁）。
            if (!tracked.NeedsReapply)
            {
                tracked.Reverts            = 0;
                tracked.NextRevertRetryUtc = DateTime.UtcNow;
            }

            // ── 重施快路径：已经是「内存人形 + 画面人形」→ 只需刷新外观 ──
            //
            // 这条路完全不碰绘制对象（不 DisableDraw / 不 EnableDraw），所以
            // 不需要任何"重建时机"条件：骑乘安置、换区边缘、绘制未就绪……全都安全。
            //
            // v1.6 一度把安置状态的拦截也对已跟踪目标放开了，那样反而让宠物
            // 在上下坐骑时去跑必然超时的完整重建 → 回滚 → 停在原形。
            // 现在改成精确区分：<b>刷新走这里，重建才走 CheckAll。</b>
            if (ModelWriter.IsHumanModel(character) && ModelWriter.IsHumanDrawObject((GameObject*)handle.Address))
            {
                try
                {
                    Apply(tracked, handle, playerCharacter);
                }
                catch (Exception ex)
                {
                    tracked.NeedsReapply = false;
                    _log.Error(ex, $"[MinionHumanizer] 刷新 {tracked.Name} 外观时异常。");
                }

                continue;
            }

            var timing = _guard.CheckAll(handle, _configuration);
            if (!timing.Allowed)
            {
                if (_configuration.VerboseLog)
                    _log.Debug($"[MinionHumanizer] 暂时不动 {tracked.Name}：{timing.Reason}");

                continue;
            }

            // 重施节流：刚施加完的一小段时间内不再重复重绘（换区后的重复巡检会命中这里）。
            // ⚠️ 显式重施请求（切设计/ 改外观）不走冷却——用户刚点完就该立刻生效，
            // 否则会出现"点了没反应，要重新开关总开关才行"。
            if (tracked.Applied && !tracked.NeedsReapply
                             && (DateTime.UtcNow - tracked.LastApplyUtc).TotalSeconds < _configuration.ReapplyCooldownSeconds)
                continue;

            try
            {
                Apply(tracked, handle, playerCharacter);
            }
            catch (Exception ex)
            {
                // Apply 内部已有 try/catch，这里是最后一道保险：
                // 绝不因为一次异常让 NeedsReapply 永久卡住（那会让宠物卡在非拟人态）。
                tracked.NeedsReapply = false;
                _log.Error(ex, $"[MinionHumanizer] 重新施加 {tracked.Name} 时异常。");
            }
        }

        // 已经消失的宠物：把记录清掉（对象都没了，不需要还原）。
        //
        // ⚠️ 但要给<b>宽限期</b>：拟人化重建绘制对象的那几帧里，宠物可能短暂从
        // finder的候选列表里消失（Owner 读不到、类型判定失败等）。
        // 如果一不见就删，连带<b>原样快照</b>一起丢 → 之后再施加时快照已是人形 →
        // revert 会把人形当"原样"写回，宠物再也变不回原形（且"立即施加"会让它闪回原型）。
        // 所以只有"连续消失超过宽限期"才真正丢弃。
        foreach (var address in _tracked.Keys.Where(a => !seen.Contains(a)).ToList())
        {
            var t = _tracked[address];
            if (t.Busy)
                continue;

            if (!_missingSince.TryGetValue(address, out var since))
            {
                _missingSince[address] = DateTime.UtcNow;
                continue;
            }

            if ((DateTime.UtcNow - since).TotalSeconds < MissingGraceSeconds)
                continue;

            _missingSince.Remove(address);
            _tracked.Remove(address);
            try
            {
                Untracked?.Invoke(address);
            }
            catch (Exception ex)
            {
                _log.Debug(ex, "[MinionHumanizer] Untracked 回调异常。");
            }
        }

        // 本轮见到的把宽限期标记清掉。
        foreach (var address in seen)
            _missingSince.Remove(address);
    }

    // ────────────────────────────────────────────────────────────────
    //  施加
    // ────────────────────────────────────────────────────────────────

    /// <summary>
    /// 新目标动手前要等的帧数。宠物 / 陆行鸟搭档是长期存在的稳定对象，返回 0（行为完全不变）。
    /// 召唤物是刚被召唤出来的，给它一个可调的保护期（见 <c>SummonSettings.SettleFrames</c>）。
    /// </summary>
    private static int SettleFramesFor(TargetKind kind, Configuration configuration)
        => kind is TargetKind.Minion or TargetKind.ChocoboPartner
            ? 0
            : Math.Clamp((configuration.SettingsFor(kind) as SummonSettings)?.SettleFrames ?? 0, 0, 1800);

    private unsafe void Apply(Tracked tracked, CompanionHandle handle, Character* playerCharacter)
    {
        // ── 召唤物的"刚出生保护期"──
        //
        // 崩溃现场（dalamud.old.log，毫秒级）：
        //   14:42:09.494  宝石兽的绘制对象刚创建（Penumbra Create CharacterBase model:411）
        //   14:42:09.556  我们跟踪它
        //   14:42:09.558  它的绘制对象已被重建成 model:0     ← 只隔 64 毫秒
        //   14:42:10      崩溃
        //
        // 陆行鸟搭档从不崩，是因为它**早就存在**，从来不是刚出生的 actor。
        // 这里给召唤物一个可调的等待，让它先把自己的资源加载完。
        if (_frame < tracked.SettleUntilFrame)
            return;

        // ── 快照污染警告（不阻断施加）──
        // 快照里 ModelCharaId == 0 表示它记的是"人类模型"，那不是"原样"。
        // ⚠️ 绝不能因此中断施加：之前这里直接 return，导致**每次重施都原地不动**，
        // 表现为"切设计和重复施加全都变回去"。
        // 正确做法：让它照常施加（施加只用 customize/equipment，不依赖快照的 ModelCharaId），
        // 只在<b>还原路径</b>上拒绝使用被污染的快照。
        if (tracked.Snapshot.ModelCharaId == 0)
            _log.Warning($"[MinionHumanizer] {tracked.Name} 的原样快照记录成了人形（污染）。"
                       + "拟人化不受影响；但 /mhuman revert 无法还原到原形，"
                       + "请执行 /mhuman resnapshot 重建基线（宠物需先处于原形）。");

        // ── 外观载荷：Glamourer 设计优先，失败/未启用则回退到本地来源。──
        DesignAppearance? design = null;
        // 宠物与陆行鸟搭档可以各选各的设计。
        var designId = _configuration.GetDesignId(handle.Kind);
        if (_configuration.UseGlamourerDesign && _glamourer is not null && designId != Guid.Empty)
        {
            design = _glamourer.ResolveDesign(designId);
            if (design is null)
            {
                // 设计拉不到（Glamourer 没开/设计被删）：不算失败，回退旧来源，但要让用户知道。
                if (_configuration.ChatFeedback)
                    _chat.PrintError("[MinionHumanizer] Glamourer 设计拉取失败，本次回退到「照抄我自己」。");
            }
        }

        byte[] customize;
        byte[] equipment;

        if (design is not null)
        {
            customize = design.Customize;
            equipment = design.Equipment;
        }
        else
        {
            (customize, equipment) = Persona.Resolve(_configuration, playerCharacter);
        }

        tracked.Busy = true;
        tracked.Job = new Job { Restore = false, Deadline = DateTime.UtcNow.AddSeconds(RebuildTimeoutSeconds) };

        var structureKey = Persona.StructureKey(customize);

        // 武器仅在显式开启且设计可用时写入（实验性）。
        ulong? mainHand = _configuration.ApplyWeapons && design is not null ? design.MainHand : null;
        ulong? offHand  = _configuration.ApplyWeapons && design is not null ? design.OffHand  : null;

        // ── 召唤物（宝石兽 / 小仙女）一律【不装武器】──
        //
        // 崩溃现场日志（dalamud.old.log，毫秒级对齐）：
        //   14:42:09.497  Penumbra: Reload Weapon drawData 槽0/1/2 → 武器 ID 全是 0
        //   14:42:09.556  我们：开始跟踪 宝石兽(411)
        //   14:42:09.556  Penumbra: Reload Weapon 槽0/1 → 4296999213   ← 我们写入的
        //   14:42:09.558  Penumbra: Create CharacterBase model: 0       ← 变人
        //   14:42:09.563  Loaded chara/equipment/e0205/e0205.imc ← Penumbra 集合
        //   14:42:10      崩溃（UpdateResourceState 空句柄）
        //
        // 召唤物是 BattleNpc，本来就**没有武器概念**：原生状态下三个武器槽的 ID 全是 0。
        // 硬塞一个人的武器进去等于逼游戏/ Penumbra 去加载一条本不该存在的资源链 ——
        // 而宠物只有两个武器槽，所以它一直没事。
        //
        // 这是召唤物独有的最小改动，其余施加逻辑与 v1.9.3 保持一致。
        var isSummon = tracked.Kind is TargetKind.Carbuncle or TargetKind.Fairy;
        if (isSummon)
        {
            mainHand = null;
            offHand  = null;
        }

        // ── 关键：能就地刷新就绝不重建 ──
        //
        // 以前无论什么情况都走"DisableDraw → 等就绪 → EnableDraw"的完整重建。
        // 而完整重建在<b>已经是人形的宠物</b>上很容易超时失败；一旦失败，
        // Finish 会立刻回滚成原形，累计 3 次还会自动关闭整个功能——
        // 这就是"切设计 / 点立即施加会变回原型，必须重新开关才恢复"的真正成因。
        //
        // 参照 Brio 的做法：只有模型或骨架真的变了才重建；否则直接让 Human
        // 按新的 DrawData 重新装配件，根本不撤下绘制对象。
        var canUpdateInPlace =
               ModelWriter.IsHumanModel(handle.Character)                  // 内存里已是人形
            && ModelWriter.IsHumanDrawObject((GameObject*)handle.Address)  // 画出来的也是人形
            && tracked.StructureKey == structureKey;                       // 骨架不用换

        if (canUpdateInPlace)
        {
            try
            {
                if (ModelWriter.TryUpdateDrawData(handle.Character, customize, equipment,
                        design?.HatVisible, design?.WeaponVisible, design?.VisorToggled))
                {
                    // 武器不在 DrawData 里，单独装载（LoadWeapon 会让游戏自己生成/回收武器绘制对象）。
                    if (mainHand.HasValue)
                        ModelWriter.WriteWeapon(handle.Character, DrawDataContainer.WeaponSlot.MainHand, mainHand.Value);

                    if (offHand.HasValue)
                        ModelWriter.WriteWeapon(handle.Character, DrawDataContainer.WeaponSlot.OffHand, offHand.Value);

                    _log.Debug($"[MinionHumanizer] {tracked.Name} 已就地刷新外观（未重建绘制对象）。");
                    MarkApplied(tracked, structureKey);
                    return;
                }

                _log.Debug($"[MinionHumanizer] {tracked.Name} 就地刷新未成功，退回完整重建。");
            }
            catch (Exception ex)
            {
                // 就地刷新只是"更优路径"，失败/异常一律退回完整重建。
                // ⚠️ 绝不能让异常从这里漏出去：此时 tracked.Busy 已经是 true，
                // 漏出去会让这只宠物永远卡在 Busy 状态（再也不会被施加）。
                _log.Warning(ex, $"[MinionHumanizer] 就地刷新 {tracked.Name} 失败，退回完整重建。");
            }
        }

        try
        {
            // 在自己的重建窗口内允许绕过游戏的类型限制检查。
            tracked.Job.BypassWindow = _bypass.Open();

            var ok = ModelWriter.WriteHuman(handle.Character, customize, equipment,
                mainHand, offHand,
                design?.HatVisible,
                isSummon ? false : design?.WeaponVisible,
                design?.VisorToggled);

            if (!ok)
            {
                // 数据长度不符属于配置问题，不是稳定性问题：记一笔但不触发自动关闭。
                Finish(tracked, success: false, "外观数据长度不符，写入被拒绝", countFailure: false);
                return;
            }

            tracked.Job.WroteAny = true;

            // 值已经写进去了。重建成功与否都以此为基准——失败回滚时会重置为 0。
            tracked.StructureKey = structureKey;

            // 跟随行为是 Companion 独有的字段，陆行鸟搭档（BattleNpc）没有。
            if (_configuration.ForceFollowBehavior && handle.Kind is TargetKind.Minion or TargetKind.FollowMount)
                handle.Companion->Behavior = CompanionMove.Obedient;

            BeginRebuild(tracked, handle, restore: false);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "[MinionHumanizer] 施加时发生异常，将回滚。");
            Finish(tracked, success: false, ex.Message);
        }
    }

    // ────────────────────────────────────────────────────────────────
    //  还原
    // ────────────────────────────────────────────────────────────────

    /// <summary>把一只宠物还原成原样。同步写值，异步完成重建。</summary>
    private unsafe bool Restore(Tracked tracked, string reason)
    {
        if (tracked.Busy)
            return false;

        var obj = _objectTable[tracked.ObjectIndex];
        if (obj is null || obj.Address != tracked.Address)
        {
            _tracked.Remove(tracked.Address);
            return false;
        }

        // 从没被我们改过：只要停止跟踪即可，不需要多余的一次重建。
        if (!tracked.Applied)
        {
            _tracked.Remove(tracked.Address);
            return true;
        }

        // 快照被污染（记录的是人形）时，绝不能拿它"还原"——那会把人形写回去，
        // 宠物从此再也变不回原形。此时明确失败并给出可执行的修复步骤。
        if (tracked.Snapshot.ModelCharaId == 0)
        {
            _log.Warning($"[MinionHumanizer] {tracked.Name} 的原样快照已污染（记录成了人形），"
                       + "无法安全还原。请让它先处于原形，然后执行 /mhuman resnapshot。");
            LastStatus = $"{tracked.Name} 的原样快照已污染，无法还原（见日志）。";
            return false;
        }

        tracked.Busy = true;
        tracked.Job  = new Job { Restore = true, Deadline = DateTime.UtcNow.AddSeconds(RebuildTimeoutSeconds) };

        try
        {
            tracked.Job.BypassWindow?.Dispose();
            tracked.Job.BypassWindow = null;

            tracked.Snapshot.RestoreTo((Character*)tracked.Address);
            BeginRebuild(tracked, new CompanionHandle(tracked.Address, tracked.ObjectIndex, tracked.Name, tracked.Kind), restore: true);
            _log.Information($"[MinionHumanizer] 已还原 {tracked.Name}（{reason}）。");
            return true;
        }
        catch (Exception ex)
        {
            _log.Error(ex, $"[MinionHumanizer] 还原 {tracked.Name} 时异常。");
            tracked.Busy = false;
            return false;
        }
    }

    public void RestoreAll(string reason)
    {
        var pending = _tracked.Values.Where(t => !t.Busy).ToList();
        if (pending.Count == 0)
            return;

        _log.Information($"[MinionHumanizer] 还原 {pending.Count} 只宠物（{reason}）。");

        // 还原会触发重绘 → 若此时丢弃视觉基准，下一次捕获到的"原始值"
        // 其实是刚写回去的修正值，于是每点一次还原就多沉一层。
        // 所以整个还原期间挂上标记，让监听方跳过基准失效处理。
        BeforeRestore?.Invoke();
        try
        {
            foreach (var tracked in pending)
                Restore(tracked, reason);
        }
        finally
        {
            AfterRestore?.Invoke();
        }
    }

    // ────────────────────────────────────────────────────────────────
    //  分帧重建
    // ────────────────────────────────────────────────────────────────

    /// <summary>
    /// DisableDraw → 等就绪 → EnableDraw → 等绘制对象出现。
    /// 每一步之间隔一个 tick，并且带超时。用 RunOnTick 而不是自己维护状态机。
    ///
    /// ⚠️ 注意：RunOnTick 是<b>插件作用域</b>的（Dalamud.Game.FrameworkPluginScoped），
    /// 插件卸载时 Dalamud 会处置掉尚未执行的回调，所以<b>这条流程不保证能跑完</b>。
    /// 卸载时的收尾一律由 Dispose() → FinalizeSynchronously() 同步负责。
    /// </summary>
    private unsafe void BeginRebuild(Tracked tracked, CompanionHandle handle, bool restore)
    {
        ModelWriter.DisableDraw((GameObject*)handle.Address);

        var phase  = 0;

        // 撤下绘制对象之后，游戏需要几帧才真正把它清掉。
        // 立刻就开始检查的话，会读到"还没清空的旧绘制对象"，误判成已经重建好了
        // —— 那是一种<b>假成功</b>：值写进去了、日志显示成功，但屏幕上还是宠物原形。
        var waited = 0;

        void Step()
        {
            try
            {
                if (_disposed && !restore)
                {
                    // 插件在重建途中被卸载：只把还原流程跑完，施加流程直接放弃。
                    Finish(tracked, success: false, "插件已卸载");
                    return;
                }

                var obj = _objectTable[handle.ObjectIndex];
                if (obj is null || obj.Address != handle.Address)
                {
                    Finish(tracked, success: false, "目标对象已消失", countFailure: false);
                    return;
                }

                var native = (GameObject*)handle.Address;

                switch (phase)
                {
                    case 0:
                        // 先给游戏两帧时间清空旧对象（见 waited 的说明）。
                        if (++waited < 2)
                            break;

                        if (ModelWriter.IsReadyToDraw(native))
                        {
                            ModelWriter.EnableDraw(native);
                            phase  = 1;
                            waited = 0;
                        }

                        break;

                    case 1:
                    {
                        // 完成判定要严格一些：绘制对象不仅得存在，还得真的可见。
                        // 只判断"指针非空"会把还没来得及清空的旧对象当成新对象。
                        var draw = native->DrawObject;
                        if (draw is null || !draw->IsVisible)
                            break;

                        // 拟人化时还要确认它<b>确实是人类模型</b>。
                        // 这样即便游戏因为某种原因没换掉绘制对象，我们也算失败并重试，
                        // 而不是把"还是宠物原形"的状态当成成功（那正是"变回去"的来源）。
                        // 还原路径不校验——还原的目标本来就是原形。
                        if (!restore && !ModelWriter.IsHumanDrawObject(native))
                            break;

                        Finish(tracked, success: true, null);
                        return;
                    }
                }

                if (DateTime.UtcNow > tracked.Job.Deadline)
                {
                    Finish(tracked, success: false, "重建绘制对象超时", countFailure: false);
                    return;
                }

                _framework.RunOnTick(Step, TimeSpan.FromMilliseconds(16));
            }
            catch (Exception ex)
            {
                _log.Error(ex, "[MinionHumanizer] 重建过程中异常。");
                Finish(tracked, success: false, ex.Message);
            }
        }

        _framework.RunOnTick(Step, TimeSpan.FromMilliseconds(16));
    }

    // ────────────────────────────────────────────────────────────────
    //  收尾 / 失败处理
    // ────────────────────────────────────────────────────────────────

    /// <summary>
    /// 施加成功的统一收尾。就地刷新与完整重建两条路径都走这里，
    /// 保证"成功"之后的状态完全一致（否则会出现界面上显示成功、实际没生效的分叉）。
    /// </summary>
    private void MarkApplied(Tracked tracked, int structureKey)
    {
        tracked.Busy             = false;
        tracked.Job.BypassWindow?.Dispose();
        tracked.Job.BypassWindow = null;

        tracked.Applied      = true;
        tracked.StructureKey = structureKey;
        tracked.NeedsReapply = false;
        tracked.Failures     = 0;
        tracked.Reverts      = 0;
        tracked.LastApplyUtc = DateTime.UtcNow;

        LastStatus = $"已把 {tracked.Name} 变成人形。";
        Redrawn?.Invoke(tracked.Address);

        if (_configuration.ChatFeedback)
            _chat.Print($"[MinionHumanizer] {tracked.Name} 已变成人形。");

        // 通知姿态控制器：重绘会打断当前动作，让它自己决定要不要重播。
        try
        {
            Applied?.Invoke();
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "[MinionHumanizer] Applied 回调异常（已忽略）。");
        }
    }

    private unsafe void Finish(Tracked tracked, bool success, string? error, bool countFailure = true)
    {
        tracked.Busy = false;
        tracked.Job.BypassWindow?.Dispose();
        tracked.Job.BypassWindow = null;

        var wasRestore = tracked.Job.Restore;

        if (success)
        {
            if (wasRestore)
            {
                tracked.Applied = false;

                // 用户主动还原（或总开关关闭）→ 不再跟踪。
                // 失败后的回滚 → 保留记录，让失败计数继续累积。
                if (!tracked.Job.Rollback)
                {
                    _tracked.Remove(tracked.Address);
                    LastStatus = $"已还原 {tracked.Name}。";
                }
            }
            else
            {
                tracked.Applied      = true;
                Redrawn?.Invoke(tracked.Address);
                tracked.Failures     = 0;
                tracked.Reverts      = 0;
                tracked.NeedsReapply = false;
                tracked.LastApplyUtc = DateTime.UtcNow;
                LastStatus = $"已把 {tracked.Name} 变成人形。";
                if (_configuration.ChatFeedback)
                    _chat.Print($"[MinionHumanizer] {tracked.Name} 已变成人形。");

                // 通知姿态控制器：重绘会打断当前动作，让它自己决定要不要重播。
                try
                {
                    Applied?.Invoke();
                }
                catch (Exception ex)
                {
                    _log.Debug(ex, "[MinionHumanizer] Applied 回调异常（已忽略）。");
                }
            }

            return;
        }

        _log.Warning($"[MinionHumanizer] {(wasRestore ? "还原" : "施加")} {tracked.Name} 失败：{error}");

        if (wasRestore)
        {
            LastStatus = $"还原 {tracked.Name} 失败：{error}";
            return;
        }

        // 施加失败 → 立刻回滚，绝不把宠物留在半改状态。
        if (countFailure)
            ++tracked.Failures;

        LastStatus = $"施加 {tracked.Name} 失败：{error}（已回滚）";

        if (tracked.Job.WroteAny)
            Rollback(tracked);

        // ⚠️ 这里<b>曾经</b>会在连续失败后自动关闭整个功能——那正是"宠物变回原形、
        // 必须手动重新开关才恢复"最难受的一环：用户看到的只是宠物不对了，
        // 根本不知道功能已经被悄悄关掉。
        //
        // 现在改为"越退越久地重试，永不悄悄关掉"：既不会每帧重绘造成闪烁，
        // 又保证最终一定能修回来，而异常状态始终对用户可见（日志 + 状态栏）。
        if (countFailure && tracked.Failures >= Math.Max(1, _configuration.FailureLimit))
        {
            var wait = Math.Min(5, 1 << Math.Min(tracked.Failures - 1, 5));
            tracked.NextRevertRetryUtc = DateTime.UtcNow.AddSeconds(wait);
            LastStatus = $"施加 {tracked.Name} 连续失败 {tracked.Failures} 次，{wait} 秒后重试（不会自动关闭）。";
            _log.Warning($"[MinionHumanizer] {tracked.Name} 连续失败 {tracked.Failures} 次，{wait} 秒后重试。");
        }

        // 失败原因直接说出来，省得用户去翻日志。
        if (_configuration.ChatFeedback && error is not null)
            _chat.PrintError($"[MinionHumanizer] 施加 {tracked.Name} 失败：{error}（已还原为原形，将自动重试）");
    }

    private unsafe void Rollback(Tracked tracked)
    {
        var obj = _objectTable[tracked.ObjectIndex];
        if (obj is null || obj.Address != tracked.Address)
            return;

        try
        {
            tracked.Snapshot.RestoreTo((Character*)tracked.Address);

            // 已经写回原形了，骨架指纹必须清掉，否则下一次会被误判成"不用重建"。
            tracked.StructureKey = 0;

            // 回滚也需要重建一次；用同一套流程，只是不再计入失败次数。
            tracked.Busy = true;
            tracked.Job  = new Job { Restore = true, Rollback = true, Deadline = DateTime.UtcNow.AddSeconds(RebuildTimeoutSeconds) };
            BeginRebuild(tracked, new CompanionHandle(tracked.Address, tracked.ObjectIndex, tracked.Name, tracked.Kind), restore: true);
        }
        catch (Exception ex)
        {
            _log.Error(ex, $"[MinionHumanizer] 回滚 {tracked.Name} 失败。");
            tracked.Busy = false;
        }
    }

    // ────────────────────────────────────────────────────────────────

    /// <summary>
    /// 自检报告：把插件内部状态摊开给用户看。
    /// 排查"到底有没有生效"时，比翻日志快得多——日志里看不到每只宠物的实时 ModelCharaId。
    /// </summary>
    public unsafe string BuildDiagnostics(PuppetController puppet)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"功能：{(_configuration.Enabled ? "开启" : "关闭")}｜正在跟踪：{_tracked.Count}｜已拟人化：{_tracked.Values.Count(t => t.Applied)}");
        sb.AppendLine($"Glamourer：{(_glamourer is null ? "未接入" : _glamourer.IsGlamourerRunning ? "运行中" : "未运行/不可用")}"
                    + $"｜使用设计：{(_configuration.UseGlamourerDesign ? _configuration.GlamourerDesignName : "（未启用）")}");
        if (_glamourer is not null && _glamourer.LastError.Length > 0)
            sb.AppendLine($"  ↳ 最近错误：{_glamourer.LastError}");

        sb.AppendLine($"类型限制短路：{(_bypass.Available ? "已挂钩" : "签名未命中")}"
                    + $"｜范围：{_configuration.BypassScope}｜当前有活人形宠物：{AnyApplied}");
        sb.AppendLine($"姿态：{(puppet.IdleActive ? "待机生效中" : "待机未生效")}"
                    + $"｜表情：{(string.IsNullOrEmpty(puppet.CurrentEmoteName) ? "无" : $"{puppet.CurrentEmoteKind.DisplayName()}·{puppet.CurrentEmoteName}")}");

        var (petIdle, petIdleId)         = _configuration.GetIdle(TargetKind.Minion);
        var (chocoboIdle, chocoboIdleId) = _configuration.GetIdle(TargetKind.ChocoboPartner);
        var (carbIdle, carbIdleId)       = _configuration.GetIdle(TargetKind.Carbuncle);
        var (fairyIdle, fairyIdleId)     = _configuration.GetIdle(TargetKind.Fairy);
        sb.AppendLine($"  宠物待机：{DescribeIdle(petIdle, petIdleId)}"
                    + $"｜陆行鸟：{DescribeIdle(chocoboIdle, chocoboIdleId)}"
                    + $"｜宝石兽：{DescribeIdle(carbIdle, carbIdleId)}"
                    + $"｜小仙女：{DescribeIdle(fairyIdle, fairyIdleId)}");

        if (_tracked.Count == 0)
        {
            sb.AppendLine("（当前没有任何被跟踪的宠物）");
            return sb.ToString();
        }

        foreach (var tracked in _tracked.Values)
        {
            var obj = _objectTable[tracked.ObjectIndex];
            var alive = obj is not null && obj.Address == tracked.Address;
            sb.Append($"· {tracked.Name}（{tracked.Kind.DisplayName()}）：");

            if (!alive)
            {
                sb.AppendLine("对象已失效（等待巡检清理）");
                continue;
            }

            var character  = (Character*)tracked.Address;
            var gameObject = (GameObject*)tracked.Address;
            sb.Append($"ModelCharaId={character->ModelContainer.ModelCharaId}");
            sb.Append(tracked.Applied ? "（已拟人）" : "（未施加）");

            // 画出来的模型类型。它和 ModelCharaId 不一致 = 内存改了但画面没换（假成功）。
            var drawKind = ModelWriter.DrawObjectModelType(gameObject);
            sb.Append($"｜画面={drawKind?.ToString() ?? "无"}");
            // 实际下沉量 = 绘制对象位置 - actor 位置（唯一补偿通路的真实效果），
            // 直接对比配置值即可判断是否一致（一对一，不该有偏差）。
            var drawObject2 = gameObject->DrawObject;
            if (drawObject2 is not null && drawObject2->Object.GetObjectType() == SceneObjectType.CharacterBase)
            {
                var actorPos = gameObject->Position;
                var drawPos  = ((CharacterBase*)drawObject2)->Position;
                sb.Append($"｜下沉={(drawPos.Y - actorPos.Y):0.00}(配置 {_configuration.GetGroundOffset(tracked.Kind):0.00})");
                sb.Append($"｜DrawOffset.Y={gameObject->DrawOffset.Y:0.00}(补偿载体)");
            }
            else
            {
                sb.Append($"｜下沉=无绘制对象");
            }

            sb.Append($"｜补偿方式={PuppetController.OffsetMethod}");
            sb.Append($"｜视觉修正={(tracked.Kind.AllowVisualPuppetry() ? "允许" : "不适用")}");

            // 体型缩放：同时给出"参数倍率"与"内存里的真实缩放"。
            // 之前"参数显示 1、实际很小/很大"就是因为两者对不上——现在直接可见。
            var factor = _configuration.GetVisualScaleValue(tracked.Kind);
            sb.Append($"｜缩放 参数×{factor:0.00}");

            var drawObject = gameObject->DrawObject;
            if (drawObject is not null && drawObject->Object.GetObjectType() == SceneObjectType.CharacterBase)
            {
                // 参数是「倍率」，实际绘制 = 基准 × 倍率 —— 必须把基准也打出来，
                // 否则会出现"参数 ×1.00、实际绘制 0.850"这种看着像 bug 的显示。
                var baseline = ScaleBaselineProbe?.Invoke(tracked.Address);
                sb.Append(baseline is { } b
                    ? $" 基准{b:0.000}×{factor:0.00}=实际{((CharacterBase*)drawObject)->Scale.X:0.000}(actor层不碰)"
                    : $" 实际绘制={((CharacterBase*)drawObject)->Scale.X:0.000}(基准未捕获)");
            }
            else
            {
                sb.Append(" 实际=无绘制对象");
            }

            if (tracked.Reverts > 0)
                sb.Append($"｜被改回 {tracked.Reverts} 次");
            if (tracked.Failures > 0)
                sb.Append($"｜失败 {tracked.Failures} 次");

            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string DescribeIdle(IdleMode mode, int timelineId)
        => mode == IdleMode.Off || timelineId == 0
            ? "未设置"
            : $"{mode} #{timelineId}";

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        // 确保游戏的类型限制检查被放回去：任何时候插件退出，都不该留下全局影响。
        _bypass.HasLiveOverride = false;

        // ── 卸载必须同步收尾 ──
        // IFramework 是插件作用域的（Dalamud.Game.FrameworkPluginScoped，它实现了
        // IInternalDisposableService.DisposeService）。插件卸载时 Dalamud 会一并处置掉
        // 该作用域里尚未执行的 RunOnTick 回调 —— 也就是说"等下一帧再 EnableDraw"
        // 这类流程在卸载这一刻永远不会到达。如果那时我们已经执行过 DisableDraw，
        // 宠物就会永久隐形（这是本项目最难看的一种失败）。
        // 因此卸载路径不再走分帧重建，而是在本帧内把还原 + 重建一次做完。
        foreach (var tracked in _tracked.Values.ToList())
        {
            tracked.Job.BypassWindow?.Dispose();
            tracked.Job.BypassWindow = null;
            FinalizeSynchronously(tracked);
        }

        _tracked.Clear();
    }

    /// <summary>
    /// 同步版收尾：写回快照 + 背靠背重建绘制对象。只用于插件卸载。
    ///
    /// 插件存活时分帧重建更安全（能避开游戏自己创建 actor 的那些帧）；
    /// 但卸载之后已经没有下一帧，所以这里是"必须现在做完"，不是"可以等等"。
    /// </summary>
    private unsafe void FinalizeSynchronously(Tracked tracked)
    {
        // 从没碰过这只宠物（既没施加成功也没写过值）→ 不需要任何动作。
        if (!tracked.Applied && !tracked.Job.WroteAny)
            return;

        var obj = _objectTable[tracked.ObjectIndex];
        if (obj is null || obj.Address != tracked.Address)
            return;

        var native = (GameObject*)tracked.Address;

        try
        {
            tracked.Snapshot.RestoreTo((Character*)tracked.Address);

            ModelWriter.DisableDraw(native);
            ModelWriter.EnableDraw(native);

            _log.Information($"[MinionHumanizer] 卸载时已同步还原 {tracked.Name}。");
        }
        catch (Exception ex)
        {
            _log.Error(ex, $"[MinionHumanizer] 卸载时还原 {tracked.Name} 失败。");

            // 出错时最要紧的是别把它留在"绘制对象已撤下"的状态——那它就是隐形的。
            try
            {
                ModelWriter.EnableDraw(native);
            }
            catch (Exception retryEx)
            {
                _log.Error(retryEx, $"[MinionHumanizer] 补齐 EnableDraw 也失败；{tracked.Name} 可能需要重新召唤或切换地图才会恢复外观。");
            }
        }
    }

    private sealed class Job
    {
        public bool      Restore;
        public bool      Rollback;

        /// <summary>是否真的往游戏对象里写过值。没写过就不需要回滚。</summary>
        public bool      WroteAny;

        public DateTime  Deadline;
        public IDisposable? BypassWindow;
    }

    private sealed class Tracked(nint address, ushort objectIndex, string name, TargetKind kind)
    {
        public readonly nint   Address     = address;
        public readonly ushort ObjectIndex = objectIndex;
        public readonly string Name        = name;

        /// <summary>目标类别（宠物 / 跟宠坐骑 / 陆行鸟搭档）。</summary>
        public TargetKind Kind = kind;

        public CompanionSnapshot Snapshot = new();
        public Job               Job      = new();
        public bool              Busy;
        public bool              Applied;

        /// <summary>用户换了设计/来源，要求下一轮巡检重写一次（哪怕已经是人形）。</summary>
        public bool NeedsReapply;

        /// <summary>召唤物"刚被召唤出来"的保护期：到这个帧号之前不许动手。</summary>
        public int SettleUntilFrame;

        /// <summary>施加成功后又变回非人形（被游戏改回去）的次数，用于抖动告警与退避。</summary>
        public int Reverts;

        /// <summary>下次允许"因为被改回而重试"的时间（指数退避，永不永久放弃）。</summary>
        public DateTime NextRevertRetryUtc;

        /// <summary>抖动告警阈值。</summary>
        public const int RevertWarnThreshold = 3;

        public int Failures;

        /// <summary>最近一次施加成功的时间（用于重施节流）。</summary>
        public DateTime LastApplyUtc;

        /// <summary>
        /// 最近一次成功施加的"骨架指纹"（种族/性别/体型/部族）。
        /// 重施时如果指纹没变，就走就地刷新外观，不必重建绘制对象。
        /// 0 表示还没有成功施加过。
        /// </summary>
        public int StructureKey;
    }
}
