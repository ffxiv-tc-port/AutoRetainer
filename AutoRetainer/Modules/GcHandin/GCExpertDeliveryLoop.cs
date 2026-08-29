using AutoRetainer.Internal;
using AutoRetainer.Internal.InventoryManagement;
using AutoRetainer.Scheduler.Handlers;
using AutoRetainer.Scheduler.Tasks;
using AutoRetainerAPI.Configuration;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons.ExcelServices;
using ECommons.GameHelpers;
using ECommons.IPC;
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

    /// <summary>遊戲一直不肯讓道具被使用時,等這麼久就放棄。</summary>
    private const long SealUsableWaitMs = 5000;

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
    private const long RetainerListLoadTimeoutMs = 30000;

    /// <summary>清單還沒載入時,隔這麼久重送一次開鈴互動。</summary>
    private const long BellInteractRetryMs = 5000;

    /// <summary>開鈴互動最多送幾次(含第一次)。上限的用途是不要退化成每五秒無限重試。</summary>
    private const int MaxBellInteractAttempts = 4;

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

    /// <summary>換角色期間,每隔這麼久把當下的狀態寫一行進 log。
    /// 🔴 換角色是這條流程裡唯一一段「什麼都看不到」的時間(登出→標題→選角→登入→場景載入),
    /// 卡住的時候沒有這些行就完全分不出卡在哪一段 —— 而每一段要修的東西都不一樣。</summary>
    private const long RelogHeartbeatMs = 15000;

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
        FinishReturnToBell,
        FinishReturnToBellWait,
        /// <summary>多角色連跑:送出換到下一個角色。</summary>
        Relog,
        /// <summary>多角色連跑:等新角色登入並安定。</summary>
        RelogWait,
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

    /// <summary>正常完成的說法,等回到鈴邊之後才真的講出來。</summary>
    private static string PendingFinishReason = "";
    /// <summary>回鈴收尾只嘗試一次。失敗就照樣收工 —— 工作本來就做完了,停不到定位不該
    /// 把一次成功講成失敗。</summary>
    private static bool FinishReturnAttempted;

    /// <summary>這一次開鈴已經送出幾次互動,以及最後一次是什麼時候。
    /// 🔴 互動本來只送一次:剛導航到位那一幀角色可能還在位移、目標還沒鎖到鈴、距離差一點,
    /// 那一發就白費了,而清單永遠不會出現 —— 流程只能乾等到逾時。</summary>
    private static int BellInteractAttempts;
    private static long LastBellInteractAt;

    /// <summary>導航窗的結束時刻。送出任何會讓角色移動/換區的東西時往後推。</summary>
    private static long NavigationDeadline;

    private static List<string> Retainers = [];
    private static int RetainerIndex;
    private static bool TravelledToBellThisRound;

    /// <summary>這一趟要跑的角色(CID),依 UI 上看到的順序。單角色跑法時是空的。
    /// 名單在**開始時**決定一次並沿用整趟 —— 跑到一半才加進來的角色不會被納入。</summary>
    private static List<ulong> BatchCIDs = [];
    private static int BatchIndex;

    /// <summary>正在等待登入的角色。0 ＝ 現在不在換角色。</summary>
    private static ulong RelogTargetCID;
    private static long RelogHeartbeatAt;

    /// <summary>換角色因為 <c>IPC.Suppressed</c> 而暫停的起始時刻。0 ＝ 現在沒有在暫停。
    /// <para>🔴 暫停期間**時鐘也要停**:換角逾時是拿 <see cref="PhaseEnteredAt"/> 比對現在時間算出來的,
    /// 被別的外掛抑制十分鐘會直接表現成一次假的「換角逾時」失敗 —— 而那個失敗訊息會指向完全無關的
    /// 成因(登入畫面/背包讀不到),把人帶去查錯的地方。恢復時把計時起點往後推暫停的長度。</para></summary>
    private static long RelogSuppressedSince;

    /// <summary>這一趟是不是多角色連跑。決定收工要不要響音、以及一個角色做完之後要換人還是收工。</summary>
    internal static bool MultiCharacterRun { get; private set; }

    /// <summary>已經完整跑完的角色數。</summary>
    internal static int CharactersDone { get; private set; }

    /// <summary>多角色連跑時的「第幾個/共幾個」。單角色跑法回 (0, 0)。</summary>
    internal static (int Current, int Total) BatchProgress => MultiCharacterRun ? (Math.Min(BatchIndex + 1, BatchCIDs.Count), BatchCIDs.Count) : (0, 0);

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
        Phase.FinishReturnToBell or Phase.FinishReturnToBellWait => Loc.T("Returning to the summoning bell"),
        // 暫停中是「原地不動而且完全正常」的狀態 —— 不講出來的話,列上看到的就只是一個永遠不動的
        // 「正在換角色」,與真的卡死一模一樣。這是要隨時掃視的資訊,所以放列上而不是 tooltip。
        Phase.Relog or Phase.RelogWait => RelogSuppressedSince != 0
            ? Loc.T("Switching character (paused: another plugin has suppressed AutoRetainer)")
            : Loc.T("Switching character"),
        _ => "?",
    };

    #region 每角色設定

    /// <summary>這個角色的覆寫設定。<paramref name="create"/> 為 false 時查不到就回 null ——
    /// ⚠️ 讀取路徑一律傳 false:UI 每幀都會問,傳 true 會替每一個被看過的角色留下一筆空設定。</summary>
    internal static ExpertDeliveryLoopCharacterConfig GetCharacterConfig(ulong cid, bool create)
    {
        if(C.ExpertDeliveryLoopPerCharacter.TryGetValue(cid, out var conf)) return conf;
        if(!create) return null;
        conf = new();
        C.ExpertDeliveryLoopPerCharacter[cid] = conf;
        return conf;
    }

    /// <summary>這個角色目前生效的手動僱員名單。沒有自己的名單就沿用第一版那份跨角色共用的清單。</summary>
    internal static List<string> GetRetainerNames(ulong cid)
        => GetCharacterConfig(cid, false)?.RetainerNames ?? C.ExpertDeliveryLoopRetainerNames;

    /// <summary>取得(必要時建立)這個角色**自己的**名單,給 UI 寫入用。
    /// <para>🔴 第一次落地時要從**目前生效的那份**複製過來,不是開一份空的 —— 舊設定是跨角色共用的,
    /// 開空的會讓使用者按下第一個勾選的瞬間覺得原本的勾選全被清掉了。</para>
    /// <para>⚠️ 只複製「這個角色真的有的僱員」:全域清單裡屬於別的角色的名字對這個角色沒有意義,
    /// 帶過來只會變成看不見(UI 只列這個角色的僱員)又永遠留著的垃圾。讀不到角色資料時才整份複製,
    /// 那種情況下寧可多帶也不要漏掉。</para></summary>
    internal static List<string> GetOwnRetainerNames(ulong cid)
    {
        var conf = GetCharacterConfig(cid, true);
        if(conf.RetainerNames == null)
        {
            var data = C.OfflineData.FirstOrDefault(x => x.CID == cid);
            conf.RetainerNames = data == null
                ? [.. C.ExpertDeliveryLoopRetainerNames]
                : [.. C.ExpertDeliveryLoopRetainerNames.Where(n => data.RetainerData.Any(r => r.Name.ToString() == n))];
        }
        return conf.RetainerNames;
    }

    /// <summary>這個角色的傳喚鈴目的地。沒設過就用全域那個。</summary>
    internal static (uint Id, byte Sub, string Name) GetBellDestination(ulong cid)
    {
        var conf = GetCharacterConfig(cid, false);
        if(conf != null && conf.BellFavoriteId != 0) return (conf.BellFavoriteId, conf.BellFavoriteSub, conf.BellFavoriteName);
        return (C.ExpertDeliveryLoopBellFavoriteId, C.ExpertDeliveryLoopBellFavoriteSub, C.ExpertDeliveryLoopBellFavoriteName);
    }

    /// <summary>這個角色的繳交點目的地。沒設過就用全域那個。</summary>
    internal static (uint Id, byte Sub, string Name) GetGCDestination(ulong cid)
    {
        var conf = GetCharacterConfig(cid, false);
        if(conf != null && conf.GCFavoriteId != 0) return (conf.GCFavoriteId, conf.GCFavoriteSub, conf.GCFavoriteName);
        return (C.ExpertDeliveryLoopGCFavoriteId, C.ExpertDeliveryLoopGCFavoriteSub, C.ExpertDeliveryLoopGCFavoriteName);
    }

    #endregion

    #region 僱員名單

    /// <summary>循環要處理的僱員。名單在**每個角色開始時**決定一次並沿用到那個角色跑完。
    /// ⚠️ 一律用 AutoRetainer 自己存的角色資料,不碰遊戲的僱員管理器 ——
    /// 那個東西在這次登入還沒開過傳喚鈴之前是空的。而且離線資料連**沒有登入的角色**都查得到,
    /// 多角色連跑就是靠這一點在開跑前把每個角色的設定驗完。</summary>
    internal static List<string> ResolveRetainers(OfflineCharacterData data)
    {
        var result = new List<string>();
        if(data == null) return result;

        if(C.ExpertDeliveryLoopManualRetainers)
        {
            var names = GetRetainerNames(data.CID);
            foreach(var retainer in data.RetainerData)
            {
                var name = retainer.Name.ToString();
                if(name.IsNullOrEmpty()) continue;
                if(names.Contains(name)) result.Add(name);
            }
            return result;
        }

        // 🔴 沒選計畫時回空清單而不是「全部」。「還沒設定」與「要對每個僱員做」是兩回事。
        if(C.ExpertDeliveryLoopEntrustPlan == Guid.Empty) return result;

        foreach(var retainer in data.RetainerData)
        {
            var name = retainer.Name.ToString();
            if(name.IsNullOrEmpty()) continue;
            // ⚠️ 這裡刻意用不建立條目的版本。Utils.GetAdditionalData 會把查不到的鍵補上一筆空資料,
            //    而這個查詢現在會掃過**每一個角色的每一個僱員**(UI 每幀、開跑前驗證各一次)。
            //    語意完全相同:新建的條目 EntrustPlan 是 Guid.Empty,而上面那道閘門已經排除了
            //    「選的計畫是 Guid.Empty」,所以空條目永遠不可能命中。
            var key = Utils.GetAdditionalDataKey(data.CID, name, false);
            if(C.AdditionalData.TryGetValue(key, out var additional) && additional.EntrustPlan == C.ExpertDeliveryLoopEntrustPlan) result.Add(name);
        }
        return result;
    }

    /// <summary>目前登入這個角色的僱員名單。</summary>
    internal static List<string> ResolveRetainers() => ResolveRetainers(Utils.GetCurrentCharacterData());

    #endregion

    #region 開始/停止

    internal static void Start()
    {
        if(Running) return;

        // 🔴 多角色模式跟這條循環是互斥的,而且**單角跑一樣會被踩**:多角色模式看到「這個角色沒事做」
        //    就自己把角色登出換掉,時機與這條流程完全無關 —— 循環會被丟在另一個角色上繼續送僱員互動,
        //    而那個角色的僱員名單根本不是它要找的。多角色連跑更兇:兩套東西同時在換角色。
        //    ⚠️ 這道守衛以前只存在於多角色連跑的前置檢查(TryBuildBatch)裡,**單角路徑完全沒擋到**。
        //    ⚠️ 看的是 Enabled 不是 Active:被別的外掛抑制中的多角色模式隨時會恢復,不算安全狀態。
        if(MultiMode.Enabled)
        {
            PluginLog.Information($"[ExpertDeliveryLoop] Refusing to start because Multi Mode is enabled: multiCharacterRun={C.ExpertDeliveryLoopMultiCharacter} multiModeEnabled={MultiMode.Enabled} multiModeActive={MultiMode.Active} suppressed={IPC.Suppressed} multiModeType={C.MultiModeType} nightMode={C.NightMode}. Multi Mode logs itself out and switches characters on its own schedule, which would strand this loop on a different character.");
            Fail(Loc.T("Multi Mode is on. Turn it off before starting this loop - Multi Mode logs out and switches characters by itself, so it would either fight this run's own character switching or leave the loop stranded on another character."));
            return;
        }

        if(!Player.Available)
        {
            Fail(Loc.T("Player is not available."));
            return;
        }
        if(Utils.IsBusy)
        {
            Fail(Loc.T("AutoRetainer or Lifestream is busy."));
            return;
        }

        List<ulong> batch = null;
        if(C.ExpertDeliveryLoopMultiCharacter)
        {
            if(!TryBuildBatch(out batch, out var batchError))
            {
                Fail(batchError);
                return;
            }
        }

        // 整批的第一個角色就是現在登入的這個時,不必先換角 —— 直接驗這個角色的前置條件。
        // 不是的話這一趟的第一個動作就是換角色,現在這個角色的設定與它無關,不該擋著開始。
        var startHere = batch == null || batch[0] == Player.CID;
        if(startHere && !TryBeginCharacter(out var error))
        {
            Fail(error);
            return;
        }

        Running = true;
        MultiCharacterRun = batch != null;
        BatchCIDs = batch ?? [];
        BatchIndex = 0;
        CharactersDone = 0;
        RelogTargetCID = 0;
        RelogSuppressedSince = 0;
        RetrievedTotal = 0;
        HandinRounds = 0;
        StatusText = "";

        if(startHere)
        {
            SetPhase(Phase.SealBonus);
            PluginLog.Information($"[ExpertDeliveryLoop] Started{(MultiCharacterRun ? $" (multi-character: {BatchCIDs.Count} character(s))" : "")}. Retainers to visit: {Retainers.Print()}. Reserved slots: {EffectiveReservedSlots} (config {C.ExpertDeliveryLoopReservedSlots}, MultiMinInventorySlots {C.MultiMinInventorySlots}).");
        }
        else
        {
            SetPhase(Phase.Relog);
            PluginLog.Information($"[ExpertDeliveryLoop] Started (multi-character: {BatchCIDs.Count} character(s)). The logged-in character (CID {Player.CID}) is not on the list, so the first thing this run does is switch to CID {BatchCIDs[0]}.");
        }
    }

    /// <summary>驗完整批角色的前置條件,並排出拜訪順序。
    /// <para>🔴 這些條件全部讀得到離線資料,**不必登入那個角色就驗得到**。少了這一步,設定漏掉的角色
    /// 要等到換過去、跑到一半才會發現 —— 而那時候已經過了十幾分鐘,還占用了一次換角色。</para>
    /// <para>回 false 時 <paramref name="error"/> 直接是給使用者看的完整說法(含是哪幾個角色、缺什麼)。</para></summary>
    private static bool TryBuildBatch(out List<ulong> batch, out string error)
    {
        batch = null;
        error = "";

        // 🔴 兩套換角色的東西同時在跑一定會打架:多開排程看到「這個角色沒事做」就會把人換走,
        //    而它換走的時機與這條流程完全無關。這不是保守,是實際會互相踩。
        // 📌 Start() 現在在更前面就擋掉了(單角跑也會被踩,那條路徑走不到這裡)。這一道刻意留著:
        //    這個函式驗的是「整批角色跑不跑得起來」,不該假設呼叫端一定先擋過。
        if(MultiMode.Enabled)
        {
            error = Loc.T(SharedText.MultiModeBlocksMultiCharacterRun);
            return false;
        }
        // 這個除錯選項會讓登出那一步直接把整條任務佇列砍掉,而且不留下任何訊息。
        if(C.DontLogout)
        {
            error = Loc.T(SharedText.DontLogoutBlocksCharacterSwitch);
            return false;
        }

        List<OfflineCharacterData> selected = [];
        List<string> problems = [];
        foreach(var cid in C.ExpertDeliveryLoopCharacters)
        {
            if(!C.OfflineData.TryGetFirst(x => x.CID == cid, out var data))
            {
                problems.Add(string.Format(Loc.T("CID {0}: there is no saved data for this character any more."), cid));
                continue;
            }
            selected.Add(data);
        }
        // 順序照 UI 上看到的(離線資料本身的順序),不要照勾選的先後 —— 使用者無從得知後者。
        selected = [.. C.OfflineData.Where(selected.Contains)];

        if(selected.Count == 0 && problems.Count == 0)
        {
            error = Loc.T("No characters are selected for the multi-character run.");
            return false;
        }

        foreach(var data in selected)
        {
            var who = Censor.Character(data.Name, data.World);
            if(data.IsLockedOut())
            {
                problems.Add(string.Format(Loc.T("{0}: this character's region is locked out right now."), who));
            }
            else if(data.GCDeliveryType == GCDeliveryType.Disabled)
            {
                problems.Add(string.Format(Loc.T("{0}: expert delivery is disabled - set a delivery mode for this character first."), who));
            }
            else if(ResolveRetainers(data).Count == 0)
            {
                problems.Add(string.Format(Loc.T("{0}: no retainer matches the current selection."), who));
            }
        }

        if(problems.Count > 0)
        {
            // 🔴 一次列出全部,不要只講第一個 —— 一次修一個問題等於要使用者重按五次按鈕。
            error = Loc.T("Not starting: some of the selected characters are not ready.") + "\n" + string.Join("\n", problems);
            return false;
        }

        batch = [.. selected.Select(x => x.CID)];
        // 從現在登入的這個角色開始,省掉一次沒必要的換角色。
        var idx = batch.IndexOf(Player.CID);
        if(idx > 0) batch = [.. batch.Skip(idx), .. batch.Take(idx)];
        return true;
    }

    /// <summary>把狀態機重設成「這個角色從頭開始」,並驗這個角色跑不跑得動。
    /// <para>🔴 每一個屬於「上一個角色」的東西都要在這裡清掉:僱員名單、僱員索引、這一輪取了幾件、
    /// 有沒有已經傳送過、導航窗、回鈴收尾旗標、跳過與在途的道具。換角色之後位置、區域、僱員清單
    /// 全部是新的,沿用任何一項都會讓流程對著上一個角色的世界做決定。</para></summary>
    private static bool TryBeginCharacter(out string error)
    {
        error = "";
        if(GCContinuation.GetGCInfo() == null)
        {
            error = Loc.T("This character is not employed by a Grand Company.");
            return false;
        }
        if(!AutoGCHandin.IsEnabled())
        {
            error = Loc.T("Expert delivery is disabled for this character - set a delivery mode first.");
            return false;
        }

        var retainers = ResolveRetainers();
        if(retainers.Count == 0)
        {
            error = C.ExpertDeliveryLoopManualRetainers
                ? Loc.T("No retainers are selected.")
                : Loc.T("No retainer of this character carries the selected entrust plan.");
            return false;
        }

        Retainers = retainers;
        RetainerIndex = 0;
        RetrievedThisRound = 0;
        TravelledToBellThisRound = false;
        UnavailableSince = 0;
        NavigationDeadline = 0;
        PendingFinishReason = "";
        FinishReturnAttempted = false;
        RoundEndReason = RoundEnd.NoGearLeft;
        BellInteractAttempts = 0;
        LastBellInteractAt = 0;
        PassFired = 0;
        SkippedItems.Clear();
        DeferredItems.Clear();
        return true;
    }

    internal static void Stop(string reason, bool success = false)
    {
        if(!Running && CurrentPhase == Phase.Idle) return;
        Running = false;
        CurrentPhase = Phase.Idle;
        RelogSuppressedSince = 0;
        StatusText = reason;
        // 🔴 停止只停外層狀態機,已經排進 AutoRetainer/Lifestream 的任務鏈會自己跑完 ——
        //    中途硬中斷互動鏈的風險比讓它跑完高。但那會造成「說停了卻還在繳交」的困惑,
        //    所以當下如果還有東西在跑,訊息要明講。
        var stillBusy = Utils.IsBusy;
        var summary = $"{reason} ({string.Format(Loc.T("retrieved {0}, handin rounds {1}"), RetrievedTotal, HandinRounds)})";
        if(MultiCharacterRun) summary += " " + string.Format(Loc.T("Characters finished: {0}/{1}."), CharactersDone, BatchCIDs.Count);
        if(stillBusy) summary += " " + Loc.T("The loop has stopped, but work already queued in AutoRetainer will finish on its own.");
        if(success)
        {
            DuoLog.Information(summary);
            if(C.GCHandinNotify) Utils.TryNotify(summary);
            // 整趟跑完了請塔塔露念一句。掛在這裡而不是三條成功路徑上,是因為這是它們唯一的匯流點,
            // 而且本函式開頭的「已經停了就直接回」守衛保證同一趟只會走到這裡一次。
            // 🔴 失敗路徑(含使用者手動按停止)不出聲:響音代表「做完了,可以回來看了」。
            // 🔴 執行緒:TryPraise 只能在主執行緒呼叫。三條 success 路徑(FinishCharacter 的兩個、
            //    TickRelog 的空批回退)全都在 GCExpertDeliveryLoop.Tick() 鏈上,而它由 AutoRetainer.Tick
            //    呼叫,那個 Tick 掛在 Svc.Framework.Update 上。外部唯一的 Stop() 呼叫點(UI 的
            //    「使用者停止」按鈕)走的是 success=false,到不了這裡。
            TataruPraiseIPC.TryPraise(TataruPraiseIPC.CategoryExpertDelivery,
                MultiCharacterRun
                    ? $"稀有品繳交循環完成:多角色 {CharactersDone}/{BatchCIDs.Count}"
                    : "稀有品繳交循環完成:單角色");
        }
        else
        {
            DuoLog.Warning(summary);
            // 🔴 多角色連跑失敗時也出聲。這種跑法本來就是丟著讓它跑的 —— 沒響的話,一批五個角色
            //    死在第二個,使用者要在半小時後才發現,而且發現時已經分不出是哪一段出的事。
            //    ⚠️ 這裡刻意**不**沿用「完成時通知」那個開關:它預設是關的,而「整批做完了」與
            //    「批次死在半路」的重要程度完全不同。失敗有自己的開關,預設開。
            if(MultiCharacterRun && C.ExpertDeliveryLoopNotifyOnFailure) Utils.TryNotify(summary);
        }
        PluginLog.Information($"[ExpertDeliveryLoop] Stopped: {reason} | retrieved={RetrievedTotal} handinRounds={HandinRounds} success={success} stillBusy={stillBusy} multiCharacter={MultiCharacterRun} charactersDone={CharactersDone}/{BatchCIDs.Count}");
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

        // 🔴 反向守衛:循環已經跑起來之後才被打開多角色模式。Start() 擋得住「先開多角色模式再按開始」,
        //    擋不住這個,而後果一模一樣 —— 多角色模式會自己把角色登出換掉,循環被丟在別的角色上,
        //    它送出去的僱員互動全部落空,畫面上卻沒有任何東西解釋為什麼。
        //    ⚠️ 讓路的是循環,不是多角色模式:多角色模式本身完全不動(它有夜間模式、開機自動啟用、
        //       IPC 等好幾條自動啟用路徑,在那裡攔會改到與本問題無關的行為),而循環是使用者按一下
        //       就能再按一次的東西,停下來的代價低得多。
        //    📌 已經排進共用佇列的任務照 Stop() 的既有語意跑完;多角色模式的換角本來就會等佇列空。
        if(MultiMode.Enabled)
        {
            PluginLog.Information($"[ExpertDeliveryLoop] Stopping because Multi Mode was turned on while the loop was running: phase={CurrentPhase} multiCharacterRun={MultiCharacterRun} charactersDone={CharactersDone}/{BatchCIDs.Count} retrieved={RetrievedTotal} handinRounds={HandinRounds} multiModeActive={MultiMode.Active} suppressed={IPC.Suppressed}. Multi Mode logs itself out and switches characters on its own schedule, which would strand this loop on a different character.");
            Stop(Loc.T("Stopped: Multi Mode was turned on. It switches characters on its own, which would leave this loop running on the wrong character."));
            return;
        }

        // 🔴 換角色那一段玩家本來就不可用(登出→標題→選角→登入),而可用性守衛在登出之後會判成
        //    「沒有東西在進行而且持續不可用」,把自己停在標題畫面。這兩個階段有自己的逾時與診斷,
        //    走的是完全不同的判準,所以在守衛之前就先分流。
        if(CurrentPhase is Phase.Relog or Phase.RelogWait)
        {
            if(RelogSuppressionHolds()) return;
            if(CurrentPhase == Phase.Relog) TickRelog();
            else TickRelogWait();
            return;
        }

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
            case Phase.FinishReturnToBell: TickFinishReturnToBell(); break;
            case Phase.FinishReturnToBellWait: TickFinishReturnToBellWait(); break;
        }
    }

    #region 換角色

    private static long RelogTimeoutMs => C.ExpertDeliveryLoopRelogTimeoutMinutes * 60L * 1000L;

    /// <summary>別的外掛把 AutoRetainer 抑制住(<c>AutoRetainer.SetSuppressed</c>)時,換角色就原地持住。
    /// 回 true ＝ 這一幀什麼都不要做。
    /// <para>🔴 抑制的意思是「現在不要接手這個角色」,而換角色是這條流程裡侵入性最強的一步:
    /// 它會把角色登出。在抑制期間登出,等於把別的外掛(或使用者自己)正在做的事直接砍掉,
    /// 而且**不可逆** —— 所以這裡選擇持住不動,不是照跑也不是放棄整趟。</para>
    /// <para>🔴 暫停期間不計入換角逾時。逾時是拿階段起始時刻比對現在時間算的,不把暫停時間補回去的話
    /// 「被抑制十分鐘」會表現成一次假的逾時失敗,而逾時訊息還會指向一個完全無關的成因。</para>
    /// <para>⚠️ 只管換角色。繳交途中不受抑制節制是既有行為,這裡不動它。</para>
    /// <para>診斷只在**狀態翻轉**時各印一行(進入暫停、恢復),不是每幀 —— 這段可以持續好幾分鐘。</para></summary>
    private static bool RelogSuppressionHolds()
    {
        var now = Environment.TickCount64;
        if(IPC.Suppressed)
        {
            if(RelogSuppressedSince == 0)
            {
                RelogSuppressedSince = now;
                PluginLog.Information($"[ExpertDeliveryLoop] Character switch paused: another plugin has suppressed AutoRetainer (AutoRetainer.SetSuppressed). Holding in phase {CurrentPhase} with targetCID {RelogTargetCID}, {TimeInPhase}ms spent in this phase so far; the {RelogTimeoutMs}ms switch timeout does not advance while paused. Nothing is logged out until this is released.");
            }
            return true;
        }

        if(RelogSuppressedSince != 0)
        {
            var pausedFor = now - RelogSuppressedSince;
            RelogSuppressedSince = 0;
            PhaseEnteredAt += pausedFor;
            RelogHeartbeatAt += pausedFor;
            PluginLog.Information($"[ExpertDeliveryLoop] Character switch resumed: the suppression was released after {pausedFor}ms. Phase {CurrentPhase}, targetCID {RelogTargetCID}; the phase and heartbeat clocks were pushed forward by that amount, so {TimeInPhase}ms of the {RelogTimeoutMs}ms switch timeout has actually been used.");
        }
        return false;
    }

    private static void TickRelog()
    {
        // 上一個角色排進去的東西還在跑、或角色還被什麼佔用著,就先等 —— 登出會把排隊中的動作全部丟掉,
        // 而換角色的原語本身在被佔用時是直接拒絕的(那會被講成「換不過去」,實際上只是還沒放手)。
        // ⚠️ 這是等待點,所以要有逾時:沒有的話「有東西永遠不放手」會表現成流程無聲地停在這裡。
        if(Utils.IsBusy || IsOccupied())
        {
            if(TimeInPhase <= RelogTimeoutMs) return;
            PluginLog.Information($"[ExpertDeliveryLoop] Gave up waiting to start the character switch after {TimeInPhase}ms (limit {RelogTimeoutMs}ms): taskManagerBusy={P.TaskManager.IsBusy} lifestreamBusy={ECommonsIPC.Lifestream.IsBusy()} gcHandinOperation={AutoGCHandin.Operation} occupied={IsOccupied()}.");
            Stop(string.Format(Loc.T("Stopped: nothing let go of this character within {0} minutes, so it could not be switched."), C.ExpertDeliveryLoopRelogTimeoutMinutes));
            return;
        }

        if(BatchIndex >= BatchCIDs.Count)
        {
            // 走不到這裡:換角色只在「還有下一個」時才被排進來。真的走到了就當整批做完,
            // 不要靜默停在一個沒有處理函式的狀態。
            PluginLog.Information($"[ExpertDeliveryLoop] Relog phase entered with no character left (index {BatchIndex} of {BatchCIDs.Count}); treating the run as finished.");
            Stop(Loc.T("Finished: every selected character has been done."), success: true);
            return;
        }

        var cid = BatchCIDs[BatchIndex];
        if(!C.OfflineData.TryGetFirst(x => x.CID == cid, out var data))
        {
            Stop(string.Format(Loc.T(SharedText.StoppedNextCharacterDataGone), cid));
            return;
        }

        if(Player.Available && Player.CID == cid)
        {
            // 已經站在目標角色上了(整批的第一個就是現在登入的這個)。不必換,直接開跑。
            BeginCharacterOrStop(data);
            return;
        }

        if(!MultiMode.Relog(data, out var error, RelogReason.ExpertDeliveryLoop))
        {
            Stop(string.Format(Loc.T("Stopped: could not switch to {0} - {1}"), Censor.Character(data.Name, data.World), error));
            return;
        }

        RelogTargetCID = cid;
        RelogHeartbeatAt = Environment.TickCount64;
        PluginLog.Information($"[ExpertDeliveryLoop] Switching to character {BatchIndex + 1}/{BatchCIDs.Count}: {data.Name}@{data.World} (CID {cid}), from CID {Player.CID}. Waited {TimeInPhase}ms for the character to be free; switch timeout {RelogTimeoutMs}ms.");
        SetPhase(Phase.RelogWait);
    }

    /// <summary>等新角色登入並且真的可以做事。
    /// <para>🔴 「登入了」不等於「可以做事了」:剛登入時背包容器可能還讀不到(空格數會回 0,與背包
    /// 真的滿了完全同形),而登入後還有場景安定延遲。所以完成條件是四件事同時成立,不是只看有沒有登入。</para>
    /// <para>逾時的原因彼此互斥而且要修的東西各不相同,所以逾時訊息要指出是哪一種,並且把實際數值印出來。</para></summary>
    private static void TickRelogWait()
    {
        var now = Environment.TickCount64;
        var elapsed = TimeInPhase;

        var loggedIn = Svc.ClientState.IsLoggedIn;
        var currentCID = Player.CID;
        var onTarget = Player.Available && currentCID == RelogTargetCID;
        var busy = Utils.IsBusy;
        var readable = Utils.IsInventoryStateReadable();
        var occupied = IsOccupied();

        if(onTarget && !busy && readable && !occupied)
        {
            PluginLog.Information($"[ExpertDeliveryLoop] Character switch finished after {elapsed}ms: now on CID {currentCID} (target {RelogTargetCID}), taskManagerBusy={P.TaskManager.IsBusy} lifestreamBusy={ECommonsIPC.Lifestream.IsBusy()} inventoryReadable={readable} occupied={occupied}.");
            RelogTargetCID = 0;
            if(!C.OfflineData.TryGetFirst(x => x.CID == currentCID, out var data))
            {
                Stop(string.Format(Loc.T(SharedText.StoppedNextCharacterDataGone), currentCID));
                return;
            }
            BeginCharacterOrStop(data);
            return;
        }

        if(now - RelogHeartbeatAt > RelogHeartbeatMs)
        {
            RelogHeartbeatAt = now;
            PluginLog.Information($"[ExpertDeliveryLoop] Still switching characters after {elapsed}ms of {RelogTimeoutMs}ms: isLoggedIn={loggedIn} currentCID={currentCID} targetCID={RelogTargetCID} playerAvailable={Player.Available} taskManagerBusy={P.TaskManager.IsBusy} lifestreamBusy={ECommonsIPC.Lifestream.IsBusy()} gcHandinOperation={AutoGCHandin.Operation} inventoryReadable={readable} occupied={occupied}.");
        }

        if(elapsed <= RelogTimeoutMs) return;

        // 互斥的成因梯,由外而內:還沒登入 → 登入到別人 → 登入了但還在忙 → 不忙但讀不到背包 → 被佔用。
        string cause;
        if(!loggedIn) cause = Loc.T("it never got past the login screen");
        else if(currentCID != RelogTargetCID) cause = Loc.T("a different character is logged in");
        else if(busy) cause = Loc.T("AutoRetainer or Lifestream never stopped being busy");
        else if(!readable) cause = Loc.T("the inventory never became readable");
        else cause = Loc.T("the character stayed occupied");

        PluginLog.Information($"[ExpertDeliveryLoop] Character switch timed out after {elapsed}ms (limit {RelogTimeoutMs}ms): isLoggedIn={loggedIn} currentCID={currentCID} targetCID={RelogTargetCID} playerAvailable={Player.Available} taskManagerBusy={P.TaskManager.IsBusy} lifestreamBusy={ECommonsIPC.Lifestream.IsBusy()} gcHandinOperation={AutoGCHandin.Operation} inventoryReadable={readable} occupied={occupied}.");
        Stop(string.Format(Loc.T("Stopped: switching characters did not finish within {0} minutes - {1}."), C.ExpertDeliveryLoopRelogTimeoutMinutes, cause));
    }

    /// <summary>新角色就位了 —— 驗它的設定,不行就停。
    /// <para>🔴 刻意**不跳過**跑不了的角色:使用者勾了它就是要它跑,靜默跳過的結果是整批看起來成功了
    /// 但少做了一個角色,而且沒有任何地方講過。跑不了就當場說是哪一個角色、缺什麼。</para></summary>
    private static void BeginCharacterOrStop(OfflineCharacterData data)
    {
        if(!TryBeginCharacter(out var error))
        {
            Stop(string.Format(Loc.T("Stopped on {0}: {1}"), Censor.Character(data.Name, data.World), error));
            return;
        }
        var bell = GetBellDestination(data.CID);
        var gc = GetGCDestination(data.CID);
        PluginLog.Information($"[ExpertDeliveryLoop] Character {BatchIndex + 1}/{BatchCIDs.Count} {data.Name}@{data.World} (CID {data.CID}) starting. Retainers: {Retainers.Print()}. Bell favourite id={bell.Id} sub={bell.Sub} \"{bell.Name}\", GC favourite id={gc.Id} sub={gc.Sub} \"{gc.Name}\". Reserved slots: {EffectiveReservedSlots}.");
        SetPhase(Phase.SealBonus);
    }

    /// <summary>一個角色跑完了。多角色連跑就換下一個,否則整趟收工。
    /// <para>🔴 每個角色收工只在聊天視窗留一行,**不響音** —— 響音代表「整件事做完了,可以回來看了」,
    /// 每個角色都響會讓它失去這個意義。</para></summary>
    private static void FinishCharacter(string reason)
    {
        if(MultiCharacterRun)
        {
            CharactersDone++;
            BatchIndex++;
            if(BatchIndex < BatchCIDs.Count)
            {
                var line = string.Format(Loc.T("{0} finished ({1}/{2} characters), switching to the next one."),
                    Censor.Character(Player.Name, Player.HomeWorld), CharactersDone, BatchCIDs.Count);
                DuoLog.Information($"{reason} {line}");
                PluginLog.Information($"[ExpertDeliveryLoop] Character {CharactersDone}/{BatchCIDs.Count} done: {reason} | retrievedTotal={RetrievedTotal} handinRounds={HandinRounds}. Next CID {BatchCIDs[BatchIndex]}.");
                SetPhase(Phase.Relog);
                return;
            }
            Stop(string.Format(Loc.T("Finished: all {0} selected characters are done."), BatchCIDs.Count), success: true);
            return;
        }
        Stop(reason, success: true);
    }

    #endregion

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

        // 🔴 走「使用能力」而不是「背包右鍵使用」。後者(AgentInventoryContext.UseItem)需要道具欄
        //    開著才有效,關著時**靜默失敗** —— 單參數版與四參數版都試過,兩次都在道具欄關著時失敗。
        //    這裡照抄 Artisan 吃食物/藥水/工程手冊的路徑,那條每天在跑而且不需要任何視窗。
        //    ⚠️ extraParam 65535 對道具是必要的,不是可以省略的預設值。
        //    ⚠️ HQ 道具的能力 id 要 +1000000(數量查詢則是用原 id 加 isHq 旗標);軍票預支單沒有
        //       HQ 版本,所以這裡直接用原 id。
        var status = ActionManager.Instance()->GetActionStatus(ActionType.Item, SealAllowanceItemId);
        if(status != 0)
        {
            // 0 以外都是遊戲說「現在不能用」(戰鬥中、詠唱中、地圖限制…)。等一下再試,
            // 但不要無限等 —— 一直不能用就照設定決定要停還是不帶加成繼續。
            if(TimeInPhase <= SealUsableWaitMs) return;

            var blocked = string.Format(Loc.T("Stopped: the game will not allow the Priority Seal Allowance to be used right now (status {0})."), status);
            PluginLog.Information($"[ExpertDeliveryLoop] GetActionStatus(Item, {SealAllowanceItemId}) = {status} for {TimeInPhase}ms, giving up on the seal bonus.");
            if(C.ExpertDeliveryLoopStopWithoutSealBonus)
            {
                Stop(blocked);
                return;
            }
            DuoLog.Warning(blocked);
            SetPhase(Phase.EnsureBell);
            return;
        }

        if(!EzThrottler.Throttle("ExpertDeliveryLoopUseAllowance", 2000)) return;

        SealAllowanceCountAtUse = GetSealAllowanceCount();
        ActionManager.Instance()->UseAction(ActionType.Item, SealAllowanceItemId, extraParam: 65535);
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
    /// (實測被送到烏爾達哈),而流程接著會在錯的城市裡找鈴。停下來比亂傳好。
    /// <para>⚠️ 一律以**現在登入的角色**去查。收藏項的清單是 Lifestream 依當前角色的傳送面板建出來的,
    /// 上一個角色的目的地對這個角色可能根本不存在(自己的房屋、不同的大國防聯軍城市)。</para></summary>
    internal static bool HasBellTarget => GetBellDestination(Player.CID).Id != 0;

    internal static bool HasGCTarget => GetGCDestination(Player.CID).Id != 0;

    /// <summary>叫 Lifestream 走到某個收藏項。回 false 代表**什麼都沒排進去**,呼叫端要立刻停,
    /// 不要等一個永遠不會來的完成訊號。</summary>
    private static bool GoToFavorite(uint id, byte sub, string what)
    {
        if(id == 0) return false;
        bool ok;
        try
        {
            ok = S.LifestreamExtra.TeleportToFavorite(id, sub);
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

    /// <summary>目前構得到、離玩家最近的傳喚鈴。
    /// <para>📌 這裡不再有「指定座標挑鈴」那一套。移動一律走使用者選的 Lifestream 我的最愛,而最愛本來就是
    /// 使用者自己加星號的定點 —— Lifestream 把人放到那裡之後,當前區域裡最近的那個鈴就是他要的那個。
    /// 原本那套座標偏好只在「不用最愛」的情況下才會生效,而那條路徑已經整個移除。</para></summary>
    internal static IGameObject GetPreferredBell()
    {
        if(Player.Object is null) return null;

        IGameObject best = null;
        var bestScore = float.MaxValue;

        foreach(var x in Svc.Objects)
        {
            if(x.ObjectKind != ObjectKind.Housing && x.ObjectKind != ObjectKind.EventObj) continue;
            if(!x.Name.ToString().EqualsIgnoreCaseAny(Lang.BellName)) continue;
            if(!x.IsTargetable) continue;
            if(Vector3.Distance(x.Position, Player.Object.Position) >= Utils.GetValidInteractionDistance(x)) continue;

            var score = Vector3.Distance(x.Position, Player.Object.Position);
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

        // 🔴 沒指定目的地就沒有移動手段,而且這是刻意的:泛用移動指令的退路已經整個拿掉。
        //    那條退路會把人送到指令自己的預設地點(實測是烏爾達哈),流程接著在錯的城市裡找鈴 ——
        //    使用者看到的是「跑去一個沒要求的地方然後說找不到鈴」,比直接停下來難懂得多。
        //    停下來,並且把「該去哪裡設定」講清楚。
        if(!HasBellTarget)
        {
            PluginLog.Information($"[ExpertDeliveryLoop] No bell in reach and no bell destination configured (ExpertDeliveryLoopBellFavoriteId=0). Stopping - travel needs a Lifestream favourite.");
            Stop(Loc.T("Stopped: no summoning bell in reach. Choose a summoning bell destination in this flow's settings - the list comes from your Lifestream teleport panel favourites."));
            return;
        }

        if(TravelledToBellThisRound)
        {
            // 已經到過目的地卻還是沒有鈴 —— 再送一次只會得到同樣的結果。
            Stop(Loc.T("Stopped: arrived at the chosen destination but there is no summoning bell within reach. Pick a favourite that is closer to a bell."));
            return;
        }

        // 使用者指定了目的地:一律走它,而且**只走它**。
        var bell = GetBellDestination(Player.CID);
        if(!GoToFavorite(bell.Id, bell.Sub, "bell"))
        {
            Stop(Loc.T("Stopped: could not travel to the chosen summoning bell destination - check that it is still starred in Lifestream."));
            return;
        }
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

        // 剛結束導航的那幾幀角色可能還在位移或演出中,這時候送互動等於浪費一發。
        if(!Player.Interactable) return;

        PluginLog.Information($"[ExpertDeliveryLoop] Interacting with the summoning bell.");
        TaskInteractWithNearestBell.Enqueue();
        BellInteractAttempts = 1;
        LastBellInteractAt = Environment.TickCount64;
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

        // 互動放槍是沉默的:沒有任何回報說「這一發沒中」,只會表現成清單一直不出現。
        // 所以在等待期間有界地重送,每一次都講出來,才分得出「還在載入」與「根本沒開起來」。
        var now = Environment.TickCount64;
        if(!Utils.IsBusy && Player.Interactable
            && BellInteractAttempts < MaxBellInteractAttempts
            && now - LastBellInteractAt > BellInteractRetryMs)
        {
            BellInteractAttempts++;
            PluginLog.Information($"[ExpertDeliveryLoop] Retrying the summoning bell interaction (attempt {BellInteractAttempts}/{MaxBellInteractAttempts}) - the retainer list has still not loaded after {now - LastBellInteractAt}ms.");
            TaskInteractWithNearestBell.Enqueue();
            LastBellInteractAt = now;
            return;
        }

        if(TimeInPhase > RetainerListLoadTimeoutMs)
        {
            // ⚠️ 走到這裡只說明清單沒載入,**不代表任何一個雇員不存在**。
            //    這條路徑刻意不產生任何「已經不存在」的訊息 —— 那會把載入問題講成資料問題。
            PluginLog.Information($"[ExpertDeliveryLoop] Giving up: the retainer list did not load after {BellInteractAttempts} bell interaction(s) over {TimeInPhase}ms.");
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
            Stop(Loc.T(SharedText.StoppedRetainerStorageClosed));
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
            Stop(Loc.T(SharedText.StoppedRetainerStorageClosed));
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
            FinishSuccessfully(Loc.T("Finished: the retainers have no more gear to deliver."));
            return;
        }

        PluginLog.Information($"[ExpertDeliveryLoop] Starting handin round {HandinRounds + 1} with {RetrievedThisRound} item(s) retrieved this round.");
        if(HasGCTarget)
        {
            // 使用者指定了繳交點:自己導航過去,然後只接繳交那一段。
            // 走內建流程的話它會再送一次自己的移動指令,等於導航兩次。
            var gc = GetGCDestination(Player.CID);
            if(!GoToFavorite(gc.Id, gc.Sub, "Grand Company"))
            {
                Stop(Loc.T("Stopped: could not travel to the chosen Grand Company destination - check that it is still starred in Lifestream."));
                return;
            }
            P.TaskManager.Enqueue(() => !ECommonsIPC.Lifestream.IsBusy(), "WaitLifestreamBeforeHandin", new(timeLimitMS: 5 * 60 * 1000));
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

    /// <summary>正常完成的收尾。
    /// 🔴 只有**正常完成**才做回鈴停靠:錯誤停止與使用者手動停止一律原地不動 ——
    /// 出錯時多一段導航只會把現場弄得更難查。
    /// 停在鈴邊是為了跟外掛平常的停靠慣例一致,多角色連跑與日常僱員流程可以直接接手 ——
    /// 📌 多角色連跑更用得到這一點:登出前停在鈴邊,下次換回這個角色時就直接登入在鈴旁邊。</summary>
    private static void FinishSuccessfully(string reason)
    {
        if(GetPreferredBell() != null)
        {
            FinishCharacter(reason);
            return;
        }

        // 沒有可用的回程手段就照樣收工,不要卡在這裡。沒設目的地就是沒有回程手段。
        if(FinishReturnAttempted || !HasBellTarget)
        {
            FinishCharacter(reason);
            return;
        }

        PendingFinishReason = reason;
        SetPhase(Phase.FinishReturnToBell);
    }

    private static void TickFinishReturnToBell()
    {
        if(Utils.IsBusy) return;

        FinishReturnAttempted = true;

        // 📌 只有 HasBellTarget 為真才進得了這個階段(FinishSuccessfully 的閘門),所以這裡不必再判一次。
        var bell = GetBellDestination(Player.CID);
        if(!GoToFavorite(bell.Id, bell.Sub, "bell"))
        {
            FinishWithoutReturning();
            return;
        }
        SetPhase(Phase.FinishReturnToBellWait);
    }

    private static void TickFinishReturnToBellWait()
    {
        if(!ChainFinished) return;

        if(GetPreferredBell() != null)
        {
            PluginLog.Information($"[ExpertDeliveryLoop] Back at the summoning bell, this character is complete.");
            FinishCharacter(PendingFinishReason + " " + Loc.T("Back at the summoning bell."));
            return;
        }
        FinishWithoutReturning();
    }

    /// <summary>回不去鈴邊時照樣算完成 —— 東西已經全部繳完了,停不到定位不該被講成失敗。</summary>
    private static void FinishWithoutReturning()
    {
        PluginLog.Information($"[ExpertDeliveryLoop] This character is complete, but could not park at a summoning bell.");
        FinishCharacter(PendingFinishReason + " " + Loc.T("Could not return to a summoning bell."));
    }

    private static void TickHandinWait()
    {
        if(ChainFinished)
        {
            PluginLog.Information($"[ExpertDeliveryLoop] Handin round {HandinRounds} finished. Round end reason: {RoundEndReason}.");
            if(RoundEndReason == RoundEnd.NoGearLeft)
            {
                FinishSuccessfully(Loc.T("Finished: everything has been delivered."));
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
