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

    /// <summary>「低等不可交易裝備放兵裝庫」的道具條件（逐格判準與容量另判）。</summary>
    /// <remarks>契約：等級比較是嚴格小於，<paramref name="levelBelow"/> 本身那一等照常存給僱員。</remarks>
    internal static bool IsLowGearCandidate(bool isGear, bool isDeliverable, bool isUntradable, int levelEquip, int levelBelow)
        => isGear && isDeliverable && isUntradable && levelEquip < levelBelow;

    /// <summary>背包那一半的規則這次生不生效：任一條不成立就照原本的方式存給僱員。</summary>
    internal static bool LowGearBagRuleActive(bool enabled, bool multiMode, bool moveAvailable, bool rejectedBefore, int bagFree, int minBagFree)
        => enabled && multiMode && moveAvailable && !rejectedBefore && bagFree >= minBagFree;

    /// <summary>照背包順序逐件替候選分配兵裝庫空間，回放得下的道具 ID。</summary>
    /// <remarks>契約：順序與判準和 <c>ArmoryStow.Step</c> 一致（先到先得、逐件扣空間）；同一個 ID 要每一件都放得下才回，
    /// 被退回的 ID 已分到的空間不還（收尾仍可能搬它）。所以只可能少排除、不會多排除。<paramref name="skips"/> 可為 null。</remarks>
    internal static HashSet<uint> AllocateWithinRoom(IEnumerable<ItemFacts> candidates, IReadOnlyDictionary<ArmorySlot, int?> rooms, IDictionary<Skip, int> skips)
    {
        var left = new Dictionary<ArmorySlot, int?>();
        foreach(var kv in rooms) left[kv.Key] = kv.Value;
        var ret = new HashSet<uint>();
        var refused = new HashSet<uint>();
        foreach(var f in candidates)
        {
            var room = left.TryGetValue(f.Target, out var r) ? r : null;
            var skip = CheckWithRoom(f, room);
            if(skip != Skip.None)
            {
                if(skips != null) skips[skip] = (skips.TryGetValue(skip, out var n) ? n : 0) + 1;
                refused.Add(f.ItemId);
                continue;
            }
            ret.Add(f.ItemId);
            left[f.Target] = room.Value - 1;
        }
        ret.ExceptWith(refused);
        return ret;
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
