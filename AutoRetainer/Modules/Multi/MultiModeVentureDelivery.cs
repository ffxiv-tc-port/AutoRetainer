using AutoRetainer.Internal;
using AutoRetainer.Modules.Voyage;
using AutoRetainer.Scheduler.Tasks;
using AutoRetainerAPI.Configuration;
using ECommons.Automation.NeoTaskManager;
using ECommons.Events;
using ECommons.GameHelpers;
using ECommons.IPC;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace AutoRetainer.Modules.Multi;

/// <summary>
/// 多角色模式：探險幣或背包空格剩下門檻（含）以下時，先去大國防聯軍籌備稀有品並照兌換清單花軍票，再照常處理；
/// 可選在這一趟結束後立刻傳送回家。
/// </summary>
/// <remarks>
/// <para>只在框架執行緒上由 <see cref="MultiMode.Tick"/> 呼叫；狀態只存在記憶體，外掛重載即清空。</para>
/// <para>🔴 迴圈防護：兩種觸發各自判成果、各自暫停。為它出發的那種觸發在這一趟沒有成果（探險幣沒變多／
/// 背包沒變空也沒少掉可交的東西）就暫停那一種，2 小時起、每次加倍、上限 24 小時。
/// 暫停中的那種不會因為另一種出發的那一趟順便有了進展而解除，所以兩種不會輪流白跑，也不會互相擋住。</para>
/// </remarks>
internal static unsafe class MultiModeVentureDelivery
{
    [Flags]
    internal enum Trigger
    {
        None = 0,
        Ventures = 1,
        Inventory = 2,
    }

    private sealed class TriggerState
    {
        /// <summary>連續幾趟沒有成果；有一趟有成果就歸零。</summary>
        public int Failures;
        /// <summary><see cref="Environment.TickCount64"/>；0＝沒有暫停。</summary>
        public long PausedUntil;
        public DateTime PausedUntilLocal;
        public bool PauseSkipLogged;
    }

    private sealed class TripState
    {
        public string Name = "";
        public string World = "";
        /// <summary>已出發、還沒判定結果的一趟；<see cref="Reasons"/> 是為了哪幾種觸發出發的。</summary>
        public bool Pending;
        public Trigger Reasons;
        public uint VenturesAtStart;
        public int FreeSlotsAtStart;
        public int DeliverableAtStart;
        public bool FinalPurchaseTried;
        public int StrandedCloses;
        public readonly TriggerState Ventures = new();
        public readonly TriggerState Inventory = new();
        /// <summary>探險幣那一種暫停中的比較基準。只有不是這些趟造成的增加（例如手動買了）才提前解除。</summary>
        public uint VenturesBaseline;
    }

    internal readonly record struct PauseInfo(Trigger Kind, string Name, string World, DateTime UntilLocal);

    /// <summary>給設定頁看的暫停清單。只會整份換掉，不會原地修改。</summary>
    internal static PauseInfo[] Pauses { get; private set; } = [];

    private static readonly Dictionary<ulong, TripState> States = [];
    private static readonly Dictionary<ulong, string> IneligibleLogged = [];
    private static readonly Dictionary<ulong, long> KeptLoggedAt = [];
    private static ulong LastSeenCID;

    // 「身上有沒有東西可交」要掃整個背包（加上兵裝庫時更多），只在忙過一段之後、換了角色或隔 30 秒才重掃。
    private static bool ChecksDirty = true;
    private static long ChecksAt;
    private static ulong ChecksCID;
    private static bool CachedHasDeliverable;
    private static int CachedDeliverableInBags;
    private static bool CachedLifestream;

    private static long StrandedSince;

    private const long RecheckMs = 30_000;
    private const long StrandedMs = 15_000;
    private const int MaxStrandedCloses = 2;
    private const long KeptLogIntervalMs = 10 * 60 * 1000;
    private const long BasePauseMs = 2L * 60 * 60 * 1000;
    private const long MaxPauseMs = 24L * 60 * 60 * 1000;
    /// <summary>卡住時會去關的視窗。</summary>
    private static readonly string[] GCWindows = ["GrandCompanySupplyList", "GrandCompanyExchange", "ShopExchangeCurrencyDialog", "SelectYesno", "SelectString"];
    /// <summary>繳交確認視窗開著時整個不動：那一扇是在決定交不交，不替使用者按，也不去關它底下的清單。</summary>
    private const string SupplyRewardWindow = "GrandCompanySupplyReward";
    private static readonly TaskManagerConfiguration CloseConf = new(abortOnTimeout: false, timeLimitMS: 30000);

    private static bool AnyTriggerEnabled => C.FullAutoGCDeliveryDeliverOnVentureExhaust || C.FullAutoGCDeliveryDeliverOnInventory;

    /// <summary>
    /// 背包空格少到會被多角色模式剔除時，背包觸發這一輪會不會去交；會去就先不剔除（同上游）。
    /// 背包觸發關著、前提不符或背包那一種暫停中都回 <see langword="false"/>＝照舊剔除，免得卡在滿背包上什麼都不做。
    /// </summary>
    internal static bool KeepsCharacterWithFullBags()
    {
        if(!C.FullAutoGCDeliveryDeliverOnInventory) return false;
        var data = Data;
        if(data == null) return false;
        NoteCharacter(data);
        if(!GetDueTriggers(data, Trigger.Inventory, out _, out var freeSlots).HasFlag(Trigger.Inventory)) return false;
        var now = Environment.TickCount64;
        if(!KeptLoggedAt.TryGetValue(data.CID, out var at) || now - at >= KeptLogIntervalMs)
        {
            KeptLoggedAt[data.CID] = now;
            PluginLog.Information($"[VentureDelivery] {Censor.Character(data.Name, data.World)}: only {freeSlots} free bag slot(s), below the Multi Mode minimum of {C.MultiMinInventorySlots}; not excluding this character because the Inventory trigger will go hand in first.");
        }
        return true;
    }

    /// <summary>多角色模式作用中每幀呼叫一次。</summary>
    internal static void Tick()
    {
        if(!AnyTriggerEnabled)
        {
            if(States.Count > 0) Reset();
            return;
        }
        if(P.TaskManager.IsBusy || IsOccupied()) ChecksDirty = true;
        WatchStrandedWindows();
    }

    /// <summary>
    /// 多角色模式的決策點（任務佇列空、允許互動）呼叫，排在換角色之前。
    /// 回 <see langword="true"/>＝這一幀已經排了動作，或正在等上一趟的結果可以判定。
    /// </summary>
    internal static bool EnqueueIfNeeded()
    {
        if(!AnyTriggerEnabled) return false;
        var data = Data;
        if(data == null || IsOccupied() || AutoGCHandin.Operation) return false;
        NoteCharacter(data);

        var tripJustEnded = false;
        if(States.TryGetValue(data.CID, out var st) && st.Pending)
        {
            if(!Utils.IsInventoryStateReadable()) return true;
            if(JudgeTrip(data, st)) return true;
            tripJustEnded = true;
        }

        var reasons = GetDueTriggers(data, Trigger.Ventures | Trigger.Inventory, out var ventures, out var freeSlots);
        if(reasons != Trigger.None)
        {
            // 佇列空但 Lifestream 正在替別人跑：這一幀不出發，免得 TaskDeliverItems 跳錯誤通知。
            if(ECommonsIPC.Lifestream.IsBusy()) return false;
            st = GetState(data.CID);
            st.Name = data.Name;
            st.World = data.World;
            if(!TaskDeliverItems.Enqueue())
            {
                if(reasons.HasFlag(Trigger.Ventures)) Pause(st, Trigger.Ventures, ventures, "the delivery could not be queued");
                if(reasons.HasFlag(Trigger.Inventory)) Pause(st, Trigger.Inventory, ventures, "the delivery could not be queued");
                return false;
            }
            st.Pending = true;
            st.Reasons = reasons;
            st.VenturesAtStart = ventures;
            st.FreeSlotsAtStart = freeSlots;
            st.DeliverableAtStart = ScanDeliverable(data).InBags;
            st.FinalPurchaseTried = false;
            st.StrandedCloses = 0;
            ChecksDirty = true;
            PluginLog.Information($"[VentureDelivery] {Who(st)}: going to the Grand Company to hand in expert delivery items (trigger: {Describe(reasons, ventures, freeSlots)}; deliverable in bags {st.DeliverableAtStart}; teleport back afterwards: {(C.FullAutoGCDeliveryTpBackToHome ? "on" : "off")}).");
            return true;
        }

        if(tripJustEnded && C.FullAutoGCDeliveryTpBackToHome && TeleportBack(data))
        {
            PluginLog.Information($"[VentureDelivery] {Who(st)}: teleporting back home after the trip.");
            return true;
        }
        return false;
    }

    /// <summary>判定上一趟。回 <see langword="true"/>＝排了最後一輪兌換，下一次決策點再判。</summary>
    private static bool JudgeTrip(OfflineCharacterData data, TripState st)
    {
        var ventures = Utils.GetVenturesAmount();
        var venturesUp = ventures > st.VenturesAtStart;
        if(st.Reasons.HasFlag(Trigger.Ventures) && !venturesUp && !st.FinalPurchaseTried && CanPurchaseHere())
        {
            // 兌換清單沒勾「籌備後交換」時只在出發那一刻買；這一趟交出來的軍票不在這裡花，要到下一趟才會花。
            st.FinalPurchaseTried = true;
            PluginLog.Information($"[VentureDelivery] {Who(st)}: no ventures bought during the trip (still {ventures}), running one more purchase round before leaving.");
            GCContinuation.EnqueueInitiation(false);
            return true;
        }

        st.Pending = false;
        var freeSlots = Utils.GetInventoryFreeSlotCount();
        var deliverable = ScanDeliverable(data).InBags;
        PluginLog.Information($"[VentureDelivery] {Who(st)}: trip finished (trigger: {st.Reasons}): ventures {st.VenturesAtStart} -> {ventures}, free slots {st.FreeSlotsAtStart} -> {freeSlots}, deliverable in bags {st.DeliverableAtStart} -> {deliverable}.");

        if(st.Reasons.HasFlag(Trigger.Ventures))
        {
            if(venturesUp) Succeed(st.Ventures);
            else Pause(st, Trigger.Ventures, ventures, "the trip bought no ventures");
        }
        else if(st.Ventures.PausedUntil != 0)
        {
            // 這一趟不是為探險幣出發的，它順便買到的探險幣不算「外面變多了」，不能拿來解除暫停。
            st.VenturesBaseline = ventures;
        }

        if(st.Reasons.HasFlag(Trigger.Inventory))
        {
            if(freeSlots > st.FreeSlotsAtStart || deliverable < st.DeliverableAtStart) Succeed(st.Inventory);
            else Pause(st, Trigger.Inventory, ventures, "the trip freed no bag space and handed in nothing from the bags");
        }
        return false;
    }

    /// <summary>這一刻 <paramref name="consider"/> 裡哪幾種觸發該出發。前提檢查共用；暫停中的那種不算。</summary>
    private static Trigger GetDueTriggers(OfflineCharacterData data, Trigger consider, out uint ventures, out int freeSlots)
    {
        ventures = 0;
        freeSlots = 0;
        if(!Utils.IsInventoryStateReadable()) return Trigger.None;
        ventures = Utils.GetVenturesAmount();
        freeSlots = Utils.GetInventoryFreeSlotCount();
        var venturesLow = consider.HasFlag(Trigger.Ventures) && C.FullAutoGCDeliveryDeliverOnVentureExhaust && ventures <= (uint)Math.Max(0, C.FullAutoGCDeliveryDeliverOnVentureLessThan);
        var bagsLow = consider.HasFlag(Trigger.Inventory) && C.FullAutoGCDeliveryDeliverOnInventory && freeSlots <= Math.Max(0, C.FullAutoGCDeliveryInventory);
        if(!venturesLow && !bagsLow) return Trigger.None;

        var retainersInPlay = MultiMode.EnabledRetainers && data.Enabled && data.GetEnabledRetainers().Length > 0;
        var deployablesInPlay = HasDeployablesInPlay(data);
        var wanted = Trigger.None;
        // 探險幣只有僱員會用；背包空間則是僱員與潛水艇／飛空艇的收穫都會用到。
        if(venturesLow && retainersInPlay) wanted |= Trigger.Ventures;
        if(bagsLow && (retainersInPlay || deployablesInPlay)) wanted |= Trigger.Inventory;
        if(wanted == Trigger.None || !Player.IsInHomeWorld) return Trigger.None;

        var reason = GetIneligibleReason(data, retainersInPlay, deployablesInPlay);
        if(reason == null)
        {
            RefreshExpensiveChecks(data);
            if(!CachedLifestream) reason = "Lifestream is not loaded";
        }
        if(reason != null)
        {
            LogIneligibleOnce(data, reason, Describe(wanted, ventures, freeSlots));
            return Trigger.None;
        }

        var due = Trigger.None;
        if(wanted.HasFlag(Trigger.Ventures) && CachedHasDeliverable && !IsPaused(data.CID, Trigger.Ventures, ventures)) due |= Trigger.Ventures;
        // 只有背包裡的東西交掉才空得出背包格子，所以這一種只看背包。
        if(wanted.HasFlag(Trigger.Inventory) && CachedDeliverableInBags > 0 && !IsPaused(data.CID, Trigger.Inventory, ventures)) due |= Trigger.Inventory;
        return due;
    }

    private static string GetIneligibleReason(OfflineCharacterData data, bool retainersInPlay, bool deployablesInPlay)
    {
        if(GCContinuation.GetGCInfo() == null) return "not employed by a Grand Company";
        if(data.GCDeliveryType == GCDeliveryType.Disabled) return "its expert delivery mode is Disabled";
        if(!GCContinuation.IsGCRankSufficientForExpertExchange()) return "its Grand Company rank is too low for expert delivery";
        // 回程只靠多角色模式本來就在用的傳送。回不去的話會停在大國防聯軍，下次處理僱員時找不到傳喚鈴就被剔除。
        if(retainersInPlay && !data.GetIsTeleportEnabledForRetainers()) return "teleport for retainers is off, so it could not get back to a summoning bell";
        if(deployablesInPlay && !data.GetAllowFcTeleportForSubs()) return "it has deployables but teleport to the free company house for deployables is off";
        return null;
    }

    /// <summary>設定面的原因每個角色每種只寫一次：這些不會自己變好，每幀寫會洗掉記錄檔。</summary>
    private static void LogIneligibleOnce(OfflineCharacterData data, string reason, string wanted)
    {
        if(IneligibleLogged.TryGetValue(data.CID, out var last) && last == reason) return;
        IneligibleLogged[data.CID] = reason;
        PluginLog.Information($"[VentureDelivery] {Censor.Character(data.Name, data.World)}: wants to go ({wanted}), but not going to the Grand Company: {reason}.");
    }

    private static string Describe(Trigger triggers, uint ventures, int freeSlots)
    {
        var parts = new List<string>(2);
        if(triggers.HasFlag(Trigger.Ventures)) parts.Add($"ventures {ventures} <= {C.FullAutoGCDeliveryDeliverOnVentureLessThan}");
        if(triggers.HasFlag(Trigger.Inventory)) parts.Add($"free slots {freeSlots} <= {C.FullAutoGCDeliveryInventory}");
        return string.Join(" + ", parts);
    }

    private static bool HasDeployablesInPlay(OfflineCharacterData data)
    {
        return data.WorkshopEnabled && MultiMode.EnabledSubmarines
            && data.GetEnabledVesselsData(VoyageType.Airship).Count + data.GetEnabledVesselsData(VoyageType.Submersible).Count > 0;
    }

    private static void RefreshExpensiveChecks(OfflineCharacterData data)
    {
        var now = Environment.TickCount64;
        if(!ChecksDirty && ChecksCID == data.CID && now - ChecksAt < RecheckMs) return;
        ChecksDirty = false;
        ChecksCID = data.CID;
        ChecksAt = now;
        CachedLifestream = ECommonsIPC.Lifestream.Available;
        (CachedDeliverableInBags, CachedHasDeliverable) = ScanDeliverable(data);
    }

    /// <summary>
    /// 背包（不含兵裝庫）裡可交的裝備格數，以及照籌備模式該算兵裝庫時連兵裝庫一起「有沒有」。
    /// 兵裝庫會連套裝裡的也算進去（同上游），多出來的白跑一趟由暫停收尾。讀不到的容器跳過，只會少報。
    /// </summary>
    private static (int InBags, bool Any) ScanDeliverable(OfflineCharacterData data)
    {
        var im = InventoryManager.Instance();
        if(im == null) return (0, false);
        var settings = data.GetIMSettings();
        var inBags = CountDeliverable(im, Utils.PlayerInvetories, settings);
        var any = inBags > 0 || (data.GCDeliveryType != GCDeliveryType.Hide_Armoury_Chest_Items && CountDeliverable(im, Utils.PlayerArmory, settings) > 0);
        return (inBags, any);
    }

    /// <summary>判定式用稀有品繳交循環那一份（<see cref="GCExpertDeliveryLoop.IsDeliverableGear(uint, InventoryManagementSettings)"/>）。</summary>
    private static int CountDeliverable(InventoryManager* im, InventoryType[] types, InventoryManagementSettings settings)
    {
        var count = 0;
        foreach(var type in types)
        {
            var inv = im->GetInventoryContainer(type);
            if(inv == null || inv->Items == null) continue;
            for(var i = 0; i < inv->Size; i++)
            {
                if(GCExpertDeliveryLoop.IsDeliverableGear(inv->Items[i].ItemId, settings)) count++;
            }
        }
        return count;
    }

    private static TriggerState GetTriggerState(TripState st, Trigger kind) => kind == Trigger.Ventures ? st.Ventures : st.Inventory;

    private static bool IsPaused(ulong cid, Trigger kind, uint ventures)
    {
        if(!States.TryGetValue(cid, out var st)) return false;
        var ts = GetTriggerState(st, kind);
        if(ts.PausedUntil == 0) return false;
        var wentUp = kind == Trigger.Ventures && ventures > st.VenturesBaseline;
        if(wentUp || Environment.TickCount64 >= ts.PausedUntil)
        {
            PluginLog.Information($"[VentureDelivery] {Who(st)}: pause of the {kind} trigger lifted ({(wentUp ? $"ventures went up {st.VenturesBaseline} -> {ventures} outside these trips" : "time is up")}).");
            ClearPause(ts);
            return false;
        }
        if(!ts.PauseSkipLogged)
        {
            ts.PauseSkipLogged = true;
            PluginLog.Information($"[VentureDelivery] {Who(st)}: the {kind} trigger is paused until {ts.PausedUntilLocal:MM-dd HH:mm} after a trip without results; not going for it.");
        }
        return true;
    }

    private static void Pause(TripState st, Trigger kind, uint ventures, string why)
    {
        var ts = GetTriggerState(st, kind);
        ts.Failures++;
        var ms = Math.Min(MaxPauseMs, BasePauseMs << Math.Min(ts.Failures - 1, 4));
        ts.PausedUntil = Environment.TickCount64 + ms;
        ts.PausedUntilLocal = DateTime.Now.AddMilliseconds(ms);
        // 這一行本身已經講了暫停；同一次登入裡不必再補一行「因暫停而沒去」。
        ts.PauseSkipLogged = true;
        if(kind == Trigger.Ventures) st.VenturesBaseline = ventures;
        PublishPauses();
        var until = kind == Trigger.Ventures ? $"its ventures go up or {ts.PausedUntilLocal:MM-dd HH:mm}" : $"{ts.PausedUntilLocal:MM-dd HH:mm}";
        PluginLog.Information($"[VentureDelivery] {Who(st)}: {why}; the {kind} trigger will not send this character again until {until} (unproductive trip #{ts.Failures} in a row for it).");
    }

    private static void Succeed(TriggerState ts)
    {
        ts.Failures = 0;
        ClearPause(ts);
    }

    private static void ClearPause(TriggerState ts)
    {
        if(ts.PausedUntil == 0) return;
        ts.PausedUntil = 0;
        PublishPauses();
    }

    private static bool CanPurchaseHere()
    {
        var gc = GCContinuation.GetGCInfo();
        if(gc == null || !AutoGCHandin.IsValidGCTerritory()) return false;
        if(!IsShopInReach(gc.Value.ShopDataID)) return false;
        return GCContinuation.GetNextPurchaseListing() != null;
    }

    /// <summary>與 <see cref="GCContinuation"/> 找補給負責人的條件相同（可選取、10 米內），不在範圍內那一輪兌換只會空等逾時。</summary>
    private static bool IsShopInReach(uint shopDataID)
    {
        var me = Player.Position;
        foreach(var o in Svc.Objects)
        {
            if(o.BaseId == shopDataID && o.IsTargetable && Vector3.Distance(me, o.Position) < 10f) return true;
        }
        return false;
    }

    /// <summary>與多角色模式自己的分支同一套判斷：有潛水艇／飛空艇可收就回公會房屋，否則照僱員的傳送設定回去。</summary>
    private static bool TeleportBack(OfflineCharacterData data)
    {
        if(data.WorkshopEnabled && data.AnyEnabledVesselsAvailable() && MultiMode.EnabledSubmarines)
        {
            if(data.ShouldWaitForAllWhenLoggedIn() && !data.AreAnyEnabledVesselsReturnInNext(0, true)) return false;
            return TaskTeleportToProperty.EnqueueIfNeededAndPossible(true);
        }
        return TaskTeleportToProperty.EnqueueIfNeededAndPossible(false);
    }

    /// <summary>
    /// 這一趟還沒判定、任務佇列與自動繳交都停了，大國防聯軍的視窗卻還開著 ⇒ 關掉，讓多角色模式能繼續。
    /// 例：繳交清單的篩選與角色的籌備模式對不上時自動繳交永遠不會開始，而多角色模式遇到「佔用中」只會一直等。
    /// </summary>
    private static void WatchStrandedWindows()
    {
        if(!ProperOnLogin.PlayerPresent || !States.TryGetValue(Player.CID, out var st) || !st.Pending || st.StrandedCloses >= MaxStrandedCloses
            || P.TaskManager.IsBusy || AutoGCHandin.Operation || !AutoGCHandin.IsValidGCTerritory()
            || IsWindowOpen(SupplyRewardWindow) || !AnyGCWindowOpen())
        {
            StrandedSince = 0;
            return;
        }
        var now = Environment.TickCount64;
        if(StrandedSince == 0)
        {
            StrandedSince = now;
            return;
        }
        if(now - StrandedSince < StrandedMs) return;
        StrandedSince = 0;
        st.StrandedCloses++;
        PluginLog.Information($"[VentureDelivery] {Who(st)}: Grand Company windows left open with nothing running for {StrandedMs / 1000}s ({DescribeOpenGCWindows()}), closing them so Multi Mode can carry on (attempt {st.StrandedCloses}/{MaxStrandedCloses}).");
        P.TaskManager.Enqueue(CloseGCWindowsStep, "VentureDelivery.CloseStrandedWindows", CloseConf);
    }

    private static bool? CloseGCWindowsStep()
    {
        if(!AnyGCWindowOpen()) return true;
        if(IsWindowOpen(SupplyRewardWindow)) return false;
        // 每個關窗方法自己有節流與「同一扇只按一次」的守衛，這裡每幀呼叫是安全的。
        if(GCContinuation.CleanupUI()) return false;
        if(IsWindowOpen("GrandCompanySupplyList")) GCContinuation.CloseSupplyList();
        else if(IsWindowOpen("GrandCompanyExchange")) GCContinuation.CloseExchange();
        else if(IsWindowOpen("SelectString")) GCContinuation.CloseSelectString();
        return false;
    }

    private static bool IsWindowOpen(string name) => TryGetAddonByName<AtkUnitBase>(name, out var addon) && IsAddonReady(addon);

    private static bool AnyGCWindowOpen()
    {
        foreach(var name in GCWindows)
        {
            if(IsWindowOpen(name)) return true;
        }
        return false;
    }

    private static string DescribeOpenGCWindows() => string.Join(", ", GCWindows.Where(IsWindowOpen));

    /// <summary>換了角色（新的一次登入）時，讓「暫停中略過」那一行在這次登入再寫一次。</summary>
    private static void NoteCharacter(OfflineCharacterData data)
    {
        if(data.CID == LastSeenCID) return;
        LastSeenCID = data.CID;
        if(States.TryGetValue(data.CID, out var seen))
        {
            seen.Ventures.PauseSkipLogged = false;
            seen.Inventory.PauseSkipLogged = false;
        }
    }

    private static TripState GetState(ulong cid)
    {
        if(!States.TryGetValue(cid, out var st)) States[cid] = st = new();
        return st;
    }

    private static void Reset()
    {
        States.Clear();
        StrandedSince = 0;
        PublishPauses();
    }

    private static void PublishPauses()
    {
        var list = new List<PauseInfo>();
        foreach(var st in States.Values)
        {
            if(st.Ventures.PausedUntil != 0) list.Add(new(Trigger.Ventures, st.Name, st.World, st.Ventures.PausedUntilLocal));
            if(st.Inventory.PausedUntil != 0) list.Add(new(Trigger.Inventory, st.Name, st.World, st.Inventory.PausedUntilLocal));
        }
        Pauses = [.. list];
    }

    private static string Who(TripState st) => Censor.Character(st.Name, st.World);
}
