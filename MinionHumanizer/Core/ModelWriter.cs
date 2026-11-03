using System;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using SceneObjectType = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.ObjectType;

namespace MinionHumanizer.Core;

/// <summary>
/// 真正改内存的地方。核心逻辑就这一件事：
///
///   ModelContainer.ModelCharaId = 0        ← 这就是"变成人类"本身
///   DrawData.CustomizeData       = 26 字节 ← 长什么样
///   DrawData.EquipmentModelIds   = 80 字节 ← 穿什么
///
/// 注意这里<b>不</b>创建、<b>不</b>持有、<b>不</b>销毁任何原生对象——
/// 绘制对象（CharacterBase）自始至终由游戏拥有和回收。
/// 这是"不可能因为悬垂指针而崩"的结构性保证。
/// </summary>
public static unsafe class ModelWriter
{
    /// <summary>ModelCharaId = 0 即"人类基础模型"；具体种族/性别/部族由 CustomizeData 决定。</summary>
    public const int HumanModelCharaId = 0;

    /// <summary>ModelCharaId_2 = -1 表示"没有第二模型，用 ModelCharaId"。</summary>
    public const int NoSecondModel = -1;

    public static bool IsHumanModel(Character* character)
        => character->ModelContainer.ModelCharaId == HumanModelCharaId;

    /// <summary>
    /// 绘制对象当前的模型类型；没有绘制对象（或不是角色型）时返回 null。
    /// </summary>
    public static CharacterBase.ModelType? DrawObjectModelType(GameObject* gameObject)
    {
        var draw = gameObject->DrawObject;
        if (draw is null || draw->Object.GetObjectType() != SceneObjectType.CharacterBase)
            return null;

        return ((CharacterBase*)draw)->GetModelType();
    }

    /// <summary>
    /// 画出来的东西<b>真的</b>是人形吗。
    ///
    /// 与 <see cref="IsHumanModel"/> 的区别：后者只读内存里的 ModelCharaId，
    /// 这个看的是<b>实际绘制对象</b>。两者不一致就说明发生了"假成功"——
    /// 值写进去了，但绘制对象没换（于是屏幕上还是宠物原形，插件却以为已经拟人化）。
    /// </summary>
    public static bool IsHumanDrawObject(GameObject* gameObject)
        => DrawObjectModelType(gameObject) == CharacterBase.ModelType.Human;

    /// <summary>Human.DrawData 缓冲区大小。给足余量，绝不越界。</summary>
    private const int UpdateDrawDataBufferSize = 128;

    /// <summary>Human.DrawData 里 customize 段的长度（其后紧跟装备段）。</summary>
    private const int UpdateDrawDataCustomizeLength = 32;

    /// <summary>
    /// 就地刷新外观：<b>不</b>撤下绘制对象，直接让 Human 按新的 DrawData 重新装配件。
    ///
    /// 已经在人形、只是换了设计或装备时应该走这条路；完整重建（DisableDraw → EnableDraw）
    /// 在"已经是人形的宠物"上很容易超时失败，一旦失败就会回滚成原形。
    ///
    /// 返回 false 表示这条路走不通（不是人形、没有绘制对象、或游戏拒绝），
    /// 调用方应当退回完整重建。
    /// </summary>
    public static bool TryUpdateDrawData(Character* character, byte[] customize, byte[] equipment,
        bool? hatVisible = null, bool? weaponVisible = null, bool? visorToggled = null)
    {
        var gameObject = (GameObject*)character;
        var draw       = gameObject->DrawObject;
        if (draw is null || draw->Object.GetObjectType() != SceneObjectType.CharacterBase)
            return false;

        var characterBase = (CharacterBase*)draw;
        if (characterBase->GetModelType() != CharacterBase.ModelType.Human)
            return false;

        // 内存里的 DrawData 必须同步写，否则下一次重绘（换图、换区）会把旧外观带回来。
        if (!WriteCustomize(character, customize) || !WriteEquipment(character, equipment))
            return false;

        // ── 元开关（帽子 / 武器 / 护目镜 / 耳朵）──
        //
        // ⚠️ 必须走 DrawDataContainer 上的<b>方法</b>：它们会顺带通知绘制对象更新。
        // 直接写 IsHatHidden 这类位域属性只改内存里的标志，画面不会跟着变——
        // 表现出来就是"附加设计不对版"（该藏的帽子还戴着）。
        // 而且它们不包含在 UpdateDrawData 的缓冲区里，顺序上也要放在前面。
        var drawData = &character->DrawData;
        drawData->HideHeadgear(0, !(hatVisible ?? true));
        drawData->SetVisor(visorToggled ?? false);
        drawData->HideVieraEars(false);

        // 武器隐藏没有对应的通知式方法，保持原有的位域写法。
        drawData->IsWeaponHidden = !(weaponVisible ?? true);

        // Human.DrawData 的布局：开头是 customize（32 字节），其后紧跟 80 字节装备。
        var buffer = new byte[UpdateDrawDataBufferSize];

        for (var i = 0; i < Persona.CustomizeLength && i < UpdateDrawDataCustomizeLength; ++i)
            buffer[i] = customize[i];

        for (var i = 0; i < Persona.EquipmentLength && i < Persona.EquipmentLength; ++i)
            buffer[UpdateDrawDataCustomizeLength + i] = equipment[i];

        var human = (Human*)characterBase;

        fixed (byte* pointer = buffer)
            return human->UpdateDrawData((Human.DrawData*)pointer, false);
    }

    /// <summary>
    /// 把宠物写成人形（旧路径：自定义字节，不带武器/元开关）。
    /// 返回 false 表示写入被结构性问题挡下（长度不符等）。
    /// </summary>
    public static bool WriteHuman(Character* character, byte[] customize, byte[] equipment)
        => WriteHuman(character, customize, equipment, null, null, null, null, null);

    /// <summary>
    /// 把宠物写成人形（完整载荷）。武器与元开关传 null 表示"不指定，保持现状"。
    /// </summary>
    public static bool WriteHuman(Character* character, byte[] customize, byte[] equipment,
        ulong? mainHand, ulong? offHand,
        bool? hatVisible, bool? weaponVisible, bool? visorToggled)
    {
        if (customize.Length < Persona.CustomizeLength || equipment.Length < Persona.EquipmentLength)
            return false;

        // 1) 模型容器：0 = 人类；第二模型槽置空，避免残留旧模型的覆盖值。
        character->ModelContainer.ModelCharaId      = HumanModelCharaId;
        character->ModelContainer.ModelSkeletonId   = 0;
        character->ModelContainer.ModelCharaId_2    = NoSecondModel;
        character->ModelContainer.ModelSkeletonId_2 = 0;

        // 2) 身体。
        if (!WriteCustomize(character, customize))
            return false;

        // 3) 十件装备。
        if (!WriteEquipment(character, equipment))
            return false;

        // 4) 武器（可选；LoadWeapon 会让游戏自己生成/回收武器绘制对象）。
        if (mainHand.HasValue)
            WriteWeapon(character, DrawDataContainer.WeaponSlot.MainHand, mainHand.Value);

        if (offHand.HasValue)
            WriteWeapon(character, DrawDataContainer.WeaponSlot.OffHand, offHand.Value);

        // 5) 视觉开关：默认归一为"正常显示"；设计里显式指定时按设计来。
        character->DrawData.IsHatHidden     = !(hatVisible ?? true);
        character->DrawData.IsWeaponHidden  = !(weaponVisible ?? true);
        character->DrawData.IsVisorToggled  = visorToggled ?? false;
        character->DrawData.VieraEarsHidden = false;

        return true;
    }

    public static bool WriteCustomize(Character* character, byte[] customize)
    {
        var destination = character->DrawData.CustomizeData.Data;
        if (destination.Length < Persona.CustomizeLength || customize.Length < Persona.CustomizeLength)
            return false;

        for (var i = 0; i < Persona.CustomizeLength; ++i)
            destination[i] = customize[i];

        return true;
    }

    public static bool WriteEquipment(Character* character, byte[] equipment)
    {
        var destination = character->DrawData.EquipmentModelIds;
        if (destination.Length < Persona.EquipmentSlots || equipment.Length < Persona.EquipmentLength)
            return false;

        for (var slot = 0; slot < Persona.EquipmentSlots; ++slot)
        {
            var offset = slot * Persona.EquipmentSlotSize;

            // 用 default 起步，确保 padding / union 部分也是干净的 0。
            var model = default(EquipmentModelId);
            model.Id      = (ushort)(equipment[offset] | (equipment[offset + 1] << 8));
            model.Variant = equipment[offset + 2];
            model.Stain0  = equipment[offset + 3];
            model.Stain1  = equipment[offset + 4];

            destination[slot] = model;
        }

        return true;
    }

    /// <summary>
    /// 写武器。weaponData 是 WeaponModelId 的 64 位打包值：
    /// 低 16 位 Id，16-31 位 Type，32-39 位 Variant，48-55 / 56-63 位 Stain0/Stain1。
    /// </summary>
    public static void WriteWeapon(Character* character, DrawDataContainer.WeaponSlot slot, ulong weaponData)
    {
        var model = default(WeaponModelId);
        model.Value = weaponData;
        character->DrawData.LoadWeapon(slot, model, 0, 0, 0, 0, false);
    }

    /// <summary>读一个武器槽的打包值（快照/校验用）。</summary>
    public static ulong ReadWeapon(Character* character, DrawDataContainer.WeaponSlot slot)
        => character->DrawData.Weapon(slot).ModelId.Value;

    /// <summary>
    /// 重建绘制对象的第一步：撤下当前绘制对象。
    /// 用 GameObject 上定义的虚函数（VF 13）调用，避免在派生类型上猜虚表下标。
    /// </summary>
    public static void DisableDraw(GameObject* gameObject)
        => gameObject->DisableDraw();

    /// <summary>
    /// 重建绘制对象的第二步：游戏会用 ModelContainer + DrawData 现场造一个新的 CharacterBase。
    /// 必须等 IsReadyToDraw() 为真之后再调用，否则会踩到半初始化的状态。
    /// </summary>
    public static void EnableDraw(GameObject* gameObject)
        => gameObject->EnableDraw();

    public static bool IsReadyToDraw(GameObject* gameObject)
        => gameObject->IsReadyToDraw();

    public static bool HasDrawObject(GameObject* gameObject)
        => gameObject->DrawObject != null;
}
