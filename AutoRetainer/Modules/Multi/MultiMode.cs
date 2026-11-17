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

    // 鈴前接續時這些視窗只要有一扇開著就不接續：它們都不是接手後第一個會按的窗；
    // 對話（Talk）在重載後可能正被沒上鎖的 TextAdvance 翻頁，接手會和它同時按。
    private static readonly string[] BellBlockingWindowNames = ["Talk", "SelectString", "SelectYesno", "ContextMenu", "RetainerTaskResult", "RetainerTaskAsk", "RetainerTaskList", "RetainerTaskSupply", "Bank", "InventoryRetainer", "InventoryRetainerLarge", "RetainerCrystalGrid", "RetainerSellList", "RetainerItemTransferList", "RetainerItemTransferProgress"];

    private static readonly Func<string, bool> IsAddonVisibleFn = IsAddonVisible;

    private const string PanelVoyageAlreadyRunningLine = "[AutoRetainer] 等待就地接續時遠航探索排程已經在運作（進入航行管制視窗時自動重新派遣，或手動勾選），不另外接續；多角色模式照舊等離開後再恢復。";

    // 面板前接續時這些視窗只要有一扇開著就不接續：航行排程重新開始時只從面板選單（種類、潛水艇、飛空艇清單）接手，
    // 其餘的窗有的會被 YesAlready／TextAdvance 或 AutoRetainer 自己的確認框處理按下，有的新實例根本不會去按。
    private static readonly string[] PanelBlockingWindowNames = ["Talk", "SelectYesno", "SelectIconString", "SelectOk", "ContextMenu", "ContextIconMenu", "AirShipExploration", "AirShipExplorationDetail", "AirShipExplorationResult", "CompanyCraftSupply", "AirShipPartsMenu"];

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
            TaskNeoHET.ResetEntranceTimeouts();
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

    // GetCurrentTargetCharacter 每幀排一次順序用的緩衝區。只在框架執行緒上用（排程 Tick 與介面繪製同一條），
    // 而且每次都先整份重填，不跨呼叫保存內容。
    private static readonly List<OfflineCharacterData> TargetOrderBuffer = [];
    private static long[] SortKeyBuffer = new long[16];

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
