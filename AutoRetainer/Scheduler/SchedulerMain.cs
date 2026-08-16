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
    /// entrust-duplicates/auto-vendor batch pass, run once every retainer's venture business is settled.
    ///
    /// <para><c>DuplicatesCandidates</c> is the retainer half of the answer, kept in a form that survives
    /// until drain time. The retainer's own inventory is only readable while that retainer is open, so it
    /// is captured once - as the set of item ids in its bags that pass the <b>retainer-side</b> filters of
    /// the duplicates rule (see <see cref="CollectDuplicatesCandidates"/>). The player-side half of that
    /// rule is then asked live at drain time by <see cref="PlayerHoldsAnyOf"/>.</para>
    ///
    /// <para>🔑 Deliberately NOT "the ids that were duplicate work at scan time": the player <b>gains</b>
    /// items during this phase (every venture collected between now and the drain lands in their bags), so
    /// a set frozen against the player's inventory would miss work that only came into existence
    /// afterwards. The retainer side is the half that cannot change while unvisited, so that is the half
    /// worth freezing - and "player holds none of these ids" is then a genuine necessary condition for
    /// "this retainer has no duplicates work", in both the multi-stack and the partial-stack variants.</para></summary>
    internal static List<(string Retainer, HashSet<uint> DuplicatesCandidates)> PendingEntrustVendorPostprocess = [];

    /// <summary>Drops the deferred entrust/vendor queue. It holds retainer <b>names</b> belonging to one
    /// specific character, and nothing else in the queue identifies which character that was, so a leftover
    /// entry would be matched by name against the next character's retainers. Nothing ever emptied it on a
    /// character change before - it only drained down naturally, so an interrupted cycle (logout mid-run,
    /// plugin disabled, retainer list closed by hand) left entries behind.</summary>
    internal static void ClearPendingEntrustVendorPass(string reason)
    {
        if(PendingEntrustVendorPostprocess.Count == 0) return;
        PluginLog.Information($"[EntrustVendorPass] Dropping {PendingEntrustVendorPostprocess.Count} pending retainer(s) [{PendingEntrustVendorPostprocess.Select(x => x.Retainer).Print()}]: {reason}");
        PendingEntrustVendorPostprocess.Clear();
    }

    internal static PluginEnableReason Reason { get; set; }

    /// <summary>true ＝ 稀有品繳交循環正在跑,所以 AutoRetainer 自己的一般僱員自動處理
    /// (收取／重派探險、存入僱員、自動賣出、僱員感知自動開鈴)這一幀要整個讓路。
    ///
    /// <para>🔴 為什麼一定要互斥:兩邊**驅動的是同一個僱員清單,而且共用同一條 <see cref="P.TaskManager"/>**。
    /// 循環開鈴用的是 <see cref="Tasks.TaskInteractWithNearestBell"/>,它會把 <c>P.IsInteractionAutomatic</c>
    /// 設成 true,於是 <c>OccupiedSummoningBell</c> 翻正時 <c>ConditionChange</c> 就替我們
    /// <see cref="EnablePlugin"/>(<see cref="PluginEnableReason.Auto"/>) —— 也就是說**循環自己把一般處理打開**。
    /// 接著 <see cref="AutoRetainer.Tick"/> 裡 <see cref="Tick"/> 排在 <c>GCExpertDeliveryLoop.Tick()</c> 前面,
    /// 每一幀都先搶到 <c>!P.TaskManager.IsBusy</c> 這道閘門,把整條收派探險的任務鏈塞進共用佇列。</para>
    ///
    /// <para>🔴 最致命的一步在收尾:一般處理把僱員跑完之後照 <c>C.TaskCompletedBehaviorAuto</c> 收尾,
    /// 設成 <c>Close_retainer_list_and_disable_plugin</c> 時會排入 <c>CloseRetainerList</c>。
    /// 2026-08-16 19:38:47.498 實測:那一幀關掉僱員清單,而循環的 <c>TickSelectRetainer</c> 在**同一幀**
    /// 看到佇列空了就送出 <c>SelectRetainerByName</c> —— 送進一個剛被關掉的清單,20 秒後逾時,
    /// 循環以「無法開啟道具管理」停在第 1/7 個角色。使用者看到的症狀就是「收僱員任務打斷連跑」。</para>
    ///
    /// <para>⚠️ 但**不要**把這個 bug 讀成「只有那個收尾設定會中」:那個設定不是預設值
    /// (預設是 <c>Stay_in_retainer_list_and_keep_plugin_enabled</c>),它只決定打斷的**烈度**。
    /// 上面第一、二段與收尾設定完全無關 —— 一般處理照樣會在循環中途搶走僱員、跑完整條收派探險,
    /// 差別只在少了那一下關清單,於是表現成「循環卡住/動作夾雜」而不是「硬停」。
    /// 互斥要擋的是**搶僱員**那一步,不是收尾那一步。</para>
    ///
    /// <para>⚠️ 這是**延後不是取消**:排程器的啟用狀態與 <see cref="Reason"/> 都原封不動留著,
    /// 只是這段期間不 Tick,循環一停下來下一幀就自己接回去跑。沒有任何探險委託會因此漏收 ——
    /// 未收取的成果是僱員身上的持續狀態(收之前連新委託都下不了),只會留著等下一輪。</para></summary>
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
        // 🔴 稀有品繳交循環在跑的時候整個讓路。閘門放在這裡(而不是呼叫端)是刻意的:
        //    呼叫端那個 if 區塊裡還有 C.SelectedRetainers 的初始化,那件事沒有理由跟著停。
        if(RetainerAutomationDeferred) return;
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
                                    // Both halves are evaluated - deliberately not short-circuited. The retainer
                                    // side of the duplicates rule is only obtainable right now (this retainer's
                                    // inventory is open), so it has to be captured even when the shared half
                                    // already said "yes".
                                    var hasSharedWork = HasSharedEntrustVendorWork(selectedPlan);
                                    var duplicatesCandidates = CollectDuplicatesCandidates(selectedPlan);
                                    if((hasSharedWork || PlayerHoldsAnyOf(selectedPlan, duplicatesCandidates))
                                        && !PendingEntrustVendorPostprocess.Any(x => x.Retainer == retainer))
                                    {
                                        PendingEntrustVendorPostprocess.Add((retainer, duplicatesCandidates));
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
    /// <remarks>
    /// 🔴 入列時算出來的「有工作」會過期。auto-vendor 與無條件存入讀的都是**玩家背包**，那是所有雇員
    /// 共用的一份 —— 佇列裡第一個雇員把該賣的賣掉、該存的存完之後，後面的雇員讀到的是同一個已經被清空的
    /// 背包，卻照樣被開起來、什麼都沒做、再關掉（使用者看到的「問完馬上關」）。所以這裡在
    /// <see cref="RetainerListHandlers.SelectRetainerByName"/> **之前**重驗共用的那一半。
    ///
    /// 逐雇員的那一半（entrust-duplicates）驗不了：那要讀雇員自己的背包，而雇員沒開起來就讀不到。
    /// 入列時記下來的旗標因此是「當時有」，不是「現在還有」。
    ///
    /// 失敗方向鎖在安全側：只有在「確定讀得到玩家背包」**且**「共用部分確定已清空」**且**「沒有逐雇員
    /// 工作」三者同時成立時才跳過。讀不到、拿不到設定、有任何一絲不確定 ⇒ 照舊開啟（＝這個修改之前的
    /// 行為）。少開一次會漏掉真正該做的搬運，多開一次只是浪費幾秒。
    /// </remarks>
    private static bool TryDrainEntrustVendorPass()
    {
        while(PendingEntrustVendorPostprocess.Count > 0)
        {
            var (next, duplicatesCandidates) = PendingEntrustVendorPostprocess[0];
            PendingEntrustVendorPostprocess.RemoveAt(0);
            if(!Utils.TryGetRetainerByName(next, out _)) continue;

            var adata = Utils.GetAdditionalData(Svc.ClientState.LocalContentId, next);
            var selectedPlan = C.EntrustPlans.FirstOrDefault(x => x.Guid == adata.EntrustPlan && !x.ManualPlan);

            // Data null 或背包讀不到 ⇒ 不重驗，維持舊行為把雇員開起來。
            var canRevalidate = Data != null && Utils.IsInventoryStateReadable();
            if(canRevalidate
                && !PlayerHoldsAnyOf(selectedPlan, duplicatesCandidates)
                && !HasSharedEntrustVendorWork(selectedPlan))
            {
                PluginLog.Information($"[EntrustVendorPass] Skipping {next}: the shared inventory work it was queued for has already been done by an earlier retainer in this batch, and the player no longer carries any of the {duplicatesCandidates.Count} item(s) this retainer could have taken as duplicates.");
                continue;
            }

            P.TaskManager.Enqueue(() => RetainerListHandlers.SelectRetainerByName(next));

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

    /// <summary>The half of the deferred entrust/vendor work that is read entirely off the <b>player's</b>
    /// inventory: auto-vendor, and entrust's "unconditional" items/categories checked against the player's
    /// carried counts. That inventory is shared by every retainer, so this answer is not specific to any
    /// one of them - and it goes stale the moment an earlier retainer in the batch consumes it, which is
    /// why <see cref="TryDrainEntrustVendorPass"/> asks again instead of trusting the queued answer.
    ///
    /// <para>The plan is still per-retainer (each retainer may point at a different
    /// <see cref="EntrustPlan"/>), so this is evaluated with that retainer's plan against the live shared
    /// inventory.</para></summary>
    /// <remarks>
    /// 讀不到的容器／格位一律 <c>continue</c>，也就是**只可能少報工作、不可能多報**。
    /// ⚠️ 解參考 null 在 .NET Core 是 corrupted-state exception，try/catch 攔不到，只能靠事前檢查。
    /// </remarks>
    private static unsafe bool HasSharedEntrustVendorWork(EntrustPlan? plan)
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
                // 存入那一側會拒絕搬燃料，這裡就不能因為背包裡有燃料而回報「有工作」——
                // 否則雇員會被開起來、發現沒東西可搬、再關掉，正是本檔要修掉的那個空開。
                if(AutoBuyFuelManager.IsFuelReservedForAutoBuy(item->ItemId)) continue;

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

        return false;
    }

    /// <summary>The half of the deferred entrust/vendor work that belongs to <b>this specific retainer</b>:
    /// entrust-duplicates, which matches what sits in this retainer's own bags against what the player is
    /// carrying.
    ///
    /// <para>🔴 Only collectable while this retainer is open - the retainer containers are not mapped
    /// otherwise. This is the half of the duplicates rule that depends on the retainer, and the only half
    /// that cannot change while it is left alone, so it is what the queue carries;
    /// <see cref="PlayerHoldsAnyOf"/> supplies the other half live at drain time.</para>
    ///
    /// <para>Both variants of the rule require the player to be holding the same item id that the retainer
    /// holds - multi-stack wants any amount of it, partial-stack wants a matching-quality stack to top up
    /// an incomplete one. So an id being absent from this set, or present but no longer carried by the
    /// player, both mean "no duplicates work for that id". The set therefore only ever needs the
    /// retainer-side conditions applied: not protected, not fuel, and (partial-stack only) a real,
    /// non-unique item whose stack here still has room.</para></summary>
    /// <remarks>
    /// 讀不到的容器／格位一律 <c>continue</c>，也就是**只可能少收候選、不可能多收**。少收＝可能少開一次雇員，
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
                // 同上：燃料不會被存入，所以雇員身上的燃料不構成「有工作」。
                if(AutoBuyFuelManager.IsFuelReservedForAutoBuy(item->ItemId)) continue;

                if(plan.DuplicatesMultiStack)
                {
                    candidates.Add(item->ItemId);
                    continue;
                }

                // 只有這個變體多一個雇員側的前提：這裡的堆疊要還有空間可以補。
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
    /// 寬 ⇒ 失敗方向是多開一次雇員，不是漏搬。
    /// </remarks>
    private static unsafe bool PlayerHoldsAnyOf(EntrustPlan? plan, HashSet<uint> itemIds)
    {
        if(plan == null || itemIds.Count == 0) return false;

        foreach(var type in plan.GetAllowedInventories())
        {
            var inv = InventoryManager.Instance()->GetInventoryContainer(type);
            if(inv == null) continue;
            for(var i = 0; i < inv->Size; i++)
            {
                var item = inv->GetInventorySlot(i);
                if(item == null) continue;
                if(item->ItemId == 0 || item->Quantity <= 0) continue;
                if(itemIds.Contains(item->ItemId)) return true;
            }
        }

        return false;
    }
}
