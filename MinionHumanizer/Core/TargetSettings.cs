using System;
using System.Collections.Generic;

namespace MinionHumanizer.Core;

/// <summary>
/// 一类"可拟人化目标"的配置基类。
///
/// <para>
/// 为什么抽成基类：每类目标（宠物 / 陆行鸟搭档 / 宝石兽 / 小仙女）需要的东西几乎一样
/// ——启用、设计、待机、视觉修正、名字关键字。<b>差异只在"怎么确认归属"</b>，
/// 而那部分放在各自的识别逻辑里（见 <see cref="CompanionFinder"/> 与 <see cref="SafetyGuard"/>）。
/// </para>
///
/// <para>
/// ⚠️ <b>per-kind 的值只能通过 <c>Configuration</c> 的 6 个 Get*For(kind) 入口拿</b>，
/// 其它模块不要直接读字段。这样新增一类目标 = 加一个实例 + 入口加一个分支 + UI 加一个页面。
/// </para>
/// </summary>
[Serializable]
public abstract class TargetSettings
{
    /// <summary>是否拟人化这一类。</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// 名字关键字（逗号分隔，任一命中即算这一类）。
    /// 默认已含中英文，改客户端语言也不用动。
    /// </summary>
    public string NameKeywordList { get; set; } = string.Empty;

    // ── 外观 ──────────────────────────────────────────────

    /// <summary>是否用<b>自己的</b> Glamourer 设计（关闭时跟随宠物当前选中的设计）。</summary>
    public bool UseOwnDesign { get; set; }

    public Guid DesignId { get; set; }

    public string DesignName { get; set; } = string.Empty;

    // ── 待机动作 ──────────────────────────────────────────

    /// <summary>是否用<b>自己的</b>待机动作（关闭时跟随宠物的待机设置）。</summary>
    public bool UseOwnIdle { get; set; }

    public IdleMode IdleMode { get; set; } = IdleMode.Off;

    public int IdleTimelineId { get; set; }

    // ── 视觉修正 ──────────────────────────────────────────

    /// <summary>是否做视觉修正（贴地 / 缩放）。</summary>
    public bool UseVisual { get; set; }

    public float GroundOffsetY { get; set; }

    public float VisualScale { get; set; } = 1f;

    /// <summary>解析后的关键字列表（每次访问重新解析，改配置后立即生效）。</summary>
    public List<string> Keywords
    {
        get
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(NameKeywordList))
                return result;

            foreach (var part in NameKeywordList.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                result.Add(part);

            return result;
        }
    }

    /// <summary>名字是否命中任一关键字。</summary>
    public bool Matches(string objectName)
    {
        foreach (var keyword in Keywords)
        {
            if (objectName.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>老配置升级 / 补默认值。</summary>
    public virtual void Sanitize()
    {
        DesignName      ??= string.Empty;
        NameKeywordList ??= string.Empty;

        if (IdleTimelineId is < 0 or > ushort.MaxValue)
            IdleTimelineId = 0;

        if (VisualScale <= 0f)
            VisualScale = 1f;
    }

    /// <summary>仅在用户没填过关键字时填入默认值（不要覆盖用户输入）。</summary>
    protected void FillKeywords(params string[] defaults)
    {
        if (string.IsNullOrWhiteSpace(NameKeywordList))
            NameKeywordList = string.Join(",", defaults);
    }
}

/// <summary>
/// 召唤物（宝石兽 / 小仙女）。
///
/// <para><b>归属可以硬校验</b>：它们在对象表里是 BattleNpc，没有 <c>Companion-&gt;Owner</c>，
/// 但 <b>IGameObject.OwnerId 存的就是主人的 ContentId</b>（见 <c>IPlayerState.ContentId</c>）。
/// 所以名字关键字只用来区分种类，归属靠 ContentId 比对——不会碰到别人的召唤物。</para>
/// </summary>
[Serializable]
public sealed class SummonSettings : TargetSettings
{
    /// <summary>
    /// 是否<b>只处理本体</b>：名字必须<b>完全等于</b> <see cref="BaseNames"/> 里的某一个才算目标。
    ///
    /// <para>
    /// 默认<b>开</b>，用来挡住<b>换色变种</b>。实测记录：
    /// <code>
    ///   宝石兽     ModelCharaId=411  ← 本体，能变人且套 mod 不崩
    ///   绿宝石兽   ModelCharaId=409  ← 换色变种，会崩
    ///   朝日小仙女 ModelCharaId=407  ← 本体
    /// </code>
    /// 关键字是"包含"匹配，所以「绿宝石兽」也会命中「宝石兽」——
    /// 必须靠<b>精确等于</b>才能把变种摘出去。
    /// </para>
    ///
    /// <para>
    /// 关掉它就恢复"包含即命中"，所有变种都会尝试拟人化（换色有崩溃风险）。
    /// </para>
    /// </summary>
    public bool OnlyExactName { get; set; } = true;

    /// <summary>
    /// 这一类召唤物是否<b>使用 Glamourer 设计</b>（默认开）。
    ///
    /// <para>
    /// 之前只有<b>全局</b>一个 <c>Configuration.UseGlamourerDesign</c>，UI 每一页显示的都是同一个开关
    /// —— 在召唤物页关掉它会连宠物一起关掉。召唤物改为各用各的。
    /// </para>
    /// </summary>
    public bool UseGlamourer { get; set; } = true;

    /// <summary>本体名字，逗号分隔。<see cref="OnlyExactName"/> 开启时只有精确命中才算目标。</summary>
    public string BaseNames { get; set; } = string.Empty;

    /// <summary>额外屏蔽的名字，逗号分隔（不分精确/包含）。</summary>
    public string BlockedNames { get; set; } = string.Empty;

    /// <summary>
    /// 召唤物"刚被召唤出来"的保护期（帧，60 帧 ≈ 1 秒）。<b>默认 0</b>（不等待）。
    ///
    /// <para>现场可调的实验旋钮："等久一点能避开崩溃"这个假设<b>已被实测否证</b>
    /// （陆行鸟搭档同样是刚生成的 BattleNpc、同样立刻重建，却完全没事）。</para>
    /// </summary>
    public int SettleFrames { get; set; }

    /// <summary>宝石兽默认关键字：英文名里也带 Carbuncle。</summary>
    public static SummonSettings CarbuncleDefault()
    {
        var s = new SummonSettings();
        s.FillKeywords("宝石兽", "Carbuncle");
        // 召唤物是<b>站在地上</b>的：目标下沉 0 就等于"脚踩在 actor 所在的位置"。
        // 飞行宠物才需要负值来抵消悬浮（那是宠物页的事，两边互不影响）。
        //
        // 移动时游戏额外叠加的垂直抬升由<b>闭环</b>自动抵消，不需要预置一个负值。
        s.GroundOffsetY = 0f;
        s.BaseNames = "宝石兽";
        return s;
    }

    /// <summary>小仙女默认关键字。英文客户端叫 Fairy。</summary>
    public static SummonSettings FairyDefault()
    {
        var s = new SummonSettings();
        s.FillKeywords("仙女", "仙子", "Fairy");
        s.GroundOffsetY = 0f;
        s.BaseNames = "朝日小仙女";
        return s;
    }

    /// <summary>把逗号分隔的字符串拆成非空片段（供 BaseNames / BlockedNames 复用）。</summary>
    internal static List<string> SplitList(string? value)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(value))
            return result;

        foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            result.Add(part);

        return result;
    }

    /// <summary>这个具体目标该不该被放行（名字已确认命中关键字）。</summary>
    public bool AllowsName(string objectName)
    {
        foreach (var blocked in SplitList(BlockedNames))
        {
            if (objectName.Contains(blocked, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        if (!OnlyExactName)
            return true;

        foreach (var name in SplitList(BaseNames))
        {
            if (objectName.Equals(name, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}

/// <summary>
/// 陆行鸟搭档。
///
/// <para>
/// ⚠️ 它<b>没有</b>可硬校验的归属字段：既是 BattleNpc，<c>OwnerId</c> 里也不是主人 ContentId。
/// 所以只能用"特征匹配（名字 / 数据 ID）+ 距离"。多人场景下这会碰到别人的陆行鸟——
/// 这是<b>已知且当前接受的取舍</b>（用户明确要求不做归属校验）。
/// </para>
/// </summary>
[Serializable]
public sealed class ChocoboSettings : TargetSettings
{
    /// <summary>目标数据 ID（BattleNpc 的 DataId，即 NPC 行号）。0 = 不按 ID 匹配。</summary>
    public uint TargetDataId { get; set; }

    /// <summary>允许处理的最大距离（yalm）。搭档必须紧跟着你，超出就不碰。</summary>
    public float MaxDistance { get; set; } = 12f;

    public static ChocoboSettings Default()
    {
        var s = new ChocoboSettings();
        s.FillKeywords("陆行鸟", "Chocobo");
        // 陆行鸟搭档也要做视觉修正（变成人形后同样需要贴地 / 缩放），
        // 默认开启——它本来就该有<b>属于自己</b>的一栏，而不是被误判成宠物借宠物的字段。
        s.UseVisual = true;
        return s;
    }

    public override void Sanitize()
    {
        base.Sanitize();

        if (MaxDistance is < 3f or > 60f)
            MaxDistance = 12f;
    }
}
