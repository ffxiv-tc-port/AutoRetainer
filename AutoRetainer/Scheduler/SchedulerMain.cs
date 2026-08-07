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

    /// <summary>使用者在 UI 上親手切換「啟用」核取方塊時的統一入口(主視窗與僱員列表懸浮列共用)。
    ///
    /// 這個核取方塊原本在多角模式執行中會被 <c>BeginDisabled</c> 鎖住，要按住 CTRL 才點得動。
    /// 🔴 鎖的理由是真的：手動 <see cref="EnablePlugin"/> 會把 <see cref="Reason"/> 從
    /// <see cref="PluginEnableReason.MultiMode"/> 覆蓋成 Auto/Manual，而 <see cref="Tick"/> 只有在
    /// Reason 是 MultiMode 時才會在本角色收工後「關閉僱員列表 ＋ 停用外掛(＋開寶箱／分解)」。
    /// 換成 Auto/Manual 之後改走 <c>C.TaskCompletedBehavior*</c>，其預設值是
    /// <see cref="TaskCompletedBehavior.Stay_in_retainer_list_and_keep_plugin_enabled"/>：
    /// 角色會一直站在傳喚鈴前，<c>IsOccupied()</c> 恆真，<see cref="MultiMode.Tick"/> 的每一條動作分支
    /// 都被擋住 ＝ 多角模式停在原地不換角，而且開寶箱／分解被靜默跳過。
    ///
    /// 使用者裁定「永遠可介入」，所以鎖已經拿掉。為了讓介入不會把排程器留在上面那個狀態，
    /// 多角模式執行中手動啟用時**沿用 MultiMode 這個理由**——使用者拿到的仍然是「按下去就生效」，
    /// 只是收工後的收尾行為與多角模式自己啟用時一致。主視窗標題會顯示 <c>[MultiMode]</c>，
    /// 所以這件事在列上看得見，不是只藏在 log 裡。
    ///
    /// ⚠️ 停用方向**不會**連帶關掉多角模式(那是使用者沒要求的行為改動，而且旁邊就有獨立的
    /// 「Multi」核取方塊)。多角模式仍在跑時它會在下一輪自己把外掛重新打開，這一點寫進了
    /// 說明圖示與這裡的 Information log。停用也**不會**中止已經排進 TaskManager 的工作。</summary>
    internal static void SetEnabledByUser(bool enable, PluginEnableReason manualReason)
    {
        if(enable)
        {
            var reason = MultiMode.Active ? PluginEnableReason.MultiMode : manualReason;
            if(reason != manualReason)
            {
                PluginLog.Information($"[UserToggle] Plugin enabled by user while MultiMode is active - using reason {reason} instead of {manualReason}, so MultiMode's completion path (close retainer list, disable plugin, coffers/desynthesis) still runs and MultiMode does not stall at the bell.");
            }
            else
            {
                PluginLog.Information($"[UserToggle] Plugin enabled by user, reason: {reason}.");
            }
            EnablePlugin(reason);
        }
        else
        {
            DisablePlugin();
            if(MultiMode.Active)
            {
                PluginLog.Information("[UserToggle] Plugin disabled by user while MultiMode is active. MultiMode itself stays on and will enable the plugin again when it moves on to the next retainer or character - untick \"Multi\" as well if you want it to stay off. Tasks already queued are not aborted.");
            }
            else
            {
                PluginLog.Information("[UserToggle] Plugin disabled by user.");
            }
        }
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
                                if(TryDrainEntrustVendorPass())
                                {
                                    //every retainer's venture business is settled for this cycle - the
                                    //deferred entrust-duplicates/auto-vendor pass just took one retainer
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
                        // 🔴 空間不足時**不能**跳過這個雇員去收下一個：收取探險成果是「送進玩家背包」，
                        // 而遊戲對每一次收取都套用同一條規則（LogMessage 4338「無法完成委託，背包裡需要至少
                        // 2格空位。」），所以下一個雇員一定會撞到同一面牆。能做的只有先把「會把道具搬出背包」
                        // 的批次跑掉，真的沒東西可搬了才停，並且停的時候要講清楚還剩誰沒收。
                        // ✅ 沒收到的探險成果不會消失：遊戲把「已歸來」當成雇員的持續狀態（Addon 2316/2319
                        // 的 [探險歸來] 標記、LogMessage 2361「無法進行委託，有進行中或已歸來的探險。」），
                        // 未收取前連新委託都下不了，所以成果只會留著等下一輪，不會遺失。
                        else if(!Utils.IsInventoryStateReadable())
                        {
                            // 🔴 讀不到容器時空格數會是 0，跟「背包真的滿了」完全同形。這條分支的終點是
                            // DisablePlugin()，屬於破壞性動作，所以讀數不可信時一律什麼都不做、等下一幀。
                            Utils.RethrottleGeneric();
                        }
                        // 先跑存入雇員／自動賣出：這是整個週期裡唯一會把道具「移出玩家背包」的步驟，
                        // 也就是唯一有機會把空間騰回來的路徑。它被關在同一個空間閘門後面時，
                        // 撞滿一次就再也救不回來（12786ae 把它從每個雇員的行程裡挪到批次之後所引入的迴歸）。
                        else if(TryDrainEntrustVendorPass())
                        {
                            if(EzThrottler.Throttle("InventoryFullEntrustFirst", 10000))
                            {
                                DuoLog.Warning(Loc.T("Inventory is full - running the pending entrust/auto-vendor pass first to try to free up space."));
                            }
                        }
                        else
                        {
                            if(EzThrottler.Throttle("CloseRetainerList", 1000))
                            {
                                DuoLog.Warning($"Your inventory is full");
                                ReportUncollectedRetainers();
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

    internal static string GetNextRetainerName() => EnumeratePendingRetainers().FirstOrDefault();

    /// <summary>Every enabled retainer of the current character that still needs a venture visit this
    /// cycle, in the order the scheduler would visit them. <see cref="GetNextRetainerName"/> takes the
    /// first of these; the inventory-full report lists all of them, so "stopped early" tells the user
    /// exactly which retainers are left instead of just that something stopped.</summary>
    internal static IEnumerable<string> EnumeratePendingRetainers()
    {
        if(!GameRetainerManager.Ready) yield break;
        if(!C.OfflineData.TryGetFirst(x => x.CID == Svc.ClientState.LocalContentId, out var cdata)) yield break;

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
                yield return rname;
            }
        }
    }

    /// <summary>Takes the next retainer off the deferred entrust-duplicates/auto-vendor queue and
    /// enqueues its visit, one retainer per call. Returns whether anything was queued.
    ///
    /// Both steps only ever move items OUT of the player's inventory (entrust hands them to the
    /// retainer, auto-vendor sells them), so this is safe - and useful - to run while the inventory
    /// is too full to collect ventures.</summary>
    private static bool TryDrainEntrustVendorPass()
    {
        while(PendingEntrustVendorPostprocess.Count > 0)
        {
            var next = PendingEntrustVendorPostprocess[0];
            PendingEntrustVendorPostprocess.RemoveAt(0);
            if(!Utils.TryGetRetainerByName(next, out _)) continue;

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
            return true;
        }
        return false;
    }

    /// <summary>Names the retainers whose finished ventures were NOT collected because the inventory
    /// ran out of space, so the remaining manual work is a known, bounded list rather than a guess.</summary>
    private static void ReportUncollectedRetainers()
    {
        try
        {
            var pending = EnumeratePendingRetainers().ToList();
            if(pending.Count == 0) return;
            DuoLog.Warning(string.Format(Loc.T("Not collected this run ({0}): {1}"), pending.Count, pending.Print()));
            DuoLog.Information(Loc.T("Venture results stay on the retainer until they are collected, so nothing is lost - free up inventory space and they will be picked up on the next run."));
        }
        catch(Exception e) { e.Log(); }
    }

    /// <summary>Whether the deferred entrust/vendor batch pass would actually find anything to do for
    /// the retainer whose inventory is currently loaded (called mid-visit, right after venture business),
    /// so this retainer isn't reopened later for nothing. Vendor only ever needs the player's own
    /// inventory; entrust's "unconditional" items/categories are checked against the player's carried
    /// counts, and duplicates are checked against this retainer's live inventory since it's already
    /// open right now - none of this is guessable without the retainer being open.</summary>
    /// <remarks>
    /// 讀不到的容器／格位一律 <c>continue</c>，也就是**只可能少報工作、不可能多報**。回傳值只用來決定
    /// 「要不要把這個雇員排進待辦」，少報＝這一輪不重開他，下一輪重新評估時就會補回來；
    /// 多報才是有代價的（白開一次雇員），而跳過永遠不會造成多報。
    /// ⚠️ 解參考 null 在 .NET Core 是 corrupted-state exception，try/catch 攔不到，只能靠事前檢查。
    /// </remarks>
    private static unsafe bool RetainerHasEntrustOrVendorWork(EntrustPlan? plan)
    {
        var vs = Data.GetIMSettings();

        if(vs.IMEnableAutoVendor)
        {
            foreach(var invType in InventorySpaceManager.GetAllowedToSellInventoryTypes())
            {
                var inv = InventoryManager.Instance()->GetInventoryContainer(invType);
                if(inv == null || inv->Items == null) continue;
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
                if(item == null) continue;
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
                    if(item == null) continue;
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
                            if(playerItem == null) continue;
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
