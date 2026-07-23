using AutoRetainer.Internal;
using AutoRetainer.Internal.InventoryManagement;
using AutoRetainer.Scheduler.Handlers;
using AutoRetainer.Scheduler.Tasks;
using AutoRetainerAPI.Configuration;
using ECommons.ExcelServices;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System.Collections.Immutable;

namespace AutoRetainer.Scheduler;

internal static unsafe class SchedulerMain
{
    internal static bool PluginEnabledInternal;
    internal static bool PluginEnabled
    {
        get
        {
            return PluginEnabledInternal && !IPC.Suppressed;
        }
        private set
        {
            PluginEnabledInternal = value;
        }
    }

    internal static bool CanAssignQuickExploration => C.EnableAssigningQuickExploration && !C.DontReassign && Utils.GetVenturesAmount() > 1;
    internal static volatile uint VentureOverride = 0;
    internal static volatile bool RetainerPostProcessLocked = false;
    internal static volatile bool CharacterPostProcessLocked = false;
    internal static ImmutableList<string> RetainerPostprocess = Array.Empty<string>().ToImmutableList();
    internal static ImmutableList<string> CharacterPostprocess = Array.Empty<string>().ToImmutableList();

    /// <summary>Retainers (this character, this automation cycle) still waiting for their deferred
    /// entrust-duplicates/auto-vendor batch pass, run once every retainer's venture business is settled.</summary>
    internal static List<string> PendingEntrustVendorPostprocess = [];

    internal static PluginEnableReason Reason { get; set; }

    internal static bool? EnablePlugin(PluginEnableReason reason)
    {
        Reason = reason;
        PluginEnabled = true;
        DebugLog($"Plugin is enabled, reason: {reason}");
        return true;
    }

    internal static bool? DisablePlugin()
    {
        PluginEnabled = false;
        DebugLog($"Plugin disabled");
        return true;
    }

    internal static void Tick()
    {
        if(PluginEnabled)
        {
            if(C.RetainerSense)
            {
                MultiMode.ValidateAutoAfkSettings();
            }
            if(C.OldRetainerSense)
            {
                MultiMode.ValidateAutoAfkSettings();
            }
            if(TryGetAddonByName<AtkUnitBase>("RetainerList", out var addon) && addon->IsVisible)
            {
                if(Utils.GenericThrottle)
                {
                    if(!P.TaskManager.IsBusy)
                    {
                        if(Utils.IsInventoryFree())
                        {
                            var retainer = GetNextRetainerName();
                            if(retainer != null && Utils.TryGetRetainerByName(retainer, out var ret))
                            {
                                if(EzThrottler.Throttle("ScheduleSelectRetainer", 2000))
                                {
                                    P.TaskManager.Enqueue(() => RetainerListHandlers.SelectRetainerByName(retainer));

                                    var adata = Utils.GetAdditionalData(Svc.ClientState.LocalContentId, ret.Name.ToString());

                                    VentureOverride = 0;

                                    IPC.FireSendRetainerToVentureEvent(retainer);

                                    if(VentureOverride > 0)
                                    {
                                        DebugLog($"Using VentureOverride = {VentureOverride}");
                                        ret.ProcessVenturePlanner(VentureOverride);
                                    }
                                    else if(!adata.IsVenturePlannerActive())
                                    {
                                        //resend retainer

                                        if(ret.VentureID != 0)
                                        {
                                            if(C.DontReassign || Utils.GetVenturesAmount() < 2)
                                            {
                                                TaskCollectVenture.Enqueue();
                                            }
                                            else
                                            {
                                                TaskReassignVenture.Enqueue();
                                            }
                                        }
                                        else
                                        {
                                            if(CanAssignQuickExploration)
                                            {
                                                TaskAssignQuickVenture.Enqueue();
                                            }
                                        }
                                    }
                                    else
                                    {
                                        var next = adata.GetNextPlannedVenture();
                                        DebugLog($"Next planned venture: {next}, current venture: {ret.VentureID}");
                                        var completed = adata.IsLastPlannedVenture();
                                        DebugLog($"Is last planned venture: {completed}");
                                        if(next == 0)
                                        {
                                            var t = ($"Next venture ID is zero, planner is to be disabled");
                                            if(!completed)
                                            {
                                                DuoLog.Warning(t);
                                            }
                                            else
                                            {
                                                DebugLog(t);
                                            }
                                        }
                                        if(next == 0 || (completed && adata.VenturePlan.PlanCompleteBehavior != PlanCompleteBehavior.Restart_plan))
                                        {
                                            DebugLog($"Completed and behavior is {adata.VenturePlan.PlanCompleteBehavior}");
                                            if(adata.VenturePlan.PlanCompleteBehavior == PlanCompleteBehavior.Repeat_last_venture)
                                            {
                                                DebugLog($"Reassigning this venture and disabling planner");
                                                TaskReassignVenture.Enqueue();
                                            }
                                            else
                                            {
                                                TaskCollectVenture.Enqueue();
                                                if(adata.VenturePlan.PlanCompleteBehavior == PlanCompleteBehavior.Assign_Quick_Venture)
                                                {
                                                    DebugLog($"Assigning quick venture");
                                                    TaskAssignQuickVenture.Enqueue();
                                                }
                                            }
                                            adata.EnablePlanner = false;
                                            DebugLog($"Now disabling planner");
                                        }
                                        else
                                        {
                                            ret.ProcessVenturePlanner(next);
                                        }
                                        if(completed)
                                        {
                                            adata.VenturePlanIndex = 0;
                                        }
                                        adata.VenturePlanIndex++;
                                    }

                                    // Entrusting duplicates and auto-vendoring are deferred to a final batch
                                    // pass (see PendingEntrustVendorPostprocess below) that only runs once every
                                    // retainer has had its venture business settled, instead of interleaving them
                                    // into each individual retainer visit. Only queue retainers that actually have
                                    // something to do - this retainer's own inventory is already loaded right now
                                    // (we're mid-visit), so it's checked live instead of just "is this enabled".
                                    var selectedPlan = C.EntrustPlans.FirstOrDefault(x => x.Guid == adata.EntrustPlan && !x.ManualPlan);
                                    if(RetainerHasEntrustOrVendorWork(selectedPlan) && !PendingEntrustVendorPostprocess.Contains(retainer))
                                    {
                                        PendingEntrustVendorPostprocess.Add(retainer);
                                    }

                                    //withdraw gil
                                    if(adata.WithdrawGil)
                                    {
                                        if(adata.Deposit)
                                        {
                                            if(TaskDepositGil.Gil > 0) TaskDepositGil.Enqueue(adata.WithdrawGilPercent);
                                        }
                                        else
                                        {
                                            TaskWithdrawGil.Enqueue(adata.WithdrawGilPercent);
                                        }
                                    }

                                    //fire event, let other plugins deal with retainer
                                    TaskPostprocessRetainerIPC.Enqueue(retainer);

                                    if(C.RetainerMenuDelay > 0)
                                    {
                                        TaskWaitSelectString.Enqueue(C.RetainerMenuDelay);
                                    }
                                    P.TaskManager.Enqueue(RetainerHandlers.SelectQuit);
                                    P.TaskManager.Enqueue(RetainerHandlers.ConfirmCantBuyback);
                                }
                            }
                            else
                            {
                                if(PendingEntrustVendorPostprocess.Count > 0)
                                {
                                    //every retainer's venture business is settled for this cycle - now run the
                                    //deferred entrust-duplicates/auto-vendor pass, one retainer per visit
                                    var next = PendingEntrustVendorPostprocess[0];
                                    PendingEntrustVendorPostprocess.RemoveAt(0);
                                    if(Utils.TryGetRetainerByName(next, out _))
                                    {
                                        var adata = Utils.GetAdditionalData(Svc.ClientState.LocalContentId, next);
                                        P.TaskManager.Enqueue(() => RetainerListHandlers.SelectRetainerByName(next));

                                        var selectedPlan = C.EntrustPlans.FirstOrDefault(x => x.Guid == adata.EntrustPlan && !x.ManualPlan);
                                        if(C.EnableEntrustManager && selectedPlan != null)
                                        {
                                            TaskEntrustDuplicates.EnqueueNew(selectedPlan);
                                        }

                                        if(Data.GetIMSettings().IMEnableAutoVendor)
                                        {
                                            TaskVendorItems.Enqueue();
                                        }

                                        if(C.RetainerMenuDelay > 0)
                                        {
                                            TaskWaitSelectString.Enqueue(C.RetainerMenuDelay);
                                        }
                                        P.TaskManager.Enqueue(RetainerHandlers.SelectQuit);
                                        P.TaskManager.Enqueue(RetainerHandlers.ConfirmCantBuyback);
                                    }
                                }
                                else if((C.Stay5 || MultiMode.Active) && !Utils.IsAllCurrentCharacterRetainersHaveMoreThan5Mins())
                                {
                                    //nothing
                                }
                                else
                                {
                                    if(Reason == PluginEnableReason.MultiMode)
                                    {
                                        DebugLog($"Scheduling closing and disabling plugin as MultiMode is running");
                                        P.TaskManager.Enqueue(RetainerListHandlers.CloseRetainerList);
                                        P.TaskManager.Enqueue(DisablePlugin);
                                        if(Data.GetIMSettings().IMEnableCofferAutoOpen) TaskOpenAllCoffers.Enqueue();
                                        if(Data.GetIMSettings().IMEnableItemDesynthesis) TaskDesynthItems.Enqueue();
                                    }
                                    else if(Reason == PluginEnableReason.Artisan)
                                    {
                                        DebugLog($"Scheduling closing as Artisan is running");
                                        P.TaskManager.Enqueue(RetainerListHandlers.CloseRetainerList);
                                        P.TaskManager.Enqueue(DisablePlugin);
                                    }
                                    else
                                    {
                                        void Process(TaskCompletedBehavior behavior)
                                        {
                                            //DebugLog($"Behavior: {behavior}");
                                            if(behavior.EqualsAny(TaskCompletedBehavior.Stay_in_retainer_list_and_disable_plugin, TaskCompletedBehavior.Close_retainer_list_and_disable_plugin))
                                            {
                                                DebugLog($"Scheduling plugin disabling (behavior={behavior})");
                                                P.TaskManager.Enqueue(DisablePlugin);
                                            }
                                            if(behavior.EqualsAny(TaskCompletedBehavior.Close_retainer_list_and_disable_plugin, TaskCompletedBehavior.Close_retainer_list_and_keep_plugin_enabled))
                                            {
                                                DebugLog($"Scheduling retainer list closing (behavior={behavior})");
                                                P.TaskManager.Enqueue(RetainerListHandlers.CloseRetainerList);
                                            }
                                        }

                                        if(Reason == PluginEnableReason.Auto)
                                        {
                                            Process(C.TaskCompletedBehaviorAuto);
                                        }
                                        else if(Reason == PluginEnableReason.Manual)
                                        {
                                            Process(C.TaskCompletedBehaviorManual);
                                        }
                                        else if(Reason == PluginEnableReason.Access)
                                        {
                                            Process(C.TaskCompletedBehaviorAccess);
                                        }
                                    }
                                }
                            }
                        }
                        else
                        {
                            if(EzThrottler.Throttle("CloseRetainerList", 1000))
                            {
                                DuoLog.Warning($"Your inventory is full");
                                if(MultiMode.Active)
                                {
                                    DebugLog($"Scheduling retainer list closing (multi mode)");
                                    P.TaskManager.Enqueue(RetainerListHandlers.CloseRetainerList);
                                }
                                else
                                {
                                    void Process(TaskCompletedBehavior behavior)
                                    {
                                        DebugLog($"Behavior: {behavior}");
                                        if(behavior.EqualsAny(TaskCompletedBehavior.Close_retainer_list_and_disable_plugin, TaskCompletedBehavior.Close_retainer_list_and_keep_plugin_enabled))
                                        {
                                            DebugLog($"Scheduling retainer list closing (behavior={behavior})");
                                            P.TaskManager.Enqueue(RetainerListHandlers.CloseRetainerList);
                                        }
                                    }

                                    if(Reason == PluginEnableReason.Auto)
                                    {
                                        Process(C.TaskCompletedBehaviorAuto);
                                    }
                                    else if(Reason == PluginEnableReason.Manual)
                                    {
                                        Process(C.TaskCompletedBehaviorManual);
                                    }
                                    else if(Reason == PluginEnableReason.Access)
                                    {
                                        Process(C.TaskCompletedBehaviorAccess);
                                    }
                                }
                                DisablePlugin();
                            }
                        }
                    }
                }
            }
            else
            {
                //DuoLog.Information($"123");
                if(C.OldRetainerSense || SchedulerMain.Reason == PluginEnableReason.Artisan)
                {
                    if(Utils.AnyRetainersAvailableCurrentChara())
                    {
                        if(!IsOccupied())
                        {
                            if(EzThrottler.Check("InteractWithBellDelay") && EzThrottler.Throttle("InteractWithBellGeneralEnqueue", 5000))
                            {
                                TaskInteractWithNearestBell.Enqueue();
                            }
                        }
                        else
                        {
                            EzThrottler.Throttle("InteractWithBellDelay", 2500, true);
                        }
                    }
                }
            }
        }
    }

    internal static string GetNextRetainerName()
    {
        if(GameRetainerManager.Ready)
        {
            if(C.OfflineData.TryGetFirst(x => x.CID == Svc.ClientState.LocalContentId, out var cdata))
            {
                List<OfflineRetainerData> retainerData = [.. cdata.RetainerData];
                if(C.LeastMBSFirst)
                {
                    retainerData = [.. cdata.RetainerData.OrderBy(x => x.MBItems)];
                }

                for(var i = 0; i < retainerData.Count; i++)
                {
                    var r = retainerData[i];
                    var rname = r.Name.ToString();
                    var adata = Utils.GetAdditionalData(Svc.ClientState.LocalContentId, rname);
                    if(P.GetSelectedRetainers(Svc.ClientState.LocalContentId).Contains(rname)
                        && r.GetVentureSecondsRemaining() <= C.UnsyncCompensation && (r.VentureID != 0 || CanAssignQuickExploration || (adata.EnablePlanner && adata.VenturePlan.ListUnwrapped.Count > 0)))
                    {
                        return rname;
                    }
                }
            }
        }
        return null;
    }

    /// <summary>Whether the deferred entrust/vendor batch pass would actually find anything to do for
    /// the retainer whose inventory is currently loaded (called mid-visit, right after venture business),
    /// so this retainer isn't reopened later for nothing. Vendor only ever needs the player's own
    /// inventory; entrust's "unconditional" items/categories are checked against the player's carried
    /// counts, and duplicates are checked against this retainer's live inventory since it's already
    /// open right now - none of this is guessable without the retainer being open.</summary>
    private static unsafe bool RetainerHasEntrustOrVendorWork(EntrustPlan? plan)
    {
        var vs = Data.GetIMSettings();

        if(vs.IMEnableAutoVendor)
        {
            foreach(var invType in InventorySpaceManager.GetAllowedToSellInventoryTypes())
            {
                var inv = InventoryManager.Instance()->GetInventoryContainer(invType);
                if(inv == null) continue;
                for(var i = 0; i < inv->Size; i++)
                {
                    var item = inv->Items[i];
                    if(item.ItemId == 0) continue;
                    if((item.Quantity < vs.IMAutoVendorHardStackLimit || vs.IMAutoVendorHardIgnoreStack.Contains(item.ItemId))
                        && vs.IMAutoVendorHard.Contains(item.ItemId)
                        && !TaskDesynthItems.DesynthEligible(item.ItemId))
                    {
                        return true;
                    }
                }
            }
        }

        if(!C.EnableEntrustManager || plan == null)
        {
            return false;
        }

        var allowedPlayerInventories = plan.GetAllowedInventories();

        //unconditional entrusts: player is carrying more than the configured keep-amount
        foreach(var type in allowedPlayerInventories)
        {
            var inv = InventoryManager.Instance()->GetInventoryContainer(type);
            if(inv == null) continue;
            for(var i = 0; i < inv->Size; i++)
            {
                var item = InventoryManager.Instance()->GetInventorySlot(type, i);
                if(item->ItemId == 0 || item->Quantity == 0) continue;
                if(plan.ExcludeProtected && vs.IMProtectList.Contains(item->ItemId)) continue;

                int? toKeep = null;
                if(plan.EntrustItems.Contains(item->ItemId))
                {
                    toKeep = plan.EntrustItemsAmountToKeep.SafeSelect(item->ItemId);
                }
                else
                {
                    var data = ExcelItemHelper.Get(item->ItemId);
                    if(data != null && plan.EntrustCategories.TryGetFirst(c => c.ID == data.Value.ItemUICategory.RowId, out var catInfo))
                    {
                        toKeep = catInfo.AmountToKeep;
                    }
                }

                if(toKeep != null && Utils.GetItemCount(allowedPlayerInventories, item->ItemId) > toKeep)
                {
                    return true;
                }
            }
        }

        //duplicates: this retainer's own inventory is loaded right now (mid-visit), so it's safe to check
        if(plan.Duplicates)
        {
            foreach(var type in Utils.RetainerInventoriesWithCrystals)
            {
                if(type.EqualsAny(InventoryType.Crystals, InventoryType.RetainerCrystals)) continue;
                var inv = InventoryManager.Instance()->GetInventoryContainer(type);
                if(inv == null) continue;
                for(var i = 0; i < inv->Size; i++)
                {
                    var item = inv->GetInventorySlot(i);
                    if(item->ItemId == 0) continue;
                    if(plan.ExcludeProtected && vs.IMProtectList.Contains(item->ItemId)) continue;

                    if(plan.DuplicatesMultiStack)
                    {
                        if(Utils.GetItemCount(allowedPlayerInventories, item->ItemId) > 0)
                        {
                            return true;
                        }
                        continue;
                    }

                    var data = ExcelItemHelper.Get(item->ItemId);
                    if(data == null || data.Value.IsUnique) continue;
                    if(data.Value.StackSize - item->Quantity <= 0) continue;

                    foreach(var playerType in allowedPlayerInventories)
                    {
                        var playerInv = InventoryManager.Instance()->GetInventoryContainer(playerType);
                        if(playerInv == null) continue;
                        for(var q = 0; q < playerInv->Size; q++)
                        {
                            var playerItem = playerInv->GetInventorySlot(q);
                            if(playerItem->ItemId == item->ItemId && playerItem->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality) == item->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality))
                            {
                                return true;
                            }
                        }
                    }
                }
            }
        }

        return false;
    }
}
