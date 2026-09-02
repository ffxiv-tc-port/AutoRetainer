using AutoRetainer.UiHelpers;
using ECommons.Automation;
using ECommons.Automation.UIInput;
using ECommons.GameHelpers;
using ECommons.Throttlers;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace AutoRetainer.Scheduler.Tasks;

// Walks up to the Company Workshop's adventurer doll NPC, opens the Free
// Company Credit Shop and buys Ceruleum Tanks up to the configured target.
internal static unsafe class TaskAutoBuyFuel
{
    private static uint LastSeenCredits = 0;
    private static bool AmountSetForThisDialog = false;

    internal static void Enqueue()
    {
        P.TaskManager.Enqueue(NewYesAlreadyManager.WaitForYesAlreadyDisabledTask);
        P.TaskManager.Enqueue(ApproachDollIfNeeded, "ApproachAdventurerDoll");
        P.TaskManager.Enqueue(SelectNearestDoll);
        P.TaskManager.Enqueue(InteractWithTargetedDoll);
        P.TaskManager.Enqueue(SelectCreditShopMenuEntry, new(timeLimitMS: 15000));
        P.TaskManager.Enqueue(BuyFuelLoop, new(timeLimitMS: 1000 * 60 * 10));
        P.TaskManager.Enqueue(CloseCreditShop);
    }

    private static bool? ApproachDollIfNeeded()
    {
        if(Utils.GetReachableAdventurerDoll() != null) return true;
        var doll = Utils.GetNearestAdventurerDoll(out var distance);
        if(doll == null || distance > 20f) return true; // give up quietly, checked again next tick
        if(Svc.Targets.Target?.Address != doll.Address)
        {
            if(EzThrottler.Throttle("AutoBuyFuel.SetTarget", 200))
            {
                Svc.Targets.Target = doll;
            }
            return false;
        }
        if(EzThrottler.Throttle("AutoBuyFuel.Lockon"))
        {
            Chat.ExecuteCommand("/lockon");
        }
        // Both halves live in the same task here, but that is not protection: this task still returns
        // false until it is in range, so a timeout aborts it with autorun left on. AutomoveManager's
        // watchdog covers that. (It also stops this from re-sending the command every single frame.)
        AutomoveManager.On();
        if(Vector3.Distance(Player.Object.Position, doll.Position) < 4f + Utils.Random * 0.25f)
        {
            AutomoveManager.Off();
            return true;
        }
        return false;
    }

    private static bool? SelectNearestDoll()
    {
        if(IsOccupied()) return false;
        var x = Utils.GetReachableAdventurerDoll();
        if(x != null && Utils.GenericThrottle)
        {
            Svc.Targets.Target = x;
            return true;
        }
        return false;
    }

    private static bool? InteractWithTargetedDoll()
    {
        var x = Svc.Targets.Target;
        if(x != null && x.Name.ToString().ContainsAny(StringComparison.OrdinalIgnoreCase, Lang.AdventurerDollNamePart) && !IsOccupied())
        {
            if(Vector3.Distance(x.Position, Svc.Objects.LocalPlayer.Position) < Utils.GetValidInteractionDistance(x) && x.IsTargetable)
            {
                if(Player.IsAnimationLocked) return false;
                if(Utils.GenericThrottle && EzThrottler.Throttle("AutoBuyFuel.Interact", 5000))
                {
                    TargetSystem.Instance()->InteractWithObject((GameObject*)x.Address, false);
                    return true;
                }
            }
        }
        return false;
    }

    private static bool? SelectCreditShopMenuEntry()
    {
        if(TryGetAddonByName<AtkUnitBase>("FreeCompanyCreditShop", out var a) && IsAddonReady(a)) return true;
        if(Utils.TrySelectSpecificEntry(Lang.FreeCompanyCreditShopMenu, () => Utils.GenericThrottle && EzThrottler.Throttle("AutoBuyFuel.SelectMenu", 1000))) return true;
        // NPC talk menus (with a "Talk"/"Cancel" entry alongside the shop option) can
        // come up as SelectIconString instead of the plain SelectString addon.
        if(TryGetAddonMaster<AddonMaster.SelectIconString>(out var m) && m.IsAddonReady)
        {
            foreach(var entry in m.Entries)
            {
                // 讀到 U+FFFD ＝ 窗記憶體正在變動,這一幀不碰;選項按下即關窗,同一扇只按一次。
                var entryText = entry.Text;
                if(!DialogGuards.TextIsUnstable(entryText) && entryText.StartsWithAny(Lang.FreeCompanyCreditShopMenu))
                {
                    if(EzThrottler.Throttle("AutoBuyFuel.SelectMenu", 1000) && DialogGuards.TryPressOnce("SelectIconString", (nint)m.Base, "AutoBuyFuel.SelectMenu"))
                    {
                        entry.Select();
                    }
                    return false;
                }
            }
        }
        return false;
    }

    private static bool? BuyFuelLoop()
    {
        // Confirm the "spend N points for x99" Yes/No prompt.
        if(TryGetAddonMaster<AddonMaster.SelectYesno>(out var m))
        {
            // 連續購買是刻意的:每次購買開一扇新的確認框。同一扇只按一次(關閉中的窗 IsAddonReady 全過,
            // 信用點回寫讓 EzThrottler 重設時首次必放行,節流擋不住)。讀到 U+FFFD 這一幀不碰。
            var text = m.Text;
            if(!DialogGuards.TextIsUnstable(text) && text.ContainsAny(StringComparison.OrdinalIgnoreCase, Lang.WorkshopBuyFuelConfirm))
            {
                if(EzThrottler.Throttle("AutoBuyFuel.YesNo") && DialogGuards.TryPressOnce("SelectYesno", (nint)m.Base, "AutoBuyFuel.YesNo")) m.Yes();
            }
            return false;
        }
        if(TryGetAddonByName<AtkUnitBase>("FreeCompanyCreditShop", out var a) && IsAddonReady(a))
        {
            var reader = new ReaderFreeCompanyCreditShop(a);
            if(LastSeenCredits != reader.Credits)
            {
                EzThrottler.Reset("AutoBuyFuel.YesNo");
                EzThrottler.Reset("AutoBuyFuel.SetAmount");
                EzThrottler.Reset("AutoBuyFuel.Buy");
                LastSeenCredits = reader.Credits;
                AmountSetForThisDialog = false;
            }
            var listing = reader.Listings.Count > 0 ? reader.Listings[0] : null;
            if(listing == null) return true;
            if(listing.InInventory >= C.AutoBuyFuelTarget) return true;
            if(reader.Credits < listing.Price) return true;

            if(!AmountSetForThisDialog)
            {
                if(EzThrottler.Throttle("AutoBuyFuel.SetAmount"))
                {
                    var remaining = (int)C.AutoBuyFuelTarget - (int)listing.InInventory;
                    var byCredits = (int)(reader.Credits / listing.Price);
                    var set = Math.Max(1, Math.Min(99, Math.Min(remaining, byCredits)));
                    if(TrySetPurchaseAmount(a, set))
                    {
                        PluginLog.Debug($"AutoBuyFuel: setting purchase amount to {set}");
                        AmountSetForThisDialog = true;
                        EzThrottler.Throttle("AutoBuyFuel.Buy", 500, true);
                    }
                }
                return false;
            }
            // 商店窗按下不關(開出確認框):帶參數組;CloseCreditShop 對同一扇按過關閉後不再送。
            if(EzThrottler.Throttle("AutoBuyFuel.Buy") && DialogGuards.TryPressOnce("FreeCompanyCreditShop", (nint)a, "AutoBuyFuel.Buy", "Buy0", escapeIsRoutine: true))
            {
                new FreeCompanyCreditShop(a).Buy(0);
                AmountSetForThisDialog = false;
            }
            return false;
        }
        return true;
    }

    // Node path confirmed via live dump: top-level listing container -> row -> numeric stepper component.
    private static bool TrySetPurchaseAmount(AtkUnitBase* a, int amount)
    {
        try
        {
            // 🔴 這裡原本是「半套邊界檢查」的教科書範例:三層 NodeList 索引全都只在**取到之後**
            //    判空,索引本身既沒驗 NodeListCount 上界也沒判元素。越界時讀到的是相鄰記憶體
            //    而不是 null —— 後面那三行 `== null` 一個都擋不住,拿到的是隨機位址。
            //    ⚠️ 外層的 try/catch 也不是防護:AccessViolationException 在 .NET Core 是
            //    corrupted-state exception,catch 不到(留著是為了擋其他一般例外,不動它)。
            //    任一層取不到就回 false → 呼叫端不會把 AmountSetForThisDialog 設成 true,
            //    下一個節流窗重試;絕不會用「沒設定成功的數量」去按購買。
            if(a == null) return false;
            var containerNode = Utils.GetNodeSafe(&a->UldManager, 21);
            var listingContainer = containerNode == null ? null : containerNode->GetAsAtkComponentNode();
            if(listingContainer == null || listingContainer->Component == null) return false;
            var rowNode = Utils.GetNodeSafe(&listingContainer->Component->UldManager, 1);
            var row = rowNode == null ? null : rowNode->GetAsAtkComponentNode();
            if(row == null || row->Component == null) return false;
            var stepperNode = Utils.GetNodeSafe(&row->Component->UldManager, 5);
            var stepper = stepperNode == null ? null : stepperNode->GetComponent();
            if(stepper == null) return false;
            ((AtkComponentNumericInput*)stepper)->SetValue(amount);
            return true;
        }
        catch(Exception e)
        {
            e.Log();
            return false;
        }
    }

    private static bool? CloseCreditShop()
    {
        if(TryGetAddonByName<AtkUnitBase>("FreeCompanyCreditShop", out var a) && IsAddonReady(a))
        {
            // Close(true) 對關閉中的窗再叫一次同樣未證安全:同一扇只關一次。
            if(EzThrottler.Throttle("AutoBuyFuel.Close") && DialogGuards.TryPressOnce("FreeCompanyCreditShop", (nint)a, "AutoBuyFuel.Close"))
            {
                a->Close(true);
            }
            return false;
        }
        return true;
    }
}
