using System;
using System.Text;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using DalamudObjectKind = Dalamud.Game.ClientState.Objects.Enums.ObjectKind;

namespace MinionHumanizer.Core;

/// <summary>
/// 目标类别。决定用哪一套校验规则——三者的内存布局与归属判据完全不同。
/// </summary>
public enum TargetKind
{
    /// <summary>"不指定"——用于"优先宠物，其次陆行鸟"这类自动选择的场合。不是一种真实目标。</summary>
    Any = -1,

    /// <summary>宠物（minion）：ObjectKind.Companion + CompanionType.Companion，归属看 Owner。</summary>
    Minion,

    /// <summary>
    /// 跟宠坐骑（CompanionType.FollowMount）：也是 Companion 结构，但它本质是坐骑。
    /// 拟人化随开关，但<b>视觉修正与动作控制一律不碰</b>——压到地下或缩小的坐骑只会碍事。
    /// </summary>
    FollowMount,

    /// <summary>
    /// 陆行鸟搭档：跟着你走的陆行鸟。它在对象表里是 <b>BattleNpc</b>（NPC 类 actor），
    /// 不是 Companion，内存布局完全不同，<b>OwnerId 里也不是主人的 ContentId</b>。
    /// 所以它没有可硬校验的归属信号，只能"特征匹配 + 距离"再叠加跟随判据。
    /// </summary>
    ChocoboPartner,

    /// <summary>
    /// 召唤师的宝石兽（红/翠/黄）。对象表里是 <b>BattleNpc</b>，
    /// 没有 <c>Companion-&gt;Owner</c>，但 <b>OwnerId 就是主人的 ContentId</b>，归属可硬校验。
    /// </summary>
    Carbuncle,

    /// <summary>
    /// 学者的小仙女。与宝石兽同属"玩家召唤出来的 NPC"，归属校验方式相同。
    /// </summary>
    Fairy,
}

public static class TargetKindExtensions
{
    /// <summary>
    /// 是否允许被「贴地补偿 / 体型缩放」碰。
    ///
    /// 宠物与陆行鸟搭档都允许（各用各的系数）；跟宠坐骑不允许。
    ///
    /// ⚠️ 真正"你正骑着的那只坐骑"由另一道闸门挡：<see cref="CompanionFinder.Enumerate"/>
    /// 里比对 <c>LocalPlayer.CurrentMount.Address</c>，命中即整条跳过；
    /// 骑乘中更是直接停掉整个 NPC 分支。所以这里不必再靠类别区分。
    /// </summary>
    public static bool AllowVisualPuppetry(this TargetKind kind)
        => kind is TargetKind.Minion or TargetKind.ChocoboPartner or TargetKind.Carbuncle or TargetKind.Fairy;

    /// <summary>
    /// 是否允许被「表情 / 待机动作 / 播放速度」控制。
    ///
    /// 宠物与陆行鸟搭档都可以——表情和待机只是"摆造型"，不会碍事，
    /// 而且陆行鸟搭档能拥有与宠物<b>不同</b>的待机。
    /// 跟宠坐骑仍然排除：它本质是坐骑，锁住动画会影响骑乘。
    /// </summary>
    public static bool AllowAnimationControl(this TargetKind kind)
        => kind is TargetKind.Minion or TargetKind.ChocoboPartner or TargetKind.Carbuncle or TargetKind.Fairy;

    public static string DisplayName(this TargetKind kind)
        => kind switch
        {
            TargetKind.Minion         => "宠物",
            TargetKind.FollowMount    => "跟宠坐骑",
            TargetKind.ChocoboPartner => "陆行鸟搭档",
            TargetKind.Carbuncle   => "宝石兽",
            TargetKind.Fairy       => "小仙女",
            _                          => "未知",
        };
}

/// <summary>
/// 一个"可以动手"的随从句柄。只读包装，不持有任何托管/原生引用，
/// 因此它不可能变成野指针——每次使用前都会重新校验。
///
/// ⚠️ 只有 <see cref="TargetKind.Minion"/> / <see cref="TargetKind.FollowMount"/>
/// 才能碰 <see cref="Companion"/> 指针：陆行鸟搭档是 BattleNpc，内存布局完全不同，
/// 读Companion 字段等于读垃圾（虽然不会崩，但判据毫无意义）。
/// </summary>
public readonly unsafe struct CompanionHandle
{
    public readonly nint Address;
    public readonly ushort ObjectIndex;
    public readonly string Name;
    public readonly TargetKind Kind;

    public CompanionHandle(nint address, ushort objectIndex, string name, TargetKind kind = TargetKind.Minion)
    {
        Address     = address;
        ObjectIndex = objectIndex;
        Name        = name;
        Kind        = kind;
    }

    public bool IsNull => Address == nint.Zero;

    public GameObject* GameObject => (GameObject*)Address;
    public Character*  Character  => (Character*)Address;

    /// <summary>仅在 Companion 类目标（宠物 / 跟宠坐骑）下有效。</summary>
    public Companion* Companion => (Companion*)Address;
}

/// <summary>
/// 找出"可以动手"的随从。
///
/// 这里是最关键的安全边界：
///  · <b>宠物</b>：ObjectKind.Companion 且 Owner 指向本地玩家（别人的宠物一律不碰）；
///  · <b>跟宠坐骑</b>：同上，但归属另作标记，视觉修正会排除它；
///  · <b>召唤物</b>（宝石兽 / 小仙女）：也是 NPC 类 actor（BattleNpc），没有 <c>Companion-&gt;Owner</c>，
///    但 <b><c>IGameObject.OwnerId</c> 存的就是主人的 ContentId</b> —— 归属可以<b>硬校验</b>，
///    不依赖距离，也<b>绝不会碰到别人的召唤物</b>；
///  · <b>陆行鸟搭档</b>：同样是 BattleNpc，但 <b>OwnerId 里不是主人 ContentId</b>，
///    没有可硬校验的归属信号 → 只能用"名字/数据 ID + 距离"做特征匹配。
///    ⚠️ 多人场景下会碰到别人的陆行鸟，这是已知且当前接受的取舍。默认关闭。
/// </summary>
public sealed class CompanionFinder(IObjectTable objectTable, IPlayerState playerState)
{
    private readonly IObjectTable _objectTable  = objectTable;
    private readonly IPlayerState  _playerState  = playerState;

    public nint LocalPlayerAddress => _objectTable.LocalPlayer?.Address ?? nint.Zero;

    /// <summary>
    /// 指针判断放在这个独立的 unsafe 方法里。
    ///
    /// 原因是一个 C# 的硬性限制：迭代器方法（含 yield）的方法体会被改写成状态机类型，
    /// 而该状态机<b>不继承</b>外层类型或方法的 unsafe 上下文，所以在迭代器体里
    /// 直接解指针会得到 CS0214（即使类和方法都标了 unsafe）。
    /// 把指针逻辑隔离到普通方法中，编译与可读性都更干净。
    /// </summary>
    /// <summary>
    /// 由 HumanizerService 注入：查询某个地址当前是否已被我们跟踪、且 EntityId 一致，
    /// 并返回<b>跟踪时确定的类别</b>。没跟踪则返回 null。
    ///
    /// 为什么要它（关键设计）：宠物分类依赖 <c>Companion-&gt;Owner</c> 与
    /// <c>Companion-&gt;CompanionType</c> 这两个字段，而它们描述的是"原始随从状态"——
    /// 一旦我们把 actor 改成人类模型，这两个字段就<b>不再可靠</b>（重建期间可能读不到）。
    /// 于是宠物会每帧被判成"陌生随从/类型不符"踢出候选列表 → 原样快照被连带删除 →
    /// 重新施加时用被污染的快照 → 宠物变回原形。
    ///
    /// <b>陆行鸟搭档没有这个问题</b>，因为它走 BattleNpc 分支，只按名字/ID + 距离识别，
    /// 不读任何"原随从类型"字段。
    ///
    /// 所以这里采用同样的思路：<b>一旦跟踪成功就信任它</b>，类别直接用跟踪时缓存的值，
    /// 每帧不再重读那些会失效的字段。
    /// </summary>
    internal static Func<nint, TargetKind?>? TrackedKindHook { get; set; }

    private static TargetKind? trackedKindOf(nint address)
        => TrackedKindHook is not null ? TrackedKindHook(address) : null;

    private static unsafe TargetKind ClassifyCompanion(nint address, nint playerAddress, bool includeFollowMounts)
    {
        var companion = (Companion*)address;

        // 归属校验：只认"我召唤出来的"。别人的随从一律不碰。
        //
        // ⚠️ 拟人化后有一个反直觉的陷阱：宠物被改成人类模型并重建绘制对象的那几帧里，
        // `Companion->Owner` 可能暂时读不到本地玩家（对象正在重建，Companion 子结构
        // 尚未完全填好）。若此时判定"不是我的"，它会被踢出候选列表 →
        // 巡检末尾的清理会连同<b>原样快照</b>一起删掉 → 下次施加时快照已是人形，
        // 还原/revert 就会把人形当成"原样"，宠物再也变不回原形。
        //
        // 所以只要"正在被跟踪的对象的 Owner 匹配失败"就不踢（由 HumanizerService 负责
        // 用 EntityId 二次确认它确实是同一只）。这里只拦真正的陌生人。
        if ((nint)companion->Owner != playerAddress)
            return TargetKind.Any;

        if (companion->CompanionType == CompanionType.Companion)
            return TargetKind.Minion;

        if (companion->CompanionType == CompanionType.FollowMount)
            return includeFollowMounts ? TargetKind.FollowMount : (TargetKind)(-1);

        return TargetKind.Any;
    }

    /// <summary>
    /// 枚举所有候选目标。宠物走老路；陆行鸟搭档只在用户开启并指定了名字/ID 时才进来。
    /// </summary>
    public IEnumerable<CompanionHandle> Enumerate(Configuration configuration)
    {
        var player = _objectTable.LocalPlayer;
        var playerAddress = player?.Address ?? nint.Zero;
        if (playerAddress == nint.Zero)
            yield break;

        // 召唤物的归属靠它比对。
        // ⚠️ `IGameObject.OwnerId` 返回的是**主人的 GameObjectId**（实测确认，不是 ContentId）。
        // ContentId 一并取出来，只用于诊断显示。
        var playerGameObjectId = player.GameObjectId;
        var myContentId        = _playerState.ContentId;

        var chocoboSettings = configuration.SettingsFor(TargetKind.ChocoboPartner) as ChocoboSettings;

        // ⚠️ 铁律：骑乘中一律不碰任何 NPC 类目标。
        // 坐骑在对象表里也是 BattleNpc，而且它必然紧贴着你（距离恒为 0~1 yalm），
        // 所以任何"名字/ID 匹配 + 距离内"的 NPC 规则都会把它一起捞进来——
        // 这正是之前"视觉修正莫名其妙作用到坐骑上"的真凶。
        // `CurrentMount` 是 `RowRef<Mount>`（坐骑表的行引用，不是 actor 指针），
        // 拿不到它的地址，所以这里直接用"是否骑乘中"来一刀切：<b>骑乘时整个枚举停摆</b>。
        var isRiding = player?.CurrentMount is { IsValid: true };

        foreach (var obj in _objectTable)
        {
            if (obj is null)
                continue;

            var address = obj.Address;
            if (address == nint.Zero || obj.ObjectIndex == 0)
                continue;

            // ──⚠️ 骑乘中一律不碰任何目标（不只是 NPC）──
            //
            // 上下坐骑时宠物进入「被安置在玩家身上」的状态：
            //   · 游戏不绘制它 → 完整重建永远等不到 IsReadyToDraw → 必然超时回滚；
            //   · 游戏会自己把模型整理回随从形态，并重建绘制对象。
            // 此时对宠物做任何事（就地刷新外观也会）都等于和游戏抢这个对象，
            // 结果就是「上下坐骑后宠物变回原形」。
            //
            // 之前只对 NPC 类目标一刀切，宠物被漏掉了 —— 现在整个枚举在骑乘期间停摆。
            // 落骑瞬间由 HumanizerService 强制巡检并清空退避，确保立刻恢复。
            if (isRiding)
                continue;

            // ── 宠物 / 跟宠坐骑 ──
            if (obj.ObjectKind == DalamudObjectKind.Companion)
            {
                // ① 已跟踪 → 直接用缓存的类别，完全不读 Owner/CompanionType
                //    （这两个字段在拟人化后不再可靠；陆行鸟分支正因为不依赖它们才一直正常）。
                var cached = trackedKindOf(address);
                if (cached is { } knownKind && knownKind != TargetKind.Any)
                {
                    yield return new CompanionHandle(address, obj.ObjectIndex, obj.Name.TextValue, knownKind);
                    continue;
                }

                // ② 未跟踪 → 走完整判定（含归属校验）。
                var kind = ClassifyCompanion(address, playerAddress, configuration.IncludeFollowMounts);
                if (kind != TargetKind.Any)
                    yield return new CompanionHandle(address, obj.ObjectIndex, obj.Name.TextValue, kind);

                continue;
            }

            // ── 召唤物（宝石兽 / 小仙女）──
            //
            // ⚠️ 它们虽然是 NPC 类 actor，但 `IGameObject.OwnerId` 存的是**主人的 ContentId**，
            // 所以归属可以**硬校验**——不像陆行鸟那样只能靠"名字 + 距离"猜，
            // 也因此**绝对不会碰到别人的召唤物**。
            // 名字关键字只用来区分"是宝石兽还是小仙女"，不参与归属判断。
            if (obj.ObjectKind is not (DalamudObjectKind.BattleNpc or DalamudObjectKind.EventNpc))
                continue;

            if (obj.IsDead)
                continue;

            // ── 召唤物：OwnerId 硬校验归属，名字关键字只用来分类 ──
            //
            // ⚠️ 实测（诊断文件 last-diag.txt）：`IGameObject.OwnerId` 对召唤物返回的是
            // **主人的 GameObjectId**，**不是 ContentId**。
            //   宝石兽 OwnerId=268884887 ＝ 本地玩家 GameObjectId=268884887
            // 之前拿 ContentId 比对，恒不相等 → 召唤物永远被跳过（症状：完全没反应）。
            var summonKind = ClassifySummon(obj.Name.TextValue, configuration);
            if (summonKind != TargetKind.Any)
            {
                if (playerGameObjectId != 0 && obj.OwnerId == playerGameObjectId)
                    yield return new CompanionHandle(address, obj.ObjectIndex, obj.Name.TextValue, summonKind);

                continue;
            }

            // ── 陆行鸟搭档：没有硬归属信号，靠特征匹配 + 跟随分级 ──
            if (chocoboSettings is null || !chocoboSettings.Enabled)
                continue;

            if (DistanceToPlayer(obj) > chocoboSettings.MaxDistance)
                continue;

            if (!MatchesChocobo(obj, chocoboSettings))
                continue;

            yield return new CompanionHandle(address, obj.ObjectIndex, obj.Name.TextValue, TargetKind.ChocoboPartner);
        }
    }

    /// <summary>名字关键字或数据 ID 命中。</summary>
    private static bool MatchesChocobo(Dalamud.Game.ClientState.Objects.Types.IGameObject obj, ChocoboSettings settings)
    {
        if (settings.TargetDataId != 0 && obj.BaseId == settings.TargetDataId)
            return true;

        return settings.Matches(obj.Name.TextValue);
    }

    private float DistanceToPlayer(Dalamud.Game.ClientState.Objects.Types.IGameObject obj)
    {
        var player = _objectTable.LocalPlayer;
        if (player is null)
            return float.MaxValue;

        var a = obj.Position;
        var b = player.Position;
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        var dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    /// <summary>
    /// 生成一份「识别诊断」：把身边每个 NPC 类 actor 逐条列出，
    /// 并标出它**卡在哪一步**（类型 / 已启用 / 关键字 / 距离 / 归属）。
    ///
    /// 用途：召唤物没生效时，能一眼看出是名字没匹配、开关没开、还是 <c>OwnerId</c> 对不上。
    /// </summary>
    public string BuildRecognitionReport(Configuration configuration)
    {
        var sb = new StringBuilder();

        var player       = _objectTable.LocalPlayer;
        var myContentId  = _playerState.ContentId;
        var playerGameObjectId = player?.GameObjectId ?? 0;
        var chocobo      = configuration.SettingsFor(TargetKind.ChocoboPartner) as ChocoboSettings;
        var carbuncle    = configuration.SettingsFor(TargetKind.Carbuncle);
        var fairy        = configuration.SettingsFor(TargetKind.Fairy);

        sb.AppendLine($"本地玩家：{(player is null ? "（无）" : $"GameObjectId={player.GameObjectId}")}");
        sb.AppendLine($"我的 ContentId：{myContentId}（仅供参照，归属比对用的是 GameObjectId）");
        sb.AppendLine($"陆行鸟：启用={chocobo?.Enabled} 关键字=[{(chocobo is null ? "" : string.Join("|", chocobo.Keywords))}] 数据ID={chocobo?.TargetDataId} 距离≤{chocobo?.MaxDistance:0.#}");
        sb.AppendLine($"宝石兽：启用={carbuncle?.Enabled} 关键字=[{(carbuncle is null ? "" : string.Join("|", carbuncle.Keywords))}]");
        sb.AppendLine($"小仙女：启用={fairy?.Enabled} 关键字=[{(fairy is null ? "" : string.Join("|", fairy.Keywords))}]");
        sb.AppendLine();
        sb.AppendLine("—— 身边的 NPC 类 actor ——");

        if (player is null)
        {
            sb.AppendLine("（本地玩家尚未加载）");
            return sb.ToString();
        }

        var playerPos = player.Position;
        var count = 0;

        foreach (var obj in _objectTable)
        {
            if (obj is null || obj.Address == nint.Zero)
                continue;

            if (obj.ObjectKind is not (Dalamud.Game.ClientState.Objects.Enums.ObjectKind.BattleNpc
                                     or Dalamud.Game.ClientState.Objects.Enums.ObjectKind.EventNpc))
                continue;

            var a = obj.Position;
            var dx = a.X - playerPos.X;
            var dy = a.Y - playerPos.Y;
            var dz = a.Z - playerPos.Z;
            var distance = MathF.Sqrt(dx * dx + dy * dy + dz * dz);

            var hitCarb = carbuncle is { Enabled: true } && carbuncle.Matches(obj.Name.TextValue);
            var hitFairy = fairy is { Enabled: true } && fairy.Matches(obj.Name.TextValue);
            var hitBird  = chocobo is { Enabled: true }
                        && (chocobo.TargetDataId != 0 && obj.BaseId == chocobo.TargetDataId || chocobo.Matches(obj.Name.TextValue));
            // ⚠️ 归属比对的基准是 GameObjectId，不是 ContentId（实测：OwnerId 就是它）。
            var ownerOk  = playerGameObjectId != 0 && obj.OwnerId == playerGameObjectId;

            // 只把"有希望"的挑出来，省得刷屏。
            if (!hitCarb && !hitFairy && !hitBird && distance > 25f)
                continue;

            var verdict = hitCarb ? (ownerOk ? "★宝石兽·会处理" : "宝石兽名但 OwnerId 不是你 → 跳过")
                          : hitFairy ? (ownerOk ? "★小仙女·会处理" : "小仙女名但 OwnerId 不是你 → 跳过")
                          : hitBird ? (distance <= chocobo!.MaxDistance ? "★陆行鸟·会处理" : "陆行鸟名但太远 → 跳过")
                          : "（未匹配任何关键字）";

            sb.AppendLine($"· {obj.Name.TextValue}｜{obj.ObjectKind}｜ID={obj.BaseId}｜{distance:0.0} yalm"
                        + $"｜OwnerId={obj.OwnerId}｜跟随={obj.TargetObjectId}"
                        + (hitCarb ? "｜[宝石兽关键字]" : hitFairy ? "｜[小仙女关键字]" : hitBird ? "｜[陆行鸟关键字]" : "")
                        + $"｜{verdict}");

            if (++count >= 25)
                break;
        }

        if (count == 0)
            sb.AppendLine("（身边 25 yalm 内没有匹配的 NPC 类 actor）");

        return sb.ToString();
    }

    /// <summary>按名字关键字判断这是哪一类召唤物（只分类，不判归属）。</summary>
    private static TargetKind ClassifySummon(string objectName, Configuration configuration)
    {
        if (configuration.Carbuncle is { Enabled: true } carbuncle
            && carbuncle.Matches(objectName)
            && carbuncle.AllowsName(objectName))
            return TargetKind.Carbuncle;

        if (configuration.Fairy is { Enabled: true } fairy
            && fairy.Matches(objectName)
            && fairy.AllowsName(objectName))
            return TargetKind.Fairy;

        return TargetKind.Any;
    }
}