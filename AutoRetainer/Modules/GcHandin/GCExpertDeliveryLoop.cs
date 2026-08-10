using AutoRetainer.Internal.InventoryManagement;
using AutoRetainer.Scheduler.Handlers;
using AutoRetainer.Scheduler.Tasks;
using ECommons.ExcelServices;
using ECommons.GameHelpers;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace AutoRetainer.Modules.GcHandin;

/// <summary>
/// 稀有品繳交循環:把指定存放計畫底下的雇員身上的裝備取出來,拿去大國防聯軍繳交,取完為止。
///
/// <para>🔴 零自動觸發。只有 <see cref="Start"/> 會讓它動起來,而 <see cref="Start"/> 只有 UI 上那顆按鈕
/// 會呼叫。沒有任何事件、排程或多角色流程會啟動它。</para>
///
/// <para>整條流程沒有一步是自己操作 addon 的:走到鈴前、選雇員、開道具管理、去大國防聯軍繳交,
/// 全部委給外掛本來就在跑的任務鏈。這裡只負責決定「下一步做什麼」以及「什麼時候該停下來」。</para>
///
/// <para>⚠️ 停下來的理由一律說人話並寫進記錄(Information 級,因為使用者的記錄等級收不到 Debug)。
/// 任何一步的狀態不如預期都是**停止**,不是重試 —— 這條流程會花掉軍票、會搬動裝備,
/// 猜錯的代價比多停一次高。</para>
/// </summary>
internal static unsafe class GCExpertDeliveryLoop
{
    /// <summary>軍票獲得量提升:1078 是道具版(軍票預支單),414 是部隊行動版。
    /// 兩個都算「已經在加成中」—— 道具的說明明寫它會覆蓋效果相同的公會特效,
    /// 所以部隊行動開著的時候再用一張,只是把它蓋掉並浪費一張。</summary>
    private static readonly uint[] SealBonusStatusIds = [1078, 414];

    /// <summary>軍票預支單。</summary>
    private const uint SealAllowanceItemId = 14946;

    /// <summary>用掉軍票預支單之後,等這麼久還沒看到加成就不等了。</summary>
    private const long SealBuffWaitMs = 6000;

    /// <summary>送出一個任務鏈之後,至少要等這麼久才准把「不忙」解讀成「做完了」。
    /// 🔴 沒有這段緩衝的話,排進佇列與佇列真的開始跑之間的那一兩幀會被誤讀成「瞬間完成」,
    /// 整條流程會在什麼都沒發生的情況下一路衝到結束。</summary>
    private const long EnqueueGraceMs = 500;

    /// <summary>取回指令之間的最小間隔。伺服器實測每格約 0.13 秒,這裡略高於它。</summary>
    private const int RetrieveIntervalMs = 150;

    private enum Phase
    {
        Idle,
        SealBonus,
        SealBonusWait,
        EnsureBell,
        EnsureBellWait,
        OpenRetainer,
        OpenRetainerWait,
        Retrieve,
        CloseRetainer,
        CloseRetainerWait,
        Handin,
        HandinWait,
    }

    /// <summary>這一輪取回停下來的理由,決定繳交完之後要不要再跑一輪。</summary>
    private enum RoundEnd
    {
        /// <summary>清單上的雇員都看過了,一件裝備都沒有 —— 繳完就收工。</summary>
        NoGearLeft,
        /// <summary>背包到保留下限,先去繳交 —— 繳完還要回來繼續取。</summary>
        ReserveReached,
    }

    internal static bool Running { get; private set; }

    private static Phase CurrentPhase = Phase.Idle;
    private static long PhaseEnteredAt;
    private static RoundEnd RoundEndReason;

    private static List<string> Retainers = [];
    private static int RetainerIndex;
    private static bool TravelledToBellThisRound;

    /// <summary>這一輪已經略過的道具:重複的獨占道具、只在水晶欄的東西。
    /// 沒有它的話同一件會被無限重掃。</summary>
    private static readonly HashSet<uint> SkippedItems = [];

    // 給 UI 看的統計
    internal static int RetrievedTotal { get; private set; }
    internal static int RetrievedThisRound { get; private set; }
    internal static int HandinRounds { get; private set; }
    internal static string StatusText { get; private set; } = "";

    /// <summary>UI 要顯示的階段名。停止之後保留最後的理由,不要洗掉。</summary>
    internal static string CurrentPhaseName => CurrentPhase switch
    {
        Phase.Idle => Loc.T("Idle"),
        Phase.SealBonus or Phase.SealBonusWait => Loc.T("Checking seal bonus"),
        Phase.EnsureBell or Phase.EnsureBellWait => Loc.T("Going to a summoning bell"),
        Phase.OpenRetainer or Phase.OpenRetainerWait => Loc.T("Opening retainer"),
        Phase.Retrieve => Loc.T("Retrieving gear"),
        Phase.CloseRetainer or Phase.CloseRetainerWait => Loc.T("Closing retainer"),
        Phase.Handin or Phase.HandinWait => Loc.T("Handing in at the Grand Company"),
        _ => "?",
    };

    /// <summary>循環要處理的雇員。名單在**開始時**決定一次並沿用整趟,免得中途改設定讓流程自己變形。</summary>
    internal static List<string> ResolveRetainers()
    {
        var result = new List<string>();
        var data = Utils.GetCurrentCharacterData();
        if(data == null) return result;

        if(C.ExpertDeliveryLoopManualRetainers)
        {
            foreach(var retainer in data.RetainerData)
            {
                var name = retainer.Name.ToString();
                if(name.IsNullOrEmpty()) continue;
                if(C.ExpertDeliveryLoopRetainerNames.Contains(name)) result.Add(name);
            }
            return result;
        }

        // 🔴 沒選計畫時回空清單而不是「全部」。「還沒設定」與「要對每個雇員做」是兩回事,
        //    而這條流程會搬動裝備,預設值倒向「不做事」。
        if(C.ExpertDeliveryLoopEntrustPlan == Guid.Empty) return result;

        foreach(var retainer in data.RetainerData)
        {
            var name = retainer.Name.ToString();
            if(name.IsNullOrEmpty()) continue;
            if(Utils.GetAdditionalData(data.CID, name).EntrustPlan == C.ExpertDeliveryLoopEntrustPlan) result.Add(name);
        }
        return result;
    }

    internal static void Start()
    {
        if(Running) return;

        if(!Player.Available)
        {
            Fail(Loc.T("Player is not available."));
            return;
        }
        if(GCContinuation.GetGCInfo() == null)
        {
            Fail(Loc.T("This character is not employed by a Grand Company."));
            return;
        }
        if(!AutoGCHandin.IsEnabled())
        {
            Fail(Loc.T("Expert delivery is disabled for this character - set a delivery mode first."));
            return;
        }
        if(Utils.IsBusy)
        {
            Fail(Loc.T("AutoRetainer or Lifestream is busy."));
            return;
        }

        Retainers = ResolveRetainers();
        if(Retainers.Count == 0)
        {
            Fail(C.ExpertDeliveryLoopManualRetainers
                ? Loc.T("No retainers are selected.")
                : Loc.T("No retainer of this character carries the selected entrust plan."));
            return;
        }

        Running = true;
        RetrievedTotal = 0;
        RetrievedThisRound = 0;
        HandinRounds = 0;
        RetainerIndex = 0;
        TravelledToBellThisRound = false;
        RoundEndReason = RoundEnd.NoGearLeft;
        SkippedItems.Clear();
        StatusText = "";
        SetPhase(Phase.SealBonus);
        PluginLog.Information($"[ExpertDeliveryLoop] Started. Retainers to visit: {Retainers.Print()}. Reserved slots: {EffectiveReservedSlots} (config {C.ExpertDeliveryLoopReservedSlots}, MultiMinInventorySlots {C.MultiMinInventorySlots}).");
    }

    internal static void Stop(string reason, bool success = false)
    {
        if(!Running && CurrentPhase == Phase.Idle) return;
        Running = false;
        CurrentPhase = Phase.Idle;
        StatusText = reason;
        var summary = $"{reason} ({string.Format(Loc.T("retrieved {0}, handin rounds {1}"), RetrievedTotal, HandinRounds)})";
        if(success)
        {
            DuoLog.Information(summary);
            if(C.GCHandinNotify) Utils.TryNotify(summary);
        }
        else
        {
            DuoLog.Warning(summary);
        }
        PluginLog.Information($"[ExpertDeliveryLoop] Stopped: {reason} | retrieved={RetrievedTotal} handinRounds={HandinRounds} success={success}");
    }

    /// <summary>還沒開始就被前置條件擋下來。不改任何狀態,只說明理由。</summary>
    private static void Fail(string reason)
    {
        StatusText = reason;
        DuoLog.Warning(reason);
        PluginLog.Information($"[ExpertDeliveryLoop] Refused to start: {reason}");
    }

    /// <summary>真正生效的保留格數。取回核心自己也擋在 <c>MultiMinInventorySlots</c>,
    /// 所以低於它的設定值不會有效果 —— UI 上也是這樣說明的。</summary>
    internal static int EffectiveReservedSlots => Math.Max(C.ExpertDeliveryLoopReservedSlots, C.MultiMinInventorySlots);

    private static void SetPhase(Phase phase)
    {
        CurrentPhase = phase;
        PhaseEnteredAt = Environment.TickCount64;
    }

    private static long TimeInPhase => Environment.TickCount64 - PhaseEnteredAt;

    /// <summary>任務鏈排進去之後,「不忙」才算數的判斷。緩衝期內一律當成還在跑。</summary>
    private static bool ChainFinished => !Utils.IsBusy && TimeInPhase > EnqueueGraceMs;

    internal static void Tick()
    {
        if(!Running) return;

        // 登出、進副本、被傳走 —— 任何一種都讓後面的假設不成立。
        if(!Player.Available)
        {
            Stop(Loc.T("Stopped: the player became unavailable."));
            return;
        }

        switch(CurrentPhase)
        {
            case Phase.SealBonus: TickSealBonus(); break;
            case Phase.SealBonusWait: TickSealBonusWait(); break;
            case Phase.EnsureBell: TickEnsureBell(); break;
            case Phase.EnsureBellWait: TickEnsureBellWait(); break;
            case Phase.OpenRetainer: TickOpenRetainer(); break;
            case Phase.OpenRetainerWait: TickOpenRetainerWait(); break;
            case Phase.Retrieve: TickRetrieve(); break;
            case Phase.CloseRetainer: TickCloseRetainer(); break;
            case Phase.CloseRetainerWait: TickCloseRetainerWait(); break;
            case Phase.Handin: TickHandin(); break;
            case Phase.HandinWait: TickHandinWait(); break;
        }
    }

    #region 軍票加成

    internal static bool HasSealBonus()
    {
        if(!Player.Available) return false;
        foreach(var status in Player.Object.StatusList)
        {
            if(status == null) continue;
            if(SealBonusStatusIds.Contains(status.StatusId)) return true;
        }
        return false;
    }

    internal static int GetSealAllowanceCount() => InventoryManager.Instance()->GetInventoryItemCount(SealAllowanceItemId);

    private static void TickSealBonus()
    {
        if(!C.ExpertDeliveryLoopUseSealAllowance || HasSealBonus())
        {
            SetPhase(Phase.EnsureBell);
            return;
        }

        if(GetSealAllowanceCount() <= 0)
        {
            if(C.ExpertDeliveryLoopStopWithoutSealBonus)
            {
                Stop(Loc.T("Stopped: no Priority Seal Allowance left and the loop is set to stop without the bonus."));
                return;
            }
            PluginLog.Information($"[ExpertDeliveryLoop] No Priority Seal Allowance in inventory, continuing without the seal bonus.");
            SetPhase(Phase.EnsureBell);
            return;
        }

        // 使用道具本身沒有回傳值可看,節流一次就好,能不能生效交給下一個階段用狀態判斷。
        if(!EzThrottler.Throttle("ExpertDeliveryLoopUseAllowance", 2000)) return;
        AgentInventoryContext.Instance()->UseItem(SealAllowanceItemId);
        PluginLog.Information($"[ExpertDeliveryLoop] Used a Priority Seal Allowance ({GetSealAllowanceCount()} left before use).");
        SetPhase(Phase.SealBonusWait);
    }

    private static void TickSealBonusWait()
    {
        if(HasSealBonus())
        {
            PluginLog.Information($"[ExpertDeliveryLoop] Seal bonus is now active.");
            SetPhase(Phase.EnsureBell);
            return;
        }
        if(TimeInPhase <= SealBuffWaitMs) return;

        if(C.ExpertDeliveryLoopStopWithoutSealBonus)
        {
            Stop(Loc.T("Stopped: the seal bonus did not appear after using a Priority Seal Allowance."));
            return;
        }
        // 沒生效不一定是壞掉(可能在戰鬥中、可能剛好被打斷),但要講出來,不要靜靜地少賺軍票。
        DuoLog.Warning(Loc.T("The seal bonus did not appear after using a Priority Seal Allowance - continuing without it."));
        SetPhase(Phase.EnsureBell);
    }

    #endregion

    #region 到鈴邊

    private static void TickEnsureBell()
    {
        if(Utils.IsBusy) return;

        if(Utils.GetReachableRetainerBell(true) != null)
        {
            TravelledToBellThisRound = false;
            RetainerIndex = 0;
            RetrievedThisRound = 0;
            RoundEndReason = RoundEnd.NoGearLeft;
            SetPhase(Phase.OpenRetainer);
            return;
        }

        if(TravelledToBellThisRound)
        {
            // 已經照設定跑過一次導航還是沒有鈴 —— 再送一次也只會得到同樣的結果。
            Stop(Loc.T("Stopped: no summoning bell in reach after travelling."));
            return;
        }

        if(!C.ExpertDeliveryLoopTravelToBell)
        {
            Stop(Loc.T("Stopped: no summoning bell in reach, and travelling to one is turned off."));
            return;
        }

        var command = C.ExpertDeliveryLoopBellCommand;
        if(command.IsNullOrEmpty())
        {
            // 🔴 空字串不可以送出去:Lifestream 把空參數當成跨世界旅行。
            Stop(Loc.T("Stopped: no summoning bell in reach and no travel command is configured."));
            return;
        }

        PluginLog.Information($"[ExpertDeliveryLoop] No bell in reach, sending Lifestream command \"{command}\".");
        S.LifestreamIPC.ExecuteCommand(command);
        TravelledToBellThisRound = true;
        SetPhase(Phase.EnsureBellWait);
    }

    private static void TickEnsureBellWait()
    {
        if(!ChainFinished) return;
        SetPhase(Phase.EnsureBell);
    }

    #endregion

    #region 取裝備

    /// <summary>可以拿去繳交稀有品的裝備。判定式與 <see cref="GCContinuation.DoesInventoryHaveDeliverableItem"/>
    /// 完全一致 —— 兩邊只要有一邊寬,循環就會取回繳交不掉的東西,把背包塞滿之後每一輪都做白工。</summary>
    internal static bool IsDeliverableGear(uint itemId)
    {
        if(itemId == 0) return false;
        if(Data.GetIMSettings().IMProtectList.Contains(itemId)) return false;
        var data = ExcelItemHelper.Get(itemId);
        if(data == null) return false;
        if(!data.Value.ItemUICategory.RowId.EqualsAny([.. Utils.ArmorsUICategories, .. Utils.WeaponsUICategories])) return false;
        if(!data.Value.GetRarity().EqualsAny(ItemRarity.Green, ItemRarity.Pink, ItemRarity.Blue)) return false;
        if(data.Value.Desynth == 0) return false;
        return true;
    }

    /// <summary>目前開著的雇員身上第一件可繳交裝備的道具 ID,沒有就是 0。</summary>
    private static uint FindGearOnOpenRetainer()
    {
        foreach(var type in Utils.RetainerInventories)
        {
            var inv = RetainerRetrieve.TryGetReadableContainer(type);
            if(inv == null) continue;
            for(var i = 0; i < inv->Size; i++)
            {
                var item = inv->GetInventorySlot(i);
                if(item == null) continue;
                if(item->ItemId == 0 || item->Quantity <= 0) continue;
                if(SkippedItems.Contains(item->ItemId)) continue;
                if(!IsDeliverableGear(item->ItemId)) continue;
                return item->ItemId;
            }
        }
        return 0;
    }

    private static bool ReserveReached => Utils.GetInventoryFreeSlotCount() <= EffectiveReservedSlots;

    private static void TickOpenRetainer()
    {
        if(Utils.IsBusy) return;

        if(ReserveReached)
        {
            PluginLog.Information($"[ExpertDeliveryLoop] Inventory down to {Utils.GetInventoryFreeSlotCount()} free slots (reserve {EffectiveReservedSlots}), going to hand in.");
            RoundEndReason = RoundEnd.ReserveReached;
            SetPhase(Phase.Handin);
            return;
        }

        if(RetainerIndex >= Retainers.Count)
        {
            // 名單走完了。RoundEndReason 維持 NoGearLeft,繳完這批就收工。
            SetPhase(Phase.Handin);
            return;
        }

        var name = Retainers[RetainerIndex];
        if(!Utils.TryGetRetainerByName(name, out _))
        {
            // 雇員被解雇/改名。跳過它繼續,不要整趟停掉。
            DuoLog.Warning(string.Format(Loc.T("Retainer \"{0}\" no longer exists, skipping."), name));
            RetainerIndex++;
            return;
        }

        PluginLog.Information($"[ExpertDeliveryLoop] Opening item storage of {name} ({RetainerIndex + 1}/{Retainers.Count}).");
        TaskInteractWithNearestBell.Enqueue();
        TaskSelectRetainer.Enqueue(name);
        P.TaskManager.Enqueue(RetainerHandlers.SelectEntrustItems, $"SelectEntrustItems({name})");
        P.TaskManager.Enqueue(InventorySpaceManager.IsRetainerInventoryLoaded, $"WaitRetainerInventoryLoaded({name})");
        SetPhase(Phase.OpenRetainerWait);
    }

    private static void TickOpenRetainerWait()
    {
        if(!ChainFinished) return;

        if(!InventorySpaceManager.IsRetainerInventoryLoaded())
        {
            // 任務鏈跑完了但視窗沒開 —— 每一步都有自己的逾時,走到這裡代表其中一步放棄了。
            // 再送一次多半只會重演,而且此時畫面上可能還開著半套的雇員 UI。
            Stop(string.Format(Loc.T("Stopped: could not open the item storage of \"{0}\"."), Retainers[RetainerIndex]));
            return;
        }

        RetainerRetrieve.ResetTracking();
        SkippedItems.Clear();
        SetPhase(Phase.Retrieve);
    }

    private static void TickRetrieve()
    {
        if(ReserveReached)
        {
            PluginLog.Information($"[ExpertDeliveryLoop] Reserve reached while retrieving ({Utils.GetInventoryFreeSlotCount()} free, reserve {EffectiveReservedSlots}).");
            RoundEndReason = RoundEnd.ReserveReached;
            SetPhase(Phase.CloseRetainer);
            return;
        }

        // 雇員視窗在兩次 tick 之間被關掉(使用者手動關、或哪裡出錯)——繼續掃只會讀到空的。
        if(!InventorySpaceManager.IsRetainerInventoryLoaded())
        {
            Stop(Loc.T("Stopped: the retainer's item storage closed unexpectedly."));
            return;
        }

        if(!EzThrottler.Throttle("ExpertDeliveryLoopRetrieve", RetrieveIntervalMs)) return;

        var itemId = FindGearOnOpenRetainer();
        if(itemId == 0)
        {
            PluginLog.Information($"[ExpertDeliveryLoop] {Retainers[RetainerIndex]} has no more deliverable gear.");
            SetPhase(Phase.CloseRetainer);
            return;
        }

        var result = RetainerRetrieve.RetrieveSlotById(itemId, false, false);
        if(result >= 1)
        {
            RetrievedTotal++;
            RetrievedThisRound++;
            return;
        }

        switch(result)
        {
            case RetainerRetrieve.ResultCommandInFlight:
                // 指令還在飛,等它落地再重掃。不是錯誤。
                return;

            case RetainerRetrieve.ResultInventoryFull:
                RoundEndReason = RoundEnd.ReserveReached;
                SetPhase(Phase.CloseRetainer);
                return;

            case RetainerRetrieve.ResultBlockedUnique:
            case RetainerRetrieve.ResultInCrystals:
            case RetainerRetrieve.ResultNotPresent:
                // 這件永遠拿不到(重複的獨占道具),或它根本不在該在的地方。
                // 記下來跳過,否則下一次掃描又會挑到同一件,變成原地空轉。
                SkippedItems.Add(itemId);
                PluginLog.Information($"[ExpertDeliveryLoop] Skipping item {itemId} ({ExcelItemHelper.GetName(itemId)}): retrieve returned {result}.");
                return;

            case RetainerRetrieve.ResultRetainerUnavailable:
                // 🔴 「讀不到」不等於「沒有」。這時候繼續往下走會把還有東西的雇員當成空的。
                Stop(string.Format(Loc.T("Stopped: could not read the storage of \"{0}\"."), Retainers[RetainerIndex]));
                return;

            default:
                Stop(string.Format(Loc.T("Stopped: unexpected retrieve result {0}."), result));
                return;
        }
    }

    private static void TickCloseRetainer()
    {
        P.TaskManager.Enqueue(RetainerHandlers.CloseAgentRetainer, "CloseAgentRetainer");
        P.TaskManager.Enqueue(() => !IsOccupied(), "WaitUntilNotOccupiedAfterRetainerClose");
        SetPhase(Phase.CloseRetainerWait);
    }

    private static void TickCloseRetainerWait()
    {
        if(!ChainFinished) return;

        RetainerIndex++;
        SetPhase(RoundEndReason == RoundEnd.ReserveReached ? Phase.Handin : Phase.OpenRetainer);
    }

    #endregion

    #region 繳交

    /// <summary>背包裡還有沒有可以繳交的裝備。取回一件都沒取到時,用它分辨
    /// 「真的沒東西可繳」與「上一輪取回的東西還在背包裡」。</summary>
    private static bool HasDeliverableGearInBags()
    {
        foreach(var type in Utils.PlayerInvetories)
        {
            var inv = RetainerRetrieve.TryGetReadableContainer(type);
            if(inv == null) continue;
            for(var i = 0; i < inv->Size; i++)
            {
                var item = inv->GetInventorySlot(i);
                if(item == null) continue;
                if(IsDeliverableGear(item->ItemId)) return true;
            }
        }
        return false;
    }

    private static void TickHandin()
    {
        if(Utils.IsBusy) return;

        if(!HasDeliverableGearInBags())
        {
            if(RoundEndReason == RoundEnd.ReserveReached)
            {
                // 背包到保留下限卻一件可繳交的都沒有 —— 塞住背包的是別的東西,再跑下去
                // 只會一輪一輪地取不到又繳不掉。
                Stop(Loc.T("Stopped: the inventory is down to the reserve but holds nothing that can be delivered."));
                return;
            }
            Stop(Loc.T("Finished: the retainers have no more gear to deliver."), success: true);
            return;
        }

        PluginLog.Information($"[ExpertDeliveryLoop] Starting handin round {HandinRounds + 1} with {RetrievedThisRound} item(s) retrieved this round.");
        TaskDeliverItems.Enqueue();
        HandinRounds++;
        SetPhase(Phase.HandinWait);
    }

    private static void TickHandinWait()
    {
        if(ChainFinished)
        {
            PluginLog.Information($"[ExpertDeliveryLoop] Handin round {HandinRounds} finished. Round end reason: {RoundEndReason}.");
            if(RoundEndReason == RoundEnd.NoGearLeft)
            {
                Stop(Loc.T("Finished: everything has been delivered."), success: true);
                return;
            }
            SetPhase(Phase.EnsureBell);
            return;
        }

        if(TimeInPhase > C.ExpertDeliveryLoopHandinTimeoutMinutes * 60L * 1000L)
        {
            Stop(Loc.T("Stopped: the handin round did not finish in time."));
        }
    }

    #endregion
}
