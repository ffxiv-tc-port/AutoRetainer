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
/// 稀有品繳交循環:把指定存放計畫底下的僱員身上的裝備取出來,拿去大國防聯軍繳交,取完為止。
///
/// <para>🔴 零自動觸發。只有 <see cref="Start"/> 會讓它動起來,而 <see cref="Start"/> 只有 UI 上那顆按鈕
/// 會呼叫。沒有任何事件、排程或多角色流程會啟動它。</para>
///
/// <para>整條流程沒有一步是自己操作 addon 的:走到鈴前、選僱員、開道具管理、去大國防聯軍繳交,
/// 全部委給外掛本來就在跑的任務鏈。這裡只負責決定「下一步做什麼」以及「什麼時候該停下來」。</para>
/// </summary>
internal static unsafe partial class GCExpertDeliveryLoop
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
    /// 🔴 道具欄沒開的時候 UseItem 是**靜默無效**的:只驗「加成有沒有出現」會把「道具沒被使用」
    /// 誤報成「加成沒生效」,使用者完全看不出真正的原因。</summary>
    private const long SealItemConsumeWaitMs = 2000;

    /// <summary>送出軍票預支單前,角色要連續這麼久都不在 <c>IsOccupied()</c> 狀態。
    /// 🔴 登入事件會亮、熄、再亮:只看一瞬間的「不忙」會在兩段之間送出,道具被事件打斷而沒被吃掉。</summary>
    private const long SealFreeQuietMs = 1500;

    /// <summary>等角色不忙的上限;逾時照樣送(與舊行為相同),由重送與既有的「沒吃掉」分支收尾。</summary>
    private const long SealOccupiedWaitMs = 15000;

    /// <summary>軍票預支單沒被吃掉時最多重送幾次。</summary>
    private const int MaxSealUseRetries = 1;

    /// <summary>送出一個任務鏈之後,至少要等這麼久才准把「不忙」解讀成「做完了」。</summary>
    private const long EnqueueGraceMs = 500;

    /// <summary>開鈴之後等僱員清單真的載入的上限。
    /// 🔴 這段等待**不能**用「我們的任務佇列排空」當結論:排空只代表我們送出的動作做完了,
    /// 遊戲還要自己把視窗開起來、把僱員資料填進去。</summary>
    private const long RetainerListLoadTimeoutMs = 30000;

    /// <summary>清單還沒載入時,隔這麼久重送一次開鈴互動。</summary>
    private const long BellInteractRetryMs = 5000;

    /// <summary>開鈴互動最多送幾次(含第一次)。上限的用途是不要退化成每五秒無限重試。</summary>
    private const int MaxBellInteractAttempts = 4;

    /// <summary>為了找鈴最多送幾次傳送(含第一次);第一次是指軍團繳交完走去鈴邊那一發。</summary>
    private const int MaxBellTravelsPerRound = 3;

    /// <summary>兩次傳送之間至少隔這麼久。Lifestream 被遊戲拒絕(狀態 579)要 3 秒才會中止任務鏈,
    /// 馬上重送等於在同一個狀態裡再被拒一次。</summary>
    private const long BellResendGapMs = 10000;

    /// <summary>送傳送前等「不在 Occupied／對話／事件中」的上限。逾時就照樣送(行為與以前相同),由次數上限收尾。</summary>
    private const long BellTravelOccupiedWaitMs = 15000;

    /// <summary>取回指令之間的最小間隔。伺服器實測每格約 0.13 秒。</summary>
    private const int RetrieveIntervalMs = 150;

    /// <summary>一輪取回送完之後,等僱員格數安定的輪詢間隔與安靜門檻。
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
    /// 🔴 傳送詠唱、換區、載入畫面期間玩家本來就不可用 —— 把這種合法過渡態當成致命,
    /// 會在 Lifestream 導航途中把自己停掉。</summary>
    private const long UnavailableGraceMs = 30000;

    /// <summary>成功收工後等 AutoRetainer／Lifestream 閒下來開回多角色模式的上限。
    /// 成功路徑都在任務鏈排空後才收工，剩下的只有收尾；超過這麼久代表別的東西在驅動，再疊開多角色模式會和它搶。</summary>
    internal const long ResumeMultiModeTimeoutMs = 5 * 60 * 1000;

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
        /// <summary>角色收工前收取並重派已到期的探險（只有 <c>ExpertDeliveryLoopRefreshVentures</c> 開著才會進來）。</summary>
        VentureStart,
        VentureSelect,
        VentureSelectWait,
        VentureCloseBell,
        VentureCloseBellWait,
        /// <summary>多角色連跑:送出換到下一個角色。</summary>
        Relog,
        /// <summary>多角色連跑:等新角色登入並安定。</summary>
        RelogWait,
    }

    /// <summary>這一輪取回停下來的理由,決定繳交完之後要不要再跑一輪。</summary>
    private enum RoundEnd
    {
        /// <summary>清單上的僱員都看過了,沒有裝備可拿 —— 繳完就收工。</summary>
        NoGearLeft,
        /// <summary>背包到保留下限,先去繳交 —— 繳完還要回來繼續取。</summary>
        ReserveReached,
    }

    internal static bool Running { get; private set; }

    /// <summary>等著開回多角色模式的起始時刻；0＝沒有在等。只由 Stop(success: true) 設定，只在框架執行緒讀寫。</summary>
    private static long ResumeMultiModeSince;

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
    /// 🔴 互動不能只送一次:剛導航到位那一幀角色可能還在位移、目標還沒鎖到鈴、距離差一點,
    /// 那一發就白費了,而清單永遠不會出現。</summary>
    private static int BellInteractAttempts;
    private static long LastBellInteractAt;

    /// <summary>導航窗的結束時刻。送出任何會讓角色移動/換區的東西時往後推。</summary>
    private static long NavigationDeadline;

    /// <summary>已印過「抵達軍團」診斷的角色；0＝這一趟還沒印。只在 <see cref="Start"/> 歸零。</summary>
    private static ulong GcArrivalLoggedCID;

    private static List<string> Retainers = [];
    private static int RetainerIndex;
    /// <summary>這一輪為了找鈴送過幾次傳送。第一次之後還沒鈴就重送一次,第二次之後才停。</summary>
    private static int BellTravelsThisRound;

    /// <summary>上一次送出找鈴傳送的時刻;只在 <see cref="BellTravelsThisRound"/> 大於 0 時有意義。</summary>
    private static long LastBellTravelAt;

    /// <summary>開始等「不再 Occupied」的時刻;0＝沒在等。</summary>
    private static long BellTravelOccupiedSince;

    /// <summary>這一趟裡「傳送過去之後真的找到鈴」的鈴目的地(id, sub)。只在 <see cref="Start"/> 清空。
    /// 用來分辨兩種停止:這個目的地這趟已經成功過＝這次是傳送沒到,不是目的地選錯。</summary>
    private static readonly HashSet<(uint Id, byte Sub)> BellDestinationsProvenThisRun = [];

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
    /// 一件一件等的話,遇到伺服器默默拒收的格子就整整空轉 10 秒(追蹤過期時間)。</summary>
    private static readonly HashSet<uint> DeferredItems = [];

    private static int PassFired;
    private static long SettleLastChangeAt;
    private static int SettleLastCount;

    /// <summary>用掉軍票預支單那一刻的持有數,用來確認道具真的被消耗掉。</summary>
    private static int SealAllowanceCountAtUse;

    /// <summary>這一次送出前已經等到角色不忙。只由 <see cref="EnterSealBonus"/> 歸零。</summary>
    private static bool SealCharacterSettled;
    /// <summary>連續不忙的起始時刻;0＝現在忙或還沒看過。</summary>
    private static long SealFreeSince;
    private static bool SealSawOccupied;
    /// <summary>這個角色已重送幾次;只在角色開始時歸零。</summary>
    private static int SealUseRetries;

    /// <summary>開鈴成功（僱員清單就緒）之後進哪個階段。只由 <see cref="TickEnsureBell"/> 與 <see cref="TickVentureStart"/> 設定。</summary>
    private static Phase AfterBellOpen = Phase.SelectRetainer;

    /// <summary>探險收尾做完之後要交給 <see cref="FinishCharacter"/> 的說法。</summary>
    private static string PendingVentureFinishReason = "";
    /// <summary>這個角色要拜訪的到期僱員，在僱員清單第一次就緒時決定一次；null＝還沒決定。</summary>
    private static List<string> VentureQueue;
    private static int VentureIndex;
    private static string VentureAction = "";
    /// <summary>這一個僱員的任務鏈排空的時刻；0＝還沒排空。用來給僱員資料一點時間更新再判結果。</summary>
    private static long VentureChainDoneAt;
    private static int VenturesResent;
    private static int VenturesCollectedOnly;
    private static readonly List<string> VentureSkips = [];
    private static int VentureCharacters;
    private static int VentureSkippedTotal;
    private static int VenturesCollectedOnlyTotal;
    internal static int VenturesResentTotal { get; private set; }

    /// <summary>重派之後等僱員資料反映新探險的上限；只影響診斷的分類，不影響流程。</summary>
    private const long VentureOutcomeSettleMs = 3000;

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
        Phase.VentureStart or Phase.VentureSelect or Phase.VentureSelectWait => Loc.T("Collecting and resending ventures"),
        Phase.VentureCloseBell or Phase.VentureCloseBellWait => Loc.T("Closing retainer"),
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

    /// <summary>這個角色目前生效的手動僱員名單。沒有自己的名單就沿用跨角色共用的那份清單。</summary>
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
        MatchRetainers(data, result);
        return result;
    }

    /// <summary>＝<c>ResolveRetainers(data).Count</c>，但不建清單（設定頁的角色表每幀每列問一次）。</summary>
    internal static int CountRetainers(OfflineCharacterData data) => MatchRetainers(data, null);

    /// <summary>回傳符合的僱員數；<paramref name="result"/> 不是 null 時依序加入名字。</summary>
    private static int MatchRetainers(OfflineCharacterData data, List<string> result)
    {
        if(data == null) return 0;
        var count = 0;

        if(C.ExpertDeliveryLoopManualRetainers)
        {
            var names = GetRetainerNames(data.CID);
            foreach(var retainer in data.RetainerData)
            {
                var name = retainer.Name.ToString();
                if(name.IsNullOrEmpty()) continue;
                if(names.Contains(name))
                {
                    count++;
                    result?.Add(name);
                }
            }
            return count;
        }

        // 🔴 沒選計畫時回空清單而不是「全部」。「還沒設定」與「要對每個僱員做」是兩回事。
        if(C.ExpertDeliveryLoopEntrustPlan == Guid.Empty) return 0;

        foreach(var retainer in data.RetainerData)
        {
            var name = retainer.Name.ToString();
            if(name.IsNullOrEmpty()) continue;
            // ⚠️ 這裡刻意用不建立條目的版本。Utils.GetAdditionalData 會把查不到的鍵補上一筆空資料,
            //    而這個查詢會掃過**每一個角色的每一個僱員**(UI 每幀、開跑前驗證各一次)。
            //    語意完全相同:新建的條目 EntrustPlan 是 Guid.Empty,而上面那道閘門已經排除了
            //    「選的計畫是 Guid.Empty」,所以空條目永遠不可能命中。
            var key = GetAdditionalDataKey(data.CID, name);
            if(C.AdditionalData.TryGetValue(key, out var additional) && additional.EntrustPlan == C.ExpertDeliveryLoopEntrustPlan)
            {
                count++;
                result?.Add(name);
            }
        }
        return count;
    }

    // 鍵字串只由（CID、名字）決定，組一次就留著（＝Utils.GetAdditionalDataKey(cid, name, false)）。只在主執行緒用。
    private static readonly Dictionary<(ulong CID, string Name), string> AdditionalDataKeys = [];

    private static string GetAdditionalDataKey(ulong cid, string name)
    {
        if(!AdditionalDataKeys.TryGetValue((cid, name), out var key))
        {
            key = Utils.GetAdditionalDataKey(cid, name, false);
            AdditionalDataKeys[(cid, name)] = key;
        }
        return key;
    }

    /// <summary>目前登入這個角色的僱員名單。</summary>
    internal static List<string> ResolveRetainers() => ResolveRetainers(Utils.GetCurrentCharacterData());

    #endregion

    internal static void Tick()
    {
        if(ResumeMultiModeSince != 0) TickResumeMultiMode();
        if(!Running) return;

        // 🔴 反向守衛:循環已經跑起來之後才被打開多角色模式。Start() 擋不住這個,而後果一模一樣 ——
        //    多角色模式會自己把角色登出換掉,循環被丟在別的角色上,送出去的僱員互動全部落空。
        //    ⚠️ 讓路的是循環,不是多角色模式:多角色模式有好幾條自動啟用路徑(夜間模式、開機自動啟用、IPC),
        //       在那裡攔會改到無關的行為;循環是使用者按一下就能再按一次的東西,停下來的代價低得多。
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
            case Phase.VentureStart: TickVentureStart(); break;
            case Phase.VentureSelect: TickVentureSelect(); break;
            case Phase.VentureSelectWait: TickVentureSelectWait(); break;
            case Phase.VentureCloseBell: TickVentureCloseBell(); break;
            case Phase.VentureCloseBellWait: TickVentureCloseBellWait(); break;
        }
    }

    /// <summary>玩家可用性守衛。
    /// 🔴 「不可用」在這條流程裡**大多數時候是正常的**:傳送詠唱、跨區載入、城內乙太網換圖都會讓
    /// <c>Player.Available</c> 變成 false,而這條流程本來就會移動好幾次(使用者手動用城內水晶換圖也算)。
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
}
