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

internal static unsafe class AutoGCHandin
{
    internal static AutoGCHandinOverlay Overlay;
    internal static bool Operation = false;

    internal static bool IsEnabled()
    {
        if(C.OfflineData.TryGetFirst(x => x.CID == Svc.ClientState.LocalContentId, out var d))
        {
            return d.GCDeliveryType != GCDeliveryType.Disabled;
        }
        return false;
    }
    internal static bool IsArmoryChestEnabled()
    {
        if(C.OfflineData.TryGetFirst(x => x.CID == Svc.ClientState.LocalContentId, out var d))
        {
            return d.GCDeliveryType.EqualsAny(GCDeliveryType.Hide_Gear_Set_Items, GCDeliveryType.Show_All_Items);
        }
        return false;
    }

    internal static bool IsAllItemsEnabled()
    {
        Safety.Check();
        if(C.OfflineData.TryGetFirst(x => x.CID == Svc.ClientState.LocalContentId, out var d))
        {
            return d.GCDeliveryType == GCDeliveryType.Show_All_Items;
        }
        return false;
    }

    internal static void Init()
    {
        Overlay = new();
        P.WindowSystem.AddWindow(Overlay);
    }

    internal static void Tick()
    {
        if(Svc.Condition[ConditionFlag.OccupiedInQuestEvent] && TryGetAddonByName<AtkUnitBase>("GrandCompanySupplyList", out var addon))
        {
            if(addon->X != 0 || addon->Y != 0)
            {
                Overlay.Position = new(addon->X, addon->Y - Overlay.height);
            }
        }
        if(Svc.Condition[ConditionFlag.OccupiedInQuestEvent] && IsEnabled())
        {
            Safety.Check();
            if(Operation && HandleConfirmation())
            {
                PluginLog.Debug($"Handle 1");
                //
            }
            else if(Operation && HandleYesno())
            {
                PluginLog.Debug($"Handle 2");
                //
            }
            else
            {
                HandleGCList();
            }
        }
        else
        {
            if(Overlay.Allowed) Overlay.Allowed = false;
            if(Operation) Operation = false;
        }
    }

    // ── 繳交節奏量測（暫時性）────────────────────────────────────────────
    // 實測每件固定 0.672 秒（八段、800+ 件，p90 0.722），把節流從 10 幀砍到 3 幀
    // 變化 0.000 秒 —— 所以瓶頸不在我們的幀節流。但「到底卡在哪一道閘門」我還沒證明過，
    // 不該用推測當結論。這裡把一個繳交週期切成四段各自計時，跑一次就知道。
    // 一律 Information：使用者的記錄等級會濾掉 Debug/Verbose。
    // Grep 標記：GCDIAG
    private static long cycleDeliverAt;      // 按下「交付」的時刻
    private static long cycleRewardGoneAt;   // 獎勵視窗消失
    private static long cycleListReadyAt;    // 軍需品清單重新可互動
    private static long cycleThrottleOkAt;   // 幀節流放行
    private static bool cycleRewardSeen;

    private static long NowMs => Environment.TickCount64;

    private static void GcDiagReset()
    {
        cycleDeliverAt = cycleRewardGoneAt = cycleListReadyAt = cycleThrottleOkAt = 0;
        cycleRewardSeen = false;
    }

    private static bool HandleConfirmation()
    {
        const string Throttler = "Handin.HandleConfirmation";
        if(TryGetAddonByName<AddonGrandCompanySupplyReward>("GrandCompanySupplyReward", out var addon) && IsAddonReady(&addon->AtkUnitBase))
        {
            cycleRewardSeen = true;
            if(addon->DeliverButton->IsEnabled && FrameThrottler.Throttle(Throttler, 10))
            {
                new AddonMaster.GrandCompanySupplyReward(addon).Deliver();
                DebugLog($"Delivering Item");
                cycleDeliverAt = NowMs;
                cycleRewardGoneAt = cycleListReadyAt = cycleThrottleOkAt = 0;
                return true;
            }
        }
        else
        {
            // 獎勵視窗已經不在了 —— 記下第一個看到它消失的時刻。
            if(cycleRewardSeen && cycleDeliverAt != 0 && cycleRewardGoneAt == 0)
            {
                cycleRewardGoneAt = NowMs;
                cycleRewardSeen = false;
            }
            //FrameThrottler.Throttle(Throttler, 4, true);
        }
        return false;
    }

    private static bool HandleYesno()
    {
        const string Throttler = "Handin.Yesno";
        if(TryGetAddonByName<AddonSelectYesno>("SelectYesno", out var addon) && IsAddonReady(&addon->AtkUnitBase) && Operation)
        {
            if(addon->YesButton->IsEnabled)
            {
                var str = addon->PromptText->NodeText.GetText().Cleanup();
                DebugLog($"SelectYesno encountered: {str}");
                //102434	Do you really want to trade a high-quality item?
                if(str.Equals(GenericHelpers.GetText(Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Addon>().GetRow(102434).Text).Cleanup()))
                {
                    if(FrameThrottler.Throttle(Throttler, 10))
                    {
                        new AddonMaster.SelectYesno((IntPtr)addon).Yes();
                        DebugLog($"Selecting yes");
                    }
                }
            }
            else
            {
                //FrameThrottler.Throttle(Throttler, 4, true);
            }
        }
        else
        {
            //FrameThrottler.Throttle(Throttler, 4, true);
        }
        return false;
    }

    private static void HandleGCList()
    {
        if(TryGetAddonByName<AtkUnitBase>("GrandCompanySupplyList", out var addon) && IsReadyToOperate(addon))
        {
            if(Operation)
            {
                if(IsDone(addon))
                {
                    var s = $"Automatic handin has been completed";
                    DuoLog.Information(s);
                    if(C.GCHandinNotify)
                    {
                        Utils.TryNotify(s);
                    }
                    Operation = false;
                    GCContinuation.EnqueueDeliveryClose();
                    if(Utils.GetGCExchangePlanWithOverrides().FinalizeByPurchasing)
                    {
                        GCContinuation.EnqueueInitiation(false);
                    }
                }
                else
                {
                    Overlay.Allowed = true;
                    // 清單重新可互動的時刻（能走到這裡就代表 addon 已 ready）
                    if(cycleDeliverAt != 0 && cycleListReadyAt == 0) cycleListReadyAt = NowMs;
                    if(FrameThrottler.Check("Handin.HandleConfirmation"))
                    {
                        if(cycleDeliverAt != 0 && cycleThrottleOkAt == 0) cycleThrottleOkAt = NowMs;
                        try
                        {
                            var reader = new ReaderGrandCompanySupplyList(addon);

                            var nextItem = FindNextHandinItem();
                            if(reader.NumItems == GetHandinItems().Count)
                            {
                                if(nextItem != null)
                                {
                                    var has = AutoGCHandin.HasInInventory(nextItem.Value.ItemID);
                                    var itemName = ExcelItemHelper.GetName(nextItem.Value.ItemID);
                                    DebugLog($"Seals: {GetSeals()}/{GetMaxSeals()}, for item {nextItem.Value.Seals} | {ExcelItemHelper.GetName(nextItem.Value.ItemID)}: {has}");
                                    if(!has)
                                    {
                                        throw new GCHandinInterruptedException($"Item {itemName} was not found in inventory");
                                    }
                                    DebugLog($"Handing in item {itemName} for {nextItem.Value.Seals} seals (index={nextItem.Value.Index})");
                                    if(cycleDeliverAt != 0)
                                    {
                                        var now = NowMs;
                                        // 一個週期切成四段：交付→獎勵視窗消失→清單可互動→幀節流放行→送出下一件
                                        PluginLog.Information(
                                            $"[GCDIAG] cycle total={now - cycleDeliverAt}ms | " +
                                            $"deliver→rewardGone={(cycleRewardGoneAt == 0 ? -1 : cycleRewardGoneAt - cycleDeliverAt)}ms | " +
                                            $"rewardGone→listReady={(cycleRewardGoneAt == 0 || cycleListReadyAt == 0 ? -1 : cycleListReadyAt - cycleRewardGoneAt)}ms | " +
                                            $"listReady→throttleOk={(cycleListReadyAt == 0 || cycleThrottleOkAt == 0 ? -1 : cycleThrottleOkAt - cycleListReadyAt)}ms | " +
                                            $"throttleOk→invoke={(cycleThrottleOkAt == 0 ? -1 : now - cycleThrottleOkAt)}ms");
                                        GcDiagReset();
                                    }
                                    InvokeHandin(addon, nextItem.Value.Index);
                                }
                                else
                                {
                                    if(FindNextHandinItem(false) == null)
                                    {
                                        GCContinuation.EnqueueDeliveryClose();
                                        throw new GCHandinInterruptedException("Auto GC handin completed");
                                    }
                                    else
                                    {
                                        GCContinuation.EnqueueDeliveryClose();
                                        if(C.AutoGCContinuation)
                                        {
                                            GCContinuation.EnqueueInitiation(true);
                                        }
                                        throw new GCHandinInterruptedException("Too many seals, please spend them");
                                    }
                                }
                            }
                        }
                        catch(FormatException e)
                        {
                            PluginLog.Verbose($"{e.Message}");
                        }
                        catch(GCHandinInterruptedException e)
                        {
                            Operation = false;
                            DuoLog.Information($"{e.Message}");
                            if(C.GCHandinNotify && !C.AutoGCContinuation)
                            {
                                Utils.TryNotify(e.Message);
                            }
                        }
                        catch(Exception e)
                        {
                            Operation = false;
                            e.Log();
                        }
                    }
                }
            }
            else
            {
                Overlay.Allowed = IsReadyToOperate(addon);
            }
        }
        else
        {
            Overlay.Allowed = Operation || IsReadyToOperate(addon);
        }
    }

    private static bool IsReadyToOperate(AtkUnitBase* GCSupplyListAddon)
    {
        try
        {
            return
                GCSupplyListAddon != null
                && IsAddonReady(GCSupplyListAddon)
                && GCSupplyListAddon->UldManager.NodeListCount > 20
                && GCSupplyListAddon->UldManager.NodeList[5]->IsVisible()
                && IsSelectedFilterValid(GCSupplyListAddon);
        }
        catch(Exception)
        {
            return false;
        }
    }
    internal static bool IsDone(AtkUnitBase* addon)
    {
        return addon->UldManager.NodeList[20]->IsVisible();
    }
    internal static bool IsSelectedFilterValid(AtkUnitBase* addon)
    {
        var step1 = addon->UldManager.NodeList[14];
        var step2 = step1->GetAsAtkComponentNode()->Component->UldManager.NodeList[1];
        var step3 = step2->GetAsAtkComponentNode()->Component->UldManager.NodeList[2];
        var text = GenericHelpers.ReadSeString(&step3->GetAsAtkTextNode()->NodeText).GetText();
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
            if(C.OfflineData.TryGetFirst(x => x.CID == Svc.ClientState.LocalContentId, out var data))
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

    internal static void InvokeHandin(AtkUnitBase* addon, int which)
    {
        if(FrameThrottler.Throttle("AutoGCHandinCallback", 10)) Callback.Fire(addon, true, 1, which, Callback.ZeroAtkValue);
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
                var ptr = (GCExpectEntry*)*(nint*)((nint)(addon) + 648);
                for(var i = 0; i < reader.NumItems; i++)
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
