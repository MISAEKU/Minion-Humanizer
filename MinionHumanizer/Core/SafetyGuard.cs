using System;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using CsObjectKind = FFXIVClientStructs.FFXIV.Client.Game.Object.ObjectKind;

namespace MinionHumanizer.Core;

/// <summary>
/// 所有"要不要动手"的判断集中在这里。它不是可选的礼貌检查，
/// 它是防止崩溃和防止污染服务器数据的唯一屏障，任何写入前都必须过这一关。
///
/// 校验按 <see cref="TargetKind"/> 分派：
///   · 宠物 / 跟宠坐骑：走 Companion 结构 + <c>Owner</c> 校验；
///   · 召唤物（宝石兽 / 小仙女）：NPC 类 actor，但 <b>OwnerId 就是主人的 ContentId</b>，
///     用它做归属硬校验——比"名字 + 距离"可靠得多，不会碰到别人的召唤物；
///   · 召唤物之外的陆行鸟搭档：<b>没有</b>这种硬信号，只能特征匹配 + 跟随判据。
/// </summary>
public sealed unsafe class SafetyGuard(IClientState clientState, ICondition condition, IObjectTable objectTable, IPlayerState playerState)
{
    /// <summary>
    /// 由 HumanizerService 注入：查询某地址当前是否<b>已被我们跟踪、且 EntityId 与快照一致</b>。
    ///
    /// 为什么要它：宠物的归属与类型判定依赖 <c>Companion-&gt;Owner</c> 和
    /// <c>Companion-&gt;CompanionType</c>，而这两个字段描述的是"原始随从状态"——
    /// 我们把 actor 改成人类模型之后它们就不再可靠，重建期间甚至读不到。
    /// 于是每一次重施（切设计、点立即施加）都会被自己的校验拦下，表现为"改了没反应"。
    ///
    /// <b>召唤物与陆行鸟搭档都没有这个问题</b>：它们不走 Companion 分支，只看类型 / 死亡状态，
    /// 压根不读那两个字段——这正是"召唤物一切正常、只有宠物不行"的差异所在。
    /// 所以这里采用同样的思路：<b>一旦跟踪成功并用 EntityId 二次确认，就不再重读它们</b>。
    /// 未跟踪的目标仍然走完整校验，陌生随从进不来，安全性不降。
    /// </summary>
    internal Func<nint, bool>? IsVerifiedTracked { get; set; }

    private bool Verified(nint address) => IsVerifiedTracked?.Invoke(address) == true;

    private readonly IClientState _clientState = clientState;
    private readonly ICondition   _condition   = condition;
    private readonly IObjectTable _objectTable = objectTable;
    private readonly IPlayerState _playerState = playerState;

    public readonly record struct Verdict(bool Allowed, string Reason)
    {
        public static Verdict Ok() => new(true, string.Empty);
        public static Verdict No(string reason) => new(false, reason);
    }

    /// <summary>
    /// 结构性检查：这个 actor 到底是不是"我自己的随从"。
    /// 即使调用方传错东西，这里也会挡下来。
    /// </summary>
    public Verdict CheckTarget(CompanionHandle handle, Configuration configuration)
    {
        if (handle.IsNull)
            return Verdict.No("目标是空指针");

        var player = _objectTable.LocalPlayer;
        if (player is null)
            return Verdict.No("本地玩家尚未加载");

        // ── 第一道，也是最重要的一道：绝不触碰玩家自己。──
        // ObjectIndex 0 恒为本地玩家；这一条直接排除了
        // "把改动写进会被客户端序列化上传的字段"的可能。
        if (handle.ObjectIndex == 0)
            return Verdict.No("拒绝：目标是本地玩家（本插件永不触碰玩家角色）");

        var gameObject = handle.GameObject;
        if (gameObject is null)
            return Verdict.No("游戏对象指针为空");

        // 玩家角色：Pc 类型永不触碰（哪怕调用方传错）。
        if (gameObject->ObjectKind == CsObjectKind.Pc)
            return Verdict.No("拒绝：目标是玩家角色");

        return handle.Kind switch
        {
            TargetKind.Carbuncle or TargetKind.Fairy => CheckSummonTarget(handle, configuration),
            TargetKind.ChocoboPartner               => CheckChocoboTarget(handle, configuration),
            _                                        => CheckCompanionTarget(handle, configuration),
        };
    }

    private Verdict CheckCompanionTarget(CompanionHandle handle, Configuration configuration)
    {
        // 类型白名单与配置开关与身份无关，任何时候都要生效。
        if (handle.Kind == TargetKind.FollowMount && !configuration.IncludeFollowMounts)
            return Verdict.No("不是宠物（minion）；如需处理跟宠坐骑请在设置里开启");

        // ── 已被我们跟踪、且 EntityId 二次确认过：身份已定，不再重读易变字段。──
        //
        // Owner / CompanionType 描述的是"原始随从状态"，拟人化之后不再可靠。
        // 每次重施都重读它们，等于用自己的校验把自己的重施拦掉——
        // 这就是"切设计没反应、变回原样"的直接原因。
        // 陆行鸟分支从不读这两个字段，所以一直正常。
        if (Verified(handle.Address))
            return Verdict.Ok();

        var player = _objectTable.LocalPlayer!;

        if (handle.GameObject->ObjectKind != CsObjectKind.Companion)
            return Verdict.No($"拒绝：目标类型是 {handle.GameObject->ObjectKind}，不是 Companion");

        // ── 归属：必须是我召唤出来的。别人的宠物一律不碰。──
        if ((nint)handle.Companion->Owner != player.Address)
            return Verdict.No("拒绝：这只宠物不属于你");

        if (handle.Kind == TargetKind.Minion && handle.Companion->CompanionType != CompanionType.Companion)
            return Verdict.No("类型与记录不符（可能已被换成别的随从）");

        return Verdict.Ok();
    }

    /// <summary>
    /// 召唤物（宝石兽 / 小仙女）：NPC 类 actor，没有 <c>Companion-&gt;Owner</c> 可校验，
    /// 但 <b><c>IGameObject.OwnerId</c> 存的就是主人的 ContentId</b>，所以归属可以硬校验。
    ///
    /// 这一道比"名字 + 距离"可靠得多：<b>别人的召唤物必然过不了</b>，
    /// 不会发生"多人场景下误伤别人"这类事故。
    /// </summary>
    private Verdict CheckSummonTarget(CompanionHandle handle, Configuration configuration)
    {
        var settings = configuration.SettingsFor(handle.Kind);
        if (settings is null || !settings.Enabled)
            return Verdict.No($"未启用{handle.Kind.DisplayName()}拟人化");

        var kind = handle.GameObject->ObjectKind;
        if (kind is not (CsObjectKind.BattleNpc or CsObjectKind.EventNpc))
            return Verdict.No($"拒绝：召唤物必须是 NPC 类 actor，当前是 {kind}");

        var obj = _objectTable[handle.ObjectIndex];
        if (obj is null || obj.Address != handle.Address)
            return Verdict.No("对象已失效");

        if (obj.IsDead)
            return Verdict.No("目标已死亡");

        // 归属硬校验。
        // ⚠️ 实测：`IGameObject.OwnerId` 对召唤物返回的是**主人的 GameObjectId**，不是 ContentId。
        var player = _objectTable.LocalPlayer;
        if (player is null)
            return Verdict.No("本地玩家尚未加载");

        if (player.GameObjectId == 0)
            return Verdict.No("读不到本地玩家的 GameObjectId，无法确认归属");

        if (obj.OwnerId != player.GameObjectId)
            return Verdict.No("拒绝：这个召唤物不属于你");

        return Verdict.Ok();
    }

    /// <summary>
    /// 陆行鸟搭档：NPC 类 actor，<b>OwnerId 里不是主人 ContentId</b>，所以没有硬归属信号。
    /// 这里只做"基本合法"校验（类型 / 存活 / 配置已开），归属交给 finder 的跟随分级处理。
    /// </summary>
    private Verdict CheckChocoboTarget(CompanionHandle handle, Configuration configuration)
    {
        var settings = configuration.SettingsFor(handle.Kind) as ChocoboSettings;
        if (settings is null || !settings.Enabled)
            return Verdict.No("未启用陆行鸟搭档拟人化");

        var kind = handle.GameObject->ObjectKind;
        if (kind is not (CsObjectKind.BattleNpc or CsObjectKind.EventNpc))
            return Verdict.No($"拒绝：陆行鸟搭档必须是 NPC 类 actor，当前是 {kind}");

        var obj = _objectTable[handle.ObjectIndex];
        if (obj is null || obj.Address != handle.Address)
            return Verdict.No("对象已失效");

        if (obj.IsDead)
            return Verdict.No("目标已死亡");

        return Verdict.Ok();
    }

    /// <summary>
    /// 时机检查：现在这个瞬间能不能安全地 DisableDraw / EnableDraw。
    /// 主要目的是躲开"游戏自己正在创建/销毁 actor"的那几帧——在那里重入就是崩。
    /// </summary>
    public Verdict CheckTiming(CompanionHandle handle, Configuration configuration)
    {
        // 角色创建 / 美容师流程：此刻客户端确实会在本地与服务器之间交换外观数据，必须回避。
        if (_condition[ConditionFlag.CreatingCharacter])
            return Verdict.No("角色创建/编辑流程中");

        // 过场动画里 actor 处于特殊状态，重建绘制对象容易出问题。
        if (_condition[ConditionFlag.OccupiedInCutSceneEvent] || _condition[ConditionFlag.WatchingCutscene])
            return Verdict.No("过场动画中");

        // 换区过程中对象表正在被重建，指针随时失效。
        // 注意：IClientState 没有 IsBetweenAreas 这个成员（已用真实程序集核实），
        // 换区状态统一走 ConditionFlag 判断。
        if (_condition[ConditionFlag.BetweenAreas]
         || _condition[ConditionFlag.BetweenAreas51])
            return Verdict.No("换区中");

        var gameObject = handle.GameObject;

        // 必须已经有绘制对象：DisableDraw/EnableDraw 要成对，没有的才有问题。
        if (!ModelWriter.HasDrawObject(gameObject))
            return Verdict.No("目标尚未生成绘制对象");

        if (!ModelWriter.IsReadyToDraw(gameObject))
            return Verdict.No("绘制尚未就绪");

        // 宠物专属：必须处于活动状态，且不是被安置在玩家身上。
        // 召唤物是 NPC，没有这些字段——跳过。
        // ⚠️ 这里<b>不能</b>对已跟踪目标也放宽。
        //
        // 上下坐骑时宠物处于「被安置在玩家身上」的状态，游戏不会绘制它——
        // 此时去跑 DisableDraw → 等IsReadyToDraw → EnableDraw 是<b>永远等不到</b>的，
        // 必然超时回滚，于是宠物就停在原形。
        //
        // 所以：需要<b>完整重建</b>的重施必须避开安置状态（等下坐骑后自动恢复）；
        // 而「就地刷新外观」不碰绘制对象，在安置状态下也安全，由调用方单独放行。
        if (handle.Kind is TargetKind.Minion or TargetKind.FollowMount)
        {
            if (handle.Companion->SpawnState != CompanionSpawnState.Active)
                return Verdict.No($"目标不处于活动状态（{handle.Companion->SpawnState}）");

            if (handle.Companion->BehaviorState == CompanionBehaviorState.Place)
                return Verdict.No("宠物正被安置在玩家身上，稍后再试");
        }

        if (configuration.BypassScope == RestrictionBypassScope.InGPoseOnly && !_clientState.IsGPosing)
            return Verdict.No("当前设置为「仅 GPose 生效」");

        return Verdict.Ok();
    }

    public Verdict CheckAll(CompanionHandle handle, Configuration configuration)
    {
        var target = CheckTarget(handle, configuration);
        return target.Allowed ? CheckTiming(handle, configuration) : target;
    }
}