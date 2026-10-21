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
    /// <remarks><c>NoRoom</c>：這個僱員真的開著時量到「玩家身上哪些（道具, HQ）它放不進」（<see cref="CaptureEntrustNoRoom"/>）；null＝沒量到，重驗不篩。
    /// <c>DuplicatesCandidates</c> 同一時機量；null＝沒量到，開僱員前不重驗、照樣開。</remarks>
    internal static List<(string Retainer, HashSet<uint> DuplicatesCandidates, HashSet<(uint ItemId, bool Hq)> NoRoom)> PendingEntrustVendorPostprocess = [];

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
                                    var hasSharedWork = HasSharedEntrustVendorWork(selectedPlan);
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

    /// <summary>Takes the next retainer off the deferred entrust-duplicates/auto-vendor queue and
    /// enqueues its visit, one retainer per call. Returns whether anything was queued.
    ///
    /// Both steps only ever move items OUT of the player's inventory (entrust hands them to the
    /// retainer, auto-vendor sells them), so this is safe - and useful - to run while the inventory
    /// is too full to collect ventures.</summary>
    /// <remarks>
    /// 🔴 入列時算出來的「有工作」會過期（auto-vendor 與無條件存入讀的是所有僱員共用的玩家背包），所以開僱員之前重驗共用的那一半。
    /// 失敗方向鎖在安全側：讀不到、拿不到設定、有任何一絲不確定 ⇒ 照樣開啟。
    /// 少開一次會漏掉真正該做的搬運，多開一次只是浪費幾秒。
    /// </remarks>
    private static bool TryDrainEntrustVendorPass()
    {
        while(PendingEntrustVendorPostprocess.Count > 0)
        {
            var (next, duplicatesCandidates, noRoom) = PendingEntrustVendorPostprocess[0];
            PendingEntrustVendorPostprocess.RemoveAt(0);
            if(!Utils.TryGetRetainerByName(next, out _)) continue;

            var adata = Utils.GetAdditionalData(Svc.PlayerState.ContentId, next);
            var selectedPlan = C.EntrustPlans.FirstOrDefault(x => x.Guid == adata.EntrustPlan && !x.ManualPlan);

            // Data null、背包讀不到、或開著時沒量到重複道具候選 ⇒ 不重驗，照樣把僱員開起來。
            var canRevalidate = Data != null && Utils.IsInventoryStateReadable() && duplicatesCandidates != null;
            TaskEntrustDuplicates.EntrustPrecheckHit precheck = default;
            if(canRevalidate
                && !TryFindPlayerHeldCandidate(selectedPlan, duplicatesCandidates, noRoom, out precheck)
                && !TryFindSharedEntrustVendorWork(selectedPlan, noRoom, out precheck))
            {
                if(noRoom != null && (TryFindPlayerHeldCandidate(selectedPlan, duplicatesCandidates, null, out var dropped) || TryFindSharedEntrustVendorWork(selectedPlan, null, out dropped)))
                {
                    PluginLog.Information($"[EntrustVendorPass] Skipping {next}: everything the player still carries for it is something it had no room for when it was open (first: {dropped.Path} item={dropped.ItemId} hq={dropped.Hq}; {noRoom.Count} item/quality pair(s) judged no-room).");
                    continue;
                }
                PluginLog.Information($"[EntrustVendorPass] Skipping {next}: no shared inventory work is left (an earlier retainer in this batch already did it, or there was none), and the player no longer carries any of the {duplicatesCandidates.Count} item(s) this retainer could have taken as duplicates.");
                continue;
            }

            P.TaskManager.Enqueue(() => RetainerListHandlers.SelectRetainerByName(next));

            if(C.EnableEntrustManager && selectedPlan != null)
            {
                TaskEntrustDuplicates.EnqueueNew(selectedPlan, next, precheck);
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

    /// <summary>The half of the deferred entrust/vendor work that is read entirely off the <b>player's</b> inventory: auto-vendor, and entrust's "unconditional" items/categories checked against the player's carried counts.
    /// That inventory is shared by every retainer, so this answer is not specific to any one of them.
    /// The plan is still per-retainer, so this is evaluated with that retainer's plan against the live shared inventory.
    /// </summary>
    /// <remarks>
    /// 讀不到的容器／格位一律 <c>continue</c>，也就是**只可能少報工作、不可能多報**。
    /// ⚠️ 解參考 null 在 .NET Core 是 corrupted-state exception，try/catch 攔不到，只能靠事前檢查。
    /// </remarks>
    private static bool HasSharedEntrustVendorWork(EntrustPlan? plan) => TryFindSharedEntrustVendorWork(plan, null, out _);

    /// <summary><see cref="HasSharedEntrustVendorWork"/> 本體；<paramref name="hit"/> 是讓它回 true 的第一件（給空開診斷用）。</summary>
    /// <param name="noRoom">null＝不篩；否則跳過其中的（道具, HQ）——實際存放對這些道具只走「無條件存入」那圈，那圈放不下就不搬。</param>
    private static unsafe bool TryFindSharedEntrustVendorWork(EntrustPlan? plan, HashSet<(uint ItemId, bool Hq)> noRoom, out TaskEntrustDuplicates.EntrustPrecheckHit hit)
    {
        hit = default;
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
                        hit = new(TaskEntrustDuplicates.EntrustPrecheckPath.Vendor, item.ItemId, item.Flags.HasFlag(InventoryItem.ItemFlags.HighQuality));
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
                // 存入那一側整件排除的道具（保護清單、燃料保留、裝備濾網）不能回報「有工作」——
                // 否則僱員會被開起來、發現沒東西可搬、再關掉（空開）。判斷與存入那一側是同一支。
                if(TaskEntrustDuplicates.GetEntrustExclusion(item->ItemId, plan, vs) != TaskEntrustDuplicates.EntrustExclusion.None) continue;

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
                    if(noRoom != null && noRoom.Contains((item->ItemId, item->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality)))) continue;
                    hit = new(TaskEntrustDuplicates.EntrustPrecheckPath.SharedEntrust, item->ItemId, item->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality));
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>The half of the deferred entrust/vendor work that belongs to <b>this specific retainer</b>: entrust-duplicates.
    /// 🔴 Only collectable while this retainer is open - the retainer containers are not mapped otherwise.
    /// This is the half of the duplicates rule that depends on the retainer, and the only half that cannot change while it is left alone, so it is what the queue carries.
    /// The set therefore only ever needs the retainer-side conditions applied.
    /// </summary>
    /// <remarks>
    /// 讀不到的容器／格位一律 <c>continue</c>，也就是**只可能少收候選、不可能多收**。少收＝可能少開一次僱員，
    /// 所以這裡刻意收得寬：品質(HQ)不納入比對、multi-stack 不預判數量，一律把判斷留給 drain 時的實際掃描。
    /// ⚠️ 解參考 null 在 .NET Core 是 corrupted-state exception，try/catch 攔不到，只能靠事前檢查。
    /// </remarks>
    private static unsafe HashSet<uint> CollectDuplicatesCandidates(EntrustPlan? plan)
    {
        HashSet<uint> candidates = [];
        if(!C.EnableEntrustManager || plan == null || !plan.Duplicates)
        {
            return candidates;
        }

        var vs = Data.GetIMSettings();

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
                // 燃料不會被存入（AutoBuyFuelManager.IsFuelReservedForAutoBuy），所以僱員身上的燃料不構成「有工作」。
                if(AutoBuyFuelManager.IsFuelReservedForAutoBuy(item->ItemId)) continue;

                if(plan.DuplicatesMultiStack)
                {
                    candidates.Add(item->ItemId);
                    continue;
                }

                // 只有這個變體多一個僱員側的前提：這裡的堆疊要還有空間可以補。
                var data = ExcelItemHelper.Get(item->ItemId);
                if(data == null || data.Value.IsUnique) continue;
                if(data.Value.StackSize - item->Quantity <= 0) continue;
                candidates.Add(item->ItemId);
            }
        }

        return candidates;
    }

    /// <summary>Whether the player is currently carrying any of <paramref name="itemIds"/> in the
    /// containers this plan is allowed to take from. This is the live, player-side half of the duplicates
    /// rule - see <see cref="CollectDuplicatesCandidates"/> for the frozen retainer-side half.</summary>
    /// <remarks>
    /// 刻意比 duplicates 的真正條件寬：不比對品質(HQ)、不看數量夠不夠、不管堆疊放不放得下。
    /// 這是「有沒有可能有工作」的必要條件而非充分條件，回 <c>true</c> 只代表「還得開起來看」。
    /// 寬 ⇒ 失敗方向是多開一次僱員，不是漏搬。
    /// </remarks>
    private static bool PlayerHoldsAnyOf(EntrustPlan? plan, HashSet<uint> itemIds) => TryFindPlayerHeldCandidate(plan, itemIds, null, out _);

    /// <summary><see cref="PlayerHoldsAnyOf"/> 本體；<paramref name="hit"/> 是讓它回 true 的第一件（給空開診斷用）。</summary>
    /// <param name="noRoom">null＝不篩。只在多堆疊模式篩：那時重複道具只經「無條件存入」那圈搬（放不下就不搬）；
    /// 補堆疊模式的實際條件是「同品質的堆疊還有空間」，不是同一支判斷，所以不篩。</param>
    private static unsafe bool TryFindPlayerHeldCandidate(EntrustPlan? plan, HashSet<uint> itemIds, HashSet<(uint ItemId, bool Hq)> noRoom, out TaskEntrustDuplicates.EntrustPrecheckHit hit)
    {
        hit = default;
        if(plan == null || itemIds.Count == 0) return false;

        InventoryManagementSettings vs = null;
        foreach(var type in plan.GetAllowedInventories())
        {
            var inv = InventoryManager.Instance()->GetInventoryContainer(type);
            if(inv == null) continue;
            for(var i = 0; i < inv->Size; i++)
            {
                var item = inv->GetInventorySlot(i);
                if(item == null) continue;
                if(item->ItemId == 0 || item->Quantity <= 0) continue;
                if(!itemIds.Contains(item->ItemId)) continue;
                // 排除在這裡現場判、不在收集候選時凍結：設定在入列與開僱員之間改了，也照當下的規則走。
                vs ??= Data.GetIMSettings();
                if(TaskEntrustDuplicates.GetEntrustExclusion(item->ItemId, plan, vs) != TaskEntrustDuplicates.EntrustExclusion.None) continue;
                if(noRoom != null && plan.DuplicatesMultiStack && noRoom.Contains((item->ItemId, item->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality)))) continue;
                hit = new(TaskEntrustDuplicates.EntrustPrecheckPath.Duplicates, item->ItemId, item->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality));
                return true;
            }
        }

        return false;
    }

    /// <summary>僱員開著、選單就緒時，把 <see cref="CollectDuplicatesCandidates"/> 與 <see cref="CollectEntrustNoRoom"/> 的結果記到這個僱員的延後批次項目上。只在框架執行緒的任務回呼裡跑。</summary>
    /// <remarks>量不到（選單被探險視窗蓋住、最近的僱員不是它、僱員背包讀不到、項目已被清掉）就不記 ⇒ 重驗照舊開，失敗方向是多開一次不是漏存。</remarks>
    /// <param name="queuedOnlyForDuplicates">這次入列只因為「可能有重複道具」：量到之後確定沒有任何工作就撤掉，等同入列當下就知道答案。</param>
    private static bool? CaptureEntrustNoRoom(string retainer, EntrustPlan plan, bool queuedOnlyForDuplicates)
    {
        if(TryGetAddonByName<AtkUnitBase>("RetainerTaskSupply", out _)) return true;
        if(!TryGetAddonByName<AtkUnitBase>("SelectString", out var addon) || !IsAddonReady(addon)) return false;
        var index = PendingEntrustVendorPostprocess.FindIndex(x => x.Retainer == retainer);
        if(index < 0) return true;
        if(!Utils.TryGetCurrentRetainer(out var current) || current != retainer)
        {
            PluginLog.Debug($"[EntrustVendorPass] {retainer}: not measuring room, the nearest retainer is {current ?? "none"}");
            return true;
        }
        var noRoom = CollectEntrustNoRoom(plan);
        if(noRoom == null)
        {
            // 背包讀不到時候選集合也收不準（讀不到的格位只會少收），所以兩者都不記。
            PluginLog.Debug($"[EntrustVendorPass] {retainer}: not measuring, retainer containers unreadable");
            return true;
        }
        var candidates = CollectDuplicatesCandidates(plan);
        if(queuedOnlyForDuplicates && !PlayerHoldsAnyOf(plan, candidates) && !HasSharedEntrustVendorWork(plan))
        {
            PendingEntrustVendorPostprocess.RemoveAt(index);
            PluginLog.Debug($"[EntrustVendorPass] {retainer}: dropped from the batch, the player carries none of its {candidates.Count} duplicate candidate(s) and there is no shared work");
            return true;
        }
        PendingEntrustVendorPostprocess[index] = PendingEntrustVendorPostprocess[index] with { DuplicatesCandidates = candidates, NoRoom = noRoom };
        PluginLog.Debug($"[EntrustVendorPass] {retainer}: {candidates.Count} duplicate candidate(s); no room for {noRoom.Count} item/quality pair(s) the player carries");
        return true;
    }

    /// <summary>玩家身上（計畫允許的容器）哪些（道具, HQ）這個僱員現在一件都放不進；null＝僱員背包讀不到，不能下結論。</summary>
    /// <remarks>
    /// 🔴 判斷就是實際存放「無條件存入」那圈用的同一支 <see cref="Utils.GetAmountThatCanFit"/>（同樣的容器、同樣的 HQ），回 0 才記。
    /// 讀不到的容器在那支裡也是回 0，所以先擋：分頁讀不到整個不記；市場／裝備欄讀不到時獨佔道具不記（只有它會看那兩格）。
    /// 量完之後玩家才拿到的道具不在集合裡，一律當放得進。
    /// </remarks>
    internal static unsafe HashSet<(uint ItemId, bool Hq)> CollectEntrustNoRoom(EntrustPlan plan)
    {
        foreach(var type in Utils.RetainerInventoriesWithCrystals)
        {
            var inv = InventoryManager.Instance()->GetInventoryContainer(type);
            if(inv == null || inv->Items == null || inv->Size <= 0) return null;
        }
        var uniqueReadable = true;
        foreach(var type in Utils.RetainerEntireInventory)
        {
            if(!type.IsInventoryReadable()) uniqueReadable = false;
        }
        HashSet<(uint ItemId, bool Hq)> seen = [];
        HashSet<(uint ItemId, bool Hq)> noRoom = [];
        foreach(var type in plan.GetAllowedInventories())
        {
            var inv = InventoryManager.Instance()->GetInventoryContainer(type);
            if(inv == null) continue;
            for(var i = 0; i < inv->Size; i++)
            {
                var item = inv->GetInventorySlot(i);
                if(item == null || item->ItemId == 0 || item->Quantity <= 0) continue;
                var key = (item->ItemId, item->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality));
                if(!seen.Add(key)) continue;
                if(!uniqueReadable && ExcelItemHelper.Get(key.Item1)?.IsUnique != false) continue;
                if(Utils.GetAmountThatCanFit(Utils.RetainerInventoriesWithCrystals, key.Item1, key.Item2) == 0) noRoom.Add(key);
            }
        }
        return noRoom;
    }
}
