using System;
using System.Collections.Generic;

namespace AutoRetainer.Internal.InventoryManagement;

/// <summary>
/// 背包→兵裝庫搬移的判準。純資料，不碰遊戲；離線探針直接編這個檔。
/// 🔴 契約：<see cref="Check"/> 回 <see cref="Skip.None"/> 才准搬；任何讀不到、對不上的情況都回某個 Skip，
/// 失敗方向是「留在背包」，不是「搬錯地方」。
/// </summary>
internal static class ArmoryStowRules
{
    /// <summary>兵裝庫的分類；<see cref="None"/>＝對不到唯一一個容器。</summary>
    internal enum ArmorySlot { None, MainHand, OffHand, Head, Body, Hands, Waist, Legs, Feet, Ears, Neck, Wrists, Rings, SoulCrystal }

    internal enum Skip
    {
        None,
        NotInBags,
        Protected,
        NoArmorySlot,
        InGearset,
        Materia,
        Glamour,
        Dye,
        Spiritbond,
        ArmoryFull,
        ArmoryUnreadable,
    }

    /// <summary>EquipSlotCategory 的 14 欄（表的欄序：MainHand, OffHand, Head, Body, Gloves, Waist, Legs, Feet, Ears, Neck, Wrists, FingerL, FingerR, SoulCrystal）。
    /// 只看值為 1 的欄（-1 是「佔用但不放這格」）；左右戒指同一個容器。</summary>
    private static readonly ArmorySlot[] ColumnSlots =
    [
        ArmorySlot.MainHand, ArmorySlot.OffHand, ArmorySlot.Head, ArmorySlot.Body, ArmorySlot.Hands, ArmorySlot.Waist,
        ArmorySlot.Legs, ArmorySlot.Feet, ArmorySlot.Ears, ArmorySlot.Neck, ArmorySlot.Wrists, ArmorySlot.Rings, ArmorySlot.Rings,
        ArmorySlot.SoulCrystal,
    ];

    /// <summary>對到唯一一個兵裝庫容器才回那個容器；欄數不對、沒有 1、對到兩個以上、或是靈魂水晶都回 None。</summary>
    internal static ArmorySlot TargetSlot(ReadOnlySpan<sbyte> columns)
    {
        if(columns.Length != ColumnSlots.Length) return ArmorySlot.None;
        var found = ArmorySlot.None;
        for(var i = 0; i < columns.Length; i++)
        {
            if(columns[i] != 1) continue;
            var slot = ColumnSlots[i];
            if(found != ArmorySlot.None && found != slot) return ArmorySlot.None;
            found = slot;
        }
        return found == ArmorySlot.SoulCrystal ? ArmorySlot.None : found;
    }

    /// <summary>判斷一件道具要用到的事實，呼叫端在同一幀從遊戲讀好。</summary>
    internal readonly record struct ItemFacts(
        uint ItemId,
        bool SourceIsBag,
        bool Protected,
        ArmorySlot Target,
        bool InGearset,
        bool AnyMateria,
        bool HasGlamour,
        bool AnyDye,
        ushort Spiritbond);

    /// <summary>道具本身准不准搬（不含容量）。</summary>
    internal static Skip Check(in ItemFacts f)
    {
        if(!f.SourceIsBag) return Skip.NotInBags;
        if(f.Protected) return Skip.Protected;
        if(f.Target == ArmorySlot.None) return Skip.NoArmorySlot;
        if(f.InGearset) return Skip.InGearset;
        if(f.AnyMateria) return Skip.Materia;
        if(f.HasGlamour) return Skip.Glamour;
        if(f.AnyDye) return Skip.Dye;
        if(f.Spiritbond != 0) return Skip.Spiritbond;
        return Skip.None;
    }

    /// <summary>扣掉保留格之後還能放幾件；讀不到（size／used 為負）回 0。</summary>
    internal static int Room(int size, int used, int reserve)
    {
        if(size <= 0 || used < 0 || used > size) return 0;
        return Math.Max(0, size - used - Math.Max(0, reserve));
    }

    /// <summary>道具判準加上容量：<paramref name="room"/> 為 null＝那個兵裝庫讀不到。</summary>
    internal static Skip CheckWithRoom(in ItemFacts f, int? room)
    {
        var skip = Check(f);
        if(skip != Skip.None) return skip;
        if(room == null) return Skip.ArmoryUnreadable;
        return room.Value > 0 ? Skip.None : Skip.ArmoryFull;
    }

    /// <summary>D1 用的「各規則跳過幾件」，依列舉順序輸出、略過 0。</summary>
    internal static string FormatSkips(IReadOnlyDictionary<Skip, int> skips)
    {
        if(skips == null || skips.Count == 0) return "none";
        var parts = new List<string>();
        foreach(Skip s in Enum.GetValues(typeof(Skip)))
        {
            if(s == Skip.None) continue;
            if(skips.TryGetValue(s, out var n) && n > 0) parts.Add($"{s}={n}");
        }
        return parts.Count == 0 ? "none" : string.Join(" ", parts);
    }
}
