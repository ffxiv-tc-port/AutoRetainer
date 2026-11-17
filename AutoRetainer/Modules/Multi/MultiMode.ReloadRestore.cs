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

internal static unsafe partial class MultiMode
{
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

    internal static bool AnyBellBlockingWindow(Func<string, bool> isOpen)
    {
        foreach(var name in BellBlockingWindowNames)
        {
            if(isOpen(name)) return true;
        }
        return false;
    }

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
}
