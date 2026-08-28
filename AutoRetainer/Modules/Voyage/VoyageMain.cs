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

    /// <summary>這一次工房面板的來訪裡,有沒有真的碰到過「已回港待處理」的船。
    ///
    /// <para>🔴 用旗標是因為「整隊處理完」必須是一條**邊**:面板上沒事可做之後,
    /// <see cref="DoWorkshopPanelTick"/> 的離開分支會被節流器每秒放行一次、一直重進,
    /// 直接在那裡通知等於每秒通知一次。旗在「看到有船要處理」時舉起、在離開分支被消費掉。</para>
    ///
    /// <para>⚠️ 失敗路徑天然不會通知:背包滿/沒維修材料會把 <c>VoyageScheduler.Enabled</c> 關掉,
    /// <see cref="DoWorkshopPanelTick"/> 從此不再被呼叫,所以走不到消費點,旗會在下次進面板時被清掉。</para>
    ///
    /// <para>⚠️ 只餵給 TataruPraise 的單向通知,<b>不參與任何流程判斷</b>。</para></summary>
    private static bool VoyageVisitDidWork = false;

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
                    VoyageVisitDidWork = false;
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
                VoyageVisitDidWork = false;
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
                        // 站在工房主選單上、飛空艇與潛水艇兩邊都沒有待處理的船、而且 5 分鐘內也沒有
                        // 要回來的 —— 這就是「這一趟整隊回港處理完了,可以離開面板」的唯一收斂點。
                        // 🔴 放在節流之前、而且一次性消費:這個分支每秒都會再進來一遍。
                        if(VoyageVisitDidWork)
                        {
                            VoyageVisitDidWork = false;
                            TataruPraiseIPC.TryPraise("潛艇整隊回港處理完");
                        }

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

    /// <summary>
    /// 把「這艘船的航線會從哪裡來」講成一句人看得懂的話，給自動派出的診斷 log 用。
    /// ⚠️ Use_plan 讀的是點位計畫、Unlock 讀的是解鎖計畫，兩個欄位各自獨立、都可能是
    /// Guid.Empty 或指向已刪掉的計畫，所以必須按行為挑欄位讀，不能只印其中一個。
    /// 🔴 只讀不寫：這個方法不得有任何副作用，它純粹是為了讓 log 說得清楚。
    /// </summary>
    private static string DescribeBehaviorSource(AdditionalVesselData adata)
    {
        try
        {
            if(adata.VesselBehavior == VesselBehavior.Use_plan)
            {
                var plan = VoyageUtils.GetSubmarinePointPlanByGuid(adata.SelectedPointPlan);
                if(plan == null) return $"點位計畫＝找不到（GUID {adata.SelectedPointPlan}）";
                var trim = PointPlanRange.IsTrimEnabled(plan) ? "已啟用" : "未啟用";
                return $"點位計畫＝「{plan.GetPointPlanName()}」（{plan.Points.Count} 點，航距不足自動裁點：{trim}）";
            }
            if(adata.VesselBehavior == VesselBehavior.Unlock)
            {
                var plan = VoyageUtils.GetSubmarineUnlockPlanByGuid(adata.SelectedUnlockPlan) ?? VoyageUtils.GetDefaultSubmarineUnlockPlan(false);
                return $"解鎖計畫＝「{plan?.Name ?? "（無）"}」，解鎖模式＝{adata.UnlockMode}";
            }
            if(adata.VesselBehavior == VesselBehavior.LevelUp) return "航線來源＝最佳經驗演算法自選";
            if(adata.VesselBehavior == VesselBehavior.Redeploy) return "航線來源＝沿用上一趟的點位";
            return "航線來源＝不派出";
        }
        catch(Exception e)
        {
            return $"（航線來源判讀失敗：{e.Message}）";
        }
    }

    private static void ScheduleResend(VoyageType type)
    {
        var next = VoyageUtils.GetNextCompletedVessel(type);
        if(next != null)
        {
            // 這一趟面板來訪真的有船要處理 —— 讓下面的離開分支有東西可以通知。
            VoyageVisitDidWork = true;
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
                                    // 📌 「面板上設了 A、實際走了 B」這類路由問題，沒有這行就只能靠猜。
                                    // 印的是**實際拿來分派的那一筆** per-vessel 設定，不是 UI 另外讀的一份。
                                    PluginLog.Information($"[Voyage] 準備自動派出 {next}（{type}）：船隻行為＝{adata.VesselBehavior}，{DescribeBehaviorSource(adata)}");
                                    foreach(var x in C.SubmarineUnlockPlans)
                                    {
                                        if(x.EnforcePlan)
                                        {
                                            PluginLog.Information($"Unlock plan {x.Name} is set as enforced");
                                            if(TaskDeployOnUnlockRoute.GetUnlockPointsFromPlan(x, UnlockMode.SpamOne).TryGetFirst(out var unlockPoint) && !x.ExcludedRoutes.Any(s => s == unlockPoint.point))
                                            {
                                                // ⚠️ 這條路徑會蓋掉上面印的船隻行為 —— 兩行都留著才看得出是被誰蓋掉的。
                                                PluginLog.Information($"Enforcing plan {x.Name} on current submarine ({next})：強制解鎖計畫蓋過原本的船隻行為 {adata.VesselBehavior}");
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
                                            // 「上次跑的和計畫一樣就直接重新派遣」這個捷徑必須拿「裁切後」的清單來比，
                                            // 否則啟用裁切的計畫會永遠比不中而每趟都重走一次完整的選點流程。
                                            // 沒啟用裁切時 GetEffectivePoints 回傳的就是 plan.Points 本身，行為完全不變。
                                            var effectivePoints = PointPlanRange.GetEffectivePoints(plan, log: false);
                                            var currentSub = CurrentSubmarine.Get();
                                            if(currentSub == null) throw new InvalidOperationException(CurrentSubmarine.Unavailable);
                                            var current = currentSub->CurrentExplorationPoints.ToArray().Select(x => (uint)x).Where(x => x != 0);
                                            if(!current.SequenceEqual(effectivePoints))
                                            {
                                                TaskDeployOnPointPlan.Enqueue(next, type, plan);
                                            }
                                            else
                                            {
                                                if(PointPlanRange.IsTrimEnabled(plan) && effectivePoints.Count != plan.Points.Count)
                                                {
                                                    PluginLog.Information($"[PointPlanRange] 上次航行的 {effectivePoints.Count} 個點與本次裁切結果相同（計畫原有 {plan.Points.Count} 點），改用重新派遣");
                                                }
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
