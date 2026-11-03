using System;
using System.Linq;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using MinionHumanizer.Core;
using MinionHumanizer.Watchers;
using MinionHumanizer.UI;

namespace MinionHumanizer;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/minionhuman";
    private const string AliasName   = "/mhuman";

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager       CommandManager  { get; private set; } = null!;
    [PluginService] internal static IChatGui              Chat            { get; private set; } = null!;
    [PluginService] internal static IObjectTable          ObjectTable     { get; private set; } = null!;
    [PluginService] internal static IClientState          ClientState     { get; private set; } = null!;
    [PluginService] internal static ICondition            Condition       { get; private set; } = null!;
    [PluginService] internal static IPlayerState         PlayerState     { get; private set; } = null!;
    [PluginService] internal static IFramework            Framework       { get; private set; } = null!;
    [PluginService] internal static ISigScanner           SigScanner      { get; private set; } = null!;
    [PluginService] internal static IGameInteropProvider  Hooks           { get; private set; } = null!;
    [PluginService] internal static IPluginLog            Log             { get; private set; } = null!;
    [PluginService] internal static IDataManager          Data            { get; private set; } = null!;

    // 故意不叫 Configuration：避免和同名的类型产生解析歧义。
    public Configuration Config { get; }

    private readonly WindowSystem          _windows = new("MinionHumanizer");
    private readonly ConfigWindow          _configWindow;
    private readonly KindRestrictionBypass _bypass;
    private readonly GlamourerBridge       _glamourer;
    private readonly CompanionFinder       _finder;

    /// <summary>诊断文件刷新倒计时（帧）。</summary>
    private int _diagCountdown;
    private readonly HumanizerService      _service;
    private readonly PuppetController      _puppet;
    private readonly DesignWatcher _watcher;


    /// <summary>
    /// 视觉基准纪元：换区时递增，让所有目标的缩放 / 贴地基准作废并重新捕获。
    /// 用静态字段承载，是为了让每帧的 Tick 不必持有插件实例也能读到它。
    /// </summary>
    private static int _visualEpoch;

    /// <summary>当前纪元值（每帧的视觉修正据此判断基准是否过期）。</summary>
    public static int VisualEpoch => _visualEpoch;

    /// <summary>
    /// 插件版本号（界面上显示用）。以 csproj 的 &lt;Version&gt; 为准，
    /// 构建时由 GenerateAssemblyInfo 注入 AssemblyVersion，这里取其前三段。
    /// </summary>
    public static string DisplayVersion
        => typeof(Plugin).Assembly.GetName().Version is { } v
            ? $"{v.Major}.{v.Minor}.{v.Build}"
            : "unknown";

    public Plugin()
    {

        Config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Config.CaptureDefaultsIfNeeded();

        _finder = new CompanionFinder(ObjectTable, PlayerState);
        var guard = new SafetyGuard(ClientState, Condition, ObjectTable, PlayerState);

        _bypass    = new KindRestrictionBypass(Hooks, SigScanner, ClientState, Config, Log);
        _glamourer = new GlamourerBridge(PluginInterface, Data, Log);
        _service   = new HumanizerService(Config, _finder, guard, _bypass, Framework, ObjectTable, Chat, Log, _glamourer);
        _puppet    = new PuppetController(Config, _service, ObjectTable, Log);

        // ⚠️ dalamud.log 有 100MB 上限，被别的插件刷满后插件日志就写不进去了
        // （实测被 VFXEditor 刷到 104MB 停写，排查链路直接断）。
        // 所以自己写一份诊断文件，加载后延迟几秒落一次，之后每次巡检也刷新。
        _diagCountdown = 240;   // 加载后约 4 秒写第一份

        // 自检报告要显示"基准 × 倍率 = 实际绘制"，基准只有姿态控制器手里有。
        _service.ScaleBaselineProbe = _puppet.GetDrawScaleBaseline;

        // 重绘会打断正在播放的动作：施加成功后自动重播一次。
        _service.Applied      += _puppet.ReplayIfActive;

        // 重绘换掉了绘制对象 →旧的缩放/贴地基准已失效，丢弃让下一帧重新捕获。
        _service.Redrawn      += _puppet.ForgetVisualState;

        // 还原期间不丢弃视觉基准（否则还原 → 重施会累积出一层偏移）。
        _service.BeforeRestore += _puppet.BeginRestore;
        _service.AfterRestore  += _puppet.EndRestore;

        // 监视 Glamourer 设计文件：你在 Glamourer 里改完保存 → 自动重新施加。
        _watcher = new DesignWatcher(Config, _service, _glamourer, Log);
        _service.Untracked    += _puppet.Forget;

        _configWindow = new ConfigWindow(Config, _service, _bypass, _glamourer, _puppet);
        _windows.AddWindow(_configWindow);

        Framework.Update                       += OnFrameworkUpdate;
        PluginInterface.UiBuilder.Draw         += _windows.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfig;
        PluginInterface.UiBuilder.OpenMainUi   += ToggleConfig;

        // 换区后主动进入密集巡检，让拟人化自动恢复而不是等默认巡检间隔。
        ClientState.TerritoryChanged           += OnTerritoryChanged;

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "宠物拟人。参数：on / off / status / diag / gtrace / apply / revert / resnapshot / emote <名|ID> [pet|bird] / stop / config，不带参数则切换开关。",
            ShowInHelp  = true,
        });

        CommandManager.AddHandler(AliasName, new CommandInfo(OnCommand)
        {
            HelpMessage = "/minionhuman 的简写。",
            ShowInHelp  = false,
        });

        Log.Information("[MinionHumanizer] 已加载。");
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        // 先让主服务巡检/施加，再做每帧视觉修正（游戏更新之后、渲染之前）。
        _service.Tick(framework);
        _puppet.Tick();
        _watcher.Tick();

        // 贴地追踪跑满 → 立即落盘（用独立文件，避免被定时诊断覆盖）。
        if (_puppet.GroundTraceReady)
        {
            var trace = _puppet.TakeGroundTrace();
            var ok    = Core.DiagFile.WriteTo("ground-trace.txt",
                "说明：'上帧写入留存差'≈0 表示我们写的值活着；明显非 0 表示被游戏冲掉了。\n"
              + "      '帧首下沉' 是这一帧开始时实际的下沉量，应约等于配置的贴地补偿。\n\n"
              + trace);
            Report(ok ? $"贴地追踪已写入：{Core.DiagFile.SiblingPath("ground-trace.txt")}"
                      : "贴地追踪写入失败（见日志）。");
        }

        // 定期把诊断落到插件自己的文件（见 DiagFile 的注释：dalamud.log 满了就写不进去）。
        // 240 帧 ≈ 4 秒写第一份，之后每 1800 帧（≈30 秒）刷新一次。
        if (--_diagCountdown > 0)
            return;

        _diagCountdown = 1800;
        try { WriteDiag(); } catch (Exception ex) { Log.Error(ex, "[MinionHumanizer] 写诊断失败。"); }
    }

    private void OnTerritoryChanged(uint territory)
    {
        // 换区：绘制对象全部换新，所有缩放 / 贴地基准一律作废，下一帧重新捕获。
        // 不清的话会拿旧基准去乘新对象的尺寸，体型就会随换区越来越离谱。
        ++_visualEpoch;
        _service.OnTerritoryChanged();
    }

    public void Dispose()
    {
        Framework.Update                       -= OnFrameworkUpdate;
        PluginInterface.UiBuilder.Draw         -= _windows.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfig;
        PluginInterface.UiBuilder.OpenMainUi   -= ToggleConfig;
        ClientState.TerritoryChanged           -= OnTerritoryChanged;

        CommandManager.RemoveHandler(CommandName);
        CommandManager.RemoveHandler(AliasName);

        // 卸载前把每一只被改过的宠物写回原样。
        // 这一步由 HumanizerService.Dispose() 同步完成 —— 插件卸载后不会再有下一帧，
        // 所以不能依赖分帧重建（原因见 HumanizerService.Dispose 里的注释）。
        if (_service.TrackedCount > 0)
            Chat.Print("[MinionHumanizer] 正在把宠物还原。");

        _service.Applied   -= _puppet.ReplayIfActive;
        _service.Redrawn   -= _puppet.ForgetVisualState;
        _service.Untracked -= _puppet.Forget;
        _service.BeforeRestore -= _puppet.BeginRestore;
        _service.AfterRestore  -= _puppet.EndRestore;

        // 顺序很重要：
        //   ① 先 service.Dispose() —— 它把模型/装备同步写回原值并重建绘制对象。
        //   ② 再 puppet.Dispose() —— 重建后绘制对象是新指针，此刻把位置/缩放还原
        //      才是对"最终那个"对象生效（反过来的话会被重建覆盖，白做）。
        _service.Dispose();
        _puppet.Dispose();
        _watcher.Dispose();
        _bypass.Dispose();
        _glamourer.Dispose();
        _windows.RemoveAllWindows();

        Log.Information("[MinionHumanizer] 已卸载。");
    }

    private void ToggleConfig() => _configWindow.Toggle();

    private void OnCommand(string command, string args)
    {
        var parts  = args.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var action = parts.Length > 0 ? parts[0].ToLowerInvariant() : string.Empty;
        var rest   = parts.Length > 1 ? string.Join(' ', parts.Skip(1)) : string.Empty;

        switch (action)
        {
            case "":
            case "toggle":
                Config.Enabled = !Config.Enabled;
                Config.Save();
                Report(Config.Enabled
                    ? "已开启：你的宠物会被变成人形。"
                    : "已关闭：正在把宠物还原。");
                break;

            case "on":
            case "enable":
                Config.Enabled = true;
                Config.Save();
                Report("已开启：你的宠物会被变成人形。");
                break;

            case "off":
            case "disable":
                Config.Enabled = false;
                Config.Save();
                Report("已关闭：正在把宠物还原。");
                break;

            case "apply":
                Config.Enabled = true;
                Config.Save();
                _service.RequestReapply("命令 apply");
                Report("已请求施加。宠物会在下一次巡检时生效（通常 1 秒内）。");
                break;

            case "revert":
            case "restore":
                Config.Enabled = false;
                Config.Save();
                _service.RestoreAll("手动执行 revert");
                Report("已请求还原。");
                break;

            case "emote":
                if (rest.Length == 0)
                {
                    Report("用法：/mhuman emote <表情名或动作ID> [pet|bird]\n"
                          + "  pet = 播给宠物（默认），bird = 播给陆行鸟搭档");
                    break;
                }

                PlayEmoteByNameOrId(rest);
                break;

            case "stop":
                _puppet.StopEmote();
                Report(_puppet.LastStatus);
                break;

            case "resnapshot":
                {
                    var n = _service.RefreshSnapshots();
                    Report($"已重建 {n} 个目标的原样基线。"
                         + "若宠物当前是人形，请先 /mhuman off 让它变回原形，再执行本命令，最后 /mhuman on。");
                    break;
                }

            case "gtrace":
            case "groundtrace":
                // 贴地逐帧追踪：跑 5 秒（其间请**正常走动**），自动落盘。
                // 用来回答"我们的补偿到底有没有被游戏冲掉"——静止对、移动浮只能靠这个定位。
                _puppet.StartGroundTrace(300);
                Report("贴地追踪已开始（300 帧 ≈ 5 秒）。请在这段时间里正常走动，走完自动写入 ground-trace.txt。");
                break;

            case "status":
                Report(BuildStatus());
                break;

            case "diag":
            case "selfcheck":
                Report(_service.BuildDiagnostics(_puppet));
                Report(WriteDiag() ? $"诊断已写入：{Core.DiagFile.Path}" : "诊断文件写入失败（见日志）");
                break;

            case "config":
            case "ui":
                ToggleConfig();
                break;

            default:
                Report("用法：/minionhuman [on|off|status|apply|revert|emote|stop|config]");
                break;
        }
    }

    private void PlayEmoteByNameOrId(string input)
    {
        // 末尾可选目标：pet（默认）/ bird（陆行鸟搭档）
        var target    = TargetKind.Minion;
        var separator = input.LastIndexOf(' ');
        if (separator > 0)
        {
            var suffix = input[(separator + 1)..].Trim();
            if (suffix.Equals("bird", StringComparison.OrdinalIgnoreCase) || suffix.Equals("chocobo", StringComparison.OrdinalIgnoreCase))
            {
                target = TargetKind.ChocoboPartner;
                input  = input[..separator];
            }
            else if (suffix.Equals("pet", StringComparison.OrdinalIgnoreCase))
            {
                input = input[..separator];
            }
        }

        if (ushort.TryParse(input, out var timelineId))
        {
            _puppet.PlayEmote(timelineId, $"#{timelineId}", target);
            Report(_puppet.LastStatus);
            return;
        }

        // 按名称在 Emote 表里找（客户端语言，国服为中文名）。
        // Emote.ActionTimeline 是一组 RowRef，取第一个（常规地面动作）。
        var emote = Data.GetExcelSheet<Lumina.Excel.Sheets.Emote>()?
            .FirstOrDefault(e => FirstTimeline(e) != 0
                              && string.Equals(e.Name.ExtractText(), input, StringComparison.OrdinalIgnoreCase));

        if (emote is null)
        {
            Report($"没找到表情「{input}」。可以打开设置窗口的表情列表按名搜索。");
            return;
        }

        var id = (ushort)FirstTimeline(emote.Value);
        _puppet.PlayEmote(id, emote.Value.Name.ExtractText(), target);
        Report(_puppet.LastStatus);
    }

    private static uint FirstTimeline(Lumina.Excel.Sheets.Emote emote)
        => emote.ActionTimeline.Count > 0 ? emote.ActionTimeline[0].RowId : 0;

    private string BuildStatus()
    {
        var bypassText = Config.BypassScope == RestrictionBypassScope.Off
            ? "关闭"
            : Config.BypassScope.ToString();

        if (Config.BypassScope != RestrictionBypassScope.Off && !_bypass.Available)
            bypassText += "（签名未命中，实际未生效）";

        var designText = Config.UseGlamourerDesign
            ? (_glamourer.IsGlamourerRunning
                ? $"启用（{Config.GlamourerDesignName}）"
                : "启用（Glamourer 未运行，会回退到照抄自己）")
            : "未启用";

        return _service.LastStatus
             + $"\n功能：{(Config.Enabled ? "开启" : "关闭")}"
             + $"\n正在跟踪：{_service.TrackedCount} 只宠物"
             + $"\nGlamourer 设计：{designText}"
             + $"\n当前动作：{(string.IsNullOrEmpty(_puppet.CurrentEmoteName) ? "无" : _puppet.CurrentEmoteName)}"
             + $"\n类型限制短路：{bypassText}";
    }

    /// <summary>
    /// 写一份诊断到插件自己的文件：识别情况 + 跟踪状态。
    /// 返回是否写成功。
    /// </summary>
    private bool WriteDiag()
    {
        try
        {
            var body = new System.Text.StringBuilder();
            body.AppendLine("【识别】");
            body.AppendLine(_finder.BuildRecognitionReport(Config));
            body.AppendLine();
            body.AppendLine("【跟踪 / 施加状态】");
            body.AppendLine(_service.BuildDiagnostics(_puppet));

            var ok = Core.DiagFile.Write(body.ToString());
            Log.Information("[MinionHumanizer] 诊断已写入 {0}（成功={1}）", Core.DiagFile.Path, ok);
            return ok;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[MinionHumanizer] 生成诊断失败。");
            return false;
        }
    }

    private static void Report(string message) => Chat.Print($"[MinionHumanizer] {message.Replace("\n", "\n                 ")}");
}
