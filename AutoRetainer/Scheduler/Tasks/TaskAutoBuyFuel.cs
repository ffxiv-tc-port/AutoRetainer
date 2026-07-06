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
        Chat.ExecuteCommand("/automove on");
        if(Vector3.Distance(Player.Object.Position, doll.Position) < 4f + Utils.Random * 0.25f)
        {
            Chat.ExecuteCommand("/automove off");
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
            if(Vector3.Distance(x.Position, Svc.ClientState.LocalPlayer.Position) < Utils.GetValidInteractionDistance(x) && x.IsTargetable)
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
                if(entry.Text.StartsWithAny(Lang.FreeCompanyCreditShopMenu))
                {
                    if(EzThrottler.Throttle("AutoBuyFuel.SelectMenu", 1000))
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
            if(m.Text.ContainsAny(StringComparison.OrdinalIgnoreCase, Lang.WorkshopBuyFuelConfirm))
            {
                if(EzThrottler.Throttle("AutoBuyFuel.YesNo")) m.Yes();
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
            if(EzThrottler.Throttle("AutoBuyFuel.Buy"))
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
            var listingContainer = a->UldManager.NodeList[21]->GetAsAtkComponentNode();
            if(listingContainer == null || listingContainer->Component == null) return false;
            var row = listingContainer->Component->UldManager.NodeList[1]->GetAsAtkComponentNode();
            if(row == null || row->Component == null) return false;
            var stepper = row->Component->UldManager.NodeList[5]->GetComponent();
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
            if(EzThrottler.Throttle("AutoBuyFuel.Close"))
            {
                a->Close(true);
            }
            return false;
        }
        return true;
    }
}
