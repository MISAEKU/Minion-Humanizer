using System;
using System.Collections.Generic;
using Dalamud.Configuration;

namespace MinionHumanizer;

/// <summary>让游戏的 EnforceKindRestrictions 检查在什么范围内被短路。</summary>
public enum RestrictionBypassScope
{
    /// <summary>不短路。最保守，但人类模型可能被游戏"修回去"造成闪烁/抖动。</summary>
    Off,

    /// <summary>只要本插件的拟人化处于开启状态就短路。默认值，对应 Brio 的 Always。</summary>
    WhileActive,

    /// <summary>只在 GPose 里短路。想全程跟随就选 WhileActive。</summary>
    InGPoseOnly,
}

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 3;

    /// <summary>拟人化总开关。关掉会立刻把宠物还原。</summary>
    public bool Enabled { get; set; }

    // ───────────────────────── 外观来源 ─────────────────────────

    /// <summary>人形身体（种族/性别/部族/脸/身高…）的来源。</summary>
    public CustomizeSource CustomizeSource { get; set; } = CustomizeSource.CopyLocalPlayer;

    /// <summary>装备（10 个槽位）的来源。</summary>
    public EquipmentSource EquipmentSource { get; set; } = EquipmentSource.CopyLocalPlayer;

    /// <summary>当来源选 Captured / Default 时使用的身体数据（26 字节 CustomizeData）。</summary>
    public byte[] CapturedCustomize { get; set; } = new byte[Persona.CustomizeLength];

    /// <summary>当来源选 Captured / Default 时使用的装备数据（10 × 8 字节）。</summary>
    public byte[] CapturedEquipment { get; set; } = new byte[Persona.EquipmentLength];

    // ───────────────────────── Glamourer 设计 ─────────────────────────

    /// <summary>使用 Glamourer 里保存的设计（只读 IPC 拉取，不进它的状态系统）。</summary>
    public bool UseGlamourerDesign { get; set; }

    /// <summary>选中的设计。</summary>
    public Guid GlamourerDesignId { get; set; }

    /// <summary>设计名（缓存，用于 UI 显示与失效提示）。</summary>
    public string GlamourerDesignName { get; set; } = string.Empty;

    /// <summary>把设计里的主/副手武器也写上去（实验性，默认关）。</summary>
    public bool ApplyWeapons { get; set; }

    // ───────────────────────── 陆行鸟搭档 ─────────────────────────

    /// <summary>
    /// 跟着你走的陆行鸟搭档。它在对象表里是 BattleNpc，<b>没有可硬校验的归属字段</b>，
    /// 只能靠「名字 / 数据 ID + 距离」识别。
    /// </summary>
    public Core.ChocoboSettings Chocobo { get; set; } = Core.ChocoboSettings.Default();

    // ───────────────────────── 召唤物（宝石兽 / 小仙女）─────────────────────────

    /// <summary>
    /// 召唤师的红/翠/黄宝石兽。
    /// 对象表里是 BattleNpc，<b>OwnerId 就是主人的 ContentId</b>，归属可以硬校验。
    /// </summary>
    public Core.SummonSettings Carbuncle { get; set; } = Core.SummonSettings.CarbuncleDefault();

    /// <summary>
    /// 学者的小仙女。
    /// 对象表里是 BattleNpc，<b>OwnerId 就是主人的 ContentId</b>，归属可以硬校验。
    /// </summary>
    public Core.SummonSettings Fairy { get; set; } = Core.SummonSettings.FairyDefault();

    /// <summary>取某一类目标的设置；宠物走顶层字段（返回 null）。</summary>
    public Core.TargetSettings? SettingsFor(Core.TargetKind kind)
        => kind switch
        {
            Core.TargetKind.ChocoboPartner => Chocobo,
            Core.TargetKind.Carbuncle      => Carbuncle,
            Core.TargetKind.Fairy          => Fairy,
            _                               => null,
        };

    // ───────────────────────── 姿态 / 视觉 ─────────────────────────

    /// <summary>宠物是否启用视觉修正（贴地补偿 + 体型缩放）。</summary>
    /// <remarks>
    /// v1.29.2 新增。此前宠物的这个开关是靠 null 模式匹配"意外恒为 true"实现的，
    /// 界面上没有对应控件——现在补上，宠物与陆行鸟搭档各自独立。
    /// </remarks>
    public bool UseVisualCorrection { get; set; } = true;

    /// <summary>贴地补偿（yalm）。飞行类宠物（蓝闪蝶等）变人后浮空时往下压。</summary>
    public float GroundOffsetY { get; set; }

    /// <summary>体型缩放（只作用于绘制对象，纯视觉）。1 = 原始大小。</summary>
    public float VisualScale { get; set; } = 1f;

    /// <summary>待机动作的生效方式。</summary>
    public Core.IdleMode IdleMode { get; set; } = Core.IdleMode.Off;

    /// <summary>常驻待机动作的 ActionTimeline 行号（0 = 不设置）。</summary>
    public int IdleTimelineId { get; set; }

    // ───────────────────────── 安全开关 ─────────────────────────

    /// <summary>短路游戏的类型限制检查。</summary>
    public RestrictionBypassScope BypassScope { get; set; } = RestrictionBypassScope.WhileActive;

    /// <summary>是否允许处理坐骑 / 跟宠（CompanionType.FollowMount）。默认只处理真正的宠物。</summary>
    public bool IncludeFollowMounts { get; set; }

    /// <summary>是否让宠物保持"跟随"行为（写入 CompanionMove.Obedient）。默认不碰行为。</summary>
    public bool ForceFollowBehavior { get; set; }

    /// <summary>换区、重新召唤之后自动重新施加。</summary>
    public bool AutoReapply { get; set; } = true;

    /// <summary>
    /// 实时同步 Glamourer 设计：在 Glamourer 里改动并保存后自动应用到宠物，不用手动点。
    /// 靠监视设计文件的最后写入时间实现——Glamourer 保存设计时会重写该文件。
    /// </summary>
    public bool LiveSyncDesign { get; set; } = true;

    /// <summary>
    /// 实时同步的防抖秒数：文件写入时间戳在这段时间内保持不变，才认为写入已完成。
    /// Glamourer 保存时可能分几次写，立刻去读会读到半截 JSON。
    /// </summary>
    public double LiveSyncDebounceSeconds { get; set; } = 0.8;

    /// <summary>巡检间隔（帧）。</summary>
    public int ReapplyIntervalFrames { get; set; } = 30;

    /// <summary>
    /// 重施冷却（秒）。刚施加成功后的这段时间内，即使检测到"需要重施"也不再重绘，
    /// 避免换区后的密集巡检把同一次施加重复做很多遍（会表现为闪烁）。
    /// 用户主动点「立即应用」不受冷却限制。
    /// </summary>
    public int ReapplyCooldownSeconds { get; set; } = 5;

    /// <summary>连续失败多少次后自动还原并关掉总开关。</summary>
    public int FailureLimit { get; set; } = 3;

    /// <summary>输出到游戏聊天框。</summary>
    public bool ChatFeedback { get; set; } = true;

    /// <summary>输出详细日志到 /xllog。</summary>
    public bool VerboseLog { get; set; }

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);

    /// <summary>老配置升级 / 补默认值，避免空引用。</summary>
    public void CaptureDefaultsIfNeeded()
    {
        if (CapturedCustomize is null || CapturedCustomize.Length != Persona.CustomizeLength)
            CapturedCustomize = Persona.DefaultCustomize();

        CapturedEquipment ??= new byte[Persona.EquipmentLength];

        if (CapturedEquipment.Length != Persona.EquipmentLength)
            CapturedEquipment = new byte[Persona.EquipmentLength];

        if (GlamourerDesignName is null)
            GlamourerDesignName = string.Empty;

        if (VisualScale <= 0f)
            VisualScale = 1f;

        if (IdleTimelineId is < 0 or > ushort.MaxValue)
            IdleTimelineId = 0;

        Chocobo   ??= Core.ChocoboSettings.Default();
        Carbuncle ??= Core.SummonSettings.CarbuncleDefault();
        Fairy     ??= Core.SummonSettings.FairyDefault();

        Chocobo.Sanitize();
        Carbuncle.Sanitize();
        Fairy.Sanitize();
    }

    // ── 以下 6 个方法是全插件唯一的"按类别取值"入口 ──
    //
    // 其它模块一律通过它们拿 per-kind 的值，不直接读字段。
    // 因此**新增一类目标只需要在 SettingsFor 里加一个分支**，别处不用动。

    /// <summary>取某个目标类别实际该用的设计（宠物与召唤物可以各选各的）。</summary>
    public Guid GetDesignId(Core.TargetKind kind)
    {
        var summon = SettingsFor(kind);

        // 召唤物可以在自己那一页单独关掉"Glamourer 设计"（全局那个开关只管宠物与陆行鸟）。
        // 返回空 GUID → 服务层自然回退到"照抄我自己"，不必在服务层加分支。
        if (summon is Core.SummonSettings { UseGlamourer: false })
            return Guid.Empty;

        return summon is { UseOwnDesign: true } && summon.DesignId != Guid.Empty
            ? summon.DesignId
            : GlamourerDesignId;
    }

    /// <summary>取某个目标类别实际该用的设计名（仅供显示）。</summary>
    public string GetDesignName(Core.TargetKind kind)
    {
        var summon = SettingsFor(kind);
        return summon is { UseOwnDesign: true } && summon.DesignName.Length > 0
            ? summon.DesignName
            : GlamourerDesignName;
    }

    /// <summary>
    /// 取某个目标类别实际该用的视觉修正（贴地 / 缩放）。
    ///
    /// <para>
    /// <b>召唤物一律启用</b>（不看它自己的 <c>UseVisual</c>）——
    /// 那个开关缺省是 false，而它同时把<b>贴地补偿</b>一起挡掉了：
    /// 补偿为 0 时绘制对象停在半空，走路就是浮的。所以这里对召唤物直接放行。
    /// </para>
    /// </summary>
    public bool GetVisualEnabled(Core.TargetKind kind)
    {
        // ⚠️ v1.29.2 修：**宠物侧的视觉修正开关此前完全失效**。
        //
        // 旧写法 `SettingsFor(kind) is not { UseVisual: false }` 依赖 null 模式匹配，
        // 而宠物走顶层字段（SettingsFor 返回 null）—— `null is not { ... }` 恒为 true，
        // 于是宠物被永久判定为"开视觉修正"，自己的开关形同虚设。
        // 更糟的是 wantOffset 是"四类任一开启"的全局 OR：
        // 宠物恒开 → 整个 OR 恒真 → 陆行鸟搭档自己设的 UseVisual=false 被架空，
        // 表现就是「陆行鸟那边的视觉修正参数跑去影响宠物了」。
        //
        // 现在按类别显式判定，不再依赖 null 模式：
        //   · 宠物 / 跟宠坐骑 / Any → 顶层开关 UseVisualCorrection
        //   · 召唤物（宝石兽/小仙女）→ 恒为 true（见上面注释：它们的开关缺省是 false，
        //     且补偿为 0 会导致悬浮，所以历史上一律放行；本次保持该行为不变）
        //   · 陆行鸟搭档等其余类别 → 各用各的 UseVisual
        var settings = SettingsFor(kind);

        if (settings is Core.SummonSettings)
            return true;   // 召唤物历史行为：恒开（补偿为 0 会悬浮，见上方注释）

        // 宠物与"不按类别过滤"的还原路径走顶层字段。
        if (settings is null)
            return UseVisualCorrection;

        return settings.UseVisual;
    }

    /// <summary>取某个目标类别实际该用的贴地补偿（每个类别各用各的，可分别设置）。</summary>
    public float GetGroundOffset(Core.TargetKind kind)
        => SettingsFor(kind)?.GroundOffsetY ?? GroundOffsetY;

    /// <summary>取某个目标类别实际该用的体型缩放（每个类别各用各的，可分别设置）。</summary>
    public float GetVisualScaleValue(Core.TargetKind kind)
        => SettingsFor(kind)?.VisualScale ?? VisualScale;

    /// <summary>取某个目标类别实际该用的待机设置。</summary>
    public (Core.IdleMode Mode, int TimelineId) GetIdle(Core.TargetKind kind)
    {
        var summon = SettingsFor(kind);
        return summon is { UseOwnIdle: true }
            ? (summon.IdleMode, summon.IdleTimelineId)
            : (IdleMode, IdleTimelineId);
    }
}
