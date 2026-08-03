using AutoRetainer.Internal;
using AutoRetainer.Modules.Voyage.PartSwapper;
using AutoRetainer.Modules.Voyage.Tasks;
using AutoRetainer.Modules.Voyage.VoyageCalculator;
using AutoRetainerAPI.Configuration;
using Dalamud.Game.Text.SeStringHandling;
using ECommons.Throttlers;

namespace AutoRetainer.Modules.Voyage;

internal static unsafe class VoyageMain
{
    private static bool IsInVoyagePanel = false;

    internal static WaitOverlay WaitOverlay;

    internal static void Init()
    {
        Svc.Framework.Update += Tick;
        Svc.Toasts.ErrorToast += Toasts_ErrorToast;
        WaitOverlay = new();
        P.WindowSystem.AddWindow(WaitOverlay);
    }

    private static void Toasts_ErrorToast(ref SeString message, ref bool isHandled)
    {
        if(MultiMode.Active || P.TaskManager.IsBusy)
        {
            var txt = message.GetText();
            if(txt == Lang.VoyageInventoryError)
            {
                DuoLog.Warning($"[Voyage] Your inventory is full!");
                VoyageScheduler.Enabled = false;
                P.TaskManager.Abort();
                P.TaskManager.Enqueue(VoyageScheduler.SelectQuitVesselSelectorMenu);
                P.TaskManager.Enqueue(VoyageScheduler.SelectExitMainPanel);
                if(C.FailureNoInventory == WorkshopFailAction.StopPlugin)
                {
                    MultiMode.Enabled = false;
                    VoyageScheduler.Enabled = false;
                }
                else if(C.FailureNoInventory == WorkshopFailAction.ExcludeChar)
                {
                    Data.WorkshopEnabled = false;
                }
            }
            if(txt.ContainsAny(StringComparison.OrdinalIgnoreCase, Lang.UnableToRepairVessel))
            {
                TaskRepairAll.Abort = true;
                DuoLog.Warning($"[Voyage] You are out of repair components!");
                if(C.FailureNoRepair == WorkshopFailAction.ExcludeVessel)
                {
                    Data.GetEnabledVesselsData(TaskRepairAll.Type).Remove(TaskRepairAll.Name);
                }
                else if(C.FailureNoRepair == WorkshopFailAction.ExcludeChar)
                {
                    Data.WorkshopEnabled = false;
                }
                else if(C.FailureNoRepair == WorkshopFailAction.StopPlugin)
                {
                    MultiMode.Enabled = false;
                    VoyageScheduler.Enabled = false;
                }
            }
        }
    }

    internal static void Shutdown()
    {
        Svc.Framework.Update -= Tick;
        Svc.Toasts.ErrorToast -= Toasts_ErrorToast;
    }

    internal static void Tick(object _)
    {
        if(VoyageUtils.IsVoyageCondition())
        {
            if(Svc.Targets.Target.IsVoyagePanel())
            {
                if(!IsInVoyagePanel)
                {
                    PluginLog.Debug($"Entered voyage panel");
                    IsInVoyagePanel = true;
                    //Notify.Info($"Entered voyage panel");
                    if(IsKeyPressed(C.Suppress))
                    {
                        Notify.Warning("No operation was requested by user");
                    }
                    else
                    {
                        if(C.SubsAutoResend2)
                        {
                            if(Data.AnyEnabledVesselsAvailable())
                            {
                                VoyageScheduler.Enabled = true;
                                PluginLog.Debug($"<!> Enabled voyage scheduler");
                            }
                            else
                            {
                                Notify.Warning($"Warning!\nDeployables were not enabled as there are nothing to process yet");
                            }
                        }
                    }
                }
            }
        }
        else
        {
            if(IsInVoyagePanel)
            {
                IsInVoyagePanel = false;
                //Notify.Info("Closed voyage panel");
                VoyageScheduler.Enabled = false;
                PluginLog.Debug($"<!> Exited voyage panel, disabled voyage scheduler");
            }
        }

        if(VoyageUtils.IsInVoyagePanel())
        {
            if(EzThrottler.Throttle("Voyage.WriteOfflineData", 100))
            {
                VoyageUtils.WriteOfflineData();
            }
        }

        if(VoyageScheduler.Enabled)
        {
            DoWorkshopPanelTick();
        }
    }

    // Environment.TickCount64 of the moment the voyage panel first looked "covered by a vessel
    // parts window while nothing is queued". long.MaxValue means we are not in that state.
    private static long VesselPartsWindowStuckSince = long.MaxValue;

    /// <summary>
    /// Recovers from an aborted task queue that left the repair / component-change window open.
    ///
    /// P.TaskManager is created with abortOnTimeout:true and TaskManager.Abort() clears the WHOLE
    /// queue, so a single step that times out, throws, or returns null anywhere inside
    /// TaskRepairAll / TaskChangeComponents also discards the trailing CloseRepair /
    /// CloseChangeComponents step (and everything that was queued after it).
    /// CompanyCraftSupply / AirShipPartsMenu then stay up, GetCurrentWorkshopPanelType reports
    /// PanelType.None because it only ever looks for SelectString, DoWorkshopPanelTick has no
    /// branch for None, and BailoutManager only rescues SelectString / _CharaSelectReturn /
    /// Dialogue - so the scheduler ticks every frame doing nothing, silently, until the user
    /// notices the window sitting there.
    ///
    /// This is deliberately narrow: it only ever fires the exact same close callback the normal
    /// flow uses, only while the deployables scheduler is enabled (i.e. AutoRetainer is the one
    /// driving the panel), only while the task queue is empty, and only while the voyage menu is
    /// actually covered. If the assumption is wrong the worst case is a close callback that the
    /// game ignores, which is what already happens whenever CloseRepair is throttled.
    /// </summary>
    private static void TickVesselPartsWindowWatchdog()
    {
        // IsVesselPartsWindowOpen is checked before the panel type on purpose - it is the cheap
        // test and it is false in every normal frame.
        if(!C.EnableBailout
            || Utils.IsBusy
            || !VoyageScheduler.IsVesselPartsWindowOpen()
            || VoyageUtils.GetCurrentWorkshopPanelType() != PanelType.None)
        {
            VesselPartsWindowStuckSince = long.MaxValue;
            return;
        }

        if(VesselPartsWindowStuckSince == long.MaxValue)
        {
            VesselPartsWindowStuckSince = Environment.TickCount64;
            return;
        }

        // Floor of 5s so a user who set BailoutTimeout very low can not make this fire during the
        // few frames it takes a normally-closed window to actually disappear.
        var stuckFor = Environment.TickCount64 - VesselPartsWindowStuckSince;
        if(stuckFor < Math.Max(C.BailoutTimeout, 5) * 1000) return;
        if(!EzThrottler.Throttle("Voyage.VesselPartsWindowBailout", 3000)) return;

        DuoLog.Warning($"[Bailout] Closing stuck vessel parts window");
        // Information, not Debug: this is the line we need from a user's log when it does not work.
        PluginLog.Information($"[Bailout] Vessel parts window blocked the voyage panel for {stuckFor}ms with an empty task queue. PartPickerOpen={VoyageScheduler.IsPartPickerOpen()}");
        VoyageScheduler.CloseRepair();
    }

    private static void DoWorkshopPanelTick()
    {
        TickVesselPartsWindowWatchdog();
        if(!P.TaskManager.IsBusy)
        {
            if(FrameThrottler.Check("SchedulerRestartCooldown"))
            {
                var data = Data;
                var panel = VoyageUtils.GetCurrentWorkshopPanelType();
                if(panel == PanelType.TypeSelector)
                {
                    if(data.AnyEnabledVesselsAvailable(VoyageType.Airship))
                    {
                        if(EzThrottler.Throttle("DoWorkshopPanelTick.EnqueuePanelSelector", 1000))
                        {
                            P.TaskManager.Enqueue(VoyageScheduler.SelectAirshipManagement);
                        }
                    }
                    else if(data.AnyEnabledVesselsAvailable(VoyageType.Submersible))
                    {
                        if(EzThrottler.Throttle("DoWorkshopPanelTick.EnqueuePanelSelector", 1000))
                        {
                            P.TaskManager.Enqueue(VoyageScheduler.SelectSubManagement);
                        }
                    }
                    else if(!data.AreAnyEnabledVesselsReturnInNext(5 * 60))
                    {
                        if(EzThrottler.Throttle("DoWorkshopPanelTick.EnqueuePanelSelector", 1000))
                        {
                            P.TaskManager.Enqueue(VoyageScheduler.SelectExitMainPanel);
                        }
                    }
                }
                else if(panel == PanelType.Submersible)
                {
                    ScheduleResend(VoyageType.Submersible);
                }
                else if(panel == PanelType.Airship)
                {
                    ScheduleResend(VoyageType.Airship);
                }
            }
        }
        else
        {
            FrameThrottler.Throttle("SchedulerRestartCooldown", 10, true);
        }
    }

    private static void ScheduleResend(VoyageType type)
    {
        var next = VoyageUtils.GetNextCompletedVessel(type);
        if(next != null)
        {
            var adata = Data.GetAdditionalVesselData(next, type);
            var data = Data.GetOfflineVesselData(next, type) ?? throw new NullReferenceException($"Offline vessel data for {next}, {type} is null");
            if((VoyageUtils.DontReassign || adata.VesselBehavior == VesselBehavior.Finalize || (C.FinalizeBeforeResend && Data.AreAnyEnabledVesselsReturnInNext(0, false, true))) && data.ReturnTime != 0)
            {
                if(EzThrottler.Throttle("DoWorkshopPanelTick.ScheduleResend", 1000))
                {
                    TaskFinalizeVessel.Enqueue(next, type, true);
                }
            }
            else
            {
                if(adata.VesselBehavior.EqualsAny(VesselBehavior.LevelUp, VesselBehavior.Unlock, VesselBehavior.Use_plan, VesselBehavior.Redeploy))
                {
                    if(EzThrottler.Throttle("DoWorkshopPanelTick.ScheduleResend", 1000))
                    {
                        if(data.ReturnTime != 0)
                        {
                            TaskFinalizeVessel.Enqueue(next, type, false);
                        }
                        else
                        {
                            TaskSelectVesselByName.Enqueue(next, type);
                        }

                        PartSwapperScheduler.EnqueuePartSwappingIfNeeded(next, type);

                        P.TaskManager.EnqueueMulti(
                            new(() => CurrentSubmarine.Get() != null),
                            new(() =>
                            {
                                P.TaskManager.BeginStack();
                                try
                                {
                                    foreach(var x in C.SubmarineUnlockPlans)
                                    {
                                        if(x.EnforcePlan)
                                        {
                                            PluginLog.Information($"Unlock plan {x.Name} is set as enforced");
                                            if(TaskDeployOnUnlockRoute.GetUnlockPointsFromPlan(x, UnlockMode.SpamOne).TryGetFirst(out var unlockPoint) && !x.ExcludedRoutes.Any(s => s == unlockPoint.point))
                                            {
                                                PluginLog.Information($"Enforcing plan {x.Name} on current submarine");
                                                TaskDeployOnUnlockRoute.Enqueue(next, type, x, UnlockMode.SpamOne);
                                                goto EndTask;
                                            }
                                        }
                                    }
                                    if(adata.VesselBehavior == VesselBehavior.LevelUp)
                                    {
                                        TaskDeployOnBestExpVoyage.Enqueue(next, type);
                                    }
                                    else if(adata.VesselBehavior == VesselBehavior.Unlock)
                                    {
                                        var mode = adata.UnlockMode;
                                        var plan = VoyageUtils.GetSubmarineUnlockPlanByGuid(adata.SelectedUnlockPlan) ?? VoyageUtils.GetDefaultSubmarineUnlockPlan();
                                        if(plan.EnforceDSSSinglePoint && TaskDeployOnUnlockRoute.GetUnlockPointsFromPlan(plan, UnlockMode.SpamOne).TryGetFirst(out var unlockPoint) && VoyageUtils.GetSubmarineExploration(unlockPoint.point).Value.Map.RowId == 1)
                                        {
                                            PluginLog.Information($"Override unlock mode to {UnlockMode.SpamOne}");
                                            mode = UnlockMode.SpamOne;
                                        }
                                        if(mode == UnlockMode.WhileLevelling)
                                        {
                                            TaskDeployOnBestExpVoyage.Enqueue(next, type, plan);
                                        }
                                        else if(mode.EqualsAny(UnlockMode.SpamOne, UnlockMode.MultiSelect))
                                        {
                                            TaskDeployOnUnlockRoute.Enqueue(next, type, plan, mode);
                                        }
                                        else
                                        {
                                            throw new ArgumentOutOfRangeException(nameof(mode));
                                        }
                                    }
                                    else if(adata.VesselBehavior == VesselBehavior.Use_plan)
                                    {
                                        var plan = VoyageUtils.GetSubmarinePointPlanByGuid(adata.SelectedPointPlan);
                                        if(plan != null && plan.Points.Count >= 1 && plan.Points.Count <= 5)
                                        {
                                            var current = CurrentSubmarine.Get()->CurrentExplorationPoints.ToArray().Select(x => (uint)x).Where(x => x != 0);
                                            if(!current.SequenceEqual(plan.Points))
                                            {
                                                TaskDeployOnPointPlan.Enqueue(next, type, plan);
                                            }
                                            else
                                            {
                                                TaskRedeployVessel.Enqueue(next, type);
                                            }
                                        }
                                        else
                                        {
                                            DuoLog.Error($"Invalid plan selected (Points.Count={plan.Points.Count})");
                                        }
                                    }
                                    else if(adata.VesselBehavior == VesselBehavior.Redeploy)
                                    {
                                        TaskRedeployVessel.Enqueue(next, type);
                                    }
                                }
                                catch(Exception e)
                                {
                                    e.Log();
                                }
                            EndTask:
                                P.TaskManager.InsertStack();
                            })
                        );

                    }
                }
            }
        }
        else
        {
            if(PartSwapperScheduler.EnqueueSubmersibleRegistrationIfPossible())
            {
                PluginLog.Information($"Enqueued submersible registration");
            }
            else if(!Data.AreAnyEnabledVesselsReturnInNext(type, 1 * 60))
            {
                if(EzThrottler.Throttle("DoWorkshopPanelTick.ScheduleResendQuitPanel", 1000))
                {
                    TaskQuitMenu.Enqueue();
                }
            }
        }
    }
}
