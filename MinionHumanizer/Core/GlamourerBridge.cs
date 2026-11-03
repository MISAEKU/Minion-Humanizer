using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using Newtonsoft.Json.Linq;

namespace MinionHumanizer.Core;

/// <summary>
/// 一份「可以直接写进游戏」的外观数据。比 Persona 的裸字节多了武器与元开关，
/// 用于承载 Glamourer 设计的完整内容。
/// </summary>
public sealed class DesignAppearance
{
    public byte[] Customize = new byte[Persona.CustomizeLength];
    public byte[] Equipment = new byte[Persona.EquipmentLength];

    /// <summary>主手/副手武器（WeaponModelId.Value 布局）。null 表示不改武器。</summary>
    public ulong? MainHand;
    public ulong? OffHand;

    /// <summary>元开关。null 表示「不指定，保持现状」。</summary>
    public bool? HatVisible;
    public bool? WeaponVisible;
    public bool? VisorToggled;
    public bool? VieraEarsVisible;
}

/// <summary>
/// Glamourer 集成层：<b>只读</b>。
///
/// 走 Glamourer 的 IPC 拉「设计列表 + 设计内容」，在本插件里把设计解析成裸字节后
/// <b>由我们自己写入</b>宠物——绝不调用 ApplyDesign / 不进 Glamourer 的状态系统。
/// 这是当初 "Turn Human" 被删的两大事故根源之一（污染同步工具的订阅状态），
/// 这条路从结构上就绕开了它：Glamourer 只当我们是「一个查资料的」。
///
/// ⚠️ 设计内容<b>不</b>走 IPC：
///   · `GetDesignJObject` 要把 Newtonsoft 的 JObject 过 Dalamud IPC，实测失败；
///   · `GetDesignBase64` 返回的也不是 JSON，而是 Penumbra 的压缩容器
///     （`CompressionFunctions.ToCompressedBase64(json, 6, Uncompressed)`，首字节即版本号）。
/// 而 Glamourer 本身就把每个设计明文存成
///   `%APPDATA%\XIVLauncherCN\pluginConfigs\Glamourer\designs\{设计ID}.json`
/// 文件名就是设计 ID，内容就是设计 JSON（UTF-8 BOM）。直接读它：
/// 最可靠、不依赖 Glamourer 是否在运行、拿到的永远是最新保存的内容。
/// IPC 只用来列设计清单（失败时退回扫磁盘）。
/// </summary>
public sealed class GlamourerBridge : IDisposable
{
    // Glamourer.Api.IpcSubscribers 里定义的 IPC 名（已对着 Glamourer 1.7.1.4 核实）。
    private const string DesignListIpc   = "Glamourer.GetDesignList.V2";
    private const string DesignJObjectIpc = "Glamourer.GetDesignJObject";

    // ── CustomizeIndex 名 → (字节偏移, 位掩码) ──
    // 与 Penumbra.GameData 的 CustomizeIndex.ToByteAndMask() 一一对应。
    private static readonly Dictionary<string, (int Byte, byte Mask)> CustomizeMap = new()
    {
        ["Race"]              = (0, 0xFF),
        ["Gender"]            = (1, 0xFF),
        ["BodyType"]          = (2, 0xFF),
        ["Height"]            = (3, 0xFF),
        ["Clan"]              = (4, 0xFF),
        ["Face"]              = (5, 0xFF),
        ["Hairstyle"]         = (6, 0xFF),
        ["Highlights"]        = (7, 0x80),
        ["SkinColor"]         = (8, 0xFF),
        ["EyeColorRight"]     = (9, 0xFF),
        ["HairColor"]         = (10, 0xFF),
        ["HighlightsColor"]   = (11, 0xFF),
        ["FacialFeature1"]    = (12, 0x01),
        ["FacialFeature2"]    = (12, 0x02),
        ["FacialFeature3"]    = (12, 0x04),
        ["FacialFeature4"]    = (12, 0x08),
        ["FacialFeature5"]    = (12, 0x10),
        ["FacialFeature6"]    = (12, 0x20),
        ["FacialFeature7"]    = (12, 0x40),
        ["LegacyTattoo"]      = (12, 0x80),
        ["TattooColor"]       = (13, 0xFF),
        ["Eyebrows"]          = (14, 0xFF),
        ["EyeColorLeft"]      = (15, 0xFF),
        ["EyeShape"]          = (16, 0x7F),
        ["SmallIris"]         = (16, 0x80),
        ["Nose"]              = (17, 0xFF),
        ["Jaw"]               = (18, 0xFF),
        ["Mouth"]             = (19, 0x7F),
        ["Lipstick"]          = (19, 0x80),
        ["LipColor"]          = (20, 0xFF),
        ["MuscleMass"]        = (21, 0xFF),
        ["TailShape"]         = (22, 0xFF),
        ["BustSize"]          = (23, 0xFF),
        ["FacePaint"]         = (24, 0x7F),
        ["FacePaintReversed"] = (24, 0x80),
        ["FacePaintColor"]    = (25, 0xFF),
    };

    // ── 设计 JSON 的装备键 → 我们的 10 槽下标（与 DrawDataContainer.EquipmentSlot 一致）──
    private static readonly Dictionary<string, int> EquipSlotMap = new()
    {
        ["Head"]    = 0,
        ["Body"]    = 1,
        ["Hands"]   = 2,
        ["Legs"]    = 3,
        ["Feet"]    = 4,
        ["Ears"]    = 5,
        ["Neck"]    = 6,
        ["Wrists"]  = 7,
        ["RFinger"] = 8,
        ["LFinger"] = 9,
    };

    private readonly IDataManager           _data;
    private readonly IPluginLog             _log;
    private readonly IDalamudPluginInterface _pi;

    private readonly object _cacheLock = new();
    private List<(Guid Id, string Name)> _designCache = [];
    private DateTime _cacheTime;
    private bool?   _runningCache;
    private DateTime _runningCacheTime;
    private string?  _designDir;

    // ── 设计解析缓存（避免主线程反复读盘造成 HITCH）──
    private Guid                 _cachedDesignId;
    private DesignAppearance?    _cachedDesign;
    private DateTime             _cachedStamp;

    public GlamourerBridge(IDalamudPluginInterface pi, IDataManager data, IPluginLog log)
    {
        _pi   = pi;
        _data = data;
        _log  = log;
    }

    /// <summary>最近一次失败的直接原因，给 UI 显示（比翻日志方便）。</summary>
    public string LastError { get; private set; } = string.Empty;

    /// <summary>
    /// Glamourer 的设计目录。国服是 XIVLauncherCN，国际服是 XIVLauncher，两个都探测。
    /// 也允许用户在设置里手动指定（找不到时 UI 会给输入框）。
    /// </summary>
    public string DesignDirectory
    {
        get
        {
            if (_designDir is not null)
                return _designDir;

            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            foreach (var launcher in new[] { "XIVLauncherCN", "XIVLauncher" })
            {
                var candidate = Path.Combine(appData, launcher, "pluginConfigs", "Glamourer", "designs");
                if (Directory.Exists(candidate))
                    return _designDir = candidate;
            }

            return _designDir = string.Empty;
        }
        set => _designDir = value ?? string.Empty;
    }

    /// <summary>Glamourer 的配置文件目录（用于回退扫描设计列表）。</summary>
    private static IEnumerable<string> ConfigRoots()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        foreach (var launcher in new[] { "XIVLauncherCN", "XIVLauncher" })
            yield return Path.Combine(appData, launcher, "pluginConfigs", "Glamourer");
    }

    // ────────────────────────────────────────────────────────────────
    //  IPC（全部只读）
    // ────────────────────────────────────────────────────────────────

    /// <summary>Glamourer 是否在运行且 IPC 可用（结果缓存 5 秒，UI 可以放心每帧调用）。</summary>
    public bool IsGlamourerRunning
    {
        get
        {
            if (_runningCache.HasValue && (DateTime.UtcNow - _runningCacheTime).TotalSeconds < 5)
                return _runningCache.Value;

            bool running;
            try
            {
                _pi.GetIpcSubscriber<Dictionary<Guid, string>>(DesignListIpc).InvokeFunc();
                running = true;
                LastError = string.Empty;
            }
            catch (Exception ex)
            {
                running  = false;
                LastError = $"Glamourer IPC 不可用：{ex.GetBaseException().Message}";
            }

            _runningCache     = running;
            _runningCacheTime = DateTime.UtcNow;
            return running;
        }
    }

    /// <summary>取设计列表（缓存 10 秒，避免 UI 每帧打 IPC）。</summary>
    public List<(Guid Id, string Name)> GetDesigns()
    {
        lock (_cacheLock)
        {
            if (_designCache.Count > 0 && (DateTime.UtcNow - _cacheTime).TotalSeconds < 10)
                return _designCache;

            // 1) 先问 Glamourer 要清单（顺带验证它在不在运行）。
            var fromIpc = false;
            try
            {
                var raw = _pi.GetIpcSubscriber<Dictionary<Guid, string>>(DesignListIpc).InvokeFunc();
                if (raw.Count > 0)
                {
                    _designCache = raw.Select(kv => (kv.Key, kv.Value))
                                      .OrderBy(p => p.Value, StringComparer.OrdinalIgnoreCase)
                                      .ToList();
                    fromIpc = true;
                    LastError = string.Empty;
                }
            }
            catch
            {
                // Glamourer 没开——磁盘扫描照样能用。
            }

            // 2) IPC 没给出结果（或想避开 IPC 依赖）时，直接扫磁盘上的设计文件。
            if (!fromIpc)
                _designCache = ScanDesignsFromDisk();

            _cacheTime = DateTime.UtcNow;
            return _designCache;
        }
    }

    /// <summary>扫磁盘：每个设计文件的名字就是它的 GUID，Name 字段在文件里。</summary>
    private List<(Guid Id, string Name)> ScanDesignsFromDisk()
    {
        var result = new List<(Guid Id, string Name)>();
        var dir    = DesignDirectory;
        if (dir.Length == 0)
            return result;

        try
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (!Guid.TryParse(name, out var id))
                    continue;

                var display = name;
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(file));
                    if (doc.RootElement.TryGetProperty("Name", out var nameElement)
                     && nameElement.ValueKind == JsonValueKind.String)
                        display = nameElement.GetString() ?? name;
                }
                catch
                {
                    // 单个文件坏了不影响其它设计。
                }

                result.Add((id, display));
            }
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "[MinionHumanizer] 扫描 Glamourer 设计目录失败。");
        }

        result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    /// <summary>拉取并解析一份设计。失败返回 null（调用方回退到其他外观来源）。</summary>
    /// <summary>
    /// 取某个设计文件的最后写入时间（UTC）。文件不存在返回 null。
    /// 用于"在 Glamourer 里改了设计 → 插件自动应用"的文件变化检测。
    /// </summary>
    public DateTime? GetStampUtc(Guid id)
    {
        if (id == Guid.Empty)
            return null;

        var dir = DesignDirectory;
        if (dir.Length == 0)
            return null;

        try
        {
            var file = Path.Combine(dir, $"{id}.json");
            return File.Exists(file) ? File.GetLastWriteTimeUtc(file) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>强制丢弃解析缓存（确认文件已变时调用，确保不会在同一 mtime 内返回旧值）。</summary>
    public void InvalidateCache()
    {
        _cachedDesignId = Guid.Empty;
        _cachedDesign   = null;
        _cachedStamp    = default;
    }


    public DesignAppearance? ResolveDesign(Guid id)
    {
        if (id == Guid.Empty)
        {
            LastError = "尚未选择设计（请在「外观」页下拉框里点一次设计名）。";
            return null;
        }

        // ── 缓存 ──
        // 读盘 + 解析 JSON 是毫秒级操作，而施加可能连着来好几只目标；
        // 不缓存就会在主线程上卡出 HITCH（实测 91ms）。
        // 设计内容在磁盘上不会自己变，所以按"文件最后写入时间"判定是否需要重读。
        var path = DesignDirectory.Length > 0 ? Path.Combine(DesignDirectory, $"{id}.json") : null;
        if (path is not null && _cachedDesignId == id)
        {
            try
            {
                var stamp = File.GetLastWriteTimeUtc(path);
                if (_cachedStamp == stamp && _cachedDesign is not null)
                    return _cachedDesign;
            }
            catch
            {
                // 取不到时间戳就当作需要重读。
            }
        }

        var result = ResolveDesignUncached(id, path);

        _cachedDesignId = id;
        _cachedDesign   = result;
        _cachedStamp    = path is not null && File.Exists(path) ? File.GetLastWriteTimeUtc(path) : default;

        return result;
    }

    private DesignAppearance? ResolveDesignUncached(Guid id, string? path)
    {
        // 路径 1（首选）：直接读 Glamourer 写盘的设计 JSON。
        if (path is not null && File.Exists(path))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                var result = Parse(document.RootElement);
                LastError = string.Empty;
                return result;
            }
            catch (Exception ex)
            {
                LastError = $"设计文件解析失败：{ex.Message}";
                _log.Error(ex, $"[MinionHumanizer] 解析设计文件 {path} 失败。");
                return null;
            }
        }

        // 路径 2（兜底）：Glamourer 的 JObject IPC。
        try
        {
            var json = _pi.GetIpcSubscriber<Guid, Newtonsoft.Json.Linq.JObject>(DesignJObjectIpc).InvokeFunc(id);
            if (json is not null)
            {
                using var document = JsonDocument.Parse(json.ToString());
                var result = Parse(document.RootElement);
                LastError = string.Empty;
                return result;
            }

            LastError = "设计文件不存在，且 IPC 也没返回数据。";
        }
        catch (Exception ex)
        {
            LastError = $"读取设计失败：{ex.GetBaseException().Message}";
            _log.Debug(ex, "[MinionHumanizer] JObject IPC 兜底失败。");
        }

        _log.Warning("[MinionHumanizer] 拉取设计失败。");
        return null;
    }

    // ────────────────────────────────────────────────────────────────
    //  解析
    // ────────────────────────────────────────────────────────────────

    private DesignAppearance Parse(JsonElement root)
    {
        var result = new DesignAppearance();

        if (root.TryGetProperty("Customize", out var customize) && customize.ValueKind == JsonValueKind.Object)
            ParseCustomize(customize, result);

        // 防护：解析完 Race 仍然是 0 说明这份设计的身体数据不可用
        //（Race=0 是非法模型，会让重建出问题）。退回内置默认身体。
        if (result.Customize[0] == 0)
        {
            _log.Warning("[MinionHumanizer] 设计缺少有效的身体数据（Race=0），退回内置默认。");
            result.Customize = Persona.DefaultCustomize();
        }

        if (root.TryGetProperty("Equipment", out var equipment) && equipment.ValueKind == JsonValueKind.Object)
            ParseEquipment(equipment, result);

        return result;
    }

    private static void ParseCustomize(JsonElement customize, DesignAppearance result)
    {
        // 非人类设计（ModelId != 0）没法变成人，直接忽略整个段落，身体保持兜底。
        if (customize.TryGetProperty("ModelId", out var modelId) && modelId.ValueKind == JsonValueKind.Number
                                                                   && modelId.GetInt64() != 0)
            return;

        foreach (var property in customize.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Object)
                continue;

            if (!CustomizeMap.TryGetValue(property.Name, out var map))
                continue;

            if (!property.Value.TryGetProperty("Value", out var valueToken) || valueToken.ValueKind != JsonValueKind.Number)
                continue;

            var value = (byte)Math.Clamp(valueToken.GetInt32(), 0, 255);
            if (value == 0)
                continue;

            // 位掩码值：置位而不是赋值（FacialFeature1..7 / SmallIris / Lipstick 等共享同一字节）。
            if (map.Mask == 0xFF)
                result.Customize[map.Byte] = value;
            else if (value != 0)
                result.Customize[map.Byte] |= map.Mask;
        }
    }

    private void ParseEquipment(JsonElement equipment, DesignAppearance result)
    {
        foreach (var property in equipment.EnumerateObject())
        {
            switch (property.Name)
            {
                case "Hat":
                    result.HatVisible = ReadBool(property.Value, "Show");
                    continue;

                case "VieraEars":
                    result.VieraEarsVisible = ReadBool(property.Value, "Show");
                    continue;

                case "Visor":
                    result.VisorToggled = ReadBool(property.Value, "IsToggled");
                    continue;

                case "Weapon":
                    result.WeaponVisible = ReadBool(property.Value, "Show");
                    continue;

                case "MainHand":
                    result.MainHand = SafeResolve(() => ResolveWeapon(property.Value), "主手");
                    continue;

                case "OffHand":
                    result.OffHand = SafeResolve(() => ResolveWeapon(property.Value), "副手");
                    continue;
            }

            if (property.Value.ValueKind != JsonValueKind.Object || !EquipSlotMap.TryGetValue(property.Name, out var slot))
                continue;

            // 单个槽位解析失败不能连累整份设计——退化成该槽位裸装即可。
            var armor = SafeResolve(() => ResolveArmor(property.Value), property.Name);
            if (armor is null)
                continue;

            var offset = slot * Persona.EquipmentSlotSize;
            result.Equipment[offset + 0] = (byte)(armor.Value.Id & 0xFF);
            result.Equipment[offset + 1] = (byte)(armor.Value.Id >> 8);
            result.Equipment[offset + 2] = armor.Value.Variant;
            result.Equipment[offset + 3] = armor.Value.Stain0;
            result.Equipment[offset + 4] = armor.Value.Stain1;
        }
    }

    /// <summary>包一层 try/catch：任何一个槽位坏掉都不该让整份设计失败。</summary>
    private T? SafeResolve<T>(Func<T> action, string what) where T : struct
    {
        try
        {
            return action();
        }
        catch (Exception ex)
        {
            _log.Debug(ex, $"[MinionHumanizer] 设计中的 {what} 槽位解析失败，按裸装处理。");
            return null;
        }
    }

    private ulong? SafeResolve(Func<ulong?> action, string what)
    {
        try
        {
            return action();
        }
        catch (Exception ex)
        {
            _log.Debug(ex, $"[MinionHumanizer] 设计中的 {what} 槽位解析失败，忽略该槽位。");
            return null;
        }
    }

    private (ushort Id, byte Variant, byte Stain0, byte Stain1) ResolveArmor(JsonElement entry)
    {
        var itemId = StripItemId(ReadNumber(entry, "ItemId"));
        var stain  = (byte)ReadNumber(entry, "Stain");
        var stain2 = (byte)ReadNumber(entry, "Stain2");

        if (itemId is 0)
            return (0, 0, stain, stain2);

        var row = _data.GetExcelSheet<Item>()?.GetRow(itemId);
        if (row is null)
        {
            _log.Debug($"[MinionHumanizer] Item {itemId} 不在表里，槽位按裸装处理。");
            return (0, 0, stain, stain2);
        }

        // 防具：低 16 位 = 模型 set，16-23 位 = variant（Penumbra EquipItem.FromArmor 的打包方式）。
        var modelMain = row.Value.ModelMain;
        return ((ushort)(modelMain & 0xFFFF), (byte)((modelMain >> 16) & 0xFF), stain, stain2);
    }

    private ulong? ResolveWeapon(JsonElement entry)
    {
        var itemId = StripItemId(ReadNumber(entry, "ItemId"));
        if (itemId is 0)
            return 0; // 设计里明确空手

        var row = _data.GetExcelSheet<Item>()?.GetRow(itemId);
        if (row is null)
        {
            _log.Debug($"[MinionHumanizer] 武器 Item {itemId} 不在表里，忽略。");
            return null;
        }

        var stain  = (byte)ReadNumber(entry, "Stain");
        var stain2 = (byte)ReadNumber(entry, "Stain2");

        // 武器：低 16 = 模型，16-31 = 类型（SecondaryId），32-39 = variant。
        var m = row.Value.ModelMain;
        return (ulong)(m & 0xFFFF)
             | (ulong)((m >> 16) & 0xFFFF) << 16
             | (ulong)((m >> 32) & 0xFF) << 32
             | (ulong)stain << 48
             | (ulong)stain2 << 56;
    }

    private static bool? ReadBool(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    private static long ReadNumber(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
            ? value.GetInt64()
            : 0;

    /// <summary>
    /// 把设计 JSON 里的 <c>ItemId</c> 还原成真正的道具行号，无效则返回 0。
    ///
    /// 坑（都是实测踩出来的）：Glamourer 写出来的是 <b>CustomItemId</b>（Penumbra 的 ulong），
    /// 里面塞了好几种"不是道具行号"的值：
    ///   · 高位带 <c>CustomFlag</c>（1&lt;&lt;48）→ mod 自定义物品，Item 表里根本没有；
    ///   · <c>0xFFFFFF7x</c> 之类的空槽标记（例如 4294967155）；
    ///   · 超出 Item 表范围的任何值。
    /// 直接拿去 <c>GetRow</c> 就会抛 ArgumentOutOfRangeException，
    /// 一个坏槽位会让<b>整份设计作废</b>——所以这里必须先筛干净。
    /// Item 表的行号远小于 100 万，用它做上界足够安全。
    /// </summary>
    private static uint StripItemId(long raw)
    {
        var value = (ulong)raw;

        // 带 CustomFlag / BonusItemFlag → mod 物品，不是真道具。
        if (value >= CustomItemIdFlag)
            return 0;

        // 0 = 空槽；超出表范围 = 空槽标记或脏数据。
        if (value == 0 || value >= MaxItemRowId)
            return 0;

        return (uint)value;
    }

    /// <summary>Penumbra 的 CustomItemId.CustomFlag（1&lt;&lt;48）。</summary>
    private const ulong CustomItemIdFlag = 1ul << 48;

    /// <summary>Item 表行号上界（保守取值，实际最大行号远小于此）。</summary>
    private const ulong MaxItemRowId = 1_000_000;

    public void Dispose()
    {
    }
}