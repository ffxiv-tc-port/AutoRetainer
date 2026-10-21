using AutoRetainer.Internal;
using AutoRetainer.Modules.Voyage;
using AutoRetainer.Modules.Voyage.Tasks;
using AutoRetainer.Scheduler.Tasks;
using AutoRetainer.Services;
using AutoRetainer.UI.MainWindow.MultiModeTab;
using AutoRetainerAPI.Configuration;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Config;
using Dalamud.Interface.ImGuiNotification;
using ECommons.CircularBuffers;
using ECommons.Events;
using ECommons.ExcelServices.TerritoryEnumeration;
using ECommons.EzSharedDataManager;
using ECommons.GameHelpers;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using static AutoRetainer.Modules.OfflineDataManager;

namespace AutoRetainer.Modules.Multi;

internal static unsafe class MultiMode
{
    internal static bool Active => Enabled && !IPC.Suppressed;

    /// <summary>多角色模式開關。寫入一律走 <see cref="SetEnabled"/>，每次開／關都會在 log 留一行（含來源）。</summary>
    internal static bool Enabled => enabled;
    private static bool enabled = false;
    private static readonly object EnabledLock = new();

    /// <summary>
    /// 改多角色模式開關的唯一入口。值真的改變時寫一行 Information；也會取消還沒執行的「重載後恢復」。
    /// </summary>
    /// <remarks>會從 IPC 呼叫端的執行緒進來（DisableAllFunctions），所以這裡只改欄位、寫 log，不碰原生層與任務佇列。</remarks>
    internal static void SetEnabled(bool value, string source, MultiModeChangeCause cause)
    {
        bool changed;
        lock(EnabledLock)
        {
            changed = enabled != value;
            enabled = value;
            restoreAfterReloadPending = false;
            if(changed) RecordChangeLocked(value, cause);
        }
        if(changed) PluginLog.Information($"[AutoRetainer] 多角色模式{(value ? "開啟" : "關閉")}（來源：{source}）");
    }

    private sealed record MultiModeChange(bool Enabled, MultiModeChangeCause Cause, long At);

    /// <summary>最後一次真的改變開關的紀錄；<c>null</c>＝載入後還沒變過。只在持有 <see cref="EnabledLock"/> 時寫，任何執行緒都可讀（整筆換參考）。</summary>
    private static volatile MultiModeChange lastChange;

    private static void RecordChangeLocked(bool value, MultiModeChangeCause cause) => lastChange = new(value, cause, Environment.TickCount64);

    /// <summary>最後一次開／關：變更後的值、<see cref="MultiModeChangeCause"/> 代碼、距今毫秒。載入後還沒變過＝（現值, 0, -1）。</summary>
    /// <remarks>任何執行緒都可呼叫：只讀一個 volatile 參考，不碰原生層與任務佇列。</remarks>
    internal static (bool Enabled, int Cause, long MillisecondsAgo) GetLastChange()
    {
        var c = lastChange;
        if(c == null) return (enabled, (int)MultiModeChangeCause.None, -1);
        return (c.Enabled, (int)c.Cause, Math.Max(0, Environment.TickCount64 - c.At));
    }

    private sealed record ManualRelog(MultiModeChangeCause Cause, long At);

    /// <summary>最後一次「手動那一組」（Overlay／Command／ConfigGUI）換角真的排進佇列的紀錄；<c>null</c>＝載入後還沒有過。整筆換參考，任何執行緒都可讀。</summary>
    private static volatile ManualRelog lastManualRelog;

    /// <summary>最後一次手動換角排入：距今毫秒、<see cref="MultiModeChangeCause"/> 代碼。載入後還沒有過＝（-1, 0）。</summary>
    /// <remarks>任何執行緒都可呼叫：只讀一個 volatile 參考，不碰原生層與任務佇列。</remarks>
    internal static (long MillisecondsAgo, int Cause) GetLastManualRelog()
    {
        var r = lastManualRelog;
        if(r == null) return (-1, (int)MultiModeChangeCause.None);
        return (Math.Max(0, Environment.TickCount64 - r.At), (int)r.Cause);
    }

    private static void RecordManualRelog(RelogReason reason, MultiModeChangeCause cause, OfflineCharacterData data)
    {
        if(!reason.EqualsAny(RelogReason.Overlay, RelogReason.Command, RelogReason.ConfigGUI)) return;
        lastManualRelog = new(cause, Environment.TickCount64);
        PluginLog.Information($"[AutoRetainer] 手動換角已排入（{reason}，來源：{cause}，目標：{(data == null ? "登出" : "換到另一個角色")}）");
    }

    // 行程級共享資料：卸載時多角色模式開著就記 true，同一個遊戲行程裡下一次載入讀一次就清回 false。
    // 值只能用 BCL 型別：新版插件在新的 AssemblyLoadContext，插件自己的型別轉不回來。
    private const string ReloadRecordKey = "AutoRetainer.MultiModeOnUnload";
    private static bool[] reloadRecord;
    private static bool restoreAfterReloadPending;

    // 另一筆行程級共享資料：卸載當下的僱員排程（SchedulerMain）。另開一個名字而不是改上面那筆的型別：
    // 舊版卸載時寫的是 bool[]，改型別會讓「從舊版更新」那一次連多角色模式都恢復不了。
    // 值：0＝沒有紀錄（舊版卸載時不寫）、1＝關、2＋(int)PluginEnableReason＝開。
    private const string SchedulerRecordKey = "AutoRetainer.RetainerSchedulerOnUnload";
    private const int SchedulerRecordNone = 0;
    private const int SchedulerRecordOff = 1;
    private const int SchedulerRecordOnBase = 2;
    private static int[] schedulerRecord;
    private static int schedulerStateAtUnload = SchedulerRecordNone;

    /// <summary>true＝待恢復，而且載入後角色一直坐在傳喚鈴前、重載前僱員排程不是關的 ⇒ 等僱員清單穩定就在鈴前接續，不等離開傳喚鈴。只在框架執行緒讀寫。</summary>
    private static bool resumeAtBell;
    private static long reloadRecordReadAt;
    private static int schedulerWritesAtLoad;

    // 鈴前接續只在「同一扇僱員清單連續 BellStableTicks 個 tick、且至少 BellStableMs 都可見＋就緒，其他僱員視窗一扇都沒開」時做。
    // 新實例的按下記號是空的，看不出舊實例最後按了哪扇窗；穩定這麼久＝不是關閉中的窗（tick 數取 DialogGuards 對按過的窗補按前要等的幀數）。
    // 等不到就在 BellResumeGiveUpMs 放棄，退回一般路徑（停在鈴前）。
    private const int BellStableTicks = DialogGuards.RePressEscapeFrames;
    private const long BellStableMs = 1000;
    private const long BellResumeGiveUpMs = 60000;
    /// <summary>觀察中的僱員清單位址：只拿來比對是不是同一扇，永不解參。</summary>
    private static nint bellListAddr;
    private static long bellListSince;
    private static int bellListTicks;

    // 第三筆行程級共享資料：卸載當下的航行排程（VoyageScheduler.Enabled）。值：0＝沒有紀錄（舊版卸載時不寫）、1＝關、2＝開。
    private const string VoyageRecordKey = "AutoRetainer.VoyageSchedulerOnUnload";
    private const int VoyageRecordNone = 0;
    private const int VoyageRecordOff = 1;
    private const int VoyageRecordOn = 2;
    private static int[] voyageRecord;
    private static int voyageStateAtUnload = VoyageRecordNone;

    /// <summary>true＝待恢復，而且載入時角色在航行管制視窗前、重載前航行排程不是關的 ⇒ 等它的選單穩定就地接續。只在框架執行緒讀寫。</summary>
    private static bool resumeAtPanel;

    // 面板前接續的門檻與鈴前相同：同一扇面板選單（SelectString）連續 PanelStableTicks 個 tick、至少 PanelStableMs 都可見＋就緒，
    // 而且整段期間 TextAdvance／YesAlready 都已被要求暫停，其他會被按的窗一扇都沒開；等不到就在 PanelResumeGiveUpMs 放棄。
    private const int PanelStableTicks = DialogGuards.RePressEscapeFrames;
    private const long PanelStableMs = 1000;
    private const long PanelResumeGiveUpMs = 60000;
    /// <summary>觀察中的面板選單位址：只拿來比對是不是同一扇，永不解參。</summary>
    private static nint panelMenuAddr;
    private static long panelMenuSince;
    private static int panelMenuTicks;

    /// <summary>
    /// 等待面板前接續的期間要求 TextAdvance／YesAlready 暫停（<see cref="TextAdvanceManager"/>、<see cref="NewYesAlreadyManager"/> 讀它）：
    /// 重載時 Dispose 已解鎖它們，而穩定觀察必須整段都在它們不會按的狀態下完成。
    /// </summary>
    internal static bool HoldsOtherAutomationForPanelResume => resumeAtPanel && restoreAfterReloadPending;

    /// <summary>在 Load 裡呼叫一次：讀取上一個實例留下的紀錄，只有更新／重載才排定恢復。</summary>
    internal static void ReadReloadRecord()
    {
        // 三筆都在這裡讀完就清，不管要不要恢復：沒清的話，下一次載入會讀到兩次重載以前的值。
        var schedulerAtUnload = ReadSchedulerRecord();
        var voyageAtUnload = ReadVoyageRecord();
        try
        {
            EzSharedData.TryGet(ReloadRecordKey, out reloadRecord, CreationMode.CreateAndKeep, [false]);
        }
        catch(Exception e)
        {
            reloadRecord = null;
            PluginLog.Information($"[AutoRetainer] 讀不到多角色模式的重載紀錄，這次不會自動恢復：{e.Message}");
            return;
        }
        if(reloadRecord == null || reloadRecord.Length == 0 || !reloadRecord[0]) return;
        reloadRecord[0] = false;
        var reason = Svc.PluginInterface.Reason;
        // 刻意只認更新與重載：從插件管理器停用再啟用（Installer）常是使用者自己要停下 AutoRetainer。
        if(reason is PluginLoadReason.Update or PluginLoadReason.Reload)
        {
            lock(EnabledLock) restoreAfterReloadPending = true;
            PluginLog.Information($"[AutoRetainer] 上一次卸載時多角色模式開著（這次載入原因：{reason}），等角色空閒時恢復。");
            schedulerStateAtUnload = schedulerAtUnload;
            voyageStateAtUnload = voyageAtUnload;
            reloadRecordReadAt = Environment.TickCount64;
            schedulerWritesAtLoad = System.Threading.Volatile.Read(ref SchedulerMain.EnabledWriteCount);
            if(Svc.Condition[ConditionFlag.OccupiedSummoningBell])
            {
                resumeAtBell = schedulerAtUnload != SchedulerRecordOff;
                PluginLog.Information(resumeAtBell
                    ? $"[AutoRetainer] 載入時角色在傳喚鈴前（重載前僱員排程：{DescribeSchedulerRecord(schedulerAtUnload)}），會在鈴前直接接續，不等離開傳喚鈴。"
                    : "[AutoRetainer] 載入時角色在傳喚鈴前，但重載前僱員排程是關的（手動暫停或手動開鈴），照舊等離開傳喚鈴後再恢復。");
            }
            else if(IsInVoyagePanelOnLoad())
            {
                PlanPanelResume();
            }
        }
        else
        {
            PluginLog.Information($"[AutoRetainer] 上一次卸載時多角色模式開著，但這次載入原因是 {reason}，不自動恢復。");
        }
    }

    /// <summary>讀出並清掉僱員排程紀錄；讀不到（型別不合）時當成沒有紀錄。</summary>
    private static int ReadSchedulerRecord()
    {
        try
        {
            EzSharedData.TryGet(SchedulerRecordKey, out schedulerRecord, CreationMode.CreateAndKeep, [SchedulerRecordNone]);
        }
        catch(Exception e)
        {
            schedulerRecord = null;
            PluginLog.Information($"[AutoRetainer] 讀不到僱員排程的重載紀錄，當成沒有紀錄：{e.Message}");
            return SchedulerRecordNone;
        }
        if(schedulerRecord == null || schedulerRecord.Length == 0) return SchedulerRecordNone;
        var value = schedulerRecord[0];
        schedulerRecord[0] = SchedulerRecordNone;
        return value;
    }

    private static string DescribeSchedulerRecord(int value)
    {
        if(value >= SchedulerRecordOnBase) return $"開，原因 {(PluginEnableReason)(value - SchedulerRecordOnBase)}";
        return value == SchedulerRecordOff ? "關" : "沒有紀錄（舊版卸載時不記）";
    }

    /// <summary>讀出並清掉航行排程紀錄；讀不到（型別不合）時當成沒有紀錄。</summary>
    private static int ReadVoyageRecord()
    {
        try
        {
            EzSharedData.TryGet(VoyageRecordKey, out voyageRecord, CreationMode.CreateAndKeep, [VoyageRecordNone]);
        }
        catch(Exception e)
        {
            voyageRecord = null;
            PluginLog.Information($"[AutoRetainer] 讀不到遠航探索排程的重載紀錄，當成沒有紀錄：{e.Message}");
            return VoyageRecordNone;
        }
        if(voyageRecord == null || voyageRecord.Length == 0) return VoyageRecordNone;
        var value = voyageRecord[0];
        voyageRecord[0] = VoyageRecordNone;
        return value;
    }

    // 在 Load 裡呼叫：這裡擲出的話 Load 後半段（IPC 預設包裝、服務初始化）都不會跑，所以接住後當成不在面板前。
    private static bool IsInVoyagePanelOnLoad()
    {
        try
        {
            return VoyageUtils.IsInVoyagePanel();
        }
        catch(Exception e)
        {
            PluginLog.Information($"[AutoRetainer] 判斷載入時是否在航行管制視窗前失敗，當成不在：{e.Message}");
            return false;
        }
    }

    private static string DescribeVoyageRecord(int value) => value switch
    {
        VoyageRecordOn => "開",
        VoyageRecordOff => "關",
        _ => "沒有紀錄（舊版卸載時不記）",
    };

    /// <summary>
    /// 載入時角色在航行管制視窗前（只在待恢復時有作用）：重載前航行排程不是關的 ⇒ 排定就地接續。
    /// 沒有紀錄（舊版卸載時不記）照「開」處理：多角色模式開著時會走到面板前的路徑（多角色模式自己去互動、進入面板時自動重新派遣）都會開航行排程。
    /// </summary>
    internal static void PlanPanelResume()
    {
        if(!restoreAfterReloadPending) return;
        resumeAtPanel = voyageStateAtUnload != VoyageRecordOff;
        ResetPanelObservation();
        PluginLog.Information(resumeAtPanel
            ? $"[AutoRetainer] 載入時角色在航行管制視窗前（重載前遠航探索排程：{DescribeVoyageRecord(voyageStateAtUnload)}），等它的選單穩定後就地接續，不等離開。"
            : "[AutoRetainer] 載入時角色在航行管制視窗前，但重載前遠航探索排程是關的，照舊等離開後再恢復。");
    }

    /// <summary>在 Dispose 最前面呼叫：把「現在開著、或恢復還沒執行」記進紀錄給下一個實例。</summary>
    internal static void WriteReloadRecord()
    {
        var scheduler = WriteSchedulerRecord();
        var voyage = WriteVoyageRecord();
        if(reloadRecord == null || reloadRecord.Length == 0) return;
        bool on;
        lock(EnabledLock) on = enabled || restoreAfterReloadPending;
        reloadRecord[0] = on;
        if(on) PluginLog.Information($"[AutoRetainer] 卸載時多角色模式開著，已記下（僱員排程：{DescribeSchedulerRecord(scheduler)}；遠航探索排程：{DescribeVoyageRecord(voyage)}）；同一個遊戲行程裡更新或重載後會自動恢復。");
    }

    /// <summary>記下卸載當下的航行排程；還在等面板前接續時，航行排程還沒被接回來，改記上一次帶過來的值。</summary>
    private static int WriteVoyageRecord()
    {
        int value;
        if(VoyageScheduler.Enabled)
        {
            value = VoyageRecordOn;
        }
        else
        {
            bool carry;
            lock(EnabledLock) carry = restoreAfterReloadPending && resumeAtPanel;
            value = carry ? voyageStateAtUnload : VoyageRecordOff;
        }
        if(voyageRecord != null && voyageRecord.Length > 0) voyageRecord[0] = value;
        return value;
    }

    /// <summary>記下卸載當下的僱員排程；還在等鈴前接續時，排程還沒被接回來，改記上一次帶過來的值。</summary>
    private static int WriteSchedulerRecord()
    {
        int value;
        if(SchedulerMain.PluginEnabledInternal)
        {
            value = SchedulerRecordOnBase + (int)SchedulerMain.Reason;
        }
        else
        {
            bool carry;
            lock(EnabledLock) carry = restoreAfterReloadPending && resumeAtBell;
            value = carry ? schedulerStateAtUnload : SchedulerRecordOff;
        }
        if(schedulerRecord != null && schedulerRecord.Length > 0) schedulerRecord[0] = value;
        return value;
    }

    /// <summary>
    /// 框架執行緒每幀呼叫。有待恢復時等到安全時機才開：任務佇列空著，且已登入時角色可操作
    /// （畫面就緒、不在交談／傳喚鈴／過場、不在戰鬥或副本裡）；在大廳不另外等，大廳裡的動作各自有就緒判斷。
    /// 例外是 <see cref="resumeAtBell"/>：角色從載入起一直坐在鈴前，等僱員清單穩定就在鈴前接續（見 <see cref="ObserveBellList"/>、<see cref="ApplyBellRestore"/>）；
    /// <see cref="resumeAtPanel"/> 同一個形狀，對象是航行管制視窗的選單（見 <see cref="ObservePanelMenu"/>、<see cref="ApplyPanelRestore"/>）。
    /// </summary>
    internal static void TickRestoreAfterReload()
    {
        if(!restoreAfterReloadPending) return;
        if(P.TaskManager.IsBusy)
        {
            ResetBellObservation();
            ResetPanelObservation();
            return;
        }
        var loggedIn = Svc.ClientState.IsLoggedIn;
        if(resumeAtBell)
        {
            if(!loggedIn || !Svc.Condition[ConditionFlag.OccupiedSummoningBell])
            {
                resumeAtBell = false;
                PluginLog.Information("[AutoRetainer] 等待鈴前接續時角色離開了傳喚鈴，改回一般的恢復時機（角色空閒時）。");
            }
            else if(BellResumeExpired(Environment.TickCount64))
            {
                GiveUpBellResume(DescribeBellWindows());
            }
            else
            {
                var list = IsScreenReady() && ProperOnLogin.PlayerPresent && !Svc.Condition[ConditionFlag.InCombat] && !Svc.Condition[ConditionFlag.BoundByDuty] ? ReadyRetainerList() : 0;
                var blocked = list != 0 && AnyBellBlockingWindow(IsAddonVisibleFn);
                if(!ObserveBellList(list, blocked, Environment.TickCount64)) return;
                if(ApplyBellRestore(DescribeBellWindows()))
                {
                    Notify.Info(Loc.T("Multi Mode was turned back on after AutoRetainer was reloaded."));
                }
                return;
            }
        }
        if(resumeAtPanel)
        {
            if(!loggedIn || !VoyageUtils.IsInVoyagePanel())
            {
                CancelPanelResume("[AutoRetainer] 等待就地接續時角色離開了航行管制視窗，改回一般的恢復時機（角色空閒時）。");
            }
            else if(VoyageScheduler.Enabled)
            {
                CancelPanelResume(PanelVoyageAlreadyRunningLine);
            }
            else if(PanelResumeExpired(Environment.TickCount64))
            {
                GiveUpPanelResume(DescribePanelWindows());
            }
            else
            {
                var menu = IsScreenReady() && ProperOnLogin.PlayerPresent && !IPC.Suppressed && !Svc.Condition[ConditionFlag.InCombat] && !Svc.Condition[ConditionFlag.BoundByDuty]
                    && !Svc.Condition[ConditionFlag.OccupiedInCutSceneEvent] && !Svc.Condition[ConditionFlag.WatchingCutscene78] ? ReadyVoyageMenu() : 0;
                var blocked = menu != 0 && (!TextAdvanceManager.IsLocked || !NewYesAlreadyManager.IsLocked || AnyPanelBlockingWindow(IsAddonVisibleFn));
                if(!ObservePanelMenu(menu, blocked, Environment.TickCount64)) return;
                if(ApplyPanelRestore(DescribePanelWindows()))
                {
                    Notify.Info(Loc.T("Multi Mode was turned back on after AutoRetainer was reloaded."));
                }
                return;
            }
        }
        if(loggedIn && (!IsScreenReady() || !ProperOnLogin.PlayerPresent || IsOccupied() || Svc.Condition[ConditionFlag.InCombat] || Svc.Condition[ConditionFlag.BoundByDuty])) return;
        // 在鎖內直接開：放出鎖再呼叫 SetEnabled 的話，同一瞬間從 IPC 來的關閉會被這裡蓋回開啟。
        lock(EnabledLock)
        {
            if(!restoreAfterReloadPending) return;
            restoreAfterReloadPending = false;
            if(enabled) return;
            enabled = true;
            RecordChangeLocked(true, MultiModeChangeCause.AutoRetainer);
        }
        PluginLog.Information($"[AutoRetainer] 重載前多角色模式開著，現在恢復（{(loggedIn ? "已登入，照介面勾選框的做法開啟" : "在大廳，照遊戲啟動自動開啟的做法開啟並從標題畫面登入")}）。");
        PluginLog.Information("[AutoRetainer] 多角色模式開啟（來源：重載後恢復）");
        if(loggedIn)
        {
            OnMultiModeEnabled();
        }
        else
        {
            BailoutManager.IsLogOnTitleEnabled = true;
        }
        Notify.Info(Loc.T("Multi Mode was turned back on after AutoRetainer was reloaded."));
    }

    /// <summary>
    /// 鈴前接續（只在僱員清單穩定開著時呼叫）：照僱員清單上「多角色模式」勾選框的做法開啟，並把僱員排程接回重載前的狀態
    /// （舊版沒有記下排程時照勾選框以 MultiMode 開啟）。之後排程從清單繼續下一名僱員，和每一輪僱員之間的狀態相同。
    /// 載入後有人寫過排程開關（使用者或指令）⇒ 不在鈴前接續，改回一般的恢復時機。
    /// </summary>
    /// <remarks>刻意不呼叫 <see cref="OnMultiModeEnabled"/>：它在這裡只剩「開啟時進屋」，角色已經在鈴前。</remarks>
    internal static bool ApplyBellRestore(string windows)
    {
        if(!restoreAfterReloadPending) return false;
        if(System.Threading.Volatile.Read(ref SchedulerMain.EnabledWriteCount) != schedulerWritesAtLoad)
        {
            resumeAtBell = false;
            PluginLog.Information($"[AutoRetainer] 等待鈴前接續時僱員排程被改過（現在：{(SchedulerMain.PluginEnabledInternal ? "開" : "關")}），不在鈴前接續，照舊等離開傳喚鈴後再恢復。");
            return false;
        }
        var recorded = schedulerStateAtUnload;
        var reason = PluginEnableReason.MultiMode;
        if(recorded >= SchedulerRecordOnBase && Enum.IsDefined(typeof(PluginEnableReason), recorded - SchedulerRecordOnBase))
        {
            reason = (PluginEnableReason)(recorded - SchedulerRecordOnBase);
        }
        // 開關與排程都在鎖內寫：IPC 急停（DisableAllFunctions）先經 SetEnabled 關多角色模式再關排程，這樣兩個都不會被這裡蓋回開啟。
        lock(EnabledLock)
        {
            if(!restoreAfterReloadPending) return false;
            restoreAfterReloadPending = false;
            resumeAtBell = false;
            if(enabled) return false;
            enabled = true;
            RecordChangeLocked(true, MultiModeChangeCause.AutoRetainer);
            SchedulerMain.EnablePluginQuiet(reason);
        }
        PluginLog.Information($"[AutoRetainer] 重載前多角色模式開著、角色一直在傳喚鈴前，現在在鈴前恢復並接續僱員處理（重載前僱員排程：{DescribeSchedulerRecord(recorded)}；現在的排程原因：{reason}；畫面上：{windows}）。");
        PluginLog.Information("[AutoRetainer] 多角色模式開啟（來源：重載後恢復，傳喚鈴前）");
        return true;
    }

    /// <summary>
    /// 餵一個 tick 的觀察，回傳僱員清單是否已經穩定（同一扇、連續 <see cref="BellStableTicks"/> 個 tick 且至少 <see cref="BellStableMs"/>）。
    /// <paramref name="listAddr"/> 為 0（清單不可見或沒就緒）或 <paramref name="blocked"/>（其他僱員視窗開著）就從頭算。
    /// </summary>
    internal static bool ObserveBellList(nint listAddr, bool blocked, long now)
    {
        if(listAddr == 0 || blocked)
        {
            ResetBellObservation();
            return false;
        }
        if(listAddr != bellListAddr)
        {
            bellListAddr = listAddr;
            bellListSince = now;
            bellListTicks = 0;
        }
        bellListTicks++;
        return bellListTicks >= BellStableTicks && now - bellListSince >= BellStableMs;
    }

    private static void ResetBellObservation()
    {
        bellListAddr = 0;
        bellListTicks = 0;
    }

    internal static bool BellResumeExpired(long now) => now - reloadRecordReadAt >= BellResumeGiveUpMs;

    /// <summary>等不到穩定的僱員清單：放棄鈴前接續，待恢復留給一般路徑（角色空閒時）。</summary>
    internal static void GiveUpBellResume(string windows)
    {
        resumeAtBell = false;
        ResetBellObservation();
        PluginLog.Information($"[AutoRetainer] 載入後 {BellResumeGiveUpMs / 1000} 秒內沒有等到穩定的僱員清單（畫面上：{windows}），放棄鈴前接續，照舊等離開傳喚鈴後再恢復。");
    }

    // 鈴前接續時這些視窗只要有一扇開著就不接續：它們都不是接手後第一個會按的窗；
    // 對話（Talk）在重載後可能正被沒上鎖的 TextAdvance 翻頁，接手會和它同時按。
    private static readonly string[] BellBlockingWindowNames = ["Talk", "SelectString", "SelectYesno", "ContextMenu", "RetainerTaskResult", "RetainerTaskAsk", "RetainerTaskList", "RetainerTaskSupply", "Bank", "InventoryRetainer", "InventoryRetainerLarge", "RetainerCrystalGrid", "RetainerSellList", "RetainerItemTransferList", "RetainerItemTransferProgress"];

    internal static bool AnyBellBlockingWindow(Func<string, bool> isOpen)
    {
        foreach(var name in BellBlockingWindowNames)
        {
            if(isOpen(name)) return true;
        }
        return false;
    }

    private static readonly Func<string, bool> IsAddonVisibleFn = IsAddonVisible;

    private static bool IsAddonVisible(string name) => TryGetAddonByName<AtkUnitBase>(name, out var addon) && addon->IsVisible;

    private static nint ReadyRetainerList() => TryGetAddonByName<AtkUnitBase>("RetainerList", out var list) && list->IsVisible && IsAddonReady(list) ? (nint)list : 0;

    /// <summary>只給鈴前接續相關的 log 用：列出這一刻畫面上開著的僱員相關視窗。</summary>
    private static string DescribeBellWindows()
    {
        var open = new List<string>();
        if(IsAddonVisible("RetainerList")) open.Add("RetainerList");
        foreach(var name in BellBlockingWindowNames)
        {
            if(IsAddonVisible(name)) open.Add(name);
        }
        return open.Count == 0 ? "（沒有）" : string.Join("、", open);
    }

    private const string PanelVoyageAlreadyRunningLine = "[AutoRetainer] 等待就地接續時遠航探索排程已經在運作（進入航行管制視窗時自動重新派遣，或手動勾選），不另外接續；多角色模式照舊等離開後再恢復。";

    /// <summary>
    /// 面板前接續（只在面板選單穩定開著時呼叫）：多角色模式與航行排程在鎖內一起開，之後由航行排程從這個選單繼續，
    /// 和多角色模式互動面板後的狀態相同。航行排程已經被別人開了 ⇒ 不接續，改回一般時機。
    /// </summary>
    /// <remarks>刻意不呼叫 <see cref="OnMultiModeEnabled"/>：理由同 <see cref="ApplyBellRestore"/>。</remarks>
    internal static bool ApplyPanelRestore(string windows)
    {
        if(!restoreAfterReloadPending) return false;
        if(VoyageScheduler.Enabled)
        {
            CancelPanelResume(PanelVoyageAlreadyRunningLine);
            return false;
        }
        // 理由同 ApplyBellRestore：IPC 急停先經 SetEnabled 關多角色模式再關航行排程，鎖內一起開才不會把急停蓋回開啟。
        lock(EnabledLock)
        {
            if(!restoreAfterReloadPending) return false;
            restoreAfterReloadPending = false;
            resumeAtPanel = false;
            if(enabled) return false;
            enabled = true;
            RecordChangeLocked(true, MultiModeChangeCause.AutoRetainer);
            VoyageScheduler.Enabled = true;
        }
        ResetPanelObservation();
        PluginLog.Information($"[AutoRetainer] 重載前多角色模式開著、角色一直在航行管制視窗前，現在就地恢復並接續遠航探索（重載前遠航探索排程：{DescribeVoyageRecord(voyageStateAtUnload)}；畫面上：{windows}）。");
        PluginLog.Information("[AutoRetainer] 多角色模式開啟（來源：重載後恢復，航行管制視窗前）");
        return true;
    }

    /// <summary>
    /// 餵一個 tick 的觀察，回傳面板選單是否已經穩定（同一扇、連續 <see cref="PanelStableTicks"/> 個 tick 且至少 <see cref="PanelStableMs"/>）。
    /// <paramref name="menuAddr"/> 為 0（不是面板選單、不可見或沒就緒）或 <paramref name="blocked"/> 就從頭算。
    /// </summary>
    internal static bool ObservePanelMenu(nint menuAddr, bool blocked, long now)
    {
        if(menuAddr == 0 || blocked)
        {
            ResetPanelObservation();
            return false;
        }
        if(menuAddr != panelMenuAddr)
        {
            panelMenuAddr = menuAddr;
            panelMenuSince = now;
            panelMenuTicks = 0;
        }
        panelMenuTicks++;
        return panelMenuTicks >= PanelStableTicks && now - panelMenuSince >= PanelStableMs;
    }

    private static void ResetPanelObservation()
    {
        panelMenuAddr = 0;
        panelMenuTicks = 0;
    }

    internal static bool PanelResumeExpired(long now) => now - reloadRecordReadAt >= PanelResumeGiveUpMs;

    /// <summary>等不到穩定的面板選單：放棄面板前接續，待恢復留給一般路徑（角色空閒時）。</summary>
    internal static void GiveUpPanelResume(string windows)
    {
        CancelPanelResume($"[AutoRetainer] 載入後 {PanelResumeGiveUpMs / 1000} 秒內沒有等到穩定的航行管制視窗選單（畫面上：{windows}），放棄就地接續，照舊等離開後再恢復。");
    }

    private static void CancelPanelResume(string line)
    {
        resumeAtPanel = false;
        ResetPanelObservation();
        PluginLog.Information(line);
    }

    // 面板前接續時這些視窗只要有一扇開著就不接續：航行排程重新開始時只從面板選單（種類、潛水艇、飛空艇清單）接手，
    // 其餘的窗有的會被 YesAlready／TextAdvance 或 AutoRetainer 自己的確認框處理按下，有的新實例根本不會去按。
    private static readonly string[] PanelBlockingWindowNames = ["Talk", "SelectYesno", "SelectIconString", "SelectOk", "ContextMenu", "ContextIconMenu", "AirShipExploration", "AirShipExplorationDetail", "AirShipExplorationResult", "CompanyCraftSupply", "AirShipPartsMenu"];

    internal static bool AnyPanelBlockingWindow(Func<string, bool> isOpen)
    {
        foreach(var name in PanelBlockingWindowNames)
        {
            if(isOpen(name)) return true;
        }
        return false;
    }

    /// <summary>航行排程重新開始時會從這扇接手的面板選單：可見＋就緒，而且是種類選擇、潛水艇清單或飛空艇清單；否則回 0。</summary>
    private static nint ReadyVoyageMenu()
    {
        if(!TryGetAddonByName<AtkUnitBase>("SelectString", out var menu) || !menu->IsVisible || !IsAddonReady(menu)) return 0;
        return VoyageUtils.GetCurrentWorkshopPanelType() is PanelType.TypeSelector or PanelType.Submersible or PanelType.Airship ? (nint)menu : 0;
    }

    /// <summary>只給面板前接續相關的 log 用：列出這一刻的面板選單種類與畫面上開著的相關視窗。</summary>
    private static string DescribePanelWindows()
    {
        var open = new List<string>();
        if(IsAddonVisible("SelectString")) open.Add($"SelectString（{VoyageUtils.GetCurrentWorkshopPanelType()}）");
        foreach(var name in PanelBlockingWindowNames)
        {
            if(IsAddonVisible(name)) open.Add(name);
        }
        if(!TextAdvanceManager.IsLocked || !NewYesAlreadyManager.IsLocked) open.Add("TextAdvance／YesAlready 尚未暫停");
        return open.Count == 0 ? "（沒有）" : string.Join("、", open);
    }

    internal static bool WaitOnLoginScreen => C.MultiWaitOnLoginScreen || BailoutManager.IsLogOnTitleEnabled || C.NightMode;

    // 決策區塊每幀會問好幾次；用 is 比對而不是 EqualsAny，才不會每次配置參數陣列、閉包與裝箱。
    // 僱員排程不可用時等同「只處理潛水艇」：不然每個角色都會被帶到鈴前、被拒絕、再帶一次。
    internal static bool EnabledRetainers => (C.MultiModeType is MultiModeType.Retainers or MultiModeType.Everything) && !VoyageUtils.IsRetainerBlockedByVoyage() && (!C.NightMode || C.NightModeRetainers) && SchedulerMain.RetainerPassAvailable;
    internal static bool EnabledSubmarines => (C.MultiModeType is MultiModeType.Submersibles or MultiModeType.Everything) && (!C.NightMode || C.NightModeDeployables);

    internal static bool Synchronize = false;
    internal static long NextInteractionAt { get; private set; } = 0;
    internal static ulong LastLogin = 0;
    internal static CircularBuffer<long> Interactions = new(5);

    internal static Dictionary<ulong, int> CharaCnt = [];
    internal static bool CanHET => Active && CanHETRaw;
    internal static bool CanHETRaw => ResidentalAreas.List.Contains(Svc.ClientState.TerritoryType) && (TaskNeoHET.GetFcOrPrivateEntranceFromMarkers() != null || TaskNeoHET.GetApartmentEntrance() != null) && (!C.NoTeleportHetWhenNextToBell || Utils.GetReachableRetainerBell(false) == null);

    internal static void Init()
    {
        ProperOnLogin.RegisterInteractable(delegate
        {
            if(Data != null)
            {
                C.LastLoggedInChara = Data.CID;
            }
            if(TaskChangeCharacter.Expected != null)
            {
                if(MultiMode.Enabled)
                {
                    if(TaskChangeCharacter.Expected.Value.Name != Player.Name || TaskChangeCharacter.Expected.Value.World != Player.HomeWorld)
                    {
                        DuoLog.Warning($"[ARERRCMM] Character mismatch, expected {TaskChangeCharacter.Expected}, but logged in on {Player.NameWithWorld}. Please report this to developer unless you have manually interfered with login process");
                    }
                }
                TaskChangeCharacter.Expected = null;
            }
            BailoutManager.IsLogOnTitleEnabled = false;
            WriteOfflineData(true, true);
            if(LastLogin == Svc.PlayerState.ContentId && Active)
            {
                DuoLog.Error("Multi mode disabled as it have detected duplicate login.");
                SetEnabled(false, "偵測到重複登入同一角色", MultiModeChangeCause.AutoRetainer);
            }
            LastLogin = MultiMode.Enabled && !C.MultiWaitOnLoginScreen ? Svc.PlayerState.ContentId : 0;
            Interactions.Clear();
            // 登出那側也清了一次；這裡是第二道，涵蓋「登出事件沒送到」與「外掛在已登入狀態下載入」。
            // 剛登入時不可能有進行中的僱員行程，所以無條件清空是安全的。
            SchedulerMain.ClearPendingEntrustVendorPass("login");
            if(CanHET)
            {
                DebugLog($"ProperOnLogin: {Svc.Objects.LocalPlayer}, residential area, scheduling HET");
                if(!TaskTeleportToProperty.ShouldVoidHET()) TaskNeoHET.Enqueue(null);
            }
            MultiModeUI.JustRelogged = true;
            if(!MultiMode.Enabled && C.HETWhenDisabled && CanHETRaw)
            {
                TaskNeoHET.Enqueue(null);
            }
        });
        if(ProperOnLogin.PlayerPresent)
        {
            WriteOfflineData(true, true);
            if(Data != null)
            {
                C.LastLoggedInChara = Data.CID;
            }
        }
    }

    internal static void BailoutNightMode()
    {
        if(!C.NightMode && !C.MultiWaitOnLoginScreen && C.EnableBailout)
        {
            BailoutManager.IsLogOnTitleEnabled = true;
        }
        if(C.NightMode)
        {
            SetEnabled(true, "開啟夜間模式", MultiModeChangeCause.User);
        }
    }

    internal static void OnMultiModeEnabled()
    {
        if(!Enabled)
        {
            return;
        }
        LastLogin = 0;
        if(!TaskTeleportToProperty.ShouldVoidHET())
        {
            if(C.MultiHETOnEnable && Player.Available && CanHET)
            {
                TaskNeoHET.Enqueue(null);
            }
        }
    }

    internal static void ValidateAutoAfkSettings()
    {
        {
            if(Svc.GameConfig.TryGet(SystemConfigOption.AutoAfkSwitchingTime, out uint val))
            {
                if(val != 0)
                {
                    Svc.GameConfig.Set(SystemConfigOption.AutoAfkSwitchingTime, 0u);
                    DuoLog.Warning($"Your Auto Afk Switching Time option was incompatible with current AutoRetainer configuration and was set to (Never). This is not an error.");
                }
            }
        }
        {
            if(Svc.GameConfig.TryGet(SystemConfigOption.IdlingCameraAFK, out uint val))
            {
                if(val != 0)
                {
                    Svc.GameConfig.Set(SystemConfigOption.IdlingCameraAFK, 0u);
                    DuoLog.Warning($"Your Idling Camera AFK option was incompatible with current AutoRetainer configuration and was set to (Disabled). This is not an error.");
                }
            }
        }
    }

    internal static void Tick()
    {
        if(Active)
        {
            ValidateAutoAfkSettings();
            // 必須在下面「任務佇列忙就返回」之前：它要看得到忙碌中的那些幀。
            MultiModeVentureDelivery.Tick();
            if(!Svc.ClientState.IsLoggedIn && TryGetAddonByName<AtkUnitBase>("Title", out _) && !P.TaskManager.IsBusy)
            {
                LastLogin = 0;
            }
            if(IsOccupied() || !IsScreenReady() || !ProperOnLogin.PlayerPresent)
            {
                BlockInteraction(1);
            }
            WatchRelogGate();
            if(P.TaskManager.IsBusy)
            {
                return;
            }
            if(MultiMode.WaitOnLoginScreen)
            {
                if(!Player.Available && Utils.CanAutoLogin())
                {
                    // 🔴 這是**寫入**：AgentLobby 取得器合法回 null，裸寫等於往位址 0 寫。
                    //    拿不到就跳過歸零（下一幀還會再來一次），不要在這裡丟例外。
                    var lobby = AgentLobby.Instance();
                    if(lobby != null) lobby->IdleTime = 0;
                    var next = GetCurrentTargetCharacter();
                    if(next != null)
                    {
                        if(EzThrottler.Throttle("MultiModeAfkOnTitleLogin", 20000))
                        {
                            if(!Relog(next, out var error, RelogReason.MultiMode))
                            {
                                PluginLog.Error($"Error while automatically logging in: {error}");
                                Notify.Error($"{error}");
                            }
                        }
                    }
                    else
                    {
                        MultiModeRoundTrip.NotifyNothingToDo();
                    }
                }
            }
            // Size 就是列舉得到的筆數；用 Count() 會每幀建一個列舉器與兩段陣列。
            if(Interactions.Size == Interactions.Capacity && Interactions.All(x => Environment.TickCount64 - x < 60000))
            {
                if(C.OfflineData.TryGetFirst(x => x.CID == Svc.PlayerState.ContentId, out var data) && data.Enabled)
                {
                    data.Enabled = false;
                    data.WorkshopEnabled = false;
                    DuoLog.Warning("Too many errors, current character is excluded.");
                    Interactions.Clear();
                    return;
                }
                else
                {
                    SetEnabled(false, "短時間內錯誤過多（Fatal error）", MultiModeChangeCause.AutoRetainer);
                    data.WorkshopEnabled = false;
                    DuoLog.Error("Fatal error. Please report this with logs.");
                    Interactions.Clear();
                    return;
                }
            }
            // 🔴 下面的剔除是永久的（Enabled 是存進設定檔的，要使用者自己回去打勾），
            // 而 ProperOnLogin.PlayerPresent 只保證 LocalPlayer 不是 null —— 換區與剛登入時它就是 true，
            // 但背包容器可能還讀不到，空格數會回 0，跟「背包真的滿了」完全同形。多開模式本來就一直在換角色登入，
            // 所以先確認讀得到再判斷；讀不到就這一幀不做決定。
            if(ProperOnLogin.PlayerPresent && !P.TaskManager.IsBusy && Utils.IsInventoryStateReadable())
            {
                // 背包觸發這一輪會去交的角色先不剔除（同上游）；背包觸發關著時這個呼叫一律回 false。
                if(!Utils.IsInventoryFree() && !MultiModeVentureDelivery.KeepsCharacterWithFullBags())
                {
                    if(C.OfflineData.TryGetFirst(x => x.CID == Svc.PlayerState.ContentId, out var data))
                    {
                        data.Enabled = false;
                    }
                }
            }
            if(ProperOnLogin.PlayerPresent && !P.TaskManager.IsBusy && IsInteractionAllowed()
                && (!Synchronize || C.OfflineData.Where(x => !x.IsLockedOut()).All(x => x.GetEnabledRetainers().All(z => z.GetVentureSecondsRemaining() <= C.UnsyncCompensation))))
            {
                Synchronize = false;
                // 排在換角色之前：探險幣或背包空格不足的角色要先處理完再決定是不是輪到下一個。
                if(MultiModeVentureDelivery.EnqueueIfNeeded())
                {
                    EndRelogGateWatch("改去軍團繳交");
                    BlockInteraction(10);
                }
                else if(IsCurrentCharacterDone() && !IsOccupied())
                {
                    var next = GetCurrentTargetCharacter();
                    if(next == null && IsAllRetainersHaveMoreThan15Mins())
                    {
                        next = GetPreferredCharacter();
                    }
                    if(next != null)
                    {
                        DebugLog($"Enqueueing relog");
                        EndRelogGateWatch("放行換角");
                        BlockInteraction(20);
                        if(!Relog(next, out var error, RelogReason.MultiMode))
                        {
                            DuoLog.Error(error);
                        }
                        else
                        {
                            DebugLog($"Relog command success");
                        }
                        Interactions.PushBack(Environment.TickCount64);
                        DebugLog($"Added interaction because of relogging (state: {Interactions.Print()})");
                    }
                    else
                    {
                        if(MultiMode.WaitOnLoginScreen)
                        {
                            DebugLog($"Enqueueing logoff");
                            EndRelogGateWatch("放行登出");
                            BlockInteraction(20);
                            if(!Relog(null, out var error, RelogReason.MultiMode))
                            {
                                DuoLog.Error(error);
                            }
                            else
                            {
                                DebugLog($"Logoff command success");
                            }
                            Interactions.PushBack(Environment.TickCount64);
                            DebugLog($"Added interaction because of logging off (state: {Interactions.Print()})");
                        }
                        else
                        {
                            MultiModeRoundTrip.NotifyNothingToDo();
                        }
                    }
                }
                else if(!IsOccupied() && !Utils.IsBusy && Data != null)
                {
                    if(Data.WorkshopEnabled && Data.AnyEnabledVesselsAvailable() && MultiMode.EnabledSubmarines)
                    {
                        if(!Data.ShouldWaitForAllWhenLoggedIn() || Data.AreAnyEnabledVesselsReturnInNext(0, true))
                        {
                            if(!TaskTeleportToProperty.EnqueueIfNeededAndPossible(true))
                            {
                                DebugLog($"Enqueueing interaction with panel");
                                EndRelogGateWatch("改去潛水艇面板");
                                BlockInteraction(10);
                                TaskInteractWithNearestPanel.Enqueue();
                                P.TaskManager.Enqueue(() => { VoyageScheduler.Enabled = true; }, "MultiMode.EnableVoyageScheduler");
                                Interactions.PushBack(Environment.TickCount64);
                                DebugLog($"Added interaction because of interacting (state: {Interactions.Print()})");
                            }
                        }
                    }
                    else if(AnyRetainersAvailable() && EnabledRetainers)
                    {
                        if(C.OfflineData.TryGetFirst(x => x.CID == Svc.PlayerState.ContentId, out var data))
                        {
                            if(!TaskTeleportToProperty.EnqueueIfNeededAndPossible(false))
                            {
                                EnterWorkshopForRetainers();
                                EnsureCharacterValidity();
                                if(data.Enabled)
                                {
                                    DebugLog($"Enqueueing interaction with bell");
                                    EndRelogGateWatch("改回傳喚鈴");
                                    TaskInteractWithNearestBell.Enqueue();
                                    P.TaskManager.Enqueue(() => { SchedulerMain.EnablePlugin(PluginEnableReason.MultiMode); return true; }, "MultiMode.EnableRetainerScheduler");
                                    BlockInteraction(10);
                                    Interactions.PushBack(Environment.TickCount64);
                                    DebugLog($"Added interaction because of interacting (state: {Interactions.Print()})");
                                }
                            }
                        }
                    }
                }
            }
        }
    }

    internal static void EnterWorkshopForRetainers()
    {
        if(Utils.GetReachableRetainerBell(true) == null && Houses.List.Contains((ushort)Player.Territory))
        {
            TaskNeoHET.TryEnterWorkshop(() =>
            {
                Data.Enabled = false;
                DuoLog.Error($"Due to absence of retainer bell and failure to find workshop, character is excluded from processing retainers");
                P.TaskManager.Abort();
            });
        }
    }

    internal static bool CheckInventoryValidity()
    {
        return Svc.Objects.LocalPlayer.HomeWorld.RowId == Svc.Objects.LocalPlayer.CurrentWorld.RowId && Utils.GetVenturesAmount() >= Data.GetNeededVentureAmount() && Utils.IsInventoryFree();
    }

    internal static IEnumerable<OfflineCharacterData> GetEnabledOfflineData()
    {
        return C.OfflineData.Where(x => x.Enabled).Where(x => !x.IsLockedOut());
    }

    internal static bool AnyRetainersAvailable(int advanceSeconds = 0)
    {
        if(TryGetEnabledCurrentCharacter(out var data) && C.SelectedRetainers.TryGetValue(data.CID, out var enabledRetainers))
        {
            return AnyEnabledRetainerDueWithin(data, enabledRetainers, C.UnsyncCompensation + advanceSeconds);
        }
        return false;
    }

    internal static bool IsAllRetainersHaveMoreThan15Mins()
    {
        foreach(var x in C.OfflineData)
        {
            if(!x.Enabled || x.IsLockedOut()) continue;
            if(!C.SelectedRetainers.TryGetValue(x.CID, out var enabledRetainers)) continue;
            foreach(var z in x.RetainerData)
            {
                if(!IsEnabledRetainer(z, enabledRetainers)) continue;
                if(z.GetVentureSecondsRemaining() < 15 * 60) return false;
            }
        }
        return true;
    }

    // 以下幾支是多角模式決策區塊每幀都會走的判斷（閒置在標題畫面或等下一個角色時也一樣），
    // 直接走訪 List 而不經 LINQ，才不會每幀配置列舉器、閉包與暫存陣列；
    // 篩選條件、走訪順序與短路點必須與各支 summary 寫的 LINQ 形式一致（剩餘秒數與鎖區都讀當下時間）。

    /// <summary>即 <c>C.OfflineData.TryGetFirst(x => x.CID == Svc.PlayerState.ContentId, out data)</c>。</summary>
    private static bool TryGetCurrentCharacter(out OfflineCharacterData data)
    {
        if(C.OfflineData != null)
        {
            foreach(var x in C.OfflineData)
            {
                if(x.CID == Svc.PlayerState.ContentId)
                {
                    data = x;
                    return true;
                }
            }
        }
        data = null;
        return false;
    }

    /// <summary>即 <c>GetEnabledOfflineData().TryGetFirst(x => x.CID == Svc.PlayerState.ContentId, out data)</c>：先看 Enabled、再看鎖區、最後比 CID。</summary>
    private static bool TryGetEnabledCurrentCharacter(out OfflineCharacterData data)
    {
        foreach(var x in C.OfflineData)
        {
            if(x.Enabled && !x.IsLockedOut() && x.CID == Svc.PlayerState.ContentId)
            {
                data = x;
                return true;
            }
        }
        data = null;
        return false;
    }

    /// <summary><see cref="GetEnabledRetainers"/> 的篩選條件。</summary>
    private static bool IsEnabledRetainer(OfflineRetainerData z, HashSet<string> enabledRetainers) => enabledRetainers.Contains(z.Name) && z.HasVenture;

    /// <summary>即 <c>data.GetEnabledRetainers().Any(z => z.GetVentureSecondsRemaining() &lt;= seconds)</c>；<paramref name="enabledRetainers"/> 是該角色在 <c>C.SelectedRetainers</c> 裡的那一份。</summary>
    private static bool AnyEnabledRetainerDueWithin(OfflineCharacterData data, HashSet<string> enabledRetainers, long seconds)
    {
        foreach(var z in data.RetainerData)
        {
            if(IsEnabledRetainer(z, enabledRetainers) && z.GetVentureSecondsRemaining() <= seconds) return true;
        }
        return false;
    }

    /// <summary>
    /// 即 <c>selected.Any() &amp;&amp; selected.All(z => z.GetVentureSecondsRemaining() &lt;= seconds)</c>。
    /// Any() 不讀時間，所以併成一趟：剩餘秒數的呼叫次數與順序和 All 那一趟相同。
    /// </summary>
    private static bool AllEnabledRetainersDueWithin(OfflineCharacterData data, HashSet<string> enabledRetainers, long seconds)
    {
        var any = false;
        foreach(var z in data.RetainerData)
        {
            if(!IsEnabledRetainer(z, enabledRetainers)) continue;
            any = true;
            if(!(z.GetVentureSecondsRemaining() <= seconds)) return false;
        }
        return any;
    }

    internal static OfflineCharacterData GetPreferredCharacter()
    {
        return C.OfflineData.FirstOrDefault(x => x.Preferred && x.CID != Svc.PlayerState.ContentId);
    }

    internal static void BlockInteraction(int seconds)
    {
        // 角色被佔用或還沒就緒的每一幀都會進來；共用的亂數產生器給的是同一個均勻分布，不必每幀新建一個。
        NextInteractionAt = Environment.TickCount64 + seconds * Random.Shared.Next(800, 1200);
    }

    internal static bool IsInteractionAllowed()
    {
        return Environment.TickCount64 > NextInteractionAt;
    }

    // 診斷：傳喚鈴關窗後到 Tick 放行下一步的時間，依 Tick 的判斷順序記下每一幀是哪一道閘門擋著；
    // 晚於 RelogGateTracker.LateMs 才在結束時印一行。每幀路徑只做比較與加法，字串只在要印時組。只在框架執行緒上碰。
    private static readonly RelogGateTracker RelogGate = new();

    private static void WatchRelogGate()
    {
        var now = Environment.TickCount64;
        if(!RelogGate.Advance(now, Svc.Condition[ConditionFlag.OccupiedSummoningBell])) return;

        var block = RelogGateBlock.None;
        string taskName = null;
        var logoutRunning = false;
        if(P.TaskManager.IsBusy)
        {
            block = RelogGateBlock.Task;
            var task = P.TaskManager.CurrentTask;
            taskName = task?.Name;
            logoutRunning = task != null && task.Name == nameof(TaskChangeCharacter.Logout) && task.Location == nameof(TaskChangeCharacter);
        }
        else if(!ProperOnLogin.PlayerPresent) block = RelogGateBlock.NoPlayer;
        else if(IsOccupied() || !IsScreenReady()) block = RelogGateBlock.Occupied;
        else if(!IsInteractionAllowed()) block = RelogGateBlock.Cooldown;
        else if(Synchronize) block = RelogGateBlock.Sync;
        else if(!IsCurrentCharacterDone()) block = RelogGateBlock.NotDone;

        var outcome = RelogGate.Account(block, taskName, logoutRunning, now);
        if(RelogGate.SnapshotDue(now)) RelogGate.Snapshot = DescribeRelogGates(now);
        if(outcome != null) EndRelogGateWatch(outcome);
    }

    private static string DescribeRelogGates(long now)
    {
        try
        {
            var flags = string.Join(",", Svc.Condition.AsReadOnlySet());
            return $"taskBusy={P.TaskManager.IsBusy} 目前任務={P.TaskManager.CurrentTask?.Name ?? "-"} 佇列={P.TaskManager.NumQueuedTasks} playerPresent={ProperOnLogin.PlayerPresent} screenReady={IsScreenReady()} occupied={IsOccupied()} 狀態旗標=[{flags}] 互動冷卻剩 {Math.Max(0, NextInteractionAt - now)}ms synchronize={Synchronize} 僱員做完={IsCurrentCharacterRetainersDone()} 潛水艇做完={IsCurrentCharacterCaptainDone()} utilsBusy={Utils.IsBusy} 排程器開著={SchedulerMain.PluginEnabled} 後處理鎖={SchedulerMain.CharacterPostProcessLocked}";
        }
        catch(Exception e)
        {
            return $"讀取失敗：{e.Message}";
        }
    }

    /// <param name="outcome">固定字面值，不可傳組出來的字串（每幀路徑上呼叫）。</param>
    private static void EndRelogGateWatch(string outcome)
    {
        var waited = RelogGate.End(Environment.TickCount64);
        if(waited <= RelogGateTracker.LateMs) return;
        var g = RelogGate;
        PluginLog.Information($"[MultiMode] [RelogGate] 傳喚鈴關窗後 {waited}ms {outcome}；各閘門擋住的時間：任務佇列 {g.TaskMs}ms（第一個 {g.FirstTask ?? "-"}、最後 {g.LastTask ?? "-"}）、角色不在 {g.NoPlayerMs}ms、角色忙碌或畫面未就緒 {g.OccupiedMs}ms、互動冷卻 {g.CooldownMs}ms、同步等待 {g.SyncMs}ms、角色還沒做完 {g.NotDoneMs}ms；第 2 秒當下：{g.Snapshot ?? "?"}");
    }

    internal static OfflineRetainerData[] GetEnabledRetainers(this OfflineCharacterData data)
    {
        if(C.SelectedRetainers.TryGetValue(data.CID, out var enabledRetainers))
        {
            return data.RetainerData.Where(z => IsEnabledRetainer(z, enabledRetainers)).ToArray();
        }
        return Array.Empty<OfflineRetainerData>();
    }

    internal static bool Relog(OfflineCharacterData data, out string ErrorMessage, RelogReason reason, bool allowFromTaskManager = false, MultiModeChangeCause cause = MultiModeChangeCause.User)
    {
        if(reason.EqualsAny(RelogReason.Overlay, RelogReason.Command, RelogReason.ConfigGUI))
        {
            if(MultiModeRoundTrip.Active) MultiModeRoundTrip.Cancel($"手動換角（{reason}）");
            if(C.MultiDisableOnRelog)
            {
                SetEnabled(false, $"手動換角（{reason}）且設定為手動換角時關閉多角色模式", cause);
            }
            if(MultiMode.Active && !C.MultiNoPreferredReset)
            {
                foreach(var z in C.OfflineData)
                {
                    z.Preferred = false;
                }
                Notify.Warning("Preferred character has been reset");
            }
        }
        ErrorMessage = string.Empty;
        if(P.TaskManager.IsBusy && !allowFromTaskManager)
        {
            ErrorMessage = "AutoRetainer is processing tasks";
        }
        else if(SchedulerMain.CharacterPostProcessLocked)
        {
            ErrorMessage = "Currently in post-processing of character";
        }
        /*else if (data != null && !data.Index.InRange(1, 9))
        {
            ErrorMessage = "Invalid character index";
        }*/
        else
        {
            if(Player.Available)
            {
                if(IsOccupied())
                {
                    ErrorMessage = "Player is occupied";
                }
                else if(data != null && data.CID == Svc.PlayerState.ContentId)
                {
                    ErrorMessage = "Targeted player is logged in";
                }
                else
                {
                    // 只在多開排程自己的換角上叫停：手動換角不該動別人的自動化。單向、不等它。
                    if(reason is RelogReason.MultiMode or RelogReason.ExpertDeliveryLoop or RelogReason.RoundTripReturn) QuestionableRelogIPC.StopBeforeRelog();
                    if(reason == RelogReason.MultiMode || C.AllowManualPostprocess)
                    {
                        TaskPostprocessCharacterIPC.Enqueue();
                    }
                    if(MultiMode.Enabled)
                    {
                        CharaCnt.IncrementOrSet(Svc.PlayerState.ContentId);
                    }
                    else
                    {
                        CharaCnt.Clear();
                    }
                    if(data != null)
                    {
                        TaskChangeCharacter.Enqueue(data.CurrentWorld, data.Name, data.World, data.ServiceAccount);
                    }
                    else
                    {
                        TaskChangeCharacter.EnqueueLogout();
                    }
                    RecordManualRelog(reason, cause, data);
                    return true;
                }
            }
            else
            {
                if(Utils.CanAutoLogin() || (allowFromTaskManager && Utils.CanAutoLoginFromTaskManager()))
                {
                    TaskChangeCharacter.EnqueueLogin(data.CurrentWorld, data.Name, data.World, data.ServiceAccount);
                    RecordManualRelog(reason, cause, data);
                    return true;
                }
                else
                {
                    ErrorMessage = "Can not log in now";
                }
            }
        }
        return false;
    }

    internal static List<OfflineCharacterData> GetRetainerSortedOfflineDatas(bool sort)
    {
        var data = new List<OfflineCharacterData>();
        FillRetainerSortedOfflineDatas(data, sort);
        return data;
    }

    // GetCurrentTargetCharacter 每幀排一次順序用的緩衝區。只在框架執行緒上用（排程 Tick 與介面繪製同一條），
    // 而且每次都先整份重填，不跨呼叫保存內容。
    private static readonly List<OfflineCharacterData> TargetOrderBuffer = [];
    private static long[] SortKeyBuffer = new long[16];

    private static long[] GetSortKeyBuffer(int count)
    {
        if(SortKeyBuffer.Length < count) SortKeyBuffer = new long[Math.Max(count, SortKeyBuffer.Length * 2)];
        return SortKeyBuffer;
    }

    /// <summary>
    /// 即 <c>data = [.. data.OrderBy(鍵)]</c>，鍵已依序算好放在 <paramref name="keys"/>。
    /// 插入排序只在前一個鍵「嚴格較大」時才往前移，同鍵維持原順序＝與 OrderBy 相同的穩定排序；角色數只有十位數。
    /// </summary>
    private static void StableSortByKey(List<OfflineCharacterData> data, long[] keys)
    {
        for(var i = 1; i < data.Count; i++)
        {
            var item = data[i];
            var key = keys[i];
            var j = i - 1;
            while(j >= 0 && keys[j] > key)
            {
                data[j + 1] = data[j];
                keys[j + 1] = keys[j];
                j--;
            }
            data[j + 1] = item;
            keys[j + 1] = key;
        }
    }

    /// <summary>把 <paramref name="data"/> 清空後填成 <see cref="GetRetainerSortedOfflineDatas"/> 會回傳的內容與順序。</summary>
    private static void FillRetainerSortedOfflineDatas(List<OfflineCharacterData> data, bool sort)
    {
        data.Clear();
        foreach(var x in C.OfflineData)
        {
            if(!x.IsLockedOut()) data.Add(x);
        }
        if(sort)
        {
            if(C.CharEqualize)
            {
                var keys = GetSortKeyBuffer(data.Count);
                for(var i = 0; i < data.Count; i++) keys[i] = CharaCnt.GetOrDefault(data[i].CID);
                StableSortByKey(data, keys);
            }
            if(C.LongestVentureFirst)
            {
                var keys = GetSortKeyBuffer(data.Count);
                for(var i = 0; i < data.Count; i++) keys[i] = data[i].GetEnabledRetainers().OrderBy(r => r.VentureEndsAt).FirstOrDefault()?.VentureEndsAt ?? long.MaxValue;
                StableSortByKey(data, keys);
            }
            if(C.CappedLevelsLast)
            {
                List<OfflineCharacterData> capped = [];
                List<OfflineCharacterData> levelling = [];
                foreach(var x in data)
                {
                    var e = x.GetEnabledRetainers();
                    foreach(var ret in e)
                    {
                        var cap = ret.Level < Player.MaxLevel && x.GetJobLevel(ret.Job) == ret.Level;
                        if(!cap) goto Getout;
                    }
                    capped.Add(x);
                Getout:
                    continue;
                }
                data.RemoveAll(capped.Contains);
                foreach(var x in data)
                {
                    var e = x.GetEnabledRetainers();
                    foreach(var ret in e)
                    {
                        var canLevel = ret.Level < Player.MaxLevel && x.GetJobLevel(ret.Job) > ret.Level;
                        if(canLevel) goto Add;
                    }
                    continue;
                Add:
                    levelling.Add(x);
                }
                data.RemoveAll(levelling.Contains);
                List<OfflineCharacterData> ordered = [.. levelling, .. data, .. capped];
                data.Clear();
                data.AddRange(ordered);
            }
        }
        if(C.MultiPreferredCharLast)
        {
            var pref = data.FirstOrDefault(x => x.Preferred);
            if(pref != null)
            {
                data.Remove(pref);
                data.Add(pref);
            }
        }
    }

    internal static OfflineCharacterData GetCurrentTargetCharacter()
    {
        if(EnabledSubmarines)
        {
            var data = TargetOrderBuffer;
            FillRetainerSortedOfflineDatas(data, false);
            foreach(var x in data)
            {
                if(x.CID == Player.CID) continue;
                if(x.WorkshopEnabled && x.GetEnabledVesselsData(VoyageType.Airship).Count + x.GetEnabledVesselsData(VoyageType.Submersible).Count > 0)
                {
                    if(x.AreAnyEnabledVesselsReturnInNext(0, C.MultiModeWorkshopConfiguration.MultiWaitForAll))
                    {
                        return x;
                    }
                }
            }
            foreach(var x in data)
            {
                if(x.CID == Player.CID) continue;
                if(x.WorkshopEnabled && x.GetEnabledVesselsData(VoyageType.Airship).Count + x.GetEnabledVesselsData(VoyageType.Submersible).Count > 0)
                {
                    if(x.AreAnyEnabledVesselsReturnInNext(C.MultiModeWorkshopConfiguration.AdvanceTimer, C.MultiModeWorkshopConfiguration.MultiWaitForAll))
                    {
                        return x;
                    }
                }
            }
        }
        if(EnabledRetainers)
        {
            var data = TargetOrderBuffer;
            FillRetainerSortedOfflineDatas(data, true);
            // 「全部都要回來」＝勾選且有探險的僱員非空、而且全部到期；否則任一到期就算。
            foreach(var x in data)
            {
                if(x.CID == Player.CID) continue;
                if(x.Enabled && C.SelectedRetainers.TryGetValue(x.CID, out var enabledRetainers))
                {
                    if(C.MultiModeRetainerConfiguration.MultiWaitForAll
                        ? AllEnabledRetainersDueWithin(x, enabledRetainers, 0)
                        : AnyEnabledRetainerDueWithin(x, enabledRetainers, 0))
                    {
                        return x;
                    }
                }
            }
            foreach(var x in data)
            {
                if(x.CID == Player.CID) continue;
                if(x.Enabled && C.SelectedRetainers.TryGetValue(x.CID, out var enabledRetainers))
                {
                    if(C.MultiModeRetainerConfiguration.MultiWaitForAll
                        ? AllEnabledRetainersDueWithin(x, enabledRetainers, C.MultiModeRetainerConfiguration.AdvanceTimer)
                        : AnyEnabledRetainerDueWithin(x, enabledRetainers, C.MultiModeRetainerConfiguration.AdvanceTimer))
                    {
                        return x;
                    }
                }
            }
        }

        return null;
    }

    internal static bool IsCurrentCharacterDone()
    {
        return IsCurrentCharacterRetainersDone() && IsCurrentCharacterCaptainDone();
    }

    internal static bool IsCurrentCharacterRetainersDone()
    {
        if(!ProperOnLogin.PlayerPresent) return false;
        if(TryGetCurrentCharacter(out var data))
        {
            if(!EnabledRetainers) return true;
            if(!data.Enabled) return true;
            if(Utils.GetVenturesAmount() < 2 || !Utils.IsInventoryFree()) return true;
            return !IsAnySelectedRetainerFinishesWithin(5 * 60);
        }
        else
        {
            return true;
        }
    }

    internal static bool IsCurrentCharacterCaptainDone()
    {
        if(!EnabledSubmarines) return true;
        if(Data == null) return true;
        if(!Data.WorkshopEnabled) return true;
        return !Data.AreAnyEnabledVesselsReturnInNext(5 * 60, Data.ShouldWaitForAllWhenLoggedIn());
    }

    internal static bool IsAnySelectedRetainerFinishesWithin(int seconds)
    {
        if(!ProperOnLogin.PlayerPresent) return false;
        if(!EnabledRetainers) return false;
        if(TryGetEnabledCurrentCharacter(out var data) && C.SelectedRetainers.TryGetValue(data.CID, out var enabledRetainers))
        {
            return AnyEnabledRetainerDueWithin(data, enabledRetainers, seconds);
        }
        return false;
    }

    internal static bool EnsureCharacterValidity(bool ro = false)
    {
        if(!ProperOnLogin.PlayerPresent) return false;
        if(C.OfflineData.TryGetFirst(x => x.CID == Svc.PlayerState.ContentId, out var data))
        {
            if(Svc.Objects.LocalPlayer.HomeWorld.RowId == Svc.Objects.LocalPlayer.CurrentWorld.RowId && Utils.GetVenturesAmount() >= data.GetNeededVentureAmount() && Utils.IsInventoryFree() && Utils.GetReachableRetainerBell(true) != null)
            {
                return true;
            }
            // 🔴 這是持久化的剔除（Enabled 存進設定檔）。上面兩個判斷都讀背包（GetVenturesAmount 走
            // GetInventoryItemCount、IsInventoryFree 走空格數），兩者在容器讀不到時都會回 0 ——
            // 「探險幣不夠」與「背包滿」會同時假成立。讀不到就先不剔除，下一幀讀得到時照樣會執行。
            if(!ro && Utils.IsInventoryStateReadable())
            {
                data.Enabled = false;
            }
        }
        return false;
    }
    internal static int GetNeededVentureAmount(this OfflineCharacterData data)
    {
        return data.GetEnabledRetainers().Length * 2;
    }

    internal static void PerformAutoStart()
    {
        EzSharedData.TryGet<object>("AutoRetainer.WasLoaded", out _, CreationMode.CreateAndKeep, new());
        for(var i = 0; i < C.AutoLoginDelay; i++)
        {
            var seconds = C.AutoLoginDelay - i;
            P.TaskManager.Enqueue(() => Svc.NotificationManager.AddNotification(new()
            {
                Content = $"Autostart in {seconds}!",
                InitialDuration = TimeSpan.FromSeconds(1),
                HardExpiry = DateTime.Now.AddSeconds(1),
                Type = NotificationType.Warning,
            }), "AutoStart.CountdownNotification");
            P.TaskManager.EnqueueDelay(1000);
        }
        P.TaskManager.Enqueue(() =>
        {
            if(C.AutoLogin != "")
            {
                OfflineCharacterData data;
                if(C.AutoLogin == "~")
                {
                    data = C.OfflineData.Where(x => !x.IsLockedOut()).FirstOrDefault(s => s.CID == C.LastLoggedInChara);
                }
                else
                {
                    data = C.OfflineData.Where(x => !x.IsLockedOut()).First(s => $"{s.Name}@{s.World}" == C.AutoLogin);
                }
                if(data == null) return true;
                if(Utils.CanAutoLoginFromTaskManager())
                {
                    MultiMode.Relog(data, out var error, RelogReason.Command, true, MultiModeChangeCause.AutoRetainer);
                    if(error == "")
                    {
                        return true;
                    }
                    else
                    {
                        DuoLog.Error($"Error during auto login: {error}");
                    }
                }
                return false;
            }
            else
            {
                return true;
            }
        }, "AutoStart.AutoLogin");
        P.TaskManager.Enqueue(() =>
        {
            if(C.MultiAutoStart)
            {
                SetEnabled(true, "遊戲啟動時自動開啟（MultiAutoStart）", MultiModeChangeCause.AutoRetainer);
                BailoutManager.IsLogOnTitleEnabled = true;
            }
        }, "AutoStart.EnableMultiMode");
    }
}
