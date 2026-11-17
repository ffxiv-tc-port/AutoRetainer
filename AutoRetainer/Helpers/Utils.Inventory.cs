using AutoRetainer.Internal;
using AutoRetainer.Modules.Voyage;
using AutoRetainer.Scheduler.Handlers;
using AutoRetainer.Scheduler.Tasks;
using AutoRetainer.Services;
using AutoRetainerAPI.Configuration;
using Dalamud.Game;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Memory;
using Dalamud.Utility;
using ECommons.Events;
using ECommons.ExcelServices;
using ECommons.ExcelServices.TerritoryEnumeration;
using ECommons.GameHelpers;
using ECommons.IPC;
using ECommons.MathHelpers;
using ECommons.Reflection;
using ECommons.Throttlers;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using Lumina.Text.ReadOnly;
using OtterGui.Text.EndObjects;
using System.Text.RegularExpressions;
using CharaData = (string Name, ushort World);
using GrandCompany = ECommons.ExcelServices.GrandCompany;

namespace AutoRetainer.Helpers;

public static unsafe partial class Utils
{
    /// <remarks>
    /// 讀不到的容器一律跳過、繼續累加。唯一的呼叫端 <c>AutoGCHandin</c> 把結果當**排序鍵**用
    /// （<c>ThenByDescending</c>），少算一個容器只會換到另一個一樣合法的順序。
    /// ⚠️ 解參考 null 在 .NET Core 是 corrupted-state exception，try/catch 攔不到，只能靠事前檢查。
    /// </remarks>
    public static int CountItemsInInventory(uint id, bool? hq, IEnumerable<InventoryType> inventories)
    {
        var ret = 0;
        foreach(var inventory in inventories)
        {
            var inv = InventoryManager.Instance()->GetInventoryContainer(inventory);
            if(inv == null || inv->Items == null) continue;
            for(var i = 0; i < inv->Size; i++)
            {
                var slot = inv->Items[i];
                var itemId = slot.ItemId;
                var itemHq = slot.Flags.HasFlag(InventoryItem.ItemFlags.HighQuality);
                if((hq == null || itemHq == hq) && itemId == id)
                {
                    ret += (int)slot.Quantity;
                }
            }
        }
        return ret;
    }

    public static InventoryType[] RetainerInventories => [InventoryType.RetainerPage1, InventoryType.RetainerPage2, InventoryType.RetainerPage3, InventoryType.RetainerPage4, InventoryType.RetainerPage5, InventoryType.RetainerPage6, InventoryType.RetainerPage7];

    public static InventoryType[] RetainerInventoriesWithCrystals => [.. RetainerInventories, InventoryType.RetainerCrystals];

    public static InventoryType[] PlayerInvetories => [InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4];

    public static InventoryType[] PlayerInvetoriesWithCrystals => [.. PlayerInvetories, InventoryType.Crystals];

    public static InventoryType[] PlayerArmory => [InventoryType.ArmoryOffHand, InventoryType.ArmoryHead, InventoryType.ArmoryBody, InventoryType.ArmoryHands, InventoryType.ArmoryWaist, InventoryType.ArmoryLegs, InventoryType.ArmoryFeets, InventoryType.ArmoryEar, InventoryType.ArmoryNeck, InventoryType.ArmoryWrist, InventoryType.ArmoryRings, InventoryType.ArmorySoulCrystal, InventoryType.ArmoryMainHand];

    public static InventoryType[] PlayerEntireInventory => [.. PlayerInvetories, .. PlayerArmory, InventoryType.EquippedItems];

    public static InventoryType[] RetainerEntireInventory => [.. RetainerInventoriesWithCrystals, InventoryType.RetainerMarket, InventoryType.RetainerEquippedItems];

    public static InventoryType[] GetAllowedInventories(this EntrustPlan plan)
    {
        return plan.AllowEntrustFromArmory ? [.. PlayerInvetoriesWithCrystals, .. PlayerArmory] : PlayerInvetoriesWithCrystals;
    }

    /// <summary>
    /// Snapshots the contents of the given inventories. Counterpart of
    /// <see cref="MatchesCapturedInventoryState"/>, which must walk the containers in the same order.
    /// </summary>
    /// <remarks>
    /// 🔴 拿不到的容器／格位一律「跳過」，不是中止整個快照。
    /// 兩次都跳過同樣的容器，比對結果仍然相等。
    /// </remarks>
    public static List<(uint ID, uint Quantity)> GetCapturedInventoryState(IEnumerable<InventoryType> inventoryTypes)
    {
        var ret = new List<(uint ID, uint Quantity)>();
        foreach(var type in inventoryTypes)
        {
            var inv = InventoryManager.Instance()->GetInventoryContainer(type);
            if(inv == null) continue;
            for(var i = 0; i < inv->Size; i++)
            {
                var item = InventoryManager.Instance()->GetInventorySlot(type, i);
                if(item == null) continue;
                ret.Add((item->ItemId, (uint)item->Quantity));
            }
        }
        return ret;
    }

    /// <summary>
    /// Whether the given inventories still hold exactly what <see cref="GetCapturedInventoryState"/>
    /// recorded. Equivalent to <c>GetCapturedInventoryState(types).SequenceEqual(captured)</c> but walks
    /// the containers in place and bails at the first difference, so it allocates nothing. The callers
    /// that poll this run once per frame, where a fresh ~200 element list every frame is pure waste.
    /// </summary>
    public static bool MatchesCapturedInventoryState(IEnumerable<InventoryType> inventoryTypes, List<(uint ID, uint Quantity)> captured)
    {
        if(captured == null) return false;
        var index = 0;
        foreach(var type in inventoryTypes)
        {
            var inv = InventoryManager.Instance()->GetInventoryContainer(type);
            if(inv == null) return false;
            for(var i = 0; i < inv->Size; i++)
            {
                if(index < 0 || index >= captured.Count) return false;
                var item = InventoryManager.Instance()->GetInventorySlot(type, i);
                if(item == null) return false;
                var recorded = captured[index];
                if(recorded.ID != item->ItemId || recorded.Quantity != (uint)item->Quantity) return false;
                index++;
            }
        }
        return index == captured.Count;
    }

    /// <summary>
    /// Request all unique items from select inventories
    /// </summary>
    /// <param name="inventoryTypes"></param>
    /// <returns></returns>
    /// <remarks>
    /// 讀不到的容器／格位一律跳過。唯一的呼叫端 <c>TaskEntrustDuplicates</c> 拿這份集合決定「這輪要存入哪些道具」，
    /// 少一個 ID 只代表那件道具這輪不處理（下輪重掃會補回來），不會做出多餘的動作。
    /// ⚠️ 解參考 null 在 .NET Core 是 corrupted-state exception，try/catch 攔不到，只能靠事前檢查。
    /// </remarks>
    public static HashSet<uint> GetItemsInInventory(IEnumerable<InventoryType> inventoryTypes)
    {
        var ret = new HashSet<uint>();
        foreach(var type in inventoryTypes)
        {
            var inv = InventoryManager.Instance()->GetInventoryContainer(type);
            if(inv == null) continue;
            for(var i = 0; i < inv->Size; i++)
            {
                var item = InventoryManager.Instance()->GetInventorySlot(type, i);
                if(item == null) continue;
                if(item->ItemId != 0)
                {
                    ret.Add(item->ItemId);
                }
            }
        }
        return ret;
    }

    /// <summary>
    /// Gets total item count of certain item across all inventories
    /// </summary>
    /// <param name="inventoryTypes"></param>
    /// <param name="itemId"></param>
    /// <returns></returns>
    /// <remarks>
    /// 讀不到的容器／格位一律跳過、繼續累加，也就是**只可能少算不可能多算**。三個呼叫端全部是拿結果去
    /// 跟門檻比大小後才「做事」（<c>SchedulerMain</c> 的 <c>&gt; toKeep</c> 決定要不要排訪問、
    /// <c>RetainerRetrieve</c> 兩處的 <c>&gt; 0</c> 決定要不要跳過獨占道具），少算一律讓判斷倒向「不做事」。
    /// ⚠️ 解參考 null 在 .NET Core 是 corrupted-state exception，try/catch 攔不到，只能靠事前檢查。
    /// </remarks>
    public static int GetItemCount(IEnumerable<InventoryType> inventoryTypes, uint itemId)
    {
        var ret = 0;
        foreach(var type in inventoryTypes)
        {
            var inv = InventoryManager.Instance()->GetInventoryContainer(type);
            if(inv == null) continue;
            for(var i = 0; i < inv->Size; i++)
            {
                var item = InventoryManager.Instance()->GetInventorySlot(type, i);
                if(item == null) continue;
                if(item->ItemId == itemId)
                {
                    ret += (int)item->Quantity;
                }
            }
        }
        return ret;
    }

    /// <summary>
    /// Whether the given container can be walked at all right now. A container that is not currently
    /// loaded (retainer pages while no retainer is open, say) comes back as a null pointer, or as a
    /// container whose backing item array has not been allocated.
    /// </summary>
    /// <remarks>
    /// 🔴 這是「能不能讀」不是「有沒有東西」。呼叫端如果需要**證明某件道具不存在**，光看
    /// <see cref="ContainsItem"/> 回 false 是不夠的——讀不到的容器也會回 false。要先用本方法確認讀得到。
    /// </remarks>
    public static bool IsInventoryReadable(this InventoryType type)
    {
        var inv = InventoryManager.Instance()->GetInventoryContainer(type);
        return inv != null && inv->Items != null;
    }

    /// <remarks>
    /// ⚠️ 讀不到的容器回 <c>false</c>——語意是「在**讀得到的格位裡**沒找到」，**不是**「這件道具不存在」。
    /// 需要後者的呼叫端請先過 <see cref="IsInventoryReadable"/>。
    /// </remarks>
    public static bool ContainsItem(this InventoryType type, uint item, bool? isHq = null)
    {
        var im = InventoryManager.Instance();
        var inv = im->GetInventoryContainer(type);
        if(inv == null || inv->Items == null) return false;
        for(var i = 0; i < inv->Size; i++)
        {
            var slot = inv->Items[i];
            if(slot.ItemId == item && (isHq == null || isHq == slot.Flags.HasFlag(InventoryItem.ItemFlags.HighQuality)))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Gets amount of items that can fit into inventories.
    /// </summary>
    /// <remarks>
    /// Use this overload unless you are actually going to show the diagnostics to someone. The
    /// <c>debugData</c> overload builds one interpolated string per scanned slot - up to ~175 for a
    /// retainer's item pages, several of them with an Excel name lookup - which is a large amount of
    /// garbage for a method that gets called inside per-item scan loops.
    /// </remarks>
    public static uint GetAmountThatCanFit(IEnumerable<InventoryType> inventoryTypes, uint itemId, bool isHq)
    {
        return GetAmountThatCanFitInternal(inventoryTypes, itemId, isHq, null);
    }

    /// <summary>
    /// Gets amount of items that can fit into inventories, along with a per-slot explanation of how the
    /// number was reached. Only call this when the explanation is going to be read - see the remarks on
    /// the three-argument overload.
    /// </summary>
    /// <param name="inventoryTypes"></param>
    /// <param name="itemId"></param>
    /// <param name="isHq"></param>
    /// <param name="debugData"></param>
    /// <returns></returns>
    public static uint GetAmountThatCanFit(IEnumerable<InventoryType> inventoryTypes, uint itemId, bool isHq, out List<string> debugData)
    {
        debugData = [];
        return GetAmountThatCanFitInternal(inventoryTypes, itemId, isHq, debugData);
    }

    /// <param name="debugData">Null to skip building the per-slot explanation entirely. Every use below is
    /// through <c>?.</c>, which short-circuits argument evaluation, so a null collector means the
    /// interpolated strings are never built in the first place.</param>
    /// <remarks>
    /// 🔴 本函式讀不到容器時一律 <c>return 0</c>，**不是** <c>continue</c>。
    /// 理由是這裡「跳過一個容器」會讓結果**偏高**，而不是偏低。
    /// 回 0 是保守失敗（這輪不動作），而且兩個呼叫端都是輪詢重跑的，容器一旦載入下一輪就自動恢復，不會卡死。
    /// </remarks>
    private static uint GetAmountThatCanFitInternal(IEnumerable<InventoryType> inventoryTypes, uint itemId, bool isHq, List<string> debugData)
    {
        uint ret = 0;
        var data = ExcelItemHelper.Get(itemId);
        if(data == null) return 0;
        if(data.Value.IsUnique)
        {
            // 讀不到的容器不能用來證明「身上沒有這件獨占道具」（ContainsItem 對讀不到的容器也是回 false），
            // 所以讀不到就當作「可能已經有了」→ 回 0。
            if(inventoryTypes.ContainsAny(Utils.PlayerEntireInventory))
            {
                if(Utils.PlayerEntireInventory.Any(i => !i.IsInventoryReadable() || i.ContainsItem(itemId, null))) return 0;
            }
            if(inventoryTypes.ContainsAny(Utils.RetainerEntireInventory))
            {
                if(Utils.RetainerEntireInventory.Any(i => !i.IsInventoryReadable() || i.ContainsItem(itemId, null))) return 0;
            }
        }
        if(data.Value.ItemUICategory.RowId == 59)//crystal special handling
        {
            foreach(var type in inventoryTypes)
            {
                var inv = InventoryManager.Instance()->GetInventoryContainer(type);
                if(inv == null) return 0;
                for(var i = 0; i < inv->Size; i++)
                {
                    var item = InventoryManager.Instance()->GetInventorySlot(type, i);
                    if(item == null) return 0;
                    if(item->ItemId == itemId)
                    {
                        ret += (uint)(data.Value.StackSize - item->Quantity);
                        debugData?.Add($"[TED] [CrystalDebugData] in {type} slot {i} found incomplete stack: {ExcelItemHelper.GetName(itemId, true)} q={item->Quantity} canFit={ret}");
                        return ret;
                    }
                }
            }
            return data.Value.StackSize;
        }
        else
        {
            foreach(var type in inventoryTypes)
            {
                if(type.EqualsAny(InventoryType.Crystals, InventoryType.RetainerCrystals)) continue;
                var inv = InventoryManager.Instance()->GetInventoryContainer(type);
                if(inv == null) return 0;
                for(var i = 0; i < inv->Size; i++)
                {
                    var item = InventoryManager.Instance()->GetInventorySlot(type, i);
                    if(item == null) return 0;
                    if(item->ItemId == itemId && item->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality) == isHq && !item->Flags.HasFlag(InventoryItem.ItemFlags.Collectable))
                    {
                        if(data.Value.IsUnique) return 0;
                        debugData?.Add($"[TED] [DebugData] in {type} slot {i} found incomplete stack: {ExcelItemHelper.GetName(itemId, true)} q={item->Quantity} canFit={ret}");
                        ret += (uint)(data.Value.StackSize - item->Quantity);
                    }
                    else if(item->ItemId == 0)
                    {
                        debugData?.Add($"[TED] [DebugData] in {type} slot {i} is empty, canFit={data.Value.StackSize}");
                        ret += data.Value.StackSize;
                    }
                }
            }
        }
        return ret;
    }

    public static bool IsItemSellableByHardList(Number item, Number quantity)
    {
        if(Data.GetIMSettings().IMProtectList.Contains(item)) return false;
        if(Data.GetIMSettings().IMAutoVendorHard.Contains(item))
        {
            if(Data.GetIMSettings().IMAutoVendorHardIgnoreStack.Contains(item)) return true;
            return quantity < Data.GetIMSettings().IMAutoVendorHardStackLimit;
        }
        else
        {
            return false;
        }
    }

    internal static bool ContainsAllItems<T>(this IEnumerable<T> a, IEnumerable<T> b)
    {
        return !b.Except(a).Any();
    }

    /// <summary>等於 <c>plan.ListUnwrapped.Count</c>，但不建出清單：給每幀都要看長度的地方用。</summary>
    /// <remarks>null 項目照樣擲例外；總數超出 int 時擲 OverflowException（建整份清單在那之前就會失敗），不靜默回錯值。</remarks>
    internal static int GetUnwrappedCount(this VenturePlan plan)
    {
        var count = 0;
        foreach(var v in plan.List)
        {
            if(v.Num > 0) count = checked(count + v.Num);
        }
        return count;
    }

    internal static string GetActivePlayerInventoryName()
    {
        {
            if(TryGetAddonByName<AtkUnitBase>("InventoryLarge", out var addon) && addon->IsVisible)
            {
                return "InventoryLarge";
            }
        }
        {
            if(TryGetAddonByName<AtkUnitBase>("InventoryExpansion", out var addon) && addon->IsVisible)
            {
                return "InventoryExpansion";
            }
        }
        return "Inventory";
    }

    internal static (string Name, int EntrustDuplicatesIndex) GetActiveRetainerInventoryName()
    {
        if(TryGetAddonByName<AtkUnitBase>("InventoryRetainerLarge", out var addon) && addon->IsVisible)
        {
            return ("InventoryRetainerLarge", 8);
        }
        return ("InventoryRetainer", 5);
    }

    internal static bool IsInventoryFree()
    {
        return GetInventoryFreeSlotCount() >= C.MultiMinInventorySlots;
    }

    /// <summary>Whether the player's own inventory can be read right now at all.
    /// 🔴 <see cref="GetInventoryFreeSlotCount"/> skips containers it cannot read, so "not loaded yet" and "completely full" both come back as 0.
    /// The containers are genuinely unreadable while zoning and for a short window after login, both of which the retainer/multi-mode flow hits constantly (it relogs between characters by design).
    /// ⚠️ Deliberately NOT folded into <see cref="IsInventoryFree"/>: that would answer "yes, free" for an inventory nobody has read.
    /// </summary>
    internal static bool IsInventoryStateReadable()
    {
        if(!Player.Available) return false;
        if(Svc.Condition[ConditionFlag.BetweenAreas] || Svc.Condition[ConditionFlag.BetweenAreas51]) return false;
        var c = InventoryManager.Instance();
        if(c == null) return false;
        ReadOnlySpan<InventoryType> types = [InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4];
        foreach(var x in types)
        {
            var inv = c->GetInventoryContainer(x);
            if(inv == null || inv->Items == null || inv->Size <= 0) return false;
        }
        return true;
    }

    /// <remarks>
    /// 讀不到的容器一律跳過、繼續累加，也就是**只可能少算空格不可能多算**。所有呼叫端都是拿結果去跟下限比
    /// （<c>&gt;= MultiMinInventorySlots</c>、<c>&lt; Max(5, UIWarningRetSlotNum)</c> 等），少算一律讓判斷倒向
    /// 「背包不夠、先不要動作」。
    /// ⚠️ 解參考 null 在 .NET Core 是 corrupted-state exception，try/catch 攔不到，只能靠事前檢查。
    /// </remarks>
    internal static int GetInventoryFreeSlotCount()
    {
        ReadOnlySpan<InventoryType> types = [InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4];
        var c = InventoryManager.Instance();
        var slots = 0;
        foreach(var x in types)
        {
            var inv = c->GetInventoryContainer(x);
            if(inv == null || inv->Items == null) continue;
            for(var i = 0; i < inv->Size; i++)
            {
                if(inv->Items[i].ItemId == 0)
                {
                    slots++;
                }
            }
        }
        return slots;
    }
}
