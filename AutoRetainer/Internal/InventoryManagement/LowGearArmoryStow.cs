using AutoRetainer.Scheduler.Tasks;
using ECommons.ExcelServices;
using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Client.Game;
using static AutoRetainer.Internal.InventoryManagement.ArmoryStowRules;

namespace AutoRetainer.Internal.InventoryManagement;

/// <summary>
/// 多角色模式：不可交易、珍稀、裝備等級低於門檻、可繳交的裝備不存給僱員，收尾時搬進兵裝庫（<see cref="Config.ArmoryStowLowGearEnabled"/>）。
/// 只對繳交模式符合 <see cref="ArmoryStow.DeliveryModeUsesArmory"/> 的角色生效；存放排除與收尾搬移兩邊都判。
/// 契約：只在框架執行緒呼叫。存放排除（<see cref="ResolveExcluded"/>）與收尾搬移（<see cref="TaskStowLowGear"/>）
/// 用同一支 <see cref="IsLowGearItem"/> 判道具、同一套 <see cref="ArmoryStowRules"/> 判逐格與容量；
/// 任何讀不到或搬移被拒的情況，方向都是「照原本的方式存給僱員」。
/// </summary>
internal static unsafe class LowGearArmoryStow
{
    internal const string Owner = "LowGear";

    /// <summary>搬移被拒過的角色，到遊戲或插件重開前都不再搬。</summary>
    /// <remarks>刻意不在登出時清：多角色模式每換一次角色就登出一次，清了等於每輪重試、排除的裝備永遠存不出去。</remarks>
    private static readonly HashSet<ulong> Rejected = [];
    private static readonly HashSet<(ulong Cid, string Reason)> InactiveReported = [];

    internal static void OnLogout() => InactiveReported.Clear();

    internal static void MarkRejected()
    {
        if(Rejected.Add(Player.CID))
        {
            PluginLog.Information("[ArmoryStow] LowGear: 遊戲沒有接受這次搬移，這個角色在遊戲或插件重開前不再把低等裝備搬進兵裝庫，之後照原本的方式存給僱員。");
        }
    }

    /// <summary>這件道具本身符不符合（不看這一格的魔晶石／投影／染色／精煉度／套裝，也不看容量）。</summary>
    /// <param name="named">存放計畫逐件點名的道具；點名的照常存給僱員。</param>
    internal static bool IsLowGearItem(uint itemId, InventoryManagementSettings ims, ICollection<uint> named)
    {
        if(itemId == 0) return false;
        var data = ExcelItemHelper.Get(itemId);
        if(data == null) return false;
        var isGear = data.Value.ItemUICategory.RowId.EqualsAny(Utils.GearUICategories);
        if(!isGear) return false;
        if(named != null && named.Contains(itemId)) return false;
        // 自動賣出清單也是分解清單：交給那兩條處理，不搶。
        if(ims.IMAutoVendorHard.Contains(itemId) || ims.IMAutoVendorSoft.Contains(itemId)) return false;
        return IsLowGearCandidate(isGear, GCExpertDeliveryLoop.IsDeliverableGear(itemId, ims), data.Value.IsUntradable, data.Value.IsUnique, data.Value.LevelEquip, C.ArmoryStowLowGearLevelBelow);
    }

    /// <summary>存放排除用：在 <paramref name="plan"/> 下不存給僱員的道具 ID；null＝規則不生效。</summary>
    /// <remarks>
    /// 背包：照 <see cref="AllocateWithinRoom"/> 只排除兵裝庫扣掉保留格放得下的那些（收尾搬得動的）。
    /// 計畫允許從兵裝庫存放時，兵裝庫裡符合條件的也排除，否則剛搬進去的會被存回僱員。
    /// </remarks>
    internal static HashSet<uint> ResolveExcluded(EntrustPlan plan, InventoryManagementSettings ims)
    {
        if(plan == null || !C.ArmoryStowLowGearEnabled || !MultiMode.Active) return null;
        if(!DeliveryModeAllows()) return null;
        HashSet<uint> ret = null;
        var cid = Player.CID;
        var bagFree = Utils.GetInventoryFreeSlotCount();
        if(LowGearBagRuleActive(true, true, ArmoryStow.MoveAvailable, Rejected.Contains(cid), bagFree, C.MultiMinInventorySlots))
        {
            var candidates = CollectBagCandidates(ims, plan.EntrustItems, out var why);
            if(candidates != null) ret = AllocateWithinRoom(candidates, ArmoryStow.ReadRooms(C.ArmoryStowReserveSlots), null);
            else ReportInactive(cid, why);
        }
        else if(!ArmoryStow.MoveAvailable) ReportInactive(cid, "MoveItemSlot is not resolved on this client");
        else if(!Rejected.Contains(cid)) ReportInactive(cid, $"bags have fewer free slots than the multi mode minimum ({C.MultiMinInventorySlots})");

        if(plan.AllowEntrustFromArmory)
        {
            foreach(var s in ArmoryStow.StowableSlots)
            {
                var inv = RetainerRetrieve.TryGetReadableContainer(ArmoryStow.ToContainer(s));
                if(inv == null) continue;
                for(var i = 0; i < inv->Size; i++)
                {
                    var it = inv->GetInventorySlot(i);
                    if(it == null || it->ItemId == 0) continue;
                    if(IsLowGearItem(it->ItemId, ims, plan.EntrustItems)) (ret ??= []).Add(it->ItemId);
                }
            }
        }
        return ret;
    }

    /// <summary>背包裡符合道具條件的格，照背包順序；任一頁或套裝讀不到回 null（這次整條不生效）。</summary>
    private static List<ItemFacts> CollectBagCandidates(InventoryManagementSettings ims, ICollection<uint> named, out string why)
    {
        why = null;
        var gearset = ArmoryStow.ReadGearsetItemIds();
        if(gearset == null)
        {
            why = "gear sets could not be read";
            return null;
        }
        var ret = new List<ItemFacts>();
        foreach(var type in Utils.PlayerInvetories)
        {
            var inv = RetainerRetrieve.TryGetReadableContainer(type);
            if(inv == null)
            {
                why = $"bag {type} could not be read";
                return null;
            }
            for(var i = 0; i < inv->Size; i++)
            {
                var item = inv->GetInventorySlot(i);
                if(item == null || item->ItemId == 0 || item->Quantity <= 0) continue;
                if(!IsLowGearItem(item->ItemId, ims, named)) continue;
                ret.Add(ArmoryStow.BuildFacts(item, true, ims.IMProtectList.Contains(item->ItemId), gearset));
            }
        }
        return ret;
    }

    /// <summary>目前角色的繳交模式准不准用兵裝庫；不准時每個角色寫一次 Information。</summary>
    private static bool DeliveryModeAllows()
    {
        var mode = Data?.GCDeliveryType;
        if(ArmoryStow.DeliveryModeUsesArmory(mode)) return true;
        ReportInactive(Player.CID, $"{ArmoryStow.DeliveryModeNotUsingArmory}, it is {mode?.ToString() ?? "?"}");
        return false;
    }

    private static void ReportInactive(ulong cid, string why)
    {
        if(!InactiveReported.Add((cid, why))) return;
        PluginLog.Information($"[ArmoryStow] LowGear: rule not applied for now ({why}); gear is entrusted the usual way.");
    }

    /// <summary>收尾搬移不做的理由；null＝可以搬。</summary>
    internal static string WhyNotStow()
    {
        if(!C.ArmoryStowLowGearEnabled) return "turned off";
        if(!DeliveryModeAllows()) return ArmoryStow.DeliveryModeNotUsingArmory;
        if(!ArmoryStow.MoveAvailable) return "MoveItemSlot is not resolved on this client";
        if(Rejected.Contains(Player.CID)) return "a move was rejected earlier (until the game or plugin restarts)";
        return null;
    }

    /// <summary>收尾那一批：只搬背包裡符合道具條件的格；逐件點名取這個角色所有僱員用的存放計畫的聯集。</summary>
    internal static ArmoryStow.Batch NewBatch()
    {
        var ims = Data.GetIMSettings();
        var named = NamedInCurrentCharacterPlans();
        return new ArmoryStow.Batch(Owner, C.ArmoryStowReserveSlots, (type, slot, itemId) => IsLowGearItem(itemId, ims, named));
    }

    private static HashSet<uint> NamedInCurrentCharacterPlans()
    {
        var ret = new HashSet<uint>();
        var data = Utils.GetCurrentCharacterData();
        if(data == null) return ret;
        foreach(var r in data.RetainerData)
        {
            var adata = Utils.GetAdditionalData(data.CID, r.Name);
            var plan = C.EntrustPlans.FirstOrDefault(x => x.Guid == adata.EntrustPlan && !x.ManualPlan);
            if(plan != null) ret.UnionWith(plan.EntrustItems);
        }
        return ret;
    }
}
