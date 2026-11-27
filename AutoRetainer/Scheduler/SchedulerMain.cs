using AutoRetainer.Internal;
using AutoRetainer.Internal.InventoryManagement;
using AutoRetainer.Scheduler.Handlers;
using AutoRetainer.Scheduler.Tasks;
using AutoRetainer.Services;
using AutoRetainerAPI.Configuration;
using ECommons.ExcelServices;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System.Collections.Immutable;

namespace AutoRetainer.Scheduler;

internal static unsafe partial class SchedulerMain
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
            System.Threading.Interlocked.Increment(ref EnabledWriteCount);
        }
    }

    /// <summary>排程開關被寫入的次數（開或關、值有沒有變都算）；重載後的鈴前接續用它判斷載入後有沒有人動過排程。</summary>
    internal static int EnabledWriteCount;

    internal static bool CanAssignQuickExploration => C.EnableAssigningQuickExploration && !C.DontReassign && Utils.GetVenturesAmount() > 1;
    internal static volatile uint VentureOverride = 0;
    internal static volatile bool RetainerPostProcessLocked = false;
    internal static volatile bool CharacterPostProcessLocked = false;
    internal static ImmutableList<string> RetainerPostprocess = Array.Empty<string>().ToImmutableList();
    internal static ImmutableList<string> CharacterPostprocess = Array.Empty<string>().ToImmutableList();

    /// <summary>Retainers (this character, this automation cycle) still waiting for their deferred entrust-duplicates/auto-vendor batch pass.
    /// The retainer's own inventory is only readable while that retainer is open, so it is captured once.
    /// 🔑 Deliberately NOT "the ids that were duplicate work at scan time": the player gains items during this phase, so a set frozen against the player's inventory would miss work that only came into existence afterwards.
    /// </summary>
    /// <remarks><c>Room</c>：這個僱員真的開著時量到的「放不放得進」快照（<see cref="CaptureEntrustNoRoom"/>、<see cref="EntrustRoomSnapshot"/>）；null＝沒量到，重驗不篩。
    /// <c>DuplicatesCandidates</c> 同一時機量；null＝沒量到，開僱員前不重驗、照樣開。</remarks>
    internal static List<(string Retainer, HashSet<uint> DuplicatesCandidates, EntrustRoomSnapshot Room)> PendingEntrustVendorPostprocess = [];

    /// <summary>傳喚鈴佔用狀態每變一次就加一（框架執行緒）。快照的空格數只在量的那一次開鈴內有效。</summary>
    internal static int EntrustBellSession;

    /// <summary>Drops the deferred entrust/vendor queue. It holds retainer <b>names</b> belonging to one
    /// specific character, and nothing else in the queue identifies which character that was, so a leftover
    /// entry would be matched by name against the next character's retainers. It otherwise only drains
    /// down naturally, so an interrupted cycle (logout mid-run, plugin disabled, retainer list closed by
    /// hand) would leave entries behind.</summary>
    internal static void ClearPendingEntrustVendorPass(string reason)
    {
        if(PendingEntrustVendorPostprocess.Count == 0) return;
        PluginLog.Information($"[EntrustVendorPass] Dropping {PendingEntrustVendorPostprocess.Count} pending retainer(s) [{PendingEntrustVendorPostprocess.Select(x => x.Retainer).Print()}]: {reason}");
        PendingEntrustVendorPostprocess.Clear();
    }

    internal static PluginEnableReason Reason { get; set; }

    /// <summary>true ＝ 稀有品繳交循環正在跑,所以 AutoRetainer 自己的一般僱員自動處理這一幀要整個讓路。
    /// 🔴 為什麼一定要互斥:兩邊**驅動的是同一個僱員清單,而且共用同一條 <see cref="P.TaskManager"/>**。
    /// ⚠️ 互斥要擋的是一般處理在循環中途**搶僱員**那一步,與收尾設定(做完後關不關清單)無關。
    /// ⚠️ 這是**延後不是取消**:排程器的啟用狀態與 <see cref="Reason"/> 都原封不動留著,只是這段期間不 Tick,循環一停下來下一幀就自己接回去跑。
    /// </summary>
    internal static bool RetainerAutomationDeferred { get; private set; }

    /// <summary>每幀更新一次 <see cref="RetainerAutomationDeferred"/>,並且**只在翻轉時**各印一行。
    /// 🔴 從 <see cref="AutoRetainer.Tick"/> 無條件呼叫,不要塞進 <c>PluginEnabled</c> 底下 ——
    /// 僱員感知自動開鈴那條路徑在排程器沒啟用時照樣會動,兩個消費端都要看得到同一個旗標。</summary>
    internal static void UpdateRetainerAutomationDeferral()
    {
        var defer = GCExpertDeliveryLoop.Running;
        if(defer == RetainerAutomationDeferred) return;
        RetainerAutomationDeferred = defer;
        if(defer)
        {
            PluginLog.Information($"[RetainerAutomationMutex] The expert delivery loop took the wheel, so AutoRetainer's own retainer automation stands down: no venture collecting/reassigning, no entrust/auto-vendor pass, no retainer-sense bell opening until the loop stops. Both drive the same retainer list through the same task queue, and the normal cycle's completion behaviour (Auto={C.TaskCompletedBehaviorAuto}) closes the retainer list out from under the loop. Deferred, not cancelled - the scheduler stays as it is (enabled={PluginEnabledInternal}, reason={Reason}) and picks up again by itself. Nothing is lost: uncollected venture results stay on the retainer until they are collected.");
        }
        else
        {
            PluginLog.Information($"[RetainerAutomationMutex] The expert delivery loop has stopped, so AutoRetainer's own retainer automation is live again (enabled={PluginEnabledInternal}, reason={Reason}). Any ventures that came due during the run are collected on the next normal pass.");
        }
    }

    /// <summary>false ＝ 僱員排程不可開始：少了「探險清單更新」的訊號，指派探險那一步只會等到逾時、停在委託視窗上。</summary>
    internal static bool RetainerPassAvailable => P.Memory?.VentureListUpdateTracked == true;

    // 在開始前拒絕並關掉排程，不讓它走到一半卡住；誰把排程打開，下一幀就會到這裡。
    private static void RefuseRetainerPass()
    {
        if(EzThrottler.Throttle("RetainerPassUnavailable", 10000))
        {
            DuoLog.Warning("AutoRetainer：遊戲改版後收不到「僱員探險清單更新」，僱員自動處理（收取／指派探險、存入、提領）已停用，請等插件更新；潛水艇與飛空艇不受影響。");
        }
        DisablePlugin();
    }

    internal static bool? EnablePlugin(PluginEnableReason reason)
    {
        Reason = reason;
        PluginEnabled = true;
        DebugLog($"Plugin is enabled, reason: {reason}");
        return true;
    }

    /// <summary>同 <see cref="EnablePlugin"/> 但不寫 log：給持有 MultiMode 開關鎖的呼叫端用，鎖內不做 I/O。</summary>
    internal static void EnablePluginQuiet(PluginEnableReason reason)
    {
        Reason = reason;
        PluginEnabled = true;
    }

    internal static bool? DisablePlugin()
    {
        PluginEnabled = false;
        DebugLog($"Plugin disabled");
        return true;
    }

    /// <summary>使用者在 UI 上親手切換「啟用」核取方塊時的統一入口(主視窗與僱員列表懸浮列共用)。
    /// 🔴 多角模式執行中手動啟用時**沿用 MultiMode 這個理由**：換成 Auto/Manual 的話收工後不會關清單，角色一直站在鈴前，
    /// 多角模式就停在原地不換角，而且開寶箱／分解被靜默跳過。使用者裁定「永遠可介入」，所以核取方塊不鎖。
    /// ⚠️ 停用方向**不會**連帶關掉多角模式。停用也**不會**中止已經排進 TaskManager 的工作。
    /// </summary>
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
        // 🔴 稀有品繳交循環在跑的時候整個讓路。閘門放在這裡(而不是呼叫端)是刻意的:
        //    呼叫端那個 if 區塊裡還有 C.SelectedRetainers 的初始化,那件事沒有理由跟著停。
        if(RetainerAutomationDeferred) return;
        if(PluginEnabled)
        {
            if(!RetainerPassAvailable)
            {
                RefuseRetainerPass();
                return;
            }
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

                                    var adata = Utils.GetAdditionalData(Svc.PlayerState.ContentId, ret.Name.ToString());

                                    EnqueueVentureBusiness(ret, retainer, adata);

                                    // Entrusting duplicates and auto-vendoring are deferred to a final batch pass (see
                                    // PendingEntrustVendorPostprocess) that only runs once every retainer has had its venture
                                    // business settled. The retainer is not open yet here (selecting it is only queued), so the
                                    // duplicates half is queued as unknown (null) and measured by CaptureEntrustNoRoom once it is.
                                    var selectedPlan = C.EntrustPlans.FirstOrDefault(x => x.Guid == adata.EntrustPlan && !x.ManualPlan);
                                    var hasSharedWork = HasSharedEntrustVendorWork(selectedPlan, retainer);
                                    var duplicatesPossible = C.EnableEntrustManager && selectedPlan != null && selectedPlan.Duplicates;
                                    var queuedOnlyForDuplicates = false;
                                    if((hasSharedWork || duplicatesPossible)
                                        && !PendingEntrustVendorPostprocess.Any(x => x.Retainer == retainer))
                                    {
                                        PendingEntrustVendorPostprocess.Add((retainer, duplicatesPossible ? null : [], null));
                                        queuedOnlyForDuplicates = !hasSharedWork;
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
                                    // 這段 Tick 跑的當下還沒選到這個僱員（選僱員只是排進佇列），僱員背包容器裡還不是它的東西，
                                    // 所以「重複道具候選」與「放不放得進」排成任務，等它真的開著、選單就緒再量。逾時只跳過這一步，不中止後面的任務。
                                    if(C.EnableEntrustManager && selectedPlan != null && PendingEntrustVendorPostprocess.Any(x => x.Retainer == retainer))
                                    {
                                        P.TaskManager.Enqueue(() => CaptureEntrustNoRoom(retainer, selectedPlan, queuedOnlyForDuplicates), "EntrustVendorPass.CaptureNoRoom", new(timeLimitMS: 5000, abortOnTimeout: false));
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
                                        if(C.ArmoryStowLowGearEnabled) TaskStowLowGear.Enqueue("multi mode wrap-up");
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
                        // 🔴 空間不足時**不能**跳過這個僱員去收下一個：收取探險成果是「送進玩家背包」，遊戲對每一次收取都套用
                        // 同一條規則（LogMessage 4338：背包裡需要至少 2 格空位），下一個僱員一定會撞到同一面牆。能做的只有先把
                        // 「會把道具搬出背包」的批次跑掉，真的沒東西可搬了才停，並且停的時候要講清楚還剩誰沒收。
                        // ✅ 沒收到的探險成果不會消失：「已歸來」是僱員的持續狀態（LogMessage 2361），未收取前連新委託都下不了，
                        // 所以成果只會留著等下一輪。
                        else if(!Utils.IsInventoryStateReadable())
                        {
                            // 🔴 讀不到容器時空格數會是 0，跟「背包真的滿了」完全同形。這條分支的終點是
                            // DisablePlugin()，屬於破壞性動作，所以讀數不可信時一律什麼都不做、等下一幀。
                            Utils.RethrottleGeneric();
                        }
                        // 先跑存入僱員／自動賣出：這是整個週期裡唯一會把道具「移出玩家背包」的步驟，
                        // 也就是唯一有機會把空間騰回來的路徑。它被關在同一個空間閘門後面時，
                        // 撞滿一次就再也救不回來。
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
                                    // 背包滿時正是排除留下的裝備最該搬走的時候；搬了也只是空出背包，不會讓這個角色回頭再收。
                                    if(C.ArmoryStowLowGearEnabled) TaskStowLowGear.Enqueue("inventory full");
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

    /// <summary>排入一個僱員這一趟的探險處理（收成果／重派／指派快速探險／探險計畫／IPC 覆寫），回傳選了哪一種（給診斷用）。
    /// 呼叫端負責先排入選這個僱員、之後排入告辭。一般排程器與稀有品繳交循環共用這一份判斷。只在框架執行緒呼叫。</summary>
    internal static string EnqueueVentureBusiness(GameRetainerManager.Retainer ret, string retainer, AdditionalRetainerData adata)
    {
        string action;

        VentureOverride = 0;

        IPC.FireSendRetainerToVentureEvent(retainer);

        if(VentureOverride > 0)
        {
            DebugLog($"Using VentureOverride = {VentureOverride}");
            ret.ProcessVenturePlanner(VentureOverride);
            action = "override";
        }
        else if(!adata.IsVenturePlannerActive())
        {
            //resend retainer

            if(ret.VentureID != 0)
            {
                if(C.DontReassign || Utils.GetVenturesAmount() < 2)
                {
                    TaskCollectVenture.Enqueue();
                    action = "collect";
                }
                else
                {
                    TaskReassignVenture.Enqueue();
                    action = "reassign";
                }
            }
            else
            {
                if(CanAssignQuickExploration)
                {
                    TaskAssignQuickVenture.Enqueue();
                    action = "assign-quick";
                }
                else
                {
                    action = "none";
                }
            }
        }
        else
        {
            action = "planner";
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

        return action;
    }

    internal static string GetNextRetainerName() => EnumeratePendingRetainers().FirstOrDefault();

    /// <summary>Every enabled retainer of the current character that still needs a venture visit this
    /// cycle, in the order the scheduler would visit them. <see cref="GetNextRetainerName"/> takes the
    /// first of these; the inventory-full report lists all of them, so "stopped early" tells the user
    /// exactly which retainers are left instead of just that something stopped.</summary>
    internal static IEnumerable<string> EnumeratePendingRetainers()
    {
        if(!GameRetainerManager.Ready) yield break;
        if(!C.OfflineData.TryGetFirst(x => x.CID == Svc.PlayerState.ContentId, out var cdata)) yield break;

        List<OfflineRetainerData> retainerData = [.. cdata.RetainerData];
        if(C.LeastMBSFirst)
        {
            retainerData = [.. cdata.RetainerData.OrderBy(x => x.MBItems)];
        }

        for(var i = 0; i < retainerData.Count; i++)
        {
            var r = retainerData[i];
            var rname = r.Name.ToString();
            var adata = Utils.GetAdditionalData(Svc.PlayerState.ContentId, rname);
            if(P.GetSelectedRetainers(Svc.PlayerState.ContentId).Contains(rname)
                && r.GetVentureSecondsRemaining() <= C.UnsyncCompensation && (r.VentureID != 0 || CanAssignQuickExploration || (adata.EnablePlanner && adata.VenturePlan.ListUnwrapped.Count > 0)))
            {
                yield return rname;
            }
        }
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
}
