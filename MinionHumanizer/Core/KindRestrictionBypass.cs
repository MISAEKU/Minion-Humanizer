using System;
using System.Threading;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;

namespace MinionHumanizer.Core;

/// <summary>
/// 游戏里有一个"按类型强制限制外观"的检查：它决定了某个 actor 允不允许用某个模型
/// （比如非玩家 actor 能不能用人类模型、种族与装备是否匹配等）。
///
/// 这正是老 Glamourer 的 "Turn Human" 会出事的根源之一——它把模型改成人形之后，
/// 游戏的这个检查会认为"违规"，于是试图把模型修回去、或触发一连串重设；
/// 同步工具（Mare 一类）看到 actor 外观反复横跳，就会跟着做错误的判断。
///
/// 解决办法（Brio 的 "NPC Appearance Hack" 用的就是同一个思路）：
/// 把这个检查短路掉，返回 0（= 无违规）。模型就不会被"修回去"，
/// 也就不会有来回抖动，同步工具看到的是一致的状态。
///
/// 这里默认只在本插件功能开启期间短路，关掉功能或卸载插件立刻恢复原函数。
/// 注意：这是一个全局检查，Brio 等插件也可能 hook 同一处，多个 hook 会链式共存。
/// </summary>
public sealed class KindRestrictionBypass : IDisposable
{
    /// <summary>与 Brio 使用的同一处签名。</summary>
    private const string Signature = "E8 ?? ?? ?? ?? 41 B0 ?? 48 8B D6 48 8B";

    private delegate byte EnforceKindRestrictionsDelegate(nint a1, nint a2);

    private readonly IClientState _clientState;
    private readonly IPluginLog   _log;
    private readonly Configuration _configuration;
    private readonly Hook<EnforceKindRestrictionsDelegate>? _hook;

    /// <summary>功能开启期间的"临时短路"计数，由 <see cref="Open"/> 增减。</summary>
    private int _openWindows;

    public bool Available { get; }

    /// <summary>
    /// 当前是否真的有一只被我们改成人的宠物活着。
    /// 用它把短路"收窄"到确实需要的时段，而不是插件一开就全局放行。
    /// 由 <see cref="HumanizerService"/> 每次巡检时刷新。
    /// </summary>
    public bool HasLiveOverride { get; set; }

    public KindRestrictionBypass(IGameInteropProvider hooks, ISigScanner sigScanner, IClientState clientState, Configuration configuration,
        IPluginLog log)
    {
        _clientState   = clientState;
        _configuration = configuration;
        _log           = log;

        nint address;
        try
        {
            address = sigScanner.ScanText(Signature);
        }
        catch (Exception ex)
        {
            _log.Warning("[MinionHumanizer] 找不到 EnforceKindRestrictions 签名，将跳过该兼容处理。" +
                          "插件仍然可用，但游戏可能把人类模型强制修回去。");
            _log.Debug(ex, "[MinionHumanizer] 签名扫描失败详情。");
            return;
        }

        try
        {
            var hook = hooks.HookFromAddress<EnforceKindRestrictionsDelegate>(address, Detour);
            hook.Enable();
            _hook     = hook;
            Available = true;
            _log.Information($"[MinionHumanizer] 已挂钩 EnforceKindRestrictions @ 0x{address:X}。");
        }
        catch (Exception ex)
        {
            _log.Error(ex, "[MinionHumanizer] 挂钩 EnforceKindRestrictions 失败，将跳过该兼容处理。");
        }
    }

    private bool ShouldBypass
    {
        get
        {
            if (!Available)
                return false;

            // 我们正在自己重建模型的那一小段时间，无条件放行。
            if (Volatile.Read(ref _openWindows) > 0)
                return true;

            // 没有活着的"人形宠物"时，一律不干预游戏自己的判断。
            if (!HasLiveOverride)
                return false;

            return _configuration.BypassScope switch
            {
                RestrictionBypassScope.WhileActive => true,
                RestrictionBypassScope.InGPoseOnly => _clientState.IsGPosing,
                _                       => false,
            };
        }
    }

    private byte Detour(nint a1, nint a2)
    {
        var hook = _hook;
        if (hook is null)
            return 0;

        try
        {
            if (ShouldBypass)
                return 0;
        }
        catch (Exception)
        {
            // 任何时候出问题都退回原函数，绝不让异常沿着游戏的调用栈往上冒。
        }

        return hook.Original(a1, a2);
    }

    /// <summary>在一次"写入 + 重建"期间保持短路。用 using 包住。</summary>
    public IDisposable Open()
    {
        Interlocked.Increment(ref _openWindows);
        return new Window(this);
    }

    public void Dispose()
    {
        _hook?.Disable();
        _hook?.Dispose();
    }

    private sealed class Window(KindRestrictionBypass owner) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            Interlocked.Decrement(ref owner._openWindows);
        }
    }
}
