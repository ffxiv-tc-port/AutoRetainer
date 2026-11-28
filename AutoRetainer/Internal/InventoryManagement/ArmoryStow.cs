using AutoRetainerAPI.Configuration;
using ECommons.ExcelServices;
using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Lumina.Excel.Sheets;
using static AutoRetainer.Internal.InventoryManagement.ArmoryStowRules;

namespace AutoRetainer.Internal.InventoryManagement;

/// <summary>
/// 背包→兵裝庫搬移核心（遊戲拖放走的同一支 <c>MoveItemSlot(…, a6: true)</c>）。
/// 🔴 契約：只在框架執行緒呼叫；原生指標同一幀取、同一幀用，跨幀只存容器、格號、道具 ID。
/// 呼叫端決定「哪些背包格是我的」（<see cref="Batch.Filter"/>），判準與容量由這裡統一判。
/// </summary>
internal static unsafe class ArmoryStow
{
    internal const int MoveIntervalMs = 150;
    /// <summary>最後一件搬完後再等這麼久，回頭驗全部仍在兵裝庫才算這批結束（伺服器拒收時會彈回來源）。</summary>
    internal const long BounceWatchMs = 3000;
    private const long ZoneCheckDelayMs = 3000;

    internal static readonly ArmorySlot[] StowableSlots =
    [
        ArmorySlot.MainHand, ArmorySlot.OffHand, ArmorySlot.Head, ArmorySlot.Body, ArmorySlot.Hands, ArmorySlot.Waist,
        ArmorySlot.Legs, ArmorySlot.Feet, ArmorySlot.Ears, ArmorySlot.Neck, ArmorySlot.Wrists, ArmorySlot.Rings,
    ];

    internal static InventoryType ToContainer(ArmorySlot s) => s switch
    {
        ArmorySlot.MainHand => InventoryType.ArmoryMainHand,
        ArmorySlot.OffHand => InventoryType.ArmoryOffHand,
        ArmorySlot.Head => InventoryType.ArmoryHead,
        ArmorySlot.Body => InventoryType.ArmoryBody,
        ArmorySlot.Hands => InventoryType.ArmoryHands,
        ArmorySlot.Waist => InventoryType.ArmoryWaist,
        ArmorySlot.Legs => InventoryType.ArmoryLegs,
        ArmorySlot.Feet => InventoryType.ArmoryFeets,
        ArmorySlot.Ears => InventoryType.ArmoryEar,
        ArmorySlot.Neck => InventoryType.ArmoryNeck,
        ArmorySlot.Wrists => InventoryType.ArmoryWrist,
        ArmorySlot.Rings => InventoryType.ArmoryRings,
        ArmorySlot.SoulCrystal => InventoryType.ArmorySoulCrystal,
        _ => InventoryType.Invalid,
    };

    /// <summary>兵裝庫緩衝與低等裝備放兵裝庫共用的繳交模式判準：只對繳交模式為 Hide_Gear_Set_Items 的角色生效。</summary>
    /// <remarks>其他模式的稀有品繳交不保證會交兵裝庫裡的裝備（Hide_Armoury_Chest_Items 完全看不到），放進去就卡住。</remarks>
    internal static bool DeliveryModeUsesArmory(GCDeliveryType? mode) => mode == GCDeliveryType.Hide_Gear_Set_Items;

    internal const string DeliveryModeNotUsingArmory = "this character's delivery mode is not Hide_Gear_Set_Items";

    /// <summary>🔴 出貨閘門：函式指標是 null 還照呼叫＝AVE，try/catch 攔不到。</summary>
    internal static bool MoveAvailable => InventoryManager.MemberFunctionPointers.MoveItemSlot != null;

    internal readonly record struct Moved(InventoryType Src, int SrcSlot, InventoryType Dst, int DstSlot, uint ItemId);

    internal enum BatchState { Moving, Watching, Done }

    internal sealed class Batch(string owner, int reserve, Func<InventoryType, int, uint, bool> filter)
    {
        internal readonly string Owner = owner;
        internal readonly int Reserve = reserve;
        /// <summary>(背包容器, 格號, 道具 ID) 是不是這個呼叫端要搬的。</summary>
        internal readonly Func<InventoryType, int, uint, bool> Filter = filter;
        internal readonly List<Moved> MovedItems = [];
        internal readonly List<string> Failures = [];
        internal Dictionary<Skip, int> Skips = [];
        internal BatchState State = BatchState.Moving;
        internal long LastMoveAt;
        internal int Bounced;
        internal string EndReason = "";
        internal int Failed => Failures.Count;
        internal bool Rejected => Failures.Count > 0 || Bounced > 0;
    }

    /// <summary>推進一步；回 true＝這批結束（結果看 <paramref name="b"/> 的欄位）。每步最多搬一件。</summary>
    internal static bool Step(Batch b)
    {
        if(b.State == BatchState.Done) return true;
        var now = Environment.TickCount64;

        if(b.State == BatchState.Watching)
        {
            if(now - b.LastMoveAt < BounceWatchMs) return false;
            var im = InventoryManager.Instance();
            foreach(var m in b.MovedItems)
            {
                if(im == null || !IsAt(m.Dst, m.DstSlot, m.ItemId))
                {
                    b.Bounced++;
                    b.Failures.Add($"{m.Src}#{m.SrcSlot} -> {m.Dst}#{m.DstSlot} item={m.ItemId}: gone from the armoury slot {now - b.LastMoveAt}ms after the batch (backInSource={IsAt(m.Src, m.SrcSlot, m.ItemId)})");
                }
            }
            End(b, b.Bounced > 0 ? "moved items bounced back" : b.EndReason);
            return true;
        }

        if(b.LastMoveAt != 0 && now - b.LastMoveAt < MoveIntervalMs) return false;
        if(!MoveAvailable)
        {
            End(b, "MoveItemSlot is not resolved on this client");
            return true;
        }
        var manager = InventoryManager.Instance();
        if(manager == null)
        {
            End(b, "InventoryManager unavailable");
            return true;
        }

        // 上一件要仍在原處才搬下一件：伺服器拒收的話停在第一件，不連續搬一整排。
        if(b.MovedItems.Count > 0)
        {
            var last = b.MovedItems[^1];
            if(!IsAt(last.Dst, last.DstSlot, last.ItemId))
            {
                b.Bounced++;
                b.Failures.Add($"{last.Src}#{last.SrcSlot} -> {last.Dst}#{last.DstSlot} item={last.ItemId}: gone from the armoury slot before the next move (backInSource={IsAt(last.Src, last.SrcSlot, last.ItemId)})");
                End(b, "previous move bounced back");
                return true;
            }
        }

        var gearset = ReadGearsetItemIds();
        if(gearset == null)
        {
            End(b, "gear sets could not be read");
            return true;
        }
        var rooms = ReadRooms(b.Reserve);
        var protect = Data.GetIMSettings().IMProtectList;
        var skips = new Dictionary<Skip, int>();

        foreach(var type in Utils.PlayerInvetories)
        {
            var inv = RetainerRetrieve.TryGetReadableContainer(type);
            if(inv == null)
            {
                End(b, $"bag {type} could not be read");
                return true;
            }
            for(var i = 0; i < inv->Size; i++)
            {
                var item = inv->GetInventorySlot(i);
                if(item == null || item->ItemId == 0 || item->Quantity <= 0) continue;
                if(!b.Filter(type, i, item->ItemId)) continue;
                var facts = BuildFacts(item, true, protect.Contains(item->ItemId), gearset);
                var skip = CheckWithRoom(facts, rooms.TryGetValue(facts.Target, out var r) ? r : null);
                if(skip != Skip.None)
                {
                    skips[skip] = skips.GetValueOrDefault(skip) + 1;
                    continue;
                }
                if(!MoveOne(b, manager, type, i, item, facts.Target, now)) End(b, "a move was rejected");
                return b.State == BatchState.Done;
            }
        }

        b.Skips = skips;
        b.EndReason = b.MovedItems.Count > 0 ? "nothing left to move" : "nothing eligible to move";
        if(b.MovedItems.Count > 0)
        {
            b.State = BatchState.Watching;
            return false;
        }
        End(b, b.EndReason);
        return true;
    }

    private static bool MoveOne(Batch b, InventoryManager* manager, InventoryType src, int srcSlot, InventoryItem* item, ArmorySlot target, long now)
    {
        // MoveItemSlot 會同步清空來源格，識別資料要先抄下來。
        var itemId = item->ItemId;
        var quantity = item->Quantity;
        var hq = item->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality);
        var dst = ToContainer(target);
        var dstInv = TryGetArmory(dst);
        var dstSlot = -1;
        if(dstInv != null)
        {
            for(var j = 0; j < dstInv->Size; j++)
            {
                var d = dstInv->GetInventorySlot(j);
                if(d != null && d->ItemId == 0)
                {
                    dstSlot = j;
                    break;
                }
            }
        }
        if(dstSlot < 0)
        {
            b.Failures.Add($"{src}#{srcSlot} -> {dst}: no empty slot found although room was counted, item={itemId}");
            return false;
        }

        // 🔴 a6: true 不能省：預設 false＝只改本機、不送給伺服器，下次同步就彈回。
        var result = manager->MoveItemSlot(src, (ushort)srcSlot, dst, (ushort)dstSlot, a6: true);
        var srcEmpty = IsEmptyAt(src, srcSlot);
        var dstHas = IsAt(dst, dstSlot, itemId);
        b.LastMoveAt = now;
        if(result == 0 && srcEmpty && dstHas)
        {
            b.MovedItems.Add(new(src, srcSlot, dst, dstSlot, itemId));
            return true;
        }
        b.Failures.Add($"{src}#{srcSlot} -> {dst}#{dstSlot} item={itemId} qty={quantity} hq={hq} result={result} sourceEmpty={srcEmpty} destinationHasItem={dstHas}");
        return false;
    }

    private static void End(Batch b, string reason)
    {
        b.EndReason = reason;
        b.State = BatchState.Done;
    }

    /// <summary>D1：每批一行，失敗逐件一行；搬成功的登記給轉區確認（D2）。</summary>
    internal static void Report(Batch b, string context)
    {
        var perContainer = b.MovedItems.GroupBy(x => x.Dst).Select(g => $"{g.Key}={g.Count()}");
        PluginLog.Information($"[ArmoryStow] {b.Owner} ({context}): moved {b.MovedItems.Count} ({string.Join(" ", perContainer)}), failed {b.Failed}, bounced {b.Bounced}, skipped {FormatSkips(b.Skips)}, reserve {b.Reserve}, end: {b.EndReason}.");
        foreach(var f in b.Failures) PluginLog.Information($"[ArmoryStow] {b.Owner} failed: {f}");
        foreach(var m in b.MovedItems)
        {
            if(IsAt(m.Dst, m.DstSlot, m.ItemId)) RegisterZoneCheck(m);
        }
    }

    internal static ItemFacts BuildFacts(InventoryItem* item, bool sourceIsBag, bool isProtected, HashSet<uint> gearsetItemIds)
    {
        var anyMateria = false;
        for(var k = 0; k < 5; k++)
        {
            if(item->Materia[k] != 0) anyMateria = true;
        }
        return new ItemFacts(
            item->ItemId,
            sourceIsBag,
            isProtected,
            TargetSlotOf(item->ItemId),
            gearsetItemIds.Contains(item->ItemId),
            anyMateria,
            item->GlamourId != 0,
            item->Stains[0] != 0 || item->Stains[1] != 0,
            item->SpiritbondOrCollectability);
    }

    internal static ArmorySlot TargetSlotOf(uint itemId)
    {
        var data = ExcelItemHelper.Get(itemId);
        if(data == null) return ArmorySlot.None;
        var row = Svc.Data.GetExcelSheet<EquipSlotCategory>().GetRowOrDefault(data.Value.EquipSlotCategory.RowId);
        if(row == null) return ArmorySlot.None;
        var r = row.Value;
        ReadOnlySpan<sbyte> cols = [r.MainHand, r.OffHand, r.Head, r.Body, r.Gloves, r.Waist, r.Legs, r.Feet, r.Ears, r.Neck, r.Wrists, r.FingerL, r.FingerR, r.SoulCrystal];
        return TargetSlot(cols);
    }

    /// <summary>所有套裝用到的道具 ID（去掉 HQ／收藏品位移）；讀不到回 null＝這次一件都不搬。</summary>
    internal static HashSet<uint> ReadGearsetItemIds()
    {
        var module = RaptureGearsetModule.Instance();
        if(module == null) return null;
        var ids = new HashSet<uint>();
        foreach(ref var entry in module->Entries)
        {
            if(!entry.Flags.HasFlag(RaptureGearsetModule.GearsetFlag.Exists)) continue;
            foreach(ref var gi in entry.Items)
            {
                var id = gi.ItemId;
                if(id >= 1_000_000) id -= 1_000_000;
                else if(id >= 500_000) id -= 500_000;
                if(id != 0) ids.Add(id);
            }
        }
        return ids;
    }

    /// <summary>各兵裝庫扣保留格後的空間；讀不到的分類不在字典裡。</summary>
    internal static Dictionary<ArmorySlot, int?> ReadRooms(int reserve)
    {
        var ret = new Dictionary<ArmorySlot, int?>();
        foreach(var s in StowableSlots)
        {
            var (size, used) = ReadSizeUsed(ToContainer(s));
            ret[s] = size < 0 ? null : Room(size, used, reserve);
        }
        return ret;
    }

    /// <summary>D6 用：各兵裝庫「大小/已用」，讀不到寫 ?。</summary>
    internal static string DescribeArmory(int reserve)
    {
        var parts = new List<string>();
        foreach(var s in StowableSlots)
        {
            var (size, used) = ReadSizeUsed(ToContainer(s));
            parts.Add(size < 0 ? $"{s}=?" : $"{s}={used}/{size}");
        }
        return $"{string.Join(" ", parts)} (reserve {reserve} per slot)";
    }

    private static (int Size, int Used) ReadSizeUsed(InventoryType type)
    {
        var inv = TryGetArmory(type);
        if(inv == null) return (-1, -1);
        var used = 0;
        for(var i = 0; i < inv->Size; i++)
        {
            var it = inv->GetInventorySlot(i);
            if(it == null) return (-1, -1);
            if(it->ItemId != 0) used++;
        }
        return (inv->Size, used);
    }

    private static InventoryContainer* TryGetArmory(InventoryType type)
    {
        var inv = RetainerRetrieve.TryGetReadableContainer(type);
        if(inv == null || !inv->IsLoaded || inv->Size <= 0) return null;
        return inv;
    }

    internal static bool IsAt(InventoryType type, int slot, uint itemId)
    {
        var inv = RetainerRetrieve.TryGetReadableContainer(type);
        if(inv == null || slot < 0 || slot >= inv->Size) return false;
        var it = inv->GetInventorySlot(slot);
        return it != null && it->ItemId == itemId && it->Quantity > 0;
    }

    private static bool IsEmptyAt(InventoryType type, int slot)
    {
        var inv = RetainerRetrieve.TryGetReadableContainer(type);
        if(inv == null || slot < 0 || slot >= inv->Size) return false;
        var it = inv->GetInventorySlot(slot);
        return it != null && it->ItemId == 0;
    }

    /// <summary>容器裡這個道具 ID 有幾格；讀不到回 -1。</summary>
    internal static int CountIn(InventoryType type, uint itemId)
    {
        var inv = RetainerRetrieve.TryGetReadableContainer(type);
        if(inv == null) return -1;
        var n = 0;
        for(var i = 0; i < inv->Size; i++)
        {
            var it = inv->GetInventorySlot(i);
            if(it != null && it->ItemId == itemId && it->Quantity > 0) n++;
        }
        return n;
    }

    #region 轉區確認（D2）

    // 轉區＝伺服器重新同步本機容器；之後還在兵裝庫才證明伺服器收了這次搬移。
    private static readonly List<Moved> AwaitingZoneCheck = [];
    private static uint ZoneCheckTerritory;
    private static ulong ZoneCheckCID;
    private static long ZoneChangedAt;

    private static void RegisterZoneCheck(Moved m)
    {
        if(AwaitingZoneCheck.Count == 0)
        {
            ZoneCheckTerritory = Svc.ClientState.TerritoryType;
            ZoneCheckCID = Player.CID;
            ZoneChangedAt = 0;
        }
        AwaitingZoneCheck.Add(m);
    }

    /// <summary>每幀呼叫；沒有待確認的搬移時只做一次判空。</summary>
    internal static void Tick()
    {
        if(AwaitingZoneCheck.Count == 0) return;
        if(!Player.Available)
        {
            if(Svc.ClientState.IsLoggedIn) return;
            PluginLog.Information($"[ArmoryStow] 上次放入兵裝庫 {AwaitingZoneCheck.Count} 件，還沒轉區就登出了，這次無法確認伺服器是否收到。");
            AwaitingZoneCheck.Clear();
            return;
        }
        if(Player.CID != ZoneCheckCID)
        {
            PluginLog.Information($"[ArmoryStow] 上次放入兵裝庫 {AwaitingZoneCheck.Count} 件，還沒轉區就換了角色，這次無法確認伺服器是否收到。");
            AwaitingZoneCheck.Clear();
            return;
        }
        if(Svc.ClientState.TerritoryType == ZoneCheckTerritory && ZoneChangedAt == 0) return;
        var now = Environment.TickCount64;
        if(ZoneChangedAt == 0)
        {
            ZoneChangedAt = now;
            return;
        }
        if(now - ZoneChangedAt < ZoneCheckDelayMs) return;

        int inArmory = 0, backInBag = 0, elsewhere = 0;
        foreach(var m in AwaitingZoneCheck)
        {
            if(IsAt(m.Dst, m.DstSlot, m.ItemId)) inArmory++;
            else if(IsAt(m.Src, m.SrcSlot, m.ItemId)) backInBag++;
            else elsewhere++;
        }
        PluginLog.Information($"[ArmoryStow] 轉區後確認：上次放入 {AwaitingZoneCheck.Count} 件，現在仍在兵裝庫 {inArmory} 件、回到背包 {backInBag} 件、都不在（已交掉或被移動）{elsewhere} 件。區域 {ZoneCheckTerritory}→{Svc.ClientState.TerritoryType}。");
        AwaitingZoneCheck.Clear();
    }

    #endregion
}
