using AutoRetainer.Internal;
using AutoRetainer.Services;
using AutoRetainer.UI.Overlays;
using AutoRetainerAPI.Configuration;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Utility;
using ECommons.ExcelServices;
using ECommons.ExcelServices.TerritoryEnumeration;
using ECommons.Throttlers;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace AutoRetainer.Modules.GcHandin;

internal static unsafe partial class AutoGCHandin
{
    private static bool IsReadyToOperate(AtkUnitBase* GCSupplyListAddon)
    {
        try
        {
            // ⚠️ 這個 try/catch 對下面的問題完全無效:AVE 在 .NET Core 是 corrupted-state exception,
            //    攔不到。上界(NodeListCount > 20)與**元素判空**是兩件事,只做上界時元素為 null 照樣穿過去,
            //    所以取節點一律走 Utils.GetNodeSafe。
            if(GCSupplyListAddon == null || !IsAddonReady(GCSupplyListAddon)) return false;
            if(GCSupplyListAddon->UldManager.NodeListCount <= 20) return false;
            var node = Utils.GetNodeSafe(&GCSupplyListAddon->UldManager, 5);
            if(node == null) return false;
            return node->IsVisible() && IsSelectedFilterValid(GCSupplyListAddon);
        }
        catch(Exception)
        {
            return false;
        }
    }

    internal static bool IsDone(AtkUnitBase* addon)
    {
        // 上界由呼叫端的 IsReadyToOperate(NodeListCount > 20)保證,但元素本身仍可為 null。
        // 讀不到就回 false ＝「還沒完成」,維持既有的繼續操作路徑(不會謊報完成而提前收工)。
        if(addon == null) return false;
        var node = Utils.GetNodeSafe(&addon->UldManager, 20);
        return node != null && node->IsVisible();
    }

    internal static bool IsSelectedFilterValid(AtkUnitBase* addon)
    {
        if(addon == null) return false;
        // 🔴 六跳鏈的每一跳都有獨立的 null 路徑,而且兩種爆法都攔不到:GetAsAtkComponentNode()／GetAsAtkTextNode()
        //    是 [MemberFunction],對 null this 呼叫＝當場 AVE;最後那個 &...->NodeText 則是靜默的毒指標 0xC0,
        //    連 ReadSeString 內部的判空都騙得過去。NodeList 索引的上界與元素判空由 Utils.GetNodeSafe 一起做掉。
        //    取不到就回 false ＝「篩選條件尚未確認有效」,呼叫端會繼續等/重試,不會誤判成可以開始繳交。
        var step1 = Utils.GetNodeSafe(&addon->UldManager, 14);
        var comp1 = step1 == null ? null : step1->GetAsAtkComponentNode();
        if(comp1 == null || comp1->Component == null) return false;

        var step2 = Utils.GetNodeSafe(&comp1->Component->UldManager, 1);
        var comp2 = step2 == null ? null : step2->GetAsAtkComponentNode();
        if(comp2 == null || comp2->Component == null) return false;

        var step3 = Utils.GetNodeSafe(&comp2->Component->UldManager, 2);
        if(!Utils.TryGetNodeText(step3, out var text)) return false;
        //4619	Hide Armoury Chest Items
        //4618	Hide Gear Set Items
        //4617	Show All Items
        var hideArmory = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Addon>().GetRow(4619).Text.ToDalamudString().GetText();
        var hideGearSet = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Addon>().GetRow(4618).Text.ToDalamudString().GetText();
        var showAll = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Addon>().GetRow(4617).Text.ToDalamudString().GetText();
        if(text.Equals(hideArmory))
        {
            return true;
        }
        else
        {
            if(C.OfflineData.TryGetFirst(x => x.CID == Svc.PlayerState.ContentId, out var data))
            {
                if(text.EqualsAny(hideGearSet))
                {
                    return IsArmoryChestEnabled() || IsAllItemsEnabled();
                }
                if(text.EqualsAny(showAll))
                {
                    return IsAllItemsEnabled();
                }
            }
        }
        return false;
    }

    /// <summary>
    /// 送出「繳交第 which 列」。真的送出去才回傳 true —— 呼叫端要靠這個決定狀態機能不能前進。
    /// 節流只是防手滑重送的保險，流程本身已經由狀態機擋住重入。
    /// </summary>
    internal static bool InvokeHandin(AtkUnitBase* addon, int which)
    {
        if(addon == null || which < 0) return false;
        if(!FrameThrottler.Throttle("AutoGCHandinCallback", 2)) return false;
        // 清單窗按下不會關(開出獎勵窗),所以帶參數組:同一扇清單對不同列各准按一次;同一列在 15 幀內
        // 不重送(狀態機一輪本來就遠超過 15 幀,這只擋清單因他因關閉中被重進的那幾幀)。
        if(!DialogGuards.TryPressOnce("GrandCompanySupplyList", (nint)addon, "AutoGCHandin.Handin", $"Handin{which}", escapeIsRoutine: true)) return false;
        Callback.Fire(addon, true, 1, which, Callback.ZeroAtkValue);
        return true;
    }

    internal static bool HasInInventory(uint itemID)
    {
        return InventoryManager.Instance()->GetInventoryItemCount(itemID) + InventoryManager.Instance()->GetInventoryItemCount(itemID, true) > 0;
    }

    public static bool IsListReady()
    {
        if(TryGetAddonByName<AtkUnitBase>("GrandCompanySupplyList", out var addon) && IsAddonReady(addon))
        {
            return true;
        }
        return false;
    }

    public static (uint ItemID, uint Seals, int Index)? FindNextHandinItem(bool checkSealCap = true)
    {
        var sealsRemaining = GetMaxSeals() - GetSeals();
        var items = GetHandinItems();
        if(C.AutoGCContinuation && GCContinuation.GetNextPurchaseListing() == null) checkSealCap = false;
        List<(uint ItemID, uint Seals, int Index)> candidates = [];
        for(var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if(Data.GetIMSettings().IMProtectList.Contains(item.ItemID)) continue;
            var seals = (uint)(item.Seals * Utils.GetGCSealMultiplier());
            if(!checkSealCap || sealsRemaining > seals) candidates.Add((item.ItemID, seals, i));
        }
        if(candidates.Count > 0)
        {
            return candidates
                .OrderByDescending(x => Utils.GetGCExchangePlanWithOverrides().Items.Where(i => i.Quantity > 0 || i.QuantitySingleTime > 0).Any(i => i.ItemID == x.ItemID))
                .ThenByDescending(x => Utils.CountItemsInInventory(x.ItemID, null, Utils.PlayerInvetories))
                .ThenBy(x => x.Seals)
                .First();
        }
        return null;
    }

    public static List<(uint ItemID, uint Seals)> GetHandinItems()
    {
        var ret = new List<(uint ItemID, uint Seals)>();
        if(TryGetAddonByName<AtkUnitBase>("GrandCompanySupplyList", out var addon) && IsAddonReady(addon))
        {
            var reader = new ReaderGrandCompanySupplyList(addon);
            if(IsListReady())
            {
                // AddonGrandCompanySupplyList 自己的項目陣列（未具名欄位，偏移由 NativeLayout 從遊戲碼推導）。
                // 🔴 清單重建期間這個指標可能是 null，解參考前一定要檢查 —— AVE 是
                // corrupted-state exception，try/catch 攔不到。
                var ptr = NativeLayout.GetSupplyListEntries(addon);
                if(ptr == null) return ret;
                var count = reader.NumItems;
                // 明顯不合理的筆數視為讀到垃圾，寧可不做也不要照著走出去。
                if(count > 1000) return ret;
                for(var i = 0; i < count; i++)
                {
                    var entry = ptr[i];
                    ret.Add((entry.ItemID, entry.Seals));
                }
            }
        }
        return ret;
    }

    public static uint GetSeals()
    {
        return GetGC() == 0 ? 0 : InventoryManager.Instance()->GetCompanySeals(GetGC());
    }

    public static uint GetMaxSeals()
    {
        return GetGC() == 0 ? 0 : InventoryManager.Instance()->GetMaxCompanySeals(GetGC());
    }

    public static byte GetGC()
    {
        return PlayerState.Instance()->GrandCompany;
    }

    public static byte GetRank()
    {
        if(GetGC() == 1) return PlayerState.Instance()->GCRankMaelstrom;
        if(GetGC() == 2) return PlayerState.Instance()->GCRankTwinAdders;
        if(GetGC() == 3) return PlayerState.Instance()->GCRankImmortalFlames;
        return 0;
    }

    public static bool IsValidGCTerritory()
    {
        if(GetGC() == 1) return Svc.ClientState.TerritoryType == MainCities.Limsa_Lominsa_Upper_Decks;
        if(GetGC() == 2) return Svc.ClientState.TerritoryType == MainCities.New_Gridania;
        if(GetGC() == 3) return Svc.ClientState.TerritoryType == MainCities.Uldah_Steps_of_Nald;
        return false;
    }
}
