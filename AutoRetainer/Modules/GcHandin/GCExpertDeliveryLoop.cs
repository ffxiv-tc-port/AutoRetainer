using AutoRetainer.Internal;
using AutoRetainer.Internal.InventoryManagement;
using AutoRetainer.Scheduler.Handlers;
using AutoRetainer.Scheduler.Tasks;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons.ExcelServices;
using ECommons.GameHelpers;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace AutoRetainer.Modules.GcHandin;

/// <summary>
/// 稀有品繳交循環:把指定存放計畫底下的雇員身上的裝備取出來,拿去大國防聯軍繳交,取完為止。
///
/// <para>🔴 零自動觸發。只有 <see cref="Start"/> 會讓它動起來,而 <see cref="Start"/> 只有 UI 上那顆按鈕
/// 會呼叫。沒有任何事件、排程或多角色流程會啟動它。</para>
///
/// <para>整條流程沒有一步是自己操作 addon 的:走到鈴前、選雇員、開道具管理、去大國防聯軍繳交,
/// 全部委給外掛本來就在跑的任務鏈。這裡只負責決定「下一步做什麼」以及「什麼時候該停下來」。</para>
/// </summary>
internal static unsafe class GCExpertDeliveryLoop
{
    /// <summary>軍票獲得量提升:1078 是道具版(軍票預支單),414 是部隊行動版。
    /// 兩個都算「已經在加成中」—— 道具的說明明寫它會覆蓋效果相同的公會特效。</summary>
    private static readonly uint[] SealBonusStatusIds = [1078, 414];

    /// <summary>軍票預支單。</summary>
    private const uint SealAllowanceItemId = 14946;

    /// <summary>用掉軍票預支單之後,等這麼久還沒看到加成就不等了。</summary>
    private const long SealBuffWaitMs = 6000;

    /// <summary>送出使用道具之後,等這麼久背包數量還沒少就當作「這次根本沒送出去」。
    /// 🔴 這是 .50 的教訓:道具欄沒開的時候 UseItem 是**靜默無效**的,而當時只驗「加成有沒有出現」,
    /// 於是把「道具沒被使用」誤報成「加成沒生效」,使用者完全看不出真正的原因。</summary>
    private const long SealItemConsumeWaitMs = 2000;

    /// <summary>送出一個任務鏈之後,至少要等這麼久才准把「不忙」解讀成「做完了」。</summary>
    private const long EnqueueGraceMs = 500;

    /// <summary>開鈴之後等雇員清單真的載入的上限。
    /// 🔴 這段等待**不能**用「我們的任務佇列排空」當結論:排空只代表我們送出的動作做完了,
    /// 遊戲還要自己把視窗開起來、把雇員資料填進去。.52 就是拿排空當結論,在互動後 526 毫秒
    /// 就判定「清單沒載入」而停止。</summary>
    private const long RetainerListLoadTimeoutMs = 20000;

    /// <summary>取回指令之間的最小間隔。伺服器實測每格約 0.13 秒。</summary>
    private const int RetrieveIntervalMs = 150;

    /// <summary>一輪取回送完之後,等雇員格數安定的輪詢間隔與安靜門檻。
    /// 抄 SND 巨集實測出來的節奏:送指令比伺服器消化快,固定秒數會嚴重低估進度。</summary>
    private const int SettlePollMs = 250;
    private const long SettleQuietMs = 750;
    private const long SettleCapMs = 15000;

    /// <summary>我們自己送出導航之後,這麼久之內的「不可用」一律當成過渡態。
    /// 傳送詠唱、跨區載入、城內乙太網換圖都在這個窗裡,而且它們的長短差很多 ——
    /// 與其列舉「哪些不可用可以忍」(漏一種就誤殺一次),不如用正向條件:
    /// **是我們自己叫它移動的,那它不可用就是正常的**。</summary>
    private const long NavigationGraceMs = 5 * 60 * 1000;

    /// <summary>玩家「不可用」持續超過這麼久才算真的出事。
    /// 🔴 傳送詠唱、換區、載入畫面期間玩家本來就不可用 —— .50 就是把這種合法過渡態當成致命,
    /// 在 Lifestream 導航途中把自己停掉兩次。</summary>
    private const long UnavailableGraceMs = 30000;

    private enum Phase
    {
        Idle,
        SealBonus,
        SealBonusWait,
        EnsureBell,
        EnsureBellWait,
        OpenBell,
        OpenBellWait,
        SelectRetainer,
        SelectRetainerWait,
        Retrieve,
        RetrieveSettle,
        LeaveRetainer,
        LeaveRetainerWait,
        CloseBell,
        CloseBellWait,
        Handin,
        HandinWait,
    }

    /// <summary>這一輪取回停下來的理由,決定繳交完之後要不要再跑一輪。</summary>
    private enum RoundEnd
    {
        /// <summary>清單上的雇員都看過了,沒有裝備可拿 —— 繳完就收工。</summary>
        NoGearLeft,
        /// <summary>背包到保留下限,先去繳交 —— 繳完還要回來繼續取。</summary>
        ReserveReached,
    }

    internal static bool Running { get; private set; }

    private static Phase CurrentPhase = Phase.Idle;
    private static long PhaseEnteredAt;
    private static RoundEnd RoundEndReason;
    private static long UnavailableSince;

    /// <summary>導航窗的結束時刻。送出任何會讓角色移動/換區的東西時往後推。</summary>
    private static long NavigationDeadline;

    private static List<string> Retainers = [];
    private static int RetainerIndex;
    private static bool TravelledToBellThisRound;

    /// <summary>這一輪永遠拿不到、不必再看的道具(重複的獨占道具等)。</summary>
    private static readonly HashSet<uint> SkippedItems = [];

    /// <summary>這一趟已經送過指令、正在飛的道具。與 <see cref="SkippedItems"/> 不同:
    /// 這些等落地之後還要再看一次。有它才能在等某一件落地的同時繼續對別的格子送指令 ——
    /// .50 是一件一件等,遇到伺服器默默拒收的格子就整整空轉 10 秒(追蹤過期時間)。</summary>
    private static readonly HashSet<uint> DeferredItems = [];

    private static int PassFired;
    private static long SettleLastChangeAt;
    private static int SettleLastCount;

    /// <summary>用掉軍票預支單那一刻的持有數,用來確認道具真的被消耗掉。</summary>
    private static int SealAllowanceCountAtUse;

    // 給 UI 看的統計
    internal static int RetrievedTotal { get; private set; }
    internal static int RetrievedThisRound { get; private set; }
    internal static int HandinRounds { get; private set; }
    internal static string StatusText { get; private set; } = "";

    internal static string CurrentPhaseName => CurrentPhase switch
    {
        Phase.Idle => Loc.T("Idle"),
        Phase.SealBonus or Phase.SealBonusWait => Loc.T("Checking seal bonus"),
        Phase.EnsureBell or Phase.EnsureBellWait => Loc.T("Going to a summoning bell"),
        Phase.OpenBell or Phase.OpenBellWait => Loc.T("Opening the summoning bell"),
        Phase.SelectRetainer or Phase.SelectRetainerWait => Loc.T("Opening retainer"),
        Phase.Retrieve or Phase.RetrieveSettle => Loc.T("Retrieving gear"),
        Phase.LeaveRetainer or Phase.LeaveRetainerWait or Phase.CloseBell or Phase.CloseBellWait => Loc.T("Closing retainer"),
        Phase.Handin or Phase.HandinWait => Loc.T("Handing in at the Grand Company"),
        _ => "?",
    };

    #region 雇員名單

    /// <summary>循環要處理的雇員。名單在**開始時**決定一次並沿用整趟。
    /// ⚠️ 一律用 AutoRetainer 自己存的角色資料,不碰遊戲的雇員管理器 ——
    /// 那個東西在這次登入還沒開過傳喚鈴之前是空的。</summary>
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

        // 🔴 沒選計畫時回空清單而不是「全部」。「還沒設定」與「要對每個雇員做」是兩回事。
        if(C.ExpertDeliveryLoopEntrustPlan == Guid.Empty) return result;

        foreach(var retainer in data.RetainerData)
        {
            var name = retainer.Name.ToString();
            if(name.IsNullOrEmpty()) continue;
            if(Utils.GetAdditionalData(data.CID, name).EntrustPlan == C.ExpertDeliveryLoopEntrustPlan) result.Add(name);
        }
        return result;
    }

    #endregion

    #region 開始/停止

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
        UnavailableSince = 0;
        NavigationDeadline = 0;
        RoundEndReason = RoundEnd.NoGearLeft;
        SkippedItems.Clear();
        DeferredItems.Clear();
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
        // 🔴 停止只停外層狀態機,已經排進 AutoRetainer/Lifestream 的任務鏈會自己跑完 ——
        //    中途硬中斷互動鏈的風險比讓它跑完高。但那會造成「說停了卻還在繳交」的困惑,
        //    所以當下如果還有東西在跑,訊息要明講。
        var stillBusy = Utils.IsBusy;
        var summary = $"{reason} ({string.Format(Loc.T("retrieved {0}, handin rounds {1}"), RetrievedTotal, HandinRounds)})";
        if(stillBusy) summary += " " + Loc.T("The loop has stopped, but work already queued in AutoRetainer will finish on its own.");
        if(success)
        {
            DuoLog.Information(summary);
            if(C.GCHandinNotify) Utils.TryNotify(summary);
        }
        else
        {
            DuoLog.Warning(summary);
        }
        PluginLog.Information($"[ExpertDeliveryLoop] Stopped: {reason} | retrieved={RetrievedTotal} handinRounds={HandinRounds} success={success} stillBusy={stillBusy}");
    }

    private static void Fail(string reason)
    {
        StatusText = reason;
        DuoLog.Warning(reason);
        PluginLog.Information($"[ExpertDeliveryLoop] Refused to start: {reason}");
    }

    /// <summary>雇員清單是不是真的可以用了。
    /// 🔴 <c>GameRetainerManager.Ready</c> 只讀 <c>RetainerManager.IsReady</c> 這個旗標,**不保證
    /// 雇員陣列已經填好** —— Ready 為 true 而 Count 為 0 是真實會出現的狀態(剛換區、剛開鈴的
    /// 那一小段)。而 <c>TryGetRetainerByName</c> 在那個狀態下對每個名字都回 false,與「這個雇員
    /// 真的不存在」完全不可分。所以「存在性」這種結論只准在這個閘門為 true 時做。</summary>
    internal static bool RetainerListLoaded => GameRetainerManager.Ready && GameRetainerManager.Count > 0;

    internal static int EffectiveReservedSlots => Math.Max(C.ExpertDeliveryLoopReservedSlots, C.MultiMinInventorySlots);

    private static void SetPhase(Phase phase)
    {
        CurrentPhase = phase;
        PhaseEnteredAt = Environment.TickCount64;
    }

    private static long TimeInPhase => Environment.TickCount64 - PhaseEnteredAt;

    private static bool ChainFinished => !Utils.IsBusy && TimeInPhase > EnqueueGraceMs;

    #endregion

    internal static void Tick()
    {
        if(!Running) return;
        if(!CheckPlayerAvailability()) return;

        switch(CurrentPhase)
        {
            case Phase.SealBonus: TickSealBonus(); break;
            case Phase.SealBonusWait: TickSealBonusWait(); break;
            case Phase.EnsureBell: TickEnsureBell(); break;
            case Phase.EnsureBellWait: TickEnsureBellWait(); break;
            case Phase.OpenBell: TickOpenBell(); break;
            case Phase.OpenBellWait: TickOpenBellWait(); break;
            case Phase.SelectRetainer: TickSelectRetainer(); break;
            case Phase.SelectRetainerWait: TickSelectRetainerWait(); break;
            case Phase.Retrieve: TickRetrieve(); break;
            case Phase.RetrieveSettle: TickRetrieveSettle(); break;
            case Phase.LeaveRetainer: TickLeaveRetainer(); break;
            case Phase.LeaveRetainerWait: TickLeaveRetainerWait(); break;
            case Phase.CloseBell: TickCloseBell(); break;
            case Phase.CloseBellWait: TickCloseBellWait(); break;
            case Phase.Handin: TickHandin(); break;
            case Phase.HandinWait: TickHandinWait(); break;
        }
    }

    /// <summary>玩家可用性守衛。
    /// 🔴 「不可用」在這條流程裡**大多數時候是正常的**:傳送詠唱、跨區載入、城內乙太網換圖都會讓
    /// <c>Player.Available</c> 變成 false,而這條流程本來就會移動好幾次。.50 直接把它當致命,
    /// 在 Lifestream 導航途中把自己停掉三次(其中一次是使用者手動用城內水晶換圖)。
    ///
    /// <para>判準刻意寫成**正向條件**而不是「哪些不可用可以忍」的列舉:過渡態的種類列不完,
    /// 漏掉一種就是又一次誤殺。只要①有任務鏈或 Lifestream 在動,或②還在我們自己送出的導航窗內,
    /// 不可用就一律視為過渡。只有「沒有任何東西在進行、而且持續不可用超過寬限期」才停。</para></summary>
    private static bool CheckPlayerAvailability()
    {
        if(Player.Available)
        {
            UnavailableSince = 0;
            return true;
        }

        var now = Environment.TickCount64;
        if(Utils.IsBusy || now < NavigationDeadline)
        {
            UnavailableSince = 0;
            return false;
        }

        if(UnavailableSince == 0)
        {
            UnavailableSince = now;
            return false;
        }
        if(now - UnavailableSince < UnavailableGraceMs) return false;

        Stop(Loc.T("Stopped: the player stayed unavailable with nothing in progress."));
        return false;
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

        // 動畫鎖住的時候送出去也是白送。
        if(Player.IsAnimationLocked) return;
        if(!EzThrottler.Throttle("ExpertDeliveryLoopUseAllowance", 2000)) return;

        SealAllowanceCountAtUse = GetSealAllowanceCount();
        // 🔴 四個參數的版本才是外掛裡已經證實可用的呼叫形式(見 TaskOpenAllCoffers)。
        //    單參數版在道具欄沒開的時候是靜默無效的 —— .50 就是這樣白白等了六秒。
        AgentInventoryContext.Instance()->UseItem(SealAllowanceItemId, (InventoryType)0x270F, 0, 0);
        PluginLog.Information($"[ExpertDeliveryLoop] Sent use-item for Priority Seal Allowance ({SealAllowanceCountAtUse} held before use).");
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

        // 先驗「道具有沒有真的被消耗」,再談加成。兩者分開才講得出真正的原因。
        if(TimeInPhase > SealItemConsumeWaitMs && GetSealAllowanceCount() == SealAllowanceCountAtUse
            && !Player.IsAnimationLocked && !Svc.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.Casting])
        {
            var reason = Loc.T("Stopped: the Priority Seal Allowance was not consumed - the game refused the use-item.");
            PluginLog.Information($"[ExpertDeliveryLoop] Use-item did not consume anything (still {GetSealAllowanceCount()} held after {TimeInPhase}ms).");
            if(C.ExpertDeliveryLoopStopWithoutSealBonus)
            {
                Stop(reason);
                return;
            }
            DuoLog.Warning(reason);
            SetPhase(Phase.EnsureBell);
            return;
        }

        if(TimeInPhase <= SealBuffWaitMs) return;

        if(C.ExpertDeliveryLoopStopWithoutSealBonus)
        {
            Stop(Loc.T("Stopped: the seal bonus did not appear after using a Priority Seal Allowance."));
            return;
        }
        DuoLog.Warning(Loc.T("The seal bonus did not appear after using a Priority Seal Allowance - continuing without it."));
        SetPhase(Phase.EnsureBell);
    }

    #endregion

    #region Lifestream 我的最愛

    /// <summary>有沒有指定「鈴在哪」。設了就代表使用者已經挑好目的地 ——
    /// 🔴 這種時候絕對不可以退回泛用的移動指令:那個指令會把人送到它自己的預設地點
    /// (實測被送到烏爾達哈),而流程接著會在錯的城市裡找鈴。停下來比亂傳好。</summary>
    internal static bool HasBellTarget => C.ExpertDeliveryLoopBellFavoriteId != 0;

    internal static bool HasGCTarget => C.ExpertDeliveryLoopGCFavoriteId != 0;

    /// <summary>叫 Lifestream 走到某個收藏項。回 false 代表**什麼都沒排進去**,呼叫端要立刻停,
    /// 不要等一個永遠不會來的完成訊號。</summary>
    private static bool GoToFavorite(uint id, byte sub, string what)
    {
        if(id == 0) return false;
        bool ok;
        try
        {
            ok = S.LifestreamIPC.TeleportToFavorite(id, sub);
        }
        catch(Exception e)
        {
            // 舊版 Lifestream 沒有這個門。
            PluginLog.Information($"[ExpertDeliveryLoop] TeleportToFavorite is unavailable: {e.Message}");
            return false;
        }
        if(!ok)
        {
            PluginLog.Information($"[ExpertDeliveryLoop] Lifestream refused to travel to the {what} favourite (id={id}, sub={sub}) - it may have been unstarred, or Lifestream is busy.");
            return false;
        }
        PluginLog.Information($"[ExpertDeliveryLoop] Travelling to the {what} favourite (id={id}, sub={sub}).");
        NavigationDeadline = Environment.TickCount64 + NavigationGraceMs;
        return true;
    }

    #endregion

    #region 到鈴邊

    /// <summary>目前拿得到的傳喚鈴。設定了「指定的鈴」時,在多個都構得到的情況下挑離指定點最近的那個 ——
    /// 純粹用「離玩家最近」在鈴擠在一起的地方會挑錯。</summary>
    internal static IGameObject GetPreferredBell()
    {
        if(Player.Object is null) return null;

        IGameObject best = null;
        var bestScore = float.MaxValue;
        // 用了最愛就不必再靠座標挑:Lifestream 已經把人放在正確的地點,當前區域裡最近的那個就是對的。
        var useSaved = !HasBellTarget && C.ExpertDeliveryLoopUseSavedBell
            && C.ExpertDeliveryLoopBellTerritory == Svc.ClientState.TerritoryType;

        foreach(var x in Svc.Objects)
        {
            if(x.ObjectKind != ObjectKind.Housing && x.ObjectKind != ObjectKind.EventObj) continue;
            if(!x.Name.ToString().EqualsIgnoreCaseAny(Lang.BellName)) continue;
            if(!x.IsTargetable) continue;
            if(Vector3.Distance(x.Position, Player.Object.Position) >= Utils.GetValidInteractionDistance(x)) continue;

            var score = useSaved
                ? Vector3.Distance(x.Position, C.ExpertDeliveryLoopBellPosition)
                : Vector3.Distance(x.Position, Player.Object.Position);
            if(score < bestScore)
            {
                bestScore = score;
                best = x;
            }
        }
        return best;
    }

    private static void TickEnsureBell()
    {
        if(Utils.IsBusy) return;

        if(GetPreferredBell() != null)
        {
            TravelledToBellThisRound = false;
            RetainerIndex = 0;
            RetrievedThisRound = 0;
            RoundEndReason = RoundEnd.NoGearLeft;
            SetPhase(Phase.OpenBell);
            return;
        }

        if(TravelledToBellThisRound)
        {
            // 已經到過目的地卻還是沒有鈴 —— 再送一次只會得到同樣的結果。
            Stop(HasBellTarget
                ? Loc.T("Stopped: arrived at the chosen destination but there is no summoning bell within reach. Pick a favourite that is closer to a bell.")
                : Loc.T("Stopped: no summoning bell in reach after travelling."));
            return;
        }

        // 使用者指定了目的地:一律走它,而且**只走它**。
        if(HasBellTarget)
        {
            if(!GoToFavorite(C.ExpertDeliveryLoopBellFavoriteId, C.ExpertDeliveryLoopBellFavoriteSub, "bell"))
            {
                Stop(Loc.T("Stopped: could not travel to the chosen summoning bell destination - check that it is still starred in Lifestream."));
                return;
            }
            TravelledToBellThisRound = true;
            SetPhase(Phase.EnsureBellWait);
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

        // ⚠️ 這條退路會把人送到該指令自己的預設地點,不一定是使用者要的鈴。
        //    指定一個 Lifestream 我的最愛才是可靠的做法。
        PluginLog.Information($"[ExpertDeliveryLoop] No bell in reach and no favourite chosen, falling back to Lifestream command \"{command}\".");
        S.LifestreamIPC.ExecuteCommand(command);
        NavigationDeadline = Environment.TickCount64 + NavigationGraceMs;
        TravelledToBellThisRound = true;
        SetPhase(Phase.EnsureBellWait);
    }

    private static void TickEnsureBellWait()
    {
        if(!ChainFinished) return;
        SetPhase(Phase.EnsureBell);
    }

    private static void TickOpenBell()
    {
        if(Utils.IsBusy) return;

        // 已經在雇員清單裡就不用再點鈴一次。
        if(RetainerListLoaded && TryGetAddonByName<AtkUnitBase>("RetainerList", out var list) && IsAddonReady(list))
        {
            SetPhase(Phase.SelectRetainer);
            return;
        }

        PluginLog.Information($"[ExpertDeliveryLoop] Interacting with the summoning bell.");
        TaskInteractWithNearestBell.Enqueue();
        SetPhase(Phase.OpenBellWait);
    }

    private static void TickOpenBellWait()
    {
        // 閘門是「清單真的載入了」,不是「佇列排空了」。兩者差了整整一段遊戲自己的載入時間。
        if(RetainerListLoaded)
        {
            SetPhase(Phase.SelectRetainer);
            return;
        }

        if(TimeInPhase > RetainerListLoadTimeoutMs)
        {
            // ⚠️ 走到這裡只說明清單沒載入,**不代表任何一個雇員不存在**。
            //    這條路徑刻意不產生任何「已經不存在」的訊息 —— 那會把載入問題講成資料問題。
            Stop(Loc.T("Stopped: the retainer list did not load after using the summoning bell."));
        }
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

    /// <summary>目前開著的雇員身上,這一趟還沒送過指令、也還沒被判定拿不到的第一件可繳交裝備。</summary>
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
                if(DeferredItems.Contains(item->ItemId)) continue;
                if(!IsDeliverableGear(item->ItemId)) continue;
                return item->ItemId;
            }
        }
        return 0;
    }

    /// <summary>雇員身上還剩幾格有東西,用來判斷「這一批指令落地了沒」。</summary>
    private static int CountOccupiedRetainerSlots()
    {
        var used = 0;
        foreach(var type in Utils.RetainerInventories)
        {
            var inv = RetainerRetrieve.TryGetReadableContainer(type);
            if(inv == null) continue;
            for(var i = 0; i < inv->Size; i++)
            {
                var item = inv->GetInventorySlot(i);
                if(item == null) continue;
                if(item->ItemId != 0 && item->Quantity > 0) used++;
            }
        }
        return used;
    }

    private static bool ReserveReached => Utils.GetInventoryFreeSlotCount() <= EffectiveReservedSlots;

    private static void TickSelectRetainer()
    {
        if(Utils.IsBusy) return;

        if(ReserveReached)
        {
            PluginLog.Information($"[ExpertDeliveryLoop] Inventory down to {Utils.GetInventoryFreeSlotCount()} free slots (reserve {EffectiveReservedSlots}), going to hand in.");
            RoundEndReason = RoundEnd.ReserveReached;
            SetPhase(Phase.CloseBell);
            return;
        }

        if(RetainerIndex >= Retainers.Count)
        {
            // 名單走完了。RoundEndReason 維持 NoGearLeft,繳完這批就收工。
            SetPhase(Phase.CloseBell);
            return;
        }

        // 🔴 存在性判斷的前置條件要在**每一次**判斷之前重驗,不能只靠「進這個階段之前驗過一次」。
        //    清單會在換區、關鈴、繳交來回之後失效,而失效狀態下 TryGetRetainerByName 對每個名字
        //    都回 false —— .52 就是在第二輪回鈴時把三個雇員全判成「已經不存在」。
        if(!RetainerListLoaded)
        {
            if(TimeInPhase > RetainerListLoadTimeoutMs)
            {
                Stop(Loc.T("Stopped: the retainer list is not loaded, so it cannot be told whether the retainers are still there."));
            }
            return;
        }

        var name = Retainers[RetainerIndex];
        // 只有在上面那道閘門為 true 時,這個 false 才真的代表「這個雇員不存在」。
        if(!Utils.TryGetRetainerByName(name, out _))
        {
            DuoLog.Warning(string.Format(Loc.T("Retainer \"{0}\" no longer exists, skipping."), name));
            PluginLog.Information($"[ExpertDeliveryLoop] Retainer {name} is not in the game's retainer list, skipping.");
            RetainerIndex++;
            return;
        }

        PluginLog.Information($"[ExpertDeliveryLoop] Opening item storage of {name} ({RetainerIndex + 1}/{Retainers.Count}).");
        P.TaskManager.Enqueue(() => RetainerListHandlers.SelectRetainerByName(name), $"SelectRetainerByName({name})");
        P.TaskManager.Enqueue(() => Utils.TryGetCurrentRetainer(out _), $"WaitCurrentRetainer({name})");
        P.TaskManager.Enqueue(RetainerHandlers.SelectEntrustItems, $"SelectEntrustItems({name})");
        P.TaskManager.Enqueue(InventorySpaceManager.IsRetainerInventoryLoaded, $"WaitRetainerInventoryLoaded({name})");
        SetPhase(Phase.SelectRetainerWait);
    }

    private static void TickSelectRetainerWait()
    {
        if(!ChainFinished) return;

        if(!InventorySpaceManager.IsRetainerInventoryLoaded())
        {
            Stop(string.Format(Loc.T("Stopped: could not open the item storage of \"{0}\"."), Retainers[RetainerIndex]));
            return;
        }

        RetainerRetrieve.ResetTracking();
        SkippedItems.Clear();
        DeferredItems.Clear();
        PassFired = 0;
        SetPhase(Phase.Retrieve);
    }

    private static void TickRetrieve()
    {
        if(ReserveReached)
        {
            PluginLog.Information($"[ExpertDeliveryLoop] Reserve reached while retrieving ({Utils.GetInventoryFreeSlotCount()} free, reserve {EffectiveReservedSlots}).");
            RoundEndReason = RoundEnd.ReserveReached;
            SetPhase(Phase.LeaveRetainer);
            return;
        }

        if(!InventorySpaceManager.IsRetainerInventoryLoaded())
        {
            Stop(Loc.T("Stopped: the retainer's item storage closed unexpectedly."));
            return;
        }

        if(!EzThrottler.Throttle("ExpertDeliveryLoopRetrieve", RetrieveIntervalMs)) return;

        var itemId = FindGearOnOpenRetainer();
        if(itemId == 0)
        {
            // 這一趟沒有還能送指令的東西了。有送出過就等它們落地再看一次;
            // 一次都沒送過就代表這個雇員真的沒有可取的裝備。
            // 🔴 這條分支是 .50 卡住的地方之一:雇員背包有東西但全都不是稀有品時,
            //    當時會走到一個永遠等不到的取回。
            if(PassFired > 0)
            {
                SettleLastCount = CountOccupiedRetainerSlots();
                SettleLastChangeAt = Environment.TickCount64;
                SetPhase(Phase.RetrieveSettle);
                return;
            }
            PluginLog.Information($"[ExpertDeliveryLoop] {Retainers[RetainerIndex]} has no more deliverable gear.");
            SetPhase(Phase.LeaveRetainer);
            return;
        }

        var result = RetainerRetrieve.RetrieveSlotById(itemId, false, false);
        if(result >= 1)
        {
            RetrievedTotal++;
            RetrievedThisRound++;
            PassFired++;
            // 送出去了就先不要再看這一件,改去對別的格子送 —— 不必站在這裡等它落地。
            DeferredItems.Add(itemId);
            return;
        }

        switch(result)
        {
            case RetainerRetrieve.ResultCommandInFlight:
                // 這一件還在飛。先跳過去做別的,等這一趟送完再統一等落地。
                DeferredItems.Add(itemId);
                return;

            case RetainerRetrieve.ResultInventoryFull:
                RoundEndReason = RoundEnd.ReserveReached;
                SetPhase(Phase.LeaveRetainer);
                return;

            case RetainerRetrieve.ResultBlockedUnique:
            case RetainerRetrieve.ResultInCrystals:
            case RetainerRetrieve.ResultNotPresent:
                SkippedItems.Add(itemId);
                PluginLog.Information($"[ExpertDeliveryLoop] Skipping item {itemId} ({ExcelItemHelper.GetName(itemId)}): retrieve returned {result}.");
                return;

            case RetainerRetrieve.ResultRetainerUnavailable:
                // 🔴 「讀不到」不等於「沒有」。
                Stop(string.Format(Loc.T("Stopped: could not read the storage of \"{0}\"."), Retainers[RetainerIndex]));
                return;

            default:
                Stop(string.Format(Loc.T("Stopped: unexpected retrieve result {0}."), result));
                return;
        }
    }

    /// <summary>等這一趟送出去的指令落地。判準是「雇員格數不再變動」而不是固定秒數 ——
    /// 送指令比伺服器消化快,固定秒數會嚴重低估進度。</summary>
    private static void TickRetrieveSettle()
    {
        if(!InventorySpaceManager.IsRetainerInventoryLoaded())
        {
            Stop(Loc.T("Stopped: the retainer's item storage closed unexpectedly."));
            return;
        }
        if(!EzThrottler.Throttle("ExpertDeliveryLoopSettle", SettlePollMs)) return;

        var now = Environment.TickCount64;
        var count = CountOccupiedRetainerSlots();
        if(count != SettleLastCount)
        {
            SettleLastCount = count;
            SettleLastChangeAt = now;
            return;
        }

        if(now - SettleLastChangeAt < SettleQuietMs && now - PhaseEnteredAt < SettleCapMs) return;

        // 落地了(或等太久)。重新開一趟:上一趟被伺服器拒收的格子這時候會重新被納入。
        RetainerRetrieve.ResetTracking();
        DeferredItems.Clear();
        PassFired = 0;
        SetPhase(Phase.Retrieve);
    }

    /// <summary>關掉道具管理視窗、離開這個雇員,回到雇員清單。
    /// 🔴 .50 只送了「關閉雇員代理」一步就去等「不再被佔用」,而站在鈴前本來就一直是被佔用狀態,
    /// 於是永遠等不到,得靠使用者手動關視窗才會繼續。正式流程的收尾是
    /// 「關道具管理 → 在雇員選單選告辭 → 回到雇員清單」,這裡照抄。</summary>
    private static void TickLeaveRetainer()
    {
        PluginLog.Information($"[ExpertDeliveryLoop] Leaving retainer {Retainers[RetainerIndex]}.");
        P.TaskManager.Enqueue(RetainerHandlers.CloseAgentRetainer, "CloseAgentRetainer");
        P.TaskManager.Enqueue(RetainerHandlers.SelectQuit, "SelectQuit");
        P.TaskManager.Enqueue(() => TryGetAddonByName<AtkUnitBase>("RetainerList", out var a) && IsAddonReady(a), "WaitRetainerList");
        SetPhase(Phase.LeaveRetainerWait);
    }

    private static void TickLeaveRetainerWait()
    {
        if(!ChainFinished) return;

        if(!TryGetAddonByName<AtkUnitBase>("RetainerList", out var list) || !IsAddonReady(list))
        {
            Stop(Loc.T("Stopped: could not get back to the retainer list."));
            return;
        }

        RetainerIndex++;
        SetPhase(RoundEndReason == RoundEnd.ReserveReached ? Phase.CloseBell : Phase.SelectRetainer);
    }

    /// <summary>關掉雇員清單,離開傳喚鈴。</summary>
    private static void TickCloseBell()
    {
        P.TaskManager.Enqueue(RetainerListHandlers.CloseRetainerList, "CloseRetainerList");
        P.TaskManager.Enqueue(() => !IsOccupied(), "WaitUntilNotOccupiedAfterBell");
        SetPhase(Phase.CloseBellWait);
    }

    private static void TickCloseBellWait()
    {
        if(!ChainFinished) return;
        if(IsOccupied())
        {
            Stop(Loc.T("Stopped: could not leave the summoning bell."));
            return;
        }
        SetPhase(Phase.Handin);
    }

    #endregion

    #region 繳交

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
                Stop(Loc.T("Stopped: the inventory is down to the reserve but holds nothing that can be delivered."));
                return;
            }
            Stop(Loc.T("Finished: the retainers have no more gear to deliver."), success: true);
            return;
        }

        PluginLog.Information($"[ExpertDeliveryLoop] Starting handin round {HandinRounds + 1} with {RetrievedThisRound} item(s) retrieved this round.");
        if(HasGCTarget)
        {
            // 使用者指定了繳交點:自己導航過去,然後只接繳交那一段。
            // 走內建流程的話它會再送一次自己的移動指令,等於導航兩次。
            if(!GoToFavorite(C.ExpertDeliveryLoopGCFavoriteId, C.ExpertDeliveryLoopGCFavoriteSub, "Grand Company"))
            {
                Stop(Loc.T("Stopped: could not travel to the chosen Grand Company destination - check that it is still starred in Lifestream."));
                return;
            }
            P.TaskManager.Enqueue(() => !S.LifestreamIPC.IsBusy(), "WaitLifestreamBeforeHandin", new(timeLimitMS: 5 * 60 * 1000));
            P.TaskManager.Enqueue(() => GCContinuation.EnqueueInitiation(true), "EnqueueInitiation");
        }
        else
        {
            TaskDeliverItems.Enqueue();
        }
        // 這一段一定含移動,整段都算導航窗。
        NavigationDeadline = Environment.TickCount64 + NavigationGraceMs;
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
