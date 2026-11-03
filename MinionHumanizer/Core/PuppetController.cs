using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Objects.Types;
using DalamudObjectKind = Dalamud.Game.ClientState.Objects.Enums.ObjectKind;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Common.Math;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using SceneObjectType = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.ObjectType;

namespace MinionHumanizer.Core;

/// <summary>待机动作的生效方式。</summary>
public enum IdleMode
{
    /// <summary>不改待机。</summary>
    Off,

    /// <summary>始终播放指定待机（含移动时，宠物会滑行）。</summary>
    Always,

    /// <summary>只在宠物静止时播放，一移动就还给游戏。</summary>
    WhenStill,
}

/// <summary>
/// 「提线木偶」控制器：一切<b>每帧、纯视觉、随时可停</b>的玩法都在这里。
///
/// 三件事：
///  1. 贴地补偿——蓝闪蝶这类飞行宠变人后会浮在半空。
///     <b>只写绘制对象位置，绝不碰 GameObject.DrawOffset</b>：每帧从 actor 的真实位置
///     重算成绝对目标值（actor 位置 + 用户偏移），不读也不累加上一次的结果。
///     · DrawOffset 与 NameplateOffset / CameraOffset 同级，是<b>游戏自己摆绘制对象</b>
///       的通用偏移（飞行宠的悬浮高度就靠它表达），插件覆写它会与游戏抢字段：
///       静止时看着对，一移动游戏按自己逻辑重算 → 反而下沉。
///     · 所以"补偿值 = 实际下沉量"能严格一对一，任何操作次数下都不漂移。
///     actor 本身的位置一个字节都不碰（那是游戏 AI/服务器管的）。
///  2. 体型缩放——actor 缩放（权威、持久）+ 绘制对象缩放（当帧生效），均为「基准 × 倍率」。
///  3. 待机动作 / 表情——用 Brio 验证过的机制：AnimLock 模式 + Timeline.BaseOverride +
///     PlayTimeline。只影响这只宠物自己的动画容器，不产生任何网络数据。
///
/// 动作机制参考 Brio 的 ActionTimelineCapability（GPL-3.0，仅借鉴思路，未复制代码）。
/// </summary>
public sealed unsafe class PuppetController : IDisposable
{
    private readonly Configuration    _configuration;
    private readonly HumanizerService _service;
    private readonly IObjectTable     _objectTable;
    private readonly IPluginLog       _log;

    /// <summary>当前正在播放的表情/动作（0 = 无）。重绘会打断动画，UI 据此提示。</summary>
    public ushort CurrentEmote { get; private set; }

    public string CurrentEmoteName { get; private set; } = string.Empty;

    /// <summary>当前表情播在哪个类别的目标上。</summary>
    public TargetKind CurrentEmoteKind { get; private set; } = TargetKind.Any;

    public string LastStatus { get; private set; } = string.Empty;

    /// <summary>待机动作当前是否真的生效（用于 UI 提示）。</summary>
    public bool IdleActive { get; private set; }

    /// <summary>玩家当前是否骑乘中。骑乘时整套视觉修正停摆。</summary>
    public static bool IsRiding
        => Plugin.ObjectTable.LocalPlayer?.CurrentMount is { IsValid: true };

    private readonly Dictionary<nint, SavedMotion> _savedMotion = new();
    private readonly Dictionary<nint, Vector3>    _lastPosition = new();

    /// <summary>
    /// 每个目标"我们期望的绘制高度"（用于诊断移动期是否被游戏改回）。
    /// 与待机的 _lastPosition 分开，避免两套逻辑互相污染。
    /// </summary>
    private readonly Dictionary<nint, float> _expectedDrawY = new();
    private readonly HashSet<nint>                 _idleApplied  = new();

    /// <summary>
    /// 补偿前每只目标的 DrawOffset 原值 + "曾施加过补偿"的标记（键存在即施加过）。
    /// 现在补偿只走绘制对象位置，DrawOffset 只作为需要清理的残留物被记录与还原。
    /// </summary>
    private readonly Dictionary<nint, Vector3> _savedDrawOffset = new();

    // ── 贴地逐帧追踪（/mhuman gtrace）────────────────────────────────
    // 用途：把"谁在覆盖谁"照出来。静止看着对、一移动就浮，只可能是我们的写入
    // 在移动时被游戏冲掉了——靠猜是猜不出来的，必须逐帧读回来对比。
    private readonly List<string>          _groundTrace  = new();
    private readonly Dictionary<nint, (float X, float Z)> _lastActorXZ = new();
    private readonly Dictionary<nint, float> _lastWriteY = new();
    private int  _traceLeft;
    private bool _traceReady;

    /// <summary>
    /// 闭环当前写入的 <c>DrawOffset.Y</c>（键 = 目标地址）。
    ///
    /// <para>为什么不能是"原值 + 固定偏移"：实测（ground-trace.txt）召唤物<b>移动时</b>
    /// 游戏会额外叠加一个<b>恒定 +1.20</b> 的垂直抬升，把 DrawOffset 与 Position
    /// 两条通路全部顶掉（159 个移动帧里 158 个的"留存差"恰好 1.200），
    /// 而那个抬升<b>不经过 DrawOffset</b>（它全程稳定在我们写的值上）。
    /// 固定偏移原理上就补不上，只能每帧按实测误差回灌。</para>
    /// </summary>
    private readonly Dictionary<nint, float> _visualOffsetY = new();

    /// <summary>补偿实现说明（供诊断显示）。</summary>
    public const string OffsetMethod =
        "闭环：每帧实测下沉量，把误差回灌 DrawOffset.Y（自动抵消游戏移动时叠加的垂直抬升）；同时写绘制位置";

    /// <summary>
    /// 每个目标的<b>缩放基准值</b>（游戏自己给它定的原始缩放），还原时写回它。
    ///
    /// 关键：缩放参数是<b>倍率</b>而不是绝对值。游戏给不同宠物定的原始缩放并不相同，
    /// 直接把配置值当绝对值写进去会抹掉这个基准——这就是"参数明明是 1，
    /// 体型却小得离谱（或大得离谱）"的根因。
    /// </summary>
    private readonly Dictionary<nint, ScaleBaseline> _scaleBaseline = new();

    private sealed class ScaleBaseline
    {
        /// <summary>
        /// 绘制缩放的<b>原始基准</b>（首次捕获时绘制对象的 Scale.X）。
        ///
        /// 注意名字沿用 Actor 是历史遗留，实际含义是"基准值"——
        /// v1.0 起我们<b>不再写 actor 的 Scale</b>（那是游戏的权威字段，
        /// 覆写它会导致基准自我污染与"反复开关越来越小"）。
        /// <b>一经捕获永不再读回</b>：任何重新采样读到的都可能是我们自己的写入结果。
        /// </summary>
        public float Actor = 1f;

        /// <summary>
        /// 绘制对象缩放相对 actor 缩放的比例（首次捕获时的Draw.X / Actor）。
        /// 绘制对象重建后按这个比例重建，而不是重新采样（采样同样会被污染）。
        /// </summary>
        public float DrawRatio = 1f;

        /// <summary>捕获基准时对应的绘制对象指针，用来发现"绘制对象被重建了"。</summary>
        public nint DrawObjectPtr;

        /// <summary>基准是否可信（游戏把缩放初始化完成之后才接受）。</summary>
        public bool Valid;
    }

    /// <summary>每帧复用的缓冲，避免在 Update 里制造垃圾。</summary>
    private readonly List<CompanionHandle> _appliedBuffer = new(4);

    /// <summary>
    /// 取某个目标当前捕获到的<b>绘制缩放基准</b>（游戏最初给它定的那个值）。
    ///
    /// 自检报告显示用：缩放参数是<b>倍率</b>，实际绘制 = 基准 × 倍率。
    /// 不把基准打印出来就会出现"参数 ×1.00、实际绘制 0.850"这种
    /// 看起来像 bug 的显示（实测被当成参数错误报了好几次）。
    /// </summary>
    public float? GetDrawScaleBaseline(nint address)
        => _scaleBaseline.TryGetValue(address, out var b) && b.Valid
            ? b.Actor * b.DrawRatio
            : null;

    private sealed class SavedMotion
    {
        public CharacterModes Mode;
        public byte           ModeParam;
        public ushort         BaseOverride;
    }

    public PuppetController(Configuration configuration, HumanizerService service, IObjectTable objectTable, IPluginLog log)
    {
        _configuration = configuration;
        _service       = service;
        _objectTable   = objectTable;
        _log           = log;
    }

    // ────────────────────────────────────────────────────────────────
    //  每帧视觉修正 + 待机
    // ────────────────────────────────────────────────────────────────

    /// <summary>由 Framework.Update 调用（游戏更新之后），修正只影响这一帧的渲染。</summary>
    public void Tick()
    {
        if (!_configuration.Enabled)
        {
            // ⚠️ 功能关闭时必须把视觉修正<b>全部落地还原</b>，否则宠物会永久停在
            // 偏移/缩放后的状态（模型会被 HumanizerService 还原，但位置和缩放不会）。
            // 这里不能像以前那样直接 return。
            if (_expectedDrawY.Count > 0 || _scaleBaseline.Count > 0 || _savedDrawOffset.Count > 0 || IdleActive)
            {
                _service.CopyAppliedTo(_appliedBuffer);
                ReleaseAllOffsets();
                RestoreDrawOffsets();
                RestoreAllActorScales();
                ReleaseAllMotion();

                _expectedDrawY.Clear();
                _scaleBaseline.Clear();
                _savedDrawOffset.Clear();
        _visualOffsetY.Clear();
                _idleState.Clear();
                _idleApplied.Clear();
                _savedMotion.Clear();
                IdleActive = false;
            }

            _appliedBuffer.Clear();
            return;
        }

        // 换区后基准作废的信号（由 Plugin 在换区时推进纪元）。
        SyncVisualEpoch(Plugin.VisualEpoch);

        // ⚠️ 纵深防御：骑乘中一律停手。
        // 坐骑与人共享同一套渲染上下文，此时对任何目标做视觉修正都可能影响到骑乘表现，
        // 所以这里整体关停，等下坐骑再恢复。finder 里已经挡过一次，这里是第二道。
        if (IsRiding)
        {
            if (IdleActive || _savedDrawOffset.Count > 0 || _scaleBaseline.Count > 0)
            {
                ReleaseAllMotion();
                RestoreDrawOffsets();
                RestoreAllActorScales();
                IdleActive = false;
            }

            return;
        }

        // 视觉修正 / 待机是否"有需求"：逐类别问配置（新增类别时这里不用改）。
        var wantOffset = VisualWanted(TargetKind.Minion)      || VisualWanted(TargetKind.ChocoboPartner)
                      || VisualWanted(TargetKind.Carbuncle)   || VisualWanted(TargetKind.Fairy);
        var wantScale  = ScaleWanted(TargetKind.Minion)       || ScaleWanted(TargetKind.ChocoboPartner)
                      || ScaleWanted(TargetKind.Carbuncle)    || ScaleWanted(TargetKind.Fairy);

        // 待机按类别各取各的：四类目标可以配成不同的待机。
        var petIdle     = _configuration.GetIdle(TargetKind.Minion);
        var chocoboIdle = _configuration.GetIdle(TargetKind.ChocoboPartner);
        var carbIdle    = _configuration.GetIdle(TargetKind.Carbuncle);
        var fairyIdle   = _configuration.GetIdle(TargetKind.Fairy);
        var wantIdle    = IdleWanted(petIdle) || IdleWanted(chocoboIdle)
                      || IdleWanted(carbIdle)    || IdleWanted(fairyIdle);

        if (!wantOffset && !wantScale && !wantIdle)
        {
            if (IdleActive)
                ReleaseAllMotion();

            RestoreDrawOffsets();
            RestoreAllActorScales();
            _appliedBuffer.Clear();
            Prune();
            IdleActive = false;
            return;
        }

        _service.CopyAppliedTo(_appliedBuffer);

        // 追踪倒计时：每帧只减一次（无论现场有几个目标）。
        if (_traceLeft > 0 && --_traceLeft == 0)
            _traceReady = true;

        foreach (var handle in _appliedBuffer)
        {
            // ── 0. 硬性范围限制 ──
            // 跟宠坐骑不参与任何视觉/动画干预；宠物与陆行鸟搭档可以，
            // 但各用各的系数（陆行鸟的视觉修正在设置里单独开关）。
            var allowVisual  = handle.Kind.AllowVisualPuppetry() && _configuration.GetVisualEnabled(handle.Kind);
            var allowAnim    = handle.Kind.AllowAnimationControl();
            if (!allowVisual && !allowAnim)
                continue;

            // ⚠️ v1.29.2：这两个值**必须按 handle.Kind 逐类别取**，
            // 不能用外层 wantOffset / wantScale 那个"四类任一开启"的全局 OR。
            //
            // 旧代码 `if (wantOffset && allowVisual)` 的坑：wantOffset 是全局 OR，
            // 宠物侧一旦为 true（此前因 null 模式匹配恒为 true）整个 OR 恒真，
            // 于是**陆行鸟搭档自己设的 UseVisual=false 被架空**，
            // 它照样被写入 —— 表现就是「陆行鸟的视觉修正参数跑去影响宠物」。
            //
            // 现在这两个局部量表达的是"**这一只**到底该不该被修正 / 缩放"，
            // 外层的 wantOffset / wantScale 只保留"是否需要进循环"的门闸作用。
            var offsetForThis = _configuration.GetGroundOffset(handle.Kind);
            var scaleForThis  = _configuration.GetVisualScaleValue(handle.Kind);
            var wantOffsetThis = VisualWanted(handle.Kind);
            var wantScaleThis  = ScaleWanted(handle.Kind);

            try
            {
                var gameObject = handle.GameObject;

                // ── 1. 贴地补偿：恢复 v0.3 的两条通路（坐骑不参与）──
                //
                // 回到"以前能压住浮空"的做法：两条通路都写。
                //  · (a) GameObject.DrawOffset —— 游戏摆绘制对象时会读它，所以移动中的
                //    重算天然带上它，这是"走动时不再浮空"的关键。
                //  · (b) 绘制对象 Position —— 兜底纠偏，静止时立刻见效。
                //
                // ⚠️ 之前"设 -1 却沉了 -3 / 每按一次施加多沉一层"的根因：
                // 两条通路各加了一次同一个偏移（渲染端两者都用 → 算两遍），
                // 而且各自维护"基准"，互相污染。
                //
                // 现在的关键改动：<b>(b) 的目标值直接取 (a) 算出来的结果</b>
                //   targetY = actor.Y + DrawOffset.Y
                // 于是 (b) 永远只是"把游戏本来就会摆到的位置补上"，两者指向
                // <b>同一个位置</b> —— 收敛关系，绝不会叠加成两倍。
                if (wantOffset && allowVisual)
                {
                    // ⚠️ 游戏原值必须在写入<b>之前</b>捕获（关闭时要原样写回去）。
                    if (!_savedDrawOffset.ContainsKey(handle.Address))
                        _savedDrawOffset[handle.Address] = gameObject->DrawOffset;

                    var drawOffset = gameObject->DrawOffset;
                    var gameLeftY  = drawOffset.Y;          // 写入前游戏留下的值（追踪用）

                    var drawObject = gameObject->DrawObject;
                    if (drawObject is not null && drawObject->Object.GetObjectType() == SceneObjectType.CharacterBase)
                    {
                        var cb       = (CharacterBase*)drawObject;
                        var actorPos = gameObject->Position;
                        var current  = cb->Position;

                        // ── 闭环自适应：每帧按实测误差回灌 DrawOffset ──────────
                        //
                        // 为什么不能再用"原值 + 固定偏移"：
                        //   召唤物<b>移动时</b>游戏会额外叠加恒定 +1.20 的垂直抬升，
                        //   把 DrawOffset 与 Position 两条路全部顶掉；那个抬升又不经过
                        //   DrawOffset，所以固定偏移原理上就补不上（这正是前几版
                        //   "怎么调数值都还是浮"的真正原因，不是数值不够）。
                        //
                        // 收敛性（一步到位）：
                        //   游戏摆出的位置 = actor.Y + DrawOffset.Y + 抬升
                        //   误差 = 目标 - 实际 = 目标 - DrawOffset.Y - 抬升
                        //   DrawOffset.Y += 误差 ⇒ DrawOffset.Y = 目标 - 抬升
                        // 抬升再变，下一帧自动跟随。限幅 0.5 防振荡。
                        if (!_visualOffsetY.TryGetValue(handle.Address, out var curY))
                            curY = _savedDrawOffset[handle.Address].Y;

                        var actualSink = current.Y - actorPos.Y;      // 帧首真实下沉（含游戏抬升）
                        var error      = offsetForThis - actualSink;   // 还差多少才到目标

                        if (Math.Abs(error) > 0.005f)
                            curY += Math.Clamp(error, -0.5f, 0.5f);

                        curY = Math.Clamp(curY, -8f, 4f);
                        _visualOffsetY[handle.Address] = curY;

                        drawOffset.Y = curY;
                        gameObject->DrawOffset = drawOffset;

                        // ── 同时直接写绘制位置：游戏这一帧若不重算，立刻见效 ──
                        var targetY = actorPos.Y + offsetForThis;
                        if (Math.Abs(current.Y - targetY) > 0.005f)
                        {
                            var p = current;
                            p.Y = targetY;
                            cb->Position = p;

                            // 通知渲染按新位置重算变换矩阵（只写字段是不够的）。
                            cb->IsTransformChanged = true;
                        }

                        _expectedDrawY[handle.Address] = targetY;
                        _lastWriteY[handle.Address]   = targetY;
                        TraceGround(handle, actorPos, gameLeftY, curY, current.Y);
                    }
                }
                else if (allowVisual)
                {
                    // ⚠️ v1.29.2：走到这里说明这一类别自己没开视觉修正
                    // （wantOffsetThis 为 false）→ 把它的脏值还原回去。
                    // 原来这里用外层全局 wantOffset 决定，与本类别无关，
                    // 导致陆行鸟搭档关掉自己的开关后仍被写入。
                    RestoreDrawOffset(handle);
                }

                // ── 2. 体型缩放：<b>只写绘制对象的 Scale</b>（纯渲染层，坐骑不参与）──
                //
                // 回退到 v1.0 的最简做法：<b>完全不碰 GameObject.Scale</b>。
                //
                // 为什么不再写 actor 缩放（v0.8~v0.13 的做法）：
                //   · actor 的 Scale 是游戏<b>权威字段</b>，它决定命中判定、跟随距离、
                //     特效挂点、很多游戏逻辑。我们每帧覆写它就是和游戏抢这个字段，
                //     一旦在错误时机被游戏重算、或我们重新采样基准，就会出现
                //     "参数显示 1但体型不对" / "反复开关越来越小"（基准自我污染 → 指数缩小）。
                //   · 而"体型缩放"本质是纯视觉诉求，只改绘制对象就够了。
                //
                // 参数语义：<b>倍率</b>，实际绘制缩放 = 首次捕获的基准 × 倍率。
                // 基准只在从未写过该绘制对象时捕获一次，之后永不重新采样（否则基准被污染）。
                if (allowVisual)
                {
                    var drawObject = gameObject->DrawObject;
                    if (drawObject is null || drawObject->Object.GetObjectType() != SceneObjectType.CharacterBase)
                        continue;

                    // 绘制对象被重建（新指针）→ 必须重新捕获基准，否则会拿失效基准去算。
                    if (!_scaleBaseline.TryGetValue(handle.Address, out var baseline)
                     || !baseline.Valid
                     || baseline.DrawObjectPtr != (nint)drawObject)
                    {
                        // 基准<b>即使在倍率=1.00 时也要捕获</b>：
                        // 否则"参数×1.00 却显示实际 1.000（基准未捕获）"，
                        // 用户无从判断体型是否正常，而且以后把倍率调离 1.00 时没有基准可用。
                        if (!TryCaptureScaleBaseline(gameObject))
                            continue;

                        baseline = _scaleBaseline[handle.Address];
                    }

                    if (wantScaleThis && Math.Abs(scaleForThis - 1f) > 0.0001f)
                    {
                        // 绝对值写入（不是累加）→ 天然幂等，反复开关都不会缩放。
                        var target = baseline.Actor * scaleForThis;
                        ((CharacterBase*)drawObject)->Scale = new Vector3(target, target, target);
                    }
                    else
                    {
                        RestoreActorScale(handle);
                    }
                }

                // ── 3. 待机动作（按类别各用各的设置）──
                if (allowAnim)
                {
                    var (mode, timelineId) = _configuration.GetIdle(handle.Kind);
                    var enabled = mode != IdleMode.Off && timelineId != 0;

                    // 表情优先：正在播表情时待机让位。
                    TickIdle(handle, gameObject->Position, enabled && CurrentEmote == 0, mode, timelineId);
                }
                else if (_idleApplied.Contains(handle.Address))
                {
                    ReleaseMotion(handle);
                }
            }
            catch (Exception ex)
            {
                _log.Debug(ex, "[MinionHumanizer] 视觉修正异常（本帧跳过）。");
            }
        }

        Prune();
    }

    /// <summary>本帧正在进行"还原"，此时不能动视觉基准（还原后会立刻重新施加）。</summary>
    private bool _restoring;

    /// <summary>标记一次还原操作的开始（期间忽略重绘带来的基准失效）。</summary>
    public void BeginRestore() => _restoring = true;

    /// <summary>标记一次还原操作的结束。</summary>
    public void EndRestore() => _restoring = false;

    /// <summary>
    /// 丢弃某个目标的全部视觉修正状态（缩放基准 / 贴地基准 / 待机存档）。
    ///
    /// 拟人化重绘会换掉绘制对象，之前捕获的缩放基准随之失效，
    /// 必须丢弃让它在下一帧重新捕获——否则换区或换装后会一直用旧基准算，
    /// 表现就是"体型越修越怪 / 参数和实际对不上"。
    ///
    /// ⚠️ <b>还原操作期间必须跳过</b>：还原后巡检会立刻重新施加，
    /// 此时若把基准清掉，下一次捕获到的"原始值"其实是刚写回去的修正值——
    /// 每次点一次"还原"就多沉一层（用户实测反馈）。
    /// </summary>
    public void ForgetVisualState(nint address)
    {
        if (_restoring)
            return;

        Forget(address);
    }

    /// <summary>上一次同步过的重绘纪元。</summary>
    private int _epochSeen;

    /// <summary>
    /// 同步重绘纪元：换区后绘制对象全部换新，之前捕获的缩放/贴地基准一律作废。
    /// 不清就会拿旧基准去乘新对象的尺寸——体型会随换区越来越离谱。
    /// </summary>
    public void SyncVisualEpoch(int epoch)
    {
        if (epoch == _epochSeen)
            return;

        _epochSeen = epoch;

        // ⚠️ 丢弃基准前<b>必须先还原</b>。
        // 只Clear 不还原 → 游戏里残留着上一轮的偏移/缩放；下一轮重新捕获时把
        //这个残留值当成"基准"，于是每换一次区就往下沉一层（用户反馈：反复施加/还原后
        // 宠物跑进地里）。所以顺序必须是：还原 → 清记录。
        RestoreAllOffsets();
        RestoreAllActorScales();
        _scaleBaseline.Clear();
        _savedDrawOffset.Clear();
        _visualOffsetY.Clear();
    }

    /// <summary>把所有还留着的贴地偏移写回原值（丢弃基准前调用）。</summary>
    private void RestoreAllOffsets()
    {
        foreach (var address in _savedDrawOffset.Keys.ToList())
        {
            if (!_savedDrawOffset.TryGetValue(address, out var original))
                continue;

            try
            {
                // 这里不能依赖 handle（对象可能已失效），直接按地址写回原值。
                var obj = _objectTable.FirstOrDefault(o => o is not null && o.Address == address);
                if (obj is not null)
                {
                    var gameObject = (GameObject*)obj.Address;
                    gameObject->DrawOffset = original;
                }
            }
            catch (Exception ex)
            {
                _log.Debug(ex, "[MinionHumanizer] 还原DrawOffset 失败。");
            }
        }

        _savedDrawOffset.Clear();
        _visualOffsetY.Clear();
    }

    /// <summary>清掉已经不再被跟踪的宠物的所有内部状态（防止字典无限增长）。</summary>
    private void Prune()
    {
        if (_savedMotion.Count == 0 && _lastPosition.Count == 0
         && _idleApplied.Count == 0 && _savedDrawOffset.Count == 0 && _scaleBaseline.Count == 0
         && _expectedDrawY.Count == 0)
            return;

        bool Alive(nint address)
        {
            if (_service.IsTracked(address))
                return true;

            foreach (var handle in _appliedBuffer)
            {
                if (handle.Address == address)
                    return true;
            }

            return false;
        }

        foreach (var address in _savedMotion.Keys.ToList())
            if (!Alive(address))
                _savedMotion.Remove(address);

        foreach (var address in _lastPosition.Keys.ToList())
            if (!Alive(address))
                _lastPosition.Remove(address);

        foreach (var address in _expectedDrawY.Keys.ToList())
            if (!Alive(address))
                _expectedDrawY.Remove(address);

        foreach (var address in _idleApplied.ToList())
            if (!Alive(address))
                _idleApplied.Remove(address);

        foreach (var address in _idleState.Keys.ToList())
            if (!Alive(address))
                _idleState.Remove(address);

        foreach (var address in _savedDrawOffset.Keys.ToList())
            if (!Alive(address))
            {
                _savedDrawOffset.Remove(address);
                _visualOffsetY.Remove(address);
            }

        foreach (var address in _scaleBaseline.Keys.ToList())
            if (!Alive(address))
                _scaleBaseline.Remove(address);
    }

    /// <summary>
    /// 捕获一个目标的原始缩放基准。
    ///
    /// 必须在游戏把缩放初始化完成之后才接受：刚创建 / 刚重绘的绘制对象可能还带着
    /// 上一次的脏值（那正是"体型随机异常"的来源之一），所以这里做区间校验，
    /// 不在合理范围内就返回 false，让调用方下一帧重试。
    /// </summary>
    private bool TryCaptureScaleBaseline(GameObject* gameObject)
    {
        // ⚠️ 只能在<b>从未写过该字段</b>时捕获。
        // 已有基准 → 不再重新采样 actor 缩放（那是我们自己写进去的值）。
        if (_scaleBaseline.TryGetValue((nint)gameObject, out var existing) && existing.Valid)
            return true;

        var drawObject = gameObject->DrawObject;
        if (drawObject is null || drawObject->Object.GetObjectType() != SceneObjectType.CharacterBase)
            return false;

        // 基准只取<b>绘制对象</b>当前的缩放（我们写它之前的那一帧）。
        var draw = ((CharacterBase*)drawObject)->Scale;
        var actor = draw.X;

        // 缩放不可能为 0、负数或几十倍；超出说明尚未初始化完或已经是脏值。
        if (!IsSaneScale(actor) || !IsSaneScale(draw.X) || !IsSaneScale(draw.Y) || !IsSaneScale(draw.Z))
        {
            _scaleBaseline.Remove((nint)gameObject);
            return false;
        }

        _scaleBaseline[(nint)gameObject] = new ScaleBaseline
        {
            Actor         = actor,
            DrawRatio     = Math.Abs(actor) > 0.0001f ? draw.X / actor : 1f,
            DrawObjectPtr = (nint)drawObject,
            Valid         = true,
        };

        return true;
    }

    private static bool IsSaneScale(float value)
        => value is > 0.005f and < 20f;

    /// <summary>
    /// 构造一个只用于"写回原值"的内部句柄。
    /// 用 <see cref="TargetKind.Any"/> 表示"不按类别过滤"——
    /// 之前硬编码 <c>TargetKind.Minion</c> 会让陆行鸟搭档被守卫挡掉、永远还原不回去。
    /// </summary>
    private static CompanionHandle VisualHandle(nint address)
        => new(address, 0, string.Empty, TargetKind.Any);

    /// <summary>
    /// 严格还原到基准：<b>actor 与绘制对象两处都写回</b>。
    ///
    /// 之前只还原 actor 缩放，绘制对象那份脏值一直留着——于是
    /// "参数已经回到 1、体型却还是那个小尺寸"就是这么来的。
    /// </summary>
    private void RestoreActorScale(CompanionHandle handle)
    {
        if (handle.IsNull)
            return;

        if (!_scaleBaseline.TryGetValue(handle.Address, out var baseline))
            return;

        try
        {
            // 只还原<b>绘制对象</b>的缩放（与写入路径严格对称）。
            // 不碰 actor 的 Scale —— 我们从未写过它，就必须从未改过它；
            // 否则"还原"反而会在 actor 层留下我们自己的脏值。
            var gameObject = handle.GameObject;
            var drawObject = gameObject->DrawObject;
            if (drawObject is not null && drawObject->Object.GetObjectType() == SceneObjectType.CharacterBase)
            {
                var original = baseline.Actor * baseline.DrawRatio;
                ((CharacterBase*)drawObject)->Scale = new Vector3(original, original, original);
            }
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "[MinionHumanizer] 还原缩放失败。");
        }

        _scaleBaseline.Remove(handle.Address);
    }

    /// <summary>
    /// 同步把所有目标的<b>绘制位置</b>放回 actor 的真实位置（卸载 / 关功能时用）。
    /// 卸载后没有下一帧，跑在 Tick 里的纠正逻辑全部失效，所以必须在这里直接落地。
    /// </summary>
    private void ReleaseAllOffsets()
    {
        if (_expectedDrawY.Count == 0)
            return;

        // 不用 _appliedBuffer（它在某些时机是空的）——直接按地址从对象表取，
        // 保证卸载 / 关开关这条路径一定能覆盖到所有被补偿过的目标。
        foreach (var obj in _objectTable)
        {
            if (obj is null || obj.Address == nint.Zero)
                continue;

            if (!_expectedDrawY.ContainsKey(obj.Address))
                continue;

            if (!TargetKindOf(obj).AllowVisualPuppetry())
                continue;

            try
            {
                var gameObject = (GameObject*)obj.Address;

                // 先把 DrawOffset 写回原值，绘制位置才能回到"actor + 原偏移"这个正确目标。
                if (_savedDrawOffset.TryGetValue(obj.Address, out var original))
                    gameObject->DrawOffset = original;

                var drawObject = gameObject->DrawObject;
                if (drawObject is not null && drawObject->Object.GetObjectType() == SceneObjectType.CharacterBase)
                    ((CharacterBase*)drawObject)->Position =
                        new Vector3(gameObject->Position.X, gameObject->Position.Y + gameObject->DrawOffset.Y, gameObject->Position.Z);
            }
            catch (Exception ex)
            {
                _log.Debug(ex, "[MinionHumanizer] 还原绘制位置失败。");
            }
        }
    }

    /// <summary>开始一段"贴地逐帧追踪"（默认 300 帧 ≈ 5 秒）。</summary>
    public void StartGroundTrace(int frames = 300)
    {
        _groundTrace.Clear();
        _lastActorXZ.Clear();
        _lastWriteY.Clear();
        _traceLeft  = frames;
        _traceReady = false;
    }

    /// <summary>追踪是否已跑满并可以取走。</summary>
    public bool GroundTraceReady => _traceReady;

    /// <summary>取走追踪内容（取后即清空 ready 标记，避免重复写文件）。</summary>
    public string TakeGroundTrace()
    {
        _traceReady = false;
        return string.Join(Environment.NewLine, _groundTrace);
    }

    /// <summary>
    /// 逐帧记录贴地补偿的<b>真实收支</b>：游戏留下什么、我们要写什么、
    /// 上一帧写进去的值到这一帧还剩多少（剩 = 没被游戏覆盖，不剩 = 被冲掉了）。
    /// </summary>
    private void TraceGround(CompanionHandle handle, Vector3 actorPos, float gameLeftY, float wantY, float beforeDrawY)
    {
        if (_traceLeft <= 0)
            return;

        _lastActorXZ.TryGetValue(handle.Address, out var prev);
        var moved = Math.Abs(actorPos.X - prev.X) + Math.Abs(actorPos.Z - prev.Z) > 0.0005f;
        _lastActorXZ[handle.Address] = (actorPos.X, actorPos.Z);

        // 上一帧写进去的目标值，这一帧还剩多少 —— 直接回答"我们的写入有没有被冲掉"。
        var kept = _lastWriteY.TryGetValue(handle.Address, out var w)
            ? $"{beforeDrawY - w:+0.000;-0.000; 0.000}"
            : "   —   ";

        _groundTrace.Add(
            $"[{handle.Kind,-8}] actorY={actorPos.Y,7:0.000}"
          + $" | DrawOffset 游戏留={gameLeftY,7:0.000} 我们要={wantY,7:0.000}"
          + $" | 帧首DrawY={beforeDrawY,7:0.000} 帧首下沉={beforeDrawY - actorPos.Y,7:0.000}"
          + $" | 上帧写入留存差={kept} | 移动={(moved ? "是" : "否")}");
    }

    /// <summary>
    /// 某一类别当前<b>实际生效</b>的下沉量（绘制位置 - actor 位置），用于 UI 实时显示。
    /// 返回 null 表示现场没有这一类目标。
    /// </summary>
    public unsafe string? DescribeVisual(Core.TargetKind kind)
    {
        _service.CopyAppliedTo(_appliedBuffer);

        foreach (var handle in _appliedBuffer)
        {
            if (handle.Kind != kind)
                continue;

            var draw = handle.GameObject->DrawObject;
            if (draw is null || draw->Object.GetObjectType() != SceneObjectType.CharacterBase)
                return "（无绘制对象）";

            var actorPos = handle.GameObject->Position;
            var sink     = ((CharacterBase*)draw)->Position.Y - actorPos.Y;

            // 闭环写入值一起显示：它与目标值的差就是"游戏此刻叠加的垂直抬升"，
            // 用户能直接看到闭环吃掉了多少，而不必再自己猜该填几个数。
            var loop = _visualOffsetY.TryGetValue(handle.Address, out var ly) ? ly : float.NaN;
            return float.IsNaN(loop)
                ? $"{sink:0.00}"
                : $"{sink:0.00}（闭环写入 {loop:0.00}）";
        }

        return null;
    }

    /// <summary>这一类别是否需要做贴地补偿。</summary>
    private bool VisualWanted(TargetKind kind)
        => _configuration.GetVisualEnabled(kind);

    /// <summary>
    /// 这一类别是否需要改体型缩放。
    ///
    /// ⚠️ v1.29.2：除了开关，还必须看倍率真的不是 1。
    /// 否则一旦宠物侧开着、倍率恰好为 1，<c>wantScale</c> 仍为 true，
    /// 会让所有类别都进缩放分支去"施加一个乘 1 的值"——纯多余的绘制对象写入。
    /// </summary>
    private bool ScaleWanted(TargetKind kind)
        => _configuration.GetVisualEnabled(kind)
        && Math.Abs(_configuration.GetVisualScaleValue(kind) - 1f) > 0.0001f;

    /// <summary>这一组待机设置是否"有内容"。</summary>
    private static bool IdleWanted((IdleMode Mode, int TimelineId) idle)
        => idle.Mode != IdleMode.Off && idle.TimelineId != 0;

    /// <summary>
    /// 卸载路径专用的类别判断：只用对象类别粗分，不做归属判断。
    /// NPC 类目标（陆行鸟 / 召唤物）共用同一套写入流程，这里统一按"陆行鸟"处理即可
    ///（per-kind 的值由配置查表，不依赖这个分类的精度）。
    /// </summary>
    private static TargetKind TargetKindOf(IGameObject obj)
        => obj.ObjectKind == DalamudObjectKind.Companion
            ? TargetKind.Minion
            : TargetKind.ChocoboPartner;

    private void RestoreDrawOffsets()
    {
        foreach (var address in _savedDrawOffset.Keys.ToList())
            RestoreDrawOffset(VisualHandle(address));

        _savedDrawOffset.Clear();
        _visualOffsetY.Clear();
    }

    private void RestoreAllActorScales()
    {
        foreach (var address in _scaleBaseline.Keys.ToList())
            RestoreActorScale(VisualHandle(address));

        _scaleBaseline.Clear();
    }

    private void RestoreDrawOffset(CompanionHandle handle)
    {
        if (handle.IsNull)
            return;

        // 记录已存在 = 说明曾经施加过补偿，需要还原。
        if (!_savedDrawOffset.TryGetValue(handle.Address, out var original))
            return;

        try
        {
            var gameObject = handle.GameObject;
            gameObject->DrawOffset = original;
            _expectedDrawY.Remove(handle.Address);

            // 绘制对象位置回到"actor + 原 DrawOffset"——与施加时完全对称。
            var actorPos = gameObject->Position;
            var drawObject = gameObject->DrawObject;
            if (drawObject is not null && drawObject->Object.GetObjectType() == SceneObjectType.CharacterBase)
            {
                var p = ((CharacterBase*)drawObject)->Position;
                ((CharacterBase*)drawObject)->Position =
                    new Vector3(p.X, actorPos.Y + original.Y, p.Z);
            }

        }
        catch (Exception ex)
        {
            _log.Debug(ex, "[MinionHumanizer] 还原贴地补偿失败。");
        }

        _savedDrawOffset.Remove(handle.Address);
            _visualOffsetY.Remove(handle.Address);
    }

    /// <summary>
    /// 待机状态机——带迟滞，且按目标类别各用各的设置。
    ///
    /// 之前有两个真bug：
    ///  1. <b>Always 模式完全失效</b>：只有 WhenStill 分支会去置 <c>state.Active</c>，
    ///     于是 Always 模式下 Active 永远是 false，下面的应用分支永远进不去——待机从没生效过。
    ///     现在 Active 的推导对三种模式统一处理。
    ///  2. <b>抽搐</b>：逐帧比较位移太天真，待机时AI 重算路径/服务器纠���会造成微小抖动，
    ///     待机反复贴上/撕掉。现在用指数平滑 + 连续 N 帧才切+ 切换后冷却。
    ///
    /// 另外每帧只写 BaseOverride、<b>不重播时间轴</b>：PlayTimeline 会让动画从头开始，
    /// 看上去就是又卡了一下。只在真正需要重新起播时才调（首次或被游戏冲掉）。
    /// </summary>
    private void TickIdle(CompanionHandle handle, Vector3 position, bool wantIdle, IdleMode mode, int timelineId)
    {
        var state = _idleState.TryGetValue(handle.Address, out var s) ? s : new IdleState();
        state.Frame++;

        // 换了待机目标（改了 ID 或换了类别设置）→ 重新从"没贴"开始算。
        if (state.TimelineId != timelineId)
        {
            state.TimelineId = timelineId;
            state.Active      = false;
            state.StillFrames = 0;
            state.MoveFrames  = 0;
        }

        // 位移做指数平滑：单帧噪声不直接翻转状态。
        if (_lastPosition.TryGetValue(handle.Address, out var last))
        {
            var dx = position.X - last.X;
            var dy = position.Y - last.Y;
            var dz = position.Z - last.Z;
            state.Speed = state.Speed * 0.8f + (dx * dx + dy * dy + dz * dz) * 0.2f;
        }

        _lastPosition[handle.Address] = position;
        _idleState[handle.Address]    = state;

        var moving = state.Speed > MoveThresholdSq;

        // ── 迟滞状态机 ──
        if (!wantIdle)
        {
            // 不该有待机：立刻回到"没贴"状态。
            state.Active        = false;
            state.StillFrames   = 0;
            state.MoveFrames    = 0;
            state.CooldownUntil = state.Frame + CooldownFrames;
            _idleState[handle.Address] = state;
        }
        else
        {
            var shouldBeActive = mode switch
            {
                IdleMode.Always => true,
                IdleMode.WhenStill => state.Active
                    ? !moving || state.MoveFrames < MoveFramesToRelease   // 已贴着：连续几帧动了才撕
                    : !moving && ++state.StillFrames >= StillFramesToApply && state.Frame >= state.CooldownUntil,
                _ => false,
            };

            if (shouldBeActive && !state.Active)
            {
                state.Active        = true;
                state.MoveFrames    = 0;
                state.CooldownUntil = state.Frame + CooldownFrames;
            }
            else if (!shouldBeActive && state.Active)
            {
                state.Active        = false;
                state.StillFrames   = 0;
                state.CooldownUntil = state.Frame + CooldownFrames;
            }
            else if (state.Active)
            {
                if (moving)
                    ++state.MoveFrames;
                else
                    state.MoveFrames = 0;
            }
            else if (!moving)
            {
                ++state.StillFrames;
            }
            else
            {
                state.StillFrames = 0;
            }

            _idleState[handle.Address] = state;
        }

        // ── 执行 ──
        if (wantIdle && state.Active)
        {
            if (!_idleApplied.Contains(handle.Address))
            {
                // 首次贴上：真正起播一次。
                if (ApplyIdle(handle))
                    LastStatus = $"待机动作 #{timelineId} 已生效。";
            }
            else
            {
                // 已经贴着：只在游戏把 BaseOverride 冲掉时补写，不重播。
                try
                {
                    var character = handle.Character;
                    if (character->Timeline.BaseOverride != (ushort)timelineId)
                        character->Timeline.BaseOverride = (ushort)timelineId;
                }
                catch (Exception ex)
                {
                    _log.Debug(ex, "[MinionHumanizer] 维持待机失败。");
                }
            }

            IdleActive = true;
        }
        else if (_idleApplied.Contains(handle.Address))
        {
            ReleaseMotion(handle);
            IdleActive = _idleApplied.Count > 0;
        }
    }

    /// <summary>移动判定阈值（平方距离）。约 0.05 yalm/帧 ≈ 3 yalm/秒。</summary>
    private const float MoveThresholdSq = 0.0025f;

    /// <summary>连续静止多少帧后贴上待机（约 0.2 秒）。</summary>
    private const int StillFramesToApply = 12;

    /// <summary>连续移动多少帧后撕掉待机（约 0.1 秒）。</summary>
    private const int MoveFramesToRelease = 6;

    /// <summary>切换后的冷却帧数（约 0.5 秒），期间不再切换。</summary>
    private const int CooldownFrames = 30;

    private struct IdleState
    {
        public int   Frame;
        public float Speed;
        public bool  Active;
        public int   StillFrames;
        public int   MoveFrames;
        public int   CooldownUntil;
        public int   TimelineId;
    }

    private readonly Dictionary<nint, IdleState> _idleState = new();

    /// <summary>
    /// 给某个目标贴上待机（存档原始模式以便恢复），并真正起播一次。
    /// 待机 ID 按目标类别取：陆行鸟搭档可以用与宠物不同的待机。
    /// </summary>
    public bool ApplyIdle(CompanionHandle handle)
    {
        if (handle.IsNull || !handle.Kind.AllowAnimationControl())
            return false;

        var (_, timelineId) = _configuration.GetIdle(handle.Kind);
        if (timelineId is <= 0 or > ushort.MaxValue)
            return false;

        var timeline = (ushort)timelineId;

        if (!SaveMotion(handle))
            return false;

        try
        {
            var character = handle.Character;
            character->SetMode(CharacterModes.AnimLock, 0);
            character->Timeline.BaseOverride = timeline;
            character->Timeline.TimelineSequencer.PlayTimeline(timeline);

            _idleApplied.Add(handle.Address);
            IdleActive = true;
            LastStatus = $"{handle.Kind.DisplayName()} 待机动作 #{timeline} 已生效。";
            return true;
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "[MinionHumanizer] 设置待机动作失败。");
            return false;
        }
    }

    /// <summary>首次覆盖某个目标时存档它的原始动画模式（之后不重复存）。</summary>
    private bool SaveMotion(CompanionHandle handle)
    {
        if (_savedMotion.ContainsKey(handle.Address))
            return true;

        try
        {
            var character = handle.Character;
            _savedMotion[handle.Address] = new SavedMotion
            {
                Mode         = character->Mode,
                ModeParam    = character->ModeParam,
                BaseOverride = character->Timeline.BaseOverride,
            };
            return true;
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "[MinionHumanizer] 存档动画模式失败。");
            return false;
        }
    }

    private void ReleaseMotion(CompanionHandle handle)
    {
        if (!_savedMotion.TryGetValue(handle.Address, out var saved))
        {
            _idleApplied.Remove(handle.Address);
            IdleActive = _idleApplied.Count > 0;
            return;
        }

        try
        {
            var character = handle.Character;
            character->SetMode(saved.Mode, saved.ModeParam);
            character->Timeline.BaseOverride = saved.BaseOverride;

            if (saved.BaseOverride != 0)
                character->Timeline.TimelineSequencer.PlayTimeline(saved.BaseOverride);
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "[MinionHumanizer] 恢复待机失败。");
        }

        _savedMotion.Remove(handle.Address);
        _lastPosition.Remove(handle.Address);
        _idleApplied.Remove(handle.Address);
        IdleActive = _idleApplied.Count > 0;
    }

    private void ReleaseAllMotion()
    {
        foreach (var handle in _service.EnumerateApplied().ToList())
            ReleaseMotion(handle);

        _savedMotion.Clear();
        _lastPosition.Clear();
        _expectedDrawY.Clear();
        _idleApplied.Clear();
        _idleState.Clear();
        IdleActive = false;
    }

    /// <summary>立刻解除待机（UI 的「停止待机」按钮）。</summary>
    public void StopIdle()
    {
        ReleaseAllMotion();
        LastStatus = "已解除待机。";
    }

    /// <summary>宠物从跟踪表里消失（被收起 / 换区重建）时清掉它的内部状态。</summary>
    public void Forget(nint address)
    {
        // ⚠️ 同上：丢弃基准/存档前必须先把内存里的值还原，
        // 否则残留值会被下一轮当成"原始基准"，误差逐次累积（宠物越沉越深）。
        RestoreDrawOffset(VisualHandle(address));
        RestoreActorScale(VisualHandle(address));

        _savedMotion.Remove(address);
        _lastPosition.Remove(address);
        _savedDrawOffset.Remove(address);
            _visualOffsetY.Remove(address);
        _scaleBaseline.Remove(address);
        _idleApplied.Remove(address);
        _idleState.Remove(address);
        IdleActive = _idleApplied.Count > 0;
    }

    // ────────────────────────────────────────────────────────────────
    //  动作 / 表情
    // ────────────────────────────────────────────────────────────────

    /// <summary>
    /// 在宠物身上播放一个表情/动作（ActionTimeline 行号）。
    /// 播放期间宠物进入 AnimLock，AI 不会打断它；<see cref="StopEmote"/> 恢复。
    /// </summary>
    public bool PlayEmote(ushort timelineId, string name) => PlayEmote(timelineId, name, TargetKind.Minion);

    /// <summary>在指定类别的目标上播放表情（默认宠物）。</summary>
    public bool PlayEmote(ushort timelineId, string name, TargetKind kind)
    {
        _service.CopyAppliedTo(_appliedBuffer);
        var handle = default(CompanionHandle);
        foreach (var candidate in _appliedBuffer)
        {
            if (!candidate.Kind.AllowAnimationControl())
                continue;

            // 指定了类别就精确匹配；未指定时优先宠物，其次陆行鸟。
            if (kind != TargetKind.Any && candidate.Kind != kind)
                continue;

            handle = candidate;
            break;
        }

        if (handle.IsNull)
        {
            LastStatus = kind == TargetKind.Minion
                ? "没有已拟人化的宠物可操作（坐骑不参与表情控制）。"
                : $"没有已拟人化的{kind.DisplayName()}——请先在「{kind.DisplayName()}」页启用。";
            return false;
        }

        try
        {
            var character = handle.Character;

            // 第一次播放时存档原始模式，停止时恢复。
            if (!_savedMotion.TryGetValue(handle.Address, out var saved))
            {
                saved = new SavedMotion
                {
                    Mode         = character->Mode,
                    ModeParam    = character->ModeParam,
                    BaseOverride = character->Timeline.BaseOverride,
                };
                _savedMotion[handle.Address] = saved;
            }

            _idleApplied.Remove(handle.Address);

            character->SetMode(CharacterModes.AnimLock, 0);
            character->Timeline.BaseOverride = timelineId;
            character->Timeline.TimelineSequencer.PlayTimeline(timelineId);

            CurrentEmote        = timelineId;
            CurrentEmoteName    = name;
            CurrentEmoteKind    = handle.Kind;
            LastStatus          = $"{handle.Kind.DisplayName()} 正在播放「{name}」(#{timelineId})。";
            return true;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "[MinionHumanizer] 播放动作失败。");
            LastStatus = $"播放失败：{ex.Message}";
            return false;
        }
    }

    /// <summary>停止表情并恢复宠物的原始模式（待机若开着会自动接回来）。</summary>
    public bool StopEmote()
    {
        CurrentEmote     = 0;
        CurrentEmoteName = string.Empty;

        _service.CopyAppliedTo(_appliedBuffer);
        foreach (var handle in _appliedBuffer)
        {
            if (handle.Kind.AllowAnimationControl())
                ReleaseMotion(handle);
        }

        CurrentEmoteKind = TargetKind.Any;

        LastStatus = "已停止表情。";
        return true;
    }

    /// <summary>重绘后动画被打断：如果用户还选着表情就重播；否则把待机重新套上。</summary>
    public void ReplayIfActive()
    {
        if (CurrentEmote != 0)
        {
            var id   = CurrentEmote;
            var name = CurrentEmoteName;
            CurrentEmote = 0;
            PlayEmote(id, name);
            return;
        }

        // 没有表情在播：把待机重新套上（重绘会把 BaseOverride 冲掉）。
        if (_configuration.IdleMode != IdleMode.Off && _configuration.IdleTimelineId != 0)
        {
            foreach (var handle in _service.EnumerateApplied().ToList())
            {
                if (handle.Kind.AllowAnimationControl())
                    ApplyIdle(handle);
            }
        }
    }

    /// <summary>调整播放速度（0 = 暂停）。作用在动画容器上，随时可改。</summary>
    public bool SetSpeed(float speed)
    {
        var any = false;
        foreach (var handle in _service.EnumerateApplied())
        {
            if (!handle.Kind.AllowAnimationControl())
                continue;

            try
            {
                handle.Character->Timeline.OverallSpeed = speed;
                any = true;
            }
            catch (Exception ex)
            {
                _log.Debug(ex, "[MinionHumanizer] 调速异常。");
            }
        }

        LastStatus = any ? $"动作速度 = {speed:0.00}。" : "没有已拟人化的宠物。";
        return any;
    }

    /// <summary>读本地玩家当前的 base 时间轴（用来抓"我自己这个种族体型的待机动作"）。</summary>
    public static ushort CapturePlayerBaseTimeline()
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player is null || player.Address == nint.Zero)
            return 0;

        var timelines = ((Character*)player.Address)->Timeline.TimelineSequencer.TimelineIds;
        return timelines.Length > 0 ? timelines[0] : (ushort)0;
    }

    public void Dispose()
    {
        try
        {
            StopEmote();
            SetSpeed(1f);
            ReleaseAllMotion();

            // ⚠️ 卸载时必须把所有视觉修正<b>同步写回原值</b>。
            // 卸载后不再有下一帧 Tick，跑在Tick 里的纠正逻辑全部失效——
            // 位置/缩放/待机若停在偏移后的状态，宠物就一直保持那个样子。
            // 所以这里不走"每帧纠正"，而是直接落地还原。
            ReleaseAllOffsets();
            RestoreAllActorScales();
            RestoreDrawOffsets();
            ReleaseAllMotion();

            _scaleBaseline.Clear();
            _savedDrawOffset.Clear();
        _visualOffsetY.Clear();
            _expectedDrawY.Clear();
            _idleState.Clear();
            _idleApplied.Clear();
            _savedMotion.Clear();
            _lastPosition.Clear();
        }
        catch
        {
            // 卸载路径：尽力而为，不抛。
        }
    }
}