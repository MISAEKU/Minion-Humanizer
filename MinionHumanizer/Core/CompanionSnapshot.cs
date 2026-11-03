using System;
using FFXIVClientStructs.FFXIV.Client.Game.Character;

namespace MinionHumanizer.Core;

/// <summary>
/// 动手之前先把这只宠物"原样"抄一份下来，之后无论还原、还是校验是否被游戏重置，
/// 都以这份快照为准。快照是纯托管字节，不持有任何原生指针。
/// </summary>
public sealed class CompanionSnapshot
{
    // ── 模型容器 ──
    public int ModelCharaId      { get; set; }
    public int ModelSkeletonId   { get; set; }
    public int ModelCharaId2     { get; set; }
    public int ModelSkeletonId2  { get; set; }

    // ── 生命期校验 ──
    public uint  EntityId  { get; set; }
    public ulong AccountId { get; set; }
    public ulong ContentId { get; set; }

    // ── 武器（还原时一并写回，避免"还原后多了把武器"）──
    public ulong MainHand { get; set; }
    public ulong OffHand { get; set; }

    // ── 外观 ──
    public byte[] Customize { get; set; } = new byte[Persona.CustomizeLength];
    public byte[] Equipment { get; set; } = new byte[Persona.EquipmentLength];
    public byte   IsHatHidden     { get; set; }
    public byte   IsWeaponHidden  { get; set; }
    public byte   IsVisorToggled  { get; set; }
    public byte   VieraEarsHidden { get; set; }

    public static unsafe CompanionSnapshot Capture(Character* character, uint entityId)
    {
        var snapshot = new CompanionSnapshot
        {
            ModelCharaId     = character->ModelContainer.ModelCharaId,
            ModelSkeletonId  = character->ModelContainer.ModelSkeletonId,
            ModelCharaId2    = character->ModelContainer.ModelCharaId_2,
            ModelSkeletonId2 = character->ModelContainer.ModelSkeletonId_2,
            EntityId         = entityId,
            AccountId        = character->AccountId,
            ContentId        = character->ContentId,
            MainHand         = ModelWriter.ReadWeapon(character, DrawDataContainer.WeaponSlot.MainHand),
            OffHand          = ModelWriter.ReadWeapon(character, DrawDataContainer.WeaponSlot.OffHand),
            IsHatHidden      = (byte)(character->DrawData.IsHatHidden ? 1 : 0),
            IsWeaponHidden   = (byte)(character->DrawData.IsWeaponHidden ? 1 : 0),
            IsVisorToggled   = (byte)(character->DrawData.IsVisorToggled ? 1 : 0),
            VieraEarsHidden  = (byte)(character->DrawData.VieraEarsHidden ? 1 : 0),
            Customize        = Persona.CaptureCustomize(character),
            Equipment        = Persona.CaptureEquipment(character),
        };

        return snapshot;
    }

    /// <summary>把快照写回游戏对象。只写我们当初改过的那几样，别的一律不动。</summary>
    public unsafe void RestoreTo(Character* character)
    {
        character->ModelContainer.ModelCharaId      = ModelCharaId;
        character->ModelContainer.ModelSkeletonId   = ModelSkeletonId;
        character->ModelContainer.ModelCharaId_2    = ModelCharaId2;
        character->ModelContainer.ModelSkeletonId_2 = ModelSkeletonId2;

        ModelWriter.WriteCustomize(character, Customize);
        ModelWriter.WriteEquipment(character, Equipment);

        ModelWriter.WriteWeapon(character, DrawDataContainer.WeaponSlot.MainHand, MainHand);
        ModelWriter.WriteWeapon(character, DrawDataContainer.WeaponSlot.OffHand, OffHand);

        character->DrawData.IsHatHidden     = IsHatHidden != 0;
        character->DrawData.IsWeaponHidden  = IsWeaponHidden != 0;
        character->DrawData.IsVisorToggled  = IsVisorToggled != 0;
        character->DrawData.VieraEarsHidden = VieraEarsHidden != 0;
    }

    /// <summary>这只宠物还是当初快照的那一只吗？（换区重召唤后 EntityId 会变）</summary>
    public bool MatchesEntityId(uint entityId)
        => entityId == EntityId;

    public byte[] CloneCustomize() => (byte[])Customize.Clone();
    public byte[] CloneEquipment() => (byte[])Equipment.Clone();
}
