using System;
using FFXIVClientStructs.FFXIV.Client.Game.Character;

namespace MinionHumanizer;

public enum CustomizeSource
{
    /// <summary>照抄本地玩家当前的身体。永远合法（游戏此刻正在渲染它）。推荐。</summary>
    CopyLocalPlayer,

    /// <summary>用配置里保存下来的那一份快照。</summary>
    Captured,

    /// <summary>内置的"平原人族男性"默认值。够用但不好看，且是硬编码数字。</summary>
    Default,
}

public enum EquipmentSource
{
    /// <summary>裸装（全部槽位 Id=0）。最安全，永远是合法值。</summary>
    Naked,

    /// <summary>照抄本地玩家当前穿的。推荐，换装最直观。</summary>
    CopyLocalPlayer,

    /// <summary>用配置里保存下来的那一份快照。</summary>
    Captured,
}

/// <summary>
/// 一只宠物要变成的"人形皮肤"。只存原始的字节，不做任何解析——
/// 这样写回游戏时就是原样字节，不经过任何会引入偏差的中间层。
/// Glamourer 设计来源时，完整载荷由 <see cref="Core.DesignAppearance"/> 承载。
/// </summary>
public static class Persona
{
    /// <summary>CustomizeData 的字节数（FFXIVClientStructs: Size = 0x1A）。</summary>
    public const int CustomizeLength = 0x1A; // 26

    public const int EquipmentSlots = 10; // Head, Body, Hands, Legs, Feet, Ears, Neck, Wrists, RFinger, LFinger

    /// <summary>EquipmentModelId 的字节数。</summary>
    public const int EquipmentSlotSize = 8;

    public const int EquipmentLength = EquipmentSlots * EquipmentSlotSize; // 80

    /// <summary>
    /// 决定"骨架"的几个 customize 字节：0 Race、1 Sex、2 BodyType、4 Tribe。
    /// 这几个值变了就必须重建绘制对象（骨架文件不同）；
    /// 只是换发型、换装备的话，就地刷新外观就够了，不必重建。
    /// </summary>
    public static int StructureKey(byte[] customize)
    {
        if (customize is null || customize.Length < 5)
            return 0;

        return customize[0] | (customize[1] << 8) | (customize[2] << 16) | (customize[4] << 24);
    }

    /// <summary>
    /// 内置兜底：一个标准平原人族男性。
    /// 布局见 FFXIVClientStructs.CustomizeData：
    /// 0 Race, 1 Sex, 2 BodyType, 3 Height, 4 Tribe, 5 Face, 6 Hairstyle, 7 (Highlights&lt;&lt;7),
    /// 8 SkinColor, 9 EyeColorRight, 10 HairColor, 11 HighlightsColor, 12 facial features,
    /// 13 TattooColor, 14 Eyebrows, 15 EyeColorLeft, 16 (EyeShape | SmallIris&lt;&lt;7),
    /// 17 Nose, 18 Jaw, 19 (Mouth | Lipstick&lt;&lt;7), 20 LipColorFurPattern, 21 MuscleMass,
    /// 22 TailShape, 23 BustSize, 24 (FacePaint | FacePaintReversed&lt;&lt;7), 25 FacePaintColor
    /// </summary>
    public static byte[] DefaultCustomize()
    {
        var b = new byte[CustomizeLength];
        b[0]  = 1;  // Race     = Hyur
        b[1]  = 0;  // Sex      = 男
        b[2]  = 1;  // BodyType = 普通
        b[3]  = 50; // Height   = 中位
        b[4]  = 1;  // Tribe    = Midlander
        b[5]  = 1;  // Face
        b[6]  = 1;  // Hairstyle
        b[8]  = 1;  // SkinColor
        b[9]  = 1;  // EyeColorRight
        b[10] = 1;  // HairColor
        b[13] = 1;  // TattooColor
        b[14] = 1;  // Eyebrows
        b[15] = 1;  // EyeColorLeft
        b[16] = 1;  // EyeShape
        b[17] = 1;  // Nose
        b[18] = 1;  // Jaw
        b[19] = 1;  // Mouth
        return b;
    }

    /// <summary>把本地玩家当前的 CustomizeData 抄出来。</summary>
    public static unsafe byte[] CaptureCustomize(Character* player)
    {
        var result = new byte[CustomizeLength];
        var source = player->DrawData.CustomizeData.Data;
        var count = Math.Min(source.Length, CustomizeLength);
        for (var i = 0; i < count; ++i)
            result[i] = source[i];

        return result;
    }

    /// <summary>把本地玩家当前的 10 个装备槽抄出来。</summary>
    public static unsafe byte[] CaptureEquipment(Character* player)
    {
        var result = new byte[EquipmentLength];
        var source = player->DrawData.EquipmentModelIds;
        var count = Math.Min(source.Length, EquipmentSlots);
        for (var i = 0; i < count; ++i)
        {
            var slot = source[i];
            var offset = i * EquipmentSlotSize;
            result[offset + 0] = (byte)(slot.Id & 0xFF);
            result[offset + 1] = (byte)(slot.Id >> 8);
            result[offset + 2] = slot.Variant;
            result[offset + 3] = slot.Stain0;
            result[offset + 4] = slot.Stain1;
        }

        return result;
    }

    /// <summary>
    /// 把一次解析出来的外观打包成可写回游戏的扁平数组（非 Glamourer 来源的旧路径）。
    /// Glamourer 设计来源时走 <see cref="Core.GlamourerBridge.ResolveDesign"/>。
    /// </summary>
    public static unsafe (byte[] Customize, byte[] Equipment) Resolve(Configuration config, Character* player)
    {
        var customize = config.CustomizeSource switch
        {
            CustomizeSource.CopyLocalPlayer => CaptureCustomize(player),
            CustomizeSource.Captured        => Normalize(config.CapturedCustomize, CustomizeLength),
            _                               => Normalize(config.CapturedCustomize, CustomizeLength, DefaultCustomize()),
        };

        var equipment = config.EquipmentSource switch
        {
            EquipmentSource.Naked           => new byte[EquipmentLength],
            EquipmentSource.CopyLocalPlayer => CaptureEquipment(player),
            _                               => Normalize(config.CapturedEquipment, EquipmentLength),
        };

        return (customize, equipment);
    }

    private static byte[] Normalize(byte[]? input, int length, byte[]? fallback = null)
    {
        var result = new byte[length];
        if (input is null || input.Length == 0)
        {
            if (fallback is not null && fallback.Length == length)
                Buffer.BlockCopy(fallback, 0, result, 0, length);

            return result;
        }

        Buffer.BlockCopy(input, 0, result, 0, Math.Min(input.Length, length));
        return result;
    }
}
