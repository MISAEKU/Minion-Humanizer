using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using Lumina.Excel.Sheets;
using MinionHumanizer.Core;
using MinionHumanizer.Watchers;

namespace MinionHumanizer.UI;

/// <summary>
/// 设置窗口。
///
/// 信息架构：<b>一页只负责一个目标</b>。
/// 以前所有设置混在"外观 / 姿态 / 安全"三页里，宠物和召唤物的选项互相穿插，
/// 看不出父子关系。现在改成：
///   · 总览—— 开关、当前在处理谁、快捷操作；
///   · 宠物—— 宠物的外观 / 视觉修正 / 待机 / 表情，一页看全；
///   · 陆行鸟搭档 / 宝石兽 / 小仙女 —— 同上，但各项独立配置；
///   · 高级—— 出问题时才需要动的参数。
/// </summary>
public sealed class ConfigWindow : Window
{
    private readonly Configuration         _configuration;
    private readonly HumanizerService      _service;
    private readonly KindRestrictionBypass _bypass;
    private readonly GlamourerBridge       _glamourer;
    private readonly PuppetController      _puppet;
    private readonly DesignWatcher?         _watcher;

    private List<EmoteEntry>? _emotes;
    private string _emoteSearch = string.Empty;

    /// <summary>表情列表里的"播放给"目标。</summary>
    private TargetKind _emoteTarget = TargetKind.Minion;

    private readonly record struct EmoteEntry(uint RowId, string Name, ushort Timeline);

    public ConfigWindow(Configuration configuration, HumanizerService service, KindRestrictionBypass bypass,
        GlamourerBridge glamourer, PuppetController puppet, DesignWatcher? watcher = null)
        : base("宠物拟人 - Minion Humanizer###MinionHumanizerConfig")
    {
        _configuration = configuration;
        _service       = service;
        _bypass        = bypass;
        _glamourer     = glamourer;
        _puppet        = puppet;
        _watcher       = watcher;

        Size           = new Vector2(660, 700);
        SizeCondition  = ImGuiCond.FirstUseEver;
    }

    public override void Draw()
    {
        if (ImGui.BeginTabBar("###MhTabs"))
        {
            if (ImGui.BeginTabItem("总览"))
            {
                ImGui.Spacing();
                DrawOverview();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("宠物"))
            {
                ImGui.Spacing();
                DrawPetPage();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("陆行鸟搭档"))
            {
                ImGui.Spacing();
                DrawNpcTargetPage(TargetKind.ChocoboPartner);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("宝石兽"))
            {
                ImGui.Spacing();
                DrawNpcTargetPage(TargetKind.Carbuncle);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("小仙女"))
            {
                ImGui.Spacing();
                DrawNpcTargetPage(TargetKind.Fairy);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("高级"))
            {
                ImGui.Spacing();
                DrawAdvancedPage();
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }
    }

    // ────────────────────────────────────────────────────────────────
    //  小组件
    // ────────────────────────────────────────────────────────────────

    /// <summary>一个带标题与说明的分组。标题下的说明就是这一组设置的用途。</summary>
    private static void BeginGroup(string title, string? hint = null)
    {
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextUnformatted(title);
        if (hint is not null)
            ImGui.TextDisabled(hint);

        ImGui.Indent();
    }

    private static void EndGroup() => ImGui.Unindent();

    /// <summary>一行"当前生效值"，让用户不用点开就知道现在是什么状态。</summary>
    private static void CurrentValue(string label, string value)
        => ImGui.TextDisabled($"当前：{label} = {value}");

    // ────────────────────────────────────────────────────────────────
    //  总览
    // ────────────────────────────────────────────────────────────────

    private void DrawOverview()
    {
        BeginGroup("功能开关", "总开关。关掉会立刻把所有目标还原成原样。");
        var enabled = _configuration.Enabled;
        if (ImGui.Checkbox("启用拟人化", ref enabled))
        {
            _configuration.Enabled = enabled;
            _configuration.Save();
        }

        ImGui.TextWrapped(_configuration.Enabled
            ? "已开启：宠物与（若已启用）陆行鸟搭档 / 召唤物会变成人形。"
            : "已关闭：正在把所有目标还原成原样。");
        EndGroup();

        BeginGroup("正在处理的目标", "这一栏是真实状态——只有开关没有反馈，就看不出跟宠坐骑到底有没有被用上。");
        DrawTrackedList();
        EndGroup();

        BeginGroup("快捷操作");
        if (ImGui.Button("立即施加"))
        {
            _configuration.Enabled = true;
            _configuration.Save();
            _service.RequestReapply("手动立即施加");
        }

        ImGui.SameLine();
        if (ImGui.Button("立即还原"))
        {
            _configuration.Enabled = false;
            _configuration.Save();
            _service.RestoreAll("手动还原");
        }

        ImGui.SameLine();
        if (ImGui.Button("输出自检报告"))
            Plugin.Chat.Print("[MinionHumanizer] 自检：\n" + _service.BuildDiagnostics(_puppet));

        ImGui.SameLine();
        if (ImGui.Button("打开日志"))
            Plugin.Chat.Print("[MinionHumanizer] 输入 /xllog 查看详细日志。");
        EndGroup();

        if (PuppetController.IsRiding)
        {
            ImGui.Spacing();
            ImGui.TextColored(new Vector4(0.6f, 0.8f, 1f, 1f),
                "骑乘中：拟人化、视觉修正与待机已临时停用，坐骑就骑在身上不能动它。");
            ImGui.TextDisabled("下坐骑后会自动重新拟人化。");
        }

        // 版本号：和 csproj 的 <Version> 一致，方便确认装的是哪一版。
        ImGui.Spacing();
        ImGui.TextDisabled($"宠物拟人 · Minion Humanizer  v{Plugin.DisplayVersion}");
    }

    private void DrawTrackedList()
    {
        var tracked = _service.DescribeTracked();
        if (tracked.Count == 0)
        {
            ImGui.TextDisabled("（当前没有跟踪任何目标。召唤宠物 / 陆行鸟搭档 / 宝石兽 / 小仙女后才会出现。）");
            return;
        }

        foreach (var (name, kind, applied) in tracked)
        {
            var color = applied ? new Vector4(0.55f, 0.95f, 0.6f, 1f) : new Vector4(1f, 0.8f, 0.4f, 1f);
            ImGui.TextColored(color, $"● {name}　{kind.DisplayName()}　{(applied ? "已拟人" : "待施加")}");
        }
    }

    // ────────────────────────────────────────────────────────────────
    //  宠物 / NPC 类目标
    // ────────────────────────────────────────────────────────────────

    private void DrawPetPage()
    {
        DrawAppearanceSection(TargetKind.Minion);
        DrawVisualSection(TargetKind.Minion);
        DrawIdleSection(TargetKind.Minion);
        DrawEmoteSection();
    }

    /// <summary>
    /// NPC 类目标页面（陆行鸟搭档 / 宝石兽 / 小仙女共用模板，功能与宠物页一致）。
    ///
    /// 三个页面只有「启用与识别」这一段不同（归属信号不同），其余三段完全共用。
    /// </summary>
    private void DrawNpcTargetPage(TargetKind kind)
    {
        if (kind == TargetKind.ChocoboPartner)
            DrawChocoboEnableSection(kind);
        else
            DrawSummonEnableSection(kind);

        DrawAppearanceSection(kind);
        DrawVisualSection(kind);
        DrawIdleSection(kind);
        DrawNpcTargetProbeSection(kind);
    }

    /// <summary>
    /// 陆行鸟搭档的「启用与识别」区。
    ///
    /// 搭档<b>没有</b>召唤物那种硬归属信号（OwnerId 里不是主人 ContentId），
    /// 所以只能「名字/数据 ID + 距离」做特征匹配。
    /// </summary>
    private void DrawChocoboEnableSection(TargetKind kind)
    {
        var settings = _configuration.SettingsFor(kind) as ChocoboSettings;
        if (settings is null)
            return;

        BeginGroup("启用与识别",
                   "搭档是 NPC（BattleNpc），没有 Owner 字段、OwnerId 里也不是你的 ContentId，"
                 + "所以只能靠名字/数据 ID + 距离来认。");

        var enabled = settings.Enabled;
        if (ImGui.Checkbox($"把{kind.DisplayName()}也变成人形", ref enabled))
        {
            settings.Enabled = enabled;
            _configuration.Save();
            _service.RequestReapply($"切换{kind.DisplayName()}拟人化");
        }

        if (settings.Enabled && settings.TargetDataId == 0 && settings.Keywords.Count == 0)
            ImGui.TextColored(new Vector4(1f, 0.7f, 0.2f, 1f),
                              "还没指定目标——即使勾了开关也不会生效。请填名字关键字或数据 ID。");

        ImGui.SetNextItemWidth(320);
        var keywords = settings.NameKeywordList;
        if (ImGui.InputTextWithHint("##chocoboNames", "名字关键字，逗号分隔", ref keywords, 128))
        {
            settings.NameKeywordList = keywords;
            _configuration.Save();
        }

        var idField = (int)settings.TargetDataId;
        ImGui.SetNextItemWidth(180);
        if (ImGui.InputInt("数据 ID（0 = 不按 ID 匹配）", ref idField))
        {
            settings.TargetDataId = (uint)Math.Max(0, idField);
            _configuration.Save();
        }

        var distance = settings.MaxDistance;
        ImGui.SetNextItemWidth(240);
        if (ImGui.SliderFloat("最大距离 (yalm)", ref distance, 3f, 30f, "%.0f"))
        {
            settings.MaxDistance = distance;
            _configuration.Save();
        }

        ImGui.TextDisabled("搭档必须在你身边这个范围内才会被处理，防止隔墙对人 NPC 下手。");
        EndGroup();
    }

    /// <summary>
    /// 召唤物的"启用与识别"区。
    ///
    /// 归属是<b>硬校验</b>的：这类 NPC 的 <c>OwnerId</c> 就是主人的 ContentId，
    /// 所以不需要（也不应该）让用户去填名字或数据 ID —— 特征匹配对所有人一视同仁，
    /// 正是"多人场景下误伤别人"的根源。名字关键字只用来区分宝石兽 / 小仙女。
    /// </summary>
    private void DrawSummonEnableSection(TargetKind kind)
    {
        var s = _configuration.SettingsFor(kind) as SummonSettings;
        if (s is null)
            return;

        BeginGroup("启用与识别",
                   "召唤物在对象表里是 NPC（BattleNpc），没有 Companion 的 Owner 字段；"
                 + "但 OwnerId 存的就是你的 GameObjectId，所以归属由插件硬校验，不会碰到别人的召唤物。");

        var enabled = s.Enabled;
        if (ImGui.Checkbox($"把{kind.DisplayName()}也变成人形", ref enabled))
        {
            s.Enabled = enabled;
            _configuration.Save();
            _service.RequestReapply($"切换{kind.DisplayName()}拟人化");
        }

        if (enabled && s.Keywords.Count == 0)
            ImGui.TextColored(new Vector4(1f, 0.7f, 0.2f, 1f),
                              "名字关键字是空的，插件认不出它 —— 请填回默认关键字。");

        ImGui.SetNextItemWidth(320);
        var keywords = s.NameKeywordList;
        if (ImGui.InputTextWithHint($"##kw{kind}", "名字关键字，逗号分隔", ref keywords, 128))
        {
            s.NameKeywordList = keywords;
            _configuration.Save();
        }

        ImGui.TextDisabled("归属判据：OwnerId == 你的 GameObjectId（硬校验），不需要距离条件。");

        // ── 屏蔽换色变种 ──
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextUnformatted("换色变种");

        var exact = s.OnlyExactName;
        if (ImGui.Checkbox($"只处理本体，换色变种不处理##exact{kind}", ref exact))
        {
            s.OnlyExactName = exact;
            _configuration.Save();
            _service.RequestReapply($"切换{kind.DisplayName()}换色屏蔽");
        }

        ImGui.TextDisabled("默认开启。实测：宝石兽(411)、朝日小仙女(407) 变人+套 mod 正常；"
                          + "换色的绿宝石兽(409) 会让游戏崩溃，所以按名字精确匹配把变种挡在外面。");

        ImGui.SetNextItemWidth(320);
        var baseNames = s.BaseNames;
        if (ImGui.InputTextWithHint($"##base{kind}", "本体名字，逗号分隔（精确匹配）", ref baseNames, 128))
        {
            s.BaseNames = baseNames;
            _configuration.Save();
        }

        ImGui.SetNextItemWidth(320);
        var blocked = s.BlockedNames;
        if (ImGui.InputTextWithHint($"##blk{kind}", "额外屏蔽的名字，逗号分隔（可留空）", ref blocked, 128))
        {
            s.BlockedNames = blocked;
            _configuration.Save();
        }

        // ── 刚出生保护期（现场可调）──
        ImGui.Spacing();
        ImGui.TextUnformatted("刚出生保护期");

        ImGui.SetNextItemWidth(320);
        var settle = s.SettleFrames;
        if (ImGui.SliderInt($"召唤后等多久再动手（帧，60 帧≈1 秒）##settle{kind}", ref settle, 0, 900))
        {
            s.SettleFrames = settle;
            _configuration.Save();
        }

        ImGui.TextDisabled("崩溃现场实锤：宝石兽的绘制对象在 14:42:09.494 刚创建，"
                          + "我们 .556 跟踪、.558 就重建了它 —— 只隔 64 毫秒，随后崩溃。");
        ImGui.TextDisabled("陆行鸟搭档从不崩，是因为它早就存在。调 0 = 立刻动手（快但容易崩）。");

        if (exact)
            ImGui.TextDisabled($"当前只处理：{s.BaseNames}（名字必须完全一致）");
        else
            ImGui.TextColored(new Vector4(1f, 0.7f, 0.2f, 1f),
                              "⚠️ 已关闭换色屏蔽：所有变种都会尝试拟人化，换色可能会让游戏崩溃。");

        EndGroup();
    }

    /// <summary>NPC 类目标的诊断区：陆行鸟看跟随关系，召唤物看 OwnerId。</summary>
    private void DrawNpcTargetProbeSection(TargetKind kind)
    {
        var isChocobo = kind == TargetKind.ChocoboPartner;

        BeginGroup(isChocobo ? "不知道搭档的名字 / ID？" : "召唤它之后诊断一下",
                   isChocobo
                       ? "召唤出陆行鸟搭档后点下面的按钮，照着列表填名字最稳。"
                       : "先在游戏里把它召唤出来，再点下面的按钮。列表会标出哪些 NPC 的 OwnerId 等于你的 ContentId。");

        if (ImGui.Button($"探测身边的候选（{kind.DisplayName()}）"))
            Plugin.Chat.Print($"[MinionHumanizer] {kind.DisplayName()} 候选：\n" + BuildNpcTargetReport(kind));

        ImGui.TextDisabled(isChocobo
            ? "认领判据：跟随你 → 认；明确跟着别人 → 跳过。列表里也会列出坐骑（骑乘中的坐骑同样是 NPC actor）。"
            : "只有「OwnerId = 我的」那一行才会被处理。");

        EndGroup();
    }

    /// <summary>列出身边 NPC 类 actor。召唤物按 OwnerId 判归属，陆行鸟按「是否跟随你」判。</summary>
    private string BuildNpcTargetReport(TargetKind kind)
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player is null)
            return "（本地玩家尚未加载）";

        var myContentId  = Plugin.PlayerState.ContentId;
        var myObjectId   = player.GameObjectId;   // ⚠️ OwnerId 比对用的是这个
        var settings    = _configuration.SettingsFor(kind);
        var sb          = new System.Text.StringBuilder();

        sb.AppendLine($"你的 GameObjectId = {myObjectId}（召唤物归属比对用这个）｜ContentId = {myContentId}");

        if (myObjectId == 0 && kind != TargetKind.ChocoboPartner)
            sb.AppendLine("⚠️ 读不到 GameObjectId —— 这种情况下插件不会处理任何召唤物（无法确认归属）。");

        var count = 0;
        foreach (var obj in Plugin.ObjectTable)
        {
            if (obj is null || obj.Address == nint.Zero)
                continue;

            if (obj.ObjectKind is not (Dalamud.Game.ClientState.Objects.Enums.ObjectKind.BattleNpc
                                     or Dalamud.Game.ClientState.Objects.Enums.ObjectKind.EventNpc))
                continue;

            var b = player.Position;
            var a = obj.Position;
            var distance = MathF.Sqrt((a.X - b.X) * (a.X - b.X)
                                    + (a.Y - b.Y) * (a.Y - b.Y)
                                    + (a.Z - b.Z) * (a.Z - b.Z));
            if (distance > 40f)
                continue;

            var isChocobo = kind == TargetKind.ChocoboPartner;

            // 召唤物：OwnerId 就是主人 ContentId。
            // 陆行鸟：OwnerId 不是，所以看它「跟着谁」——本地玩家就是主人。
            var mine = isChocobo
                ? obj.TargetObjectId != 0 && obj.TargetObjectId == myObjectId
                : myObjectId != 0 && obj.OwnerId == myObjectId;

            var hit = settings is not null && settings.Matches(obj.Name.TextValue);

            sb.AppendLine($"· {obj.Name.TextValue}｜ID={obj.BaseId}｜{distance:0.0} yalm"
                        + (isChocobo ? $"｜跟随={obj.TargetObjectId}" : $"｜OwnerId={obj.OwnerId}")
                        + (mine ? "｜✅ 认领" : "｜")
                        + (hit ? "｜关键字命中" : "｜关键字不匹配"));

            if (++count >= 12)
                break;
        }

        return count == 0 ? "（身边 40 yalm 内没有 NPC 类 actor）" : sb.ToString();
    }

    private void DrawAppearanceSection(TargetKind kind)
    {
        // 宠物的设置直接存在 Configuration 上；召唤物有一份独立的 SummonSettings。
        // 全 UI 只有这一个分支点，新增类别不用改这里。
        var s     = _configuration.SettingsFor(kind);
        var isPet = s is null;

        BeginGroup(isPet ? "外观 · 宠物" : $"外观 · {kind.DisplayName()}",
                   "优先用 Glamourer 里保存的设计；拉取不到时回退到下方的本地来源。");

        if (!isPet)
        {
            var own = s.UseOwnDesign;
            if (ImGui.Checkbox("使用与宠物不同的设计", ref own))
            {
                s.UseOwnDesign = own;
                _configuration.Save();
                _service.RequestReapply($"切换{kind.DisplayName()}独立设计");
            }
        }

        var designId   = _configuration.GetDesignId(kind);
        var designName = _configuration.GetDesignName(kind);

        if (!isPet && !s.UseOwnDesign)
        {
            CurrentValue("设计", $"{designName}（跟随宠物）");
        }
        else
        {
            // 召唤物用<b>自己那份</b>开关；宠物与陆行鸟沿用全局的。
            var use = s is Core.SummonSettings summon
                ? summon.UseGlamourer
                : _configuration.UseGlamourerDesign;

            if (ImGui.Checkbox($"使用 Glamourer 设计##gl{kind}", ref use))
            {
                if (s is Core.SummonSettings sm) sm.UseGlamourer = use;
                else                      _configuration.UseGlamourerDesign = use;

                _configuration.Save();
                _service.RequestReapply("切换 Glamourer 设计开关");
            }

            if (use)
            {
                var designs = _glamourer.GetDesigns();
                if (designs.Count == 0)
                {
                    ImGui.TextDisabled("（Glamourer 未运行，或没有可用设计）");
                }
                else
                {
                    var labels = designs.Select(d => d.Name).ToArray();
                    var index  = designs.FindIndex(d => d.Id == designId);
                    if (index < 0)
                        index = 0;

                    ImGui.SetNextItemWidth(320);
                    if (ImGui.Combo("设计", ref index, labels, labels.Length))
                    {
                        if (isPet)
                        {
                            _configuration.GlamourerDesignId   = designs[index].Id;
                            _configuration.GlamourerDesignName = designs[index].Name;
                        }
                        else
                        {
                            s.DesignId   = designs[index].Id;
                            s.DesignName = designs[index].Name;
                        }

                        _configuration.Save();
                        _service.RequestReapply("切换设计");
                    }

                    if (_glamourer.LastError.Length > 0)
                        ImGui.TextColored(new Vector4(1f, 0.55f, 0.3f, 1f), _glamourer.LastError);

                    var live = _configuration.LiveSyncDesign;
                    if (ImGui.Checkbox("实时同步（在 Glamourer 里改完自动应用）", ref live))
                    {
                        _configuration.LiveSyncDesign = live;
                        _configuration.Save();
                    }

                    if (_configuration.LiveSyncDesign)
                    {
                        ImGui.SameLine();
                        ImGui.TextDisabled(_watcher?.LastSyncNote ?? "监视中…（每 0.5 秒检查一次设计文件）");
                    }

                    ImGui.TextDisabled("通过监视设计文件的写入时间实现，不需要 Glamourer 开着 IPC；"
                                     + "带防抖，避免读到保存过程中写了一半的文件。");

                    if (_watcher is not null && _watcher.LastSyncUtc != DateTime.MinValue)
                    {
                        var ago = (DateTime.UtcNow - _watcher.LastSyncUtc).TotalSeconds;
                        if (ago < 60)
                            ImGui.TextDisabled($"上次自动同步：{ago:0} 秒前");
                    }
                }

                var weapons = _configuration.ApplyWeapons;
                if (ImGui.Checkbox("同时应用主/副手武器（实验性）", ref weapons))
                {
                    _configuration.ApplyWeapons = weapons;
                    _configuration.Save();
                    _service.RequestReapply("切换武器应用");
                }
            }
        }

        ImGui.Spacing();
        ImGui.TextDisabled("以下作为回退（Glamourer 不可用时使用）：");

        var customize = _configuration.CustomizeSource;
        if (EnumCombo("身体", ref customize, CustomizeOptions))
        {
            _configuration.CustomizeSource = customize;
            _configuration.Save();
            _service.RequestReapply("切换身体来源");
        }

        var equipment = _configuration.EquipmentSource;
        if (EnumCombo("装备", ref equipment, EquipmentOptions))
        {
            _configuration.EquipmentSource = equipment;
            _configuration.Save();
            _service.RequestReapply("切换装备来源");
        }

        if (ImGui.Button("捕获我当前的外观为快照"))
            CaptureFromPlayer();

        EndGroup();
    }

    private void DrawVisualSection(TargetKind kind)
    {
        // ⚠️ 关键：不要 `as Core.SummonSettings`。
        // 陆行鸟搭档是 ChocoboSettings，强转 SummonSettings 会得到 null，
        // 于是被误判成「宠物」，那一栏的滑块会写进宠物的顶层字段
        // （GroundOffsetY / VisualScale / UseVisualCorrection）—— 表现就是
        // 「陆行鸟那边的视觉修正参数跑去影响宠物」。这里用基类拿引用，
        // 仅靠 kind == Minion 判断「宠物走顶层字段」。
        var s     = _configuration.SettingsFor(kind);
        var isPet = kind == TargetKind.Minion;

        BeginGroup(isPet ? "视觉修正 · 宠物" : $"视觉修正 · {kind.DisplayName()}",
                   "纯渲染层，随时可归零。骑乘中会自动停用。");

        // 每个类别（含宠物与陆行鸟搭档）都有自己可见可控的「启用视觉修正」开关：
        //   · 宠物 → 顶层 UseVisualCorrection
        //   · 其余（陆行鸟搭档 / 宝石兽 / 小仙女）→ 各自 TargetSettings.UseVisual
        // 这个开关只控制"是否真写入修正值"（应用层 GetVisualEnabled 生效），
        // 不折叠界面——陆行鸟搭档的那一栏始终可见。
        var visualOn = _configuration.GetVisualEnabled(kind);
        if (ImGui.Checkbox("启用视觉修正（贴地 / 缩放）##useVisual", ref visualOn))
        {
            if (isPet) _configuration.UseVisualCorrection = visualOn;
            else if (s is not null) s.UseVisual = visualOn;
            _configuration.Save();
        }

        // 召唤物恒为开启（历史行为：补偿为 0 会导致悬浮），它的开关无效，需说明白。
        if (s is Core.SummonSettings)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("（召唤物恒为开启）");
        }

        // 宠物走顶层字段；其余类别（含陆行鸟搭档）各用各的 TargetSettings，互不影响。
        if (s is not null)
            ImGui.TextWrapped("这一类别<b>各自独立</b>设置，与宠物互不影响。"
                            + "<b>走路浮空不用再调数值</b>——移动时游戏叠加的垂直抬升已由闭环自动抵消。");

        // ⚠️ 不再用 GetVisualEnabled 把整栏折叠掉：
        // 折叠会让「陆行鸟搭档」那一栏在 UseVisual=false 时整个消失（即用户说的"毙了那栏"）。
        // 这一栏是陆行鸟<b>自己</b>的，应当始终可见；上面的开关只控制"是否真的写入修正值"
        // （由 GetVisualEnabled 在应用层生效），而不是把界面藏起来。
        var offset = _configuration.GetGroundOffset(kind);
        ImGui.SetNextItemWidth(240);
        // 范围放宽到 -6~+2：召唤物变成人形后需要的下沉量与宠物不是一个量级，
        // 原来上限只有 -3，某些召唤物根本拉不到位（表现为"怎么调都还浮"）。
        if (ImGui.SliderFloat("贴地补偿 (yalm)", ref offset, -6f, 2f, "%.2f"))
        {
            // 每个类别各写各的（召唤物写自己的 SummonSettings，互不影响）。
            if (isPet) _configuration.GroundOffsetY = offset;
            else       s.GroundOffsetY           = offset;
            _configuration.Save();
        }

        // 粗调：一档 0.5，比拖滑块快得多（找数量级用）。
        ImGui.SameLine();
        if (ImGui.Button($"-0.5##dnOff{kind}"))
        {
            var v = offset - 0.5f;
            if (isPet) _configuration.GroundOffsetY = v; else s.GroundOffsetY = v;
            _configuration.Save();
        }

        ImGui.SameLine();
        if (ImGui.Button($"+0.5##upOff{kind}"))
        {
            var v = offset + 0.5f;
            if (isPet) _configuration.GroundOffsetY = v; else s.GroundOffsetY = v;
            _configuration.Save();
        }

        if (!isPet)
        {
            ImGui.SameLine();
            if (ImGui.Button($"用宠物的值##cpOff{kind}"))
            {
                s.GroundOffsetY = _configuration.GetGroundOffset(TargetKind.Minion);
                _configuration.Save();
            }

            ImGui.SameLine();
            if (ImGui.Button($"归零##off{kind}"))
            {
                s.GroundOffsetY = 0f;
                _configuration.Save();
            }
        }

        // 体型缩放是**倍率**语义：1.00 = 保持游戏给的原始尺寸。
        var scale = _configuration.GetVisualScaleValue(kind);
        ImGui.SetNextItemWidth(240);
        if (ImGui.SliderFloat("体型缩放倍率##scale", ref scale, 0.2f, 2.5f, "×%.2f"))
        {
            if (isPet) _configuration.VisualScale = Math.Clamp(scale, 0.1f, 3f);
            else       s.VisualScale         = Math.Clamp(scale, 0.1f, 3f);
            _configuration.Save();
        }

        if (!isPet)
        {
            ImGui.SameLine();
            if (ImGui.Button($"用宠物的值##cpSc{kind}"))
            {
                s.VisualScale = _configuration.GetVisualScaleValue(TargetKind.Minion);
                _configuration.Save();
            }

            ImGui.SameLine();
            if (ImGui.Button($"还原##sc{kind}"))
            {
                s.VisualScale = 1f;
                _configuration.Save();
            }
        }

        // 实时显示"实际下沉量"：调滑块时能立刻看到效果，不用凭感觉猜。
        var live = _puppet.DescribeVisual(kind);
        if (live is not null)
            CurrentValue("现场实际下沉量（补偿是否生效）", $"{live}  ← 目标接近 0 或与配置一致就对了");

        CurrentValue("贴地 / 缩放倍率",
            $"{_configuration.GetGroundOffset(kind):0.00} / ×{_configuration.GetVisualScaleValue(kind):0.00}");
        ImGui.TextDisabled("倍率 1.00 = 游戏原始体型（严格还原，不会残留上一次的尺寸）。");
        ImGui.TextDisabled("目标 = 绘制位置相对 actor 的下沉量。召唤物站在地上，0 就是脚踩地面；飞行宠物才需要负值抵消悬浮。");
        EndGroup();
    }

    private void DrawIdleSection(TargetKind kind)
    {
        var s     = _configuration.SettingsFor(kind);
        var isPet = s is null;

        BeginGroup(isPet ? "待机动作 · 宠物" : $"待机动作 · {kind.DisplayName()}",
                   "只改动画层。想让目标做表情/动作，用下面的表情列表。");

        if (!isPet)
        {
            var own = s.UseOwnIdle;
            if (ImGui.Checkbox("使用与宠物不同的待机", ref own))
            {
                s.UseOwnIdle = own;
                _configuration.Save();
                _puppet.StopIdle();
                _service.RequestReapply($"切换{kind.DisplayName()}独立待机");
            }

            if (!own)
            {
                var (petMode, petId) = _configuration.GetIdle(TargetKind.Minion);
                CurrentValue("待机",
                    petMode == IdleMode.Off || petId == 0 ? "未设置（跟随宠物）" : $"{petMode} #{petId}（跟随宠物）");
                EndGroup();
                return;
            }
        }

        var (mode, id) = _configuration.GetIdle(kind);

        if (EnumCombo("生效方式", ref mode, IdleOptions))
        {
            if (isPet) _configuration.IdleMode = mode; else s.IdleMode = mode;
            _configuration.Save();
        }

        ImGui.SetNextItemWidth(160);
        if (ImGui.InputInt("待机ID", ref id))
        {
            id = Math.Clamp(id, 0, ushort.MaxValue);
            if (isPet) _configuration.IdleTimelineId = id; else s.IdleTimelineId = id;
            _configuration.Save();
        }

        var suffix = $"##{kind}";
        ImGui.SameLine();
        if (ImGui.Button($"用我当前的待机{suffix}"))
        {
            var captured = PuppetController.CapturePlayerBaseTimeline();
            if (captured == 0)
            {
                Plugin.Chat.PrintError("[MinionHumanizer] 没读到你的当前动作。先站定一秒再点。");
            }
            else
            {
                if (isPet)
                {
                    _configuration.IdleTimelineId = captured;
                    if (_configuration.IdleMode == IdleMode.Off)
                        _configuration.IdleMode = IdleMode.Always;
                }
                else
                {
                    s.IdleTimelineId = captured;
                    s.UseOwnIdle     = true;
                    if (s.IdleMode == IdleMode.Off)
                        s.IdleMode = IdleMode.Always;
                }

                _configuration.Save();
                _puppet.StopIdle();
                Plugin.Chat.Print($"[MinionHumanizer] 待机已设为 #{captured}（你当前站立用的动作）。");
            }
        }

        ImGui.SameLine();
        if (ImGui.Button($"停止待机{suffix}"))
            _puppet.StopIdle();

        CurrentValue("待机", mode == IdleMode.Off || id == 0 ? "未设置" : $"{mode} #{id}");
        ImGui.TextDisabled("待机 ID 是游戏的 ActionTimeline 行号；「用我当前的待机」最省事，种族体型天然匹配。");
        EndGroup();
    }

    // ── 表情 ──

    private void DrawEmoteSection()
    {
        BeginGroup("表情 / 动作", "点击即在选中的目标上播放；表情播完会自动接回待机。");
        DrawEmoteList();
        EndGroup();
    }

    private void DrawEmoteList()
    {
        _emotes ??= LoadEmotes();

        var targets= new[] { TargetKind.Minion, TargetKind.ChocoboPartner, TargetKind.Carbuncle, TargetKind.Fairy };
        var labels = new[] { "宠物", "陆行鸟搭档", "宝石兽", "小仙女" };
        var index  = _emoteTarget switch
        {
            TargetKind.ChocoboPartner => 1,
            TargetKind.Carbuncle      => 2,
            TargetKind.Fairy          => 3,
            _                          => 0,
        };

        ImGui.SetNextItemWidth(180);
        if (ImGui.Combo("播放给", ref index, labels, labels.Length))
            _emoteTarget = targets[index];

        ImGui.SameLine();
        if (ImGui.Button("停止动作"))
            _puppet.StopEmote();

        ImGui.SameLine();
        ImGui.TextDisabled(_puppet.CurrentEmoteName.Length > 0
            ? $"正在播放：{_puppet.CurrentEmoteName}"
            : "当前没有播放任何动作");

        ImGui.SetNextItemWidth(260);
        ImGui.InputTextWithHint("##emoteSearch", "搜索表情名…", ref _emoteSearch, 32);

        var keyword  = _emoteSearch.Trim();
        var filtered = keyword.Length == 0
            ? _emotes!
            : _emotes!.Where(e => e.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase)).ToList();

        if (filtered.Count == 0)
        {
            ImGui.TextDisabled("（没有匹配的表情）");
            return;
        }

        if (!ImGui.BeginChild("##emoteList", new Vector2(-1, 230), true))
        {
            ImGui.EndChild();
            return;
        }

        foreach (var emote in filtered.Take(60))
        {
            ImGui.PushID((int)emote.RowId);
            if (ImGui.Button(emote.Name))
                _puppet.PlayEmote(emote.Timeline, emote.Name, _emoteTarget);

            ImGui.SameLine();
            ImGui.TextDisabled($"#{emote.Timeline}");
            ImGui.PopID();
        }

        if (filtered.Count > 60)
            ImGui.TextDisabled($"…还有 {filtered.Count - 60} 个，输入关键词缩小范围。");

        ImGui.EndChild();
    }

    private string BuildNearbyNpcReport()
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player is null)
            return "（本地玩家尚未加载）";

        var sb = new System.Text.StringBuilder();
        var count = 0;

        foreach (var obj in Plugin.ObjectTable)
        {
            if (obj is null || obj.Address == nint.Zero)
                continue;

            if (obj.ObjectKind is not (Dalamud.Game.ClientState.Objects.Enums.ObjectKind.BattleNpc
                                     or Dalamud.Game.ClientState.Objects.Enums.ObjectKind.EventNpc))
                continue;

            var a = obj.Position;
            var b = player.Position;
            var dx = a.X - b.X;
            var dy = a.Y - b.Y;
            var dz = a.Z - b.Z;
            var distance = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
            if (distance > 30f)
                continue;

            sb.AppendLine($"· {obj.Name.TextValue}｜{obj.ObjectKind}｜ID={obj.BaseId}｜{distance:0.0} yalm");
            if (++count >= 12)
                break;
        }

        return count == 0 ? "（身边 30 yalm 内没有 NPC 类 actor）" : sb.ToString();
    }

    // ────────────────────────────────────────────────────────────────
    //  高级
    // ────────────────────────────────────────────────────────────────

    private void DrawAdvancedPage()
    {
        ImGui.TextWrapped("这一页只在出问题时需要动，日常使用不必碰。");

        BeginGroup("游戏兼容性", "部分游戏版本会拒绝非玩家 actor 使用人类模型，需要短路它的检查。");
        var bypass = _configuration.BypassScope;
        if (EnumCombo("类型限制短路", ref bypass, BypassOptions))
        {
            _configuration.BypassScope = bypass;
            _configuration.Save();
        }

        if (!_bypass.Available && bypass != RestrictionBypassScope.Off)
            ImGui.TextColored(new Vector4(1f, 0.7f, 0.2f, 1f), "签名未命中：游戏版本对不上，短路没生效。功能可用，但模型可能被游戏修回去。");
        else if (bypass != RestrictionBypassScope.Off)
            ImGui.TextColored(new Vector4(1f, 0.7f, 0.2f, 1f), "这是一处全局检查，开启期间对所有 actor 放行，关掉功能即恢复。");
        EndGroup();

        BeginGroup("目标范围", "决定插件会动哪些 actor。");
        var mounts = _configuration.IncludeFollowMounts;
        if (ImGui.Checkbox("处理跟宠坐骑", ref mounts))
        {
            _configuration.IncludeFollowMounts = mounts;
            _configuration.Save();
            _service.RequestReapply("切换跟宠坐骑处理");
        }

        ImGui.TextDisabled("跟宠坐骑只做拟人化，不做视觉修正与表情——坐着时把它压到地下只会碍事。");

        var forceFollow = _configuration.ForceFollowBehavior;
        if (ImGui.Checkbox("让宠物保持「跟随」行为", ref forceFollow))
        {
            _configuration.ForceFollowBehavior = forceFollow;
            _configuration.Save();
        }

        CurrentValue("跟宠坐骑", _configuration.IncludeFollowMounts ? "已开启" : "已关闭");
        EndGroup();

        BeginGroup("稳定性", "换区、重新召唤、模型被游戏改回时的重试策略。");
        var autoReapply = _configuration.AutoReapply;
        if (ImGui.Checkbox("换区 / 重新召唤后自动重新施加", ref autoReapply))
        {
            _configuration.AutoReapply = autoReapply;
            _configuration.Save();
        }

        var interval = _configuration.ReapplyIntervalFrames;
        ImGui.SetNextItemWidth(220);
        if (ImGui.SliderInt("巡检间隔（帧）", ref interval, 5, 300))
        {
            _configuration.ReapplyIntervalFrames = interval;
            _configuration.Save();
        }

        var cooldown = _configuration.ReapplyCooldownSeconds;
        ImGui.SetNextItemWidth(220);
        if (ImGui.SliderInt("重施冷却（秒）", ref cooldown, 0, 30))
        {
            _configuration.ReapplyCooldownSeconds = cooldown;
            _configuration.Save();
        }

        var failures = _configuration.FailureLimit;
        ImGui.SetNextItemWidth(220);
        if (ImGui.SliderInt("连续失败几次后自动关闭", ref failures, 1, 10))
        {
            _configuration.FailureLimit = failures;
            _configuration.Save();
        }
        EndGroup();

        BeginGroup("反馈与诊断");
        var chat = _configuration.ChatFeedback;
        if (ImGui.Checkbox("输出到聊天框", ref chat))
        {
            _configuration.ChatFeedback = chat;
            _configuration.Save();
        }

        var verbose = _configuration.VerboseLog;
        if (ImGui.Checkbox("详细日志（/xllog）", ref verbose))
        {
            _configuration.VerboseLog = verbose;
            _configuration.Save();
        }

        if (ImGui.Button("输出自检报告到聊天框"))
            Plugin.Chat.Print("[MinionHumanizer] 自检：\n" + _service.BuildDiagnostics(_puppet));
        EndGroup();

        ImGui.Spacing();
        ImGui.TextWrapped("首次使用建议：先在安全区、宠物处于跟随状态时打开开关，观察 10 秒无异常再离开安全区。");
    }

    // ────────────────────────────────────────────────────────────────
    //  工具
    // ────────────────────────────────────────────────────────────────

    private static unsafe void CaptureFromPlayer()
    {
        var player = Plugin.ObjectTable.LocalPlayer;
        if (player is null || player.Address == nint.Zero)
        {
            Plugin.Chat.PrintError("[MinionHumanizer] 读不到本地玩家，无法捕获。");
            return;
        }

        var character = (Character*)player.Address;
        Plugin.Chat.Print("[MinionHumanizer] 已捕获当前外观（记得把「身体」与「装备」都切到「自定义快照」才会生效）。");

        var configuration = Plugin.PluginInterface.GetPluginConfig() as Configuration;
        if (configuration is null)
            return;

        configuration.CapturedCustomize = Persona.CaptureCustomize(character);
        configuration.CapturedEquipment = Persona.CaptureEquipment(character);
        configuration.Save();
    }

    private static List<EmoteEntry> LoadEmotes()
    {
        var result = new List<EmoteEntry>();
        var sheet  = Plugin.Data.GetExcelSheet<Emote>();
        if (sheet is null)
            return result;

        foreach (var emote in sheet)
        {
            var timeline = emote.ActionTimeline.Count > 0 ? emote.ActionTimeline[0].RowId : 0;
            if (timeline == 0 || timeline > ushort.MaxValue)
                continue;

            var name = emote.Name.ExtractText();
            if (string.IsNullOrWhiteSpace(name))
                continue;

            result.Add(new EmoteEntry(emote.RowId, name, (ushort)timeline));
        }

        return result;
    }

    private static readonly IReadOnlyList<(CustomizeSource Value, string Label)> CustomizeOptions =
    [
        (CustomizeSource.CopyLocalPlayer, "照抄我自己"),
        (CustomizeSource.Captured,        "自定义快照"),
        (CustomizeSource.Default,         "内置默认（平原人族男性）"),
    ];

    private static readonly IReadOnlyList<(EquipmentSource Value, string Label)> EquipmentOptions =
    [
        (EquipmentSource.CopyLocalPlayer, "照抄我自己"),
        (EquipmentSource.Naked,           "裸装"),
        (EquipmentSource.Captured,        "自定义快照"),
    ];

    private static readonly IReadOnlyList<(RestrictionBypassScope Value, string Label)> BypassOptions =
    [
        (RestrictionBypassScope.WhileActive, "开启期间一直短路（推荐）"),
        (RestrictionBypassScope.InGPoseOnly, "只在 GPose 中短路"),
        (RestrictionBypassScope.Off,         "不短路"),
    ];

    private static readonly IReadOnlyList<(IdleMode Value, string Label)> IdleOptions =
    [
        (IdleMode.Off,       "不改（用它自己的）"),
        (IdleMode.Always,    "始终用这个待机"),
        (IdleMode.WhenStill, "只在静止时用（推荐）"),
    ];

    private static bool EnumCombo<TEnum>(string label, ref TEnum value, IReadOnlyList<(TEnum Value, string Label)> options)
        where TEnum : struct, Enum
    {
        var labels  = new string[options.Count];
        var current = 0;

        for (var i = 0; i < options.Count; ++i)
        {
            labels[i] = options[i].Label;
            if (EqualityComparer<TEnum>.Default.Equals(options[i].Value, value))
                current = i;
        }

        ImGui.SetNextItemWidth(320);
        if (!ImGui.Combo(label, ref current, labels, labels.Length))
            return false;

        value = options[current].Value;
        return true;
    }
}