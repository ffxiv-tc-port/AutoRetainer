using AutoRetainer.Internal;
using AutoRetainer.Internal.InventoryManagement;
using AutoRetainer.Modules.Statistics;
using AutoRetainer.Modules.Voyage;
using AutoRetainer.Scheduler.Handlers;
using AutoRetainer.Scheduler.Tasks;
using AutoRetainer.Services;
using AutoRetainer.UI.MainWindow;
using AutoRetainer.UI.Overlays;
using AutoRetainer.UI.Windows;
using AutoRetainerAPI;
using AutoRetainerAPI.Configuration;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Gui.Toast;
using Dalamud.Game.Text.SeStringHandling;
using ECommons.Automation;
using ECommons.Automation.NeoTaskManager;
using ECommons.Configuration;
using ECommons.Events;
using ECommons.ExcelServices;
using ECommons.ExcelServices.TerritoryEnumeration;
using ECommons.EzIpcManager;
using ECommons.EzSharedDataManager;
using ECommons.IPC;
using ECommons.IPC.Subscribers;
using ECommons.GameHelpers;
using ECommons.Reflection;
using ECommons.Singletons;
using ECommons.Throttlers;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using NotificationMasterAPI;
using PunishLib;
using System.Diagnostics;
using LoginOverlay = AutoRetainer.UI.Overlays.LoginOverlay;

namespace AutoRetainer;

public unsafe class AutoRetainer : IDalamudPlugin
{
    public string Name => "AutoRetainer";
    internal static AutoRetainer P;
    internal static Config C => P.config;
    private Config config;
    internal WindowSystem WindowSystem;
    internal AutoRetainerWindow AutoRetainerWindow;
    internal bool IsInteractionAutomatic = false;
    internal QuickSellItems quickSellItems;
    internal TaskManager TaskManager;
    internal TaskManager ODMTaskManager;
    internal Memory Memory;
    internal bool WasEnabled = false;
    internal bool IsCloseActionAutomatic = false;
    internal long LastMovementAt;
    internal Vector3 LastPosition;
    internal bool IsNextToBell;
    internal bool ConditionWasEnabled = false;
    internal VenturePlanner VenturePlanner;
    internal VentureBrowser VentureBrowser;
    internal LogWindow LogWindow;
    internal AutoRetainerApi API;
    internal LoginOverlay LoginOverlay;
    internal MarketCooldownOverlay MarketCooldownOverlay;
    internal SubmarineUnlockPlanUI SubmarineUnlockPlanUI;
    internal SubmarinePointPlanUI SubmarinePointPlanUI;

    internal long Time => C.UseServerTime ? CSFramework.GetServerTime() : DateTimeOffset.Now.ToUnixTimeSeconds();

    internal RetainerListOverlay RetainerListOverlay;
    internal uint LastVentureID = 0;
    internal uint ListUpdateFrame = 0;

    internal bool LogOpcodes = false;
    internal int LastLoadedItems = 0;
    internal NotificationMasterApi NotificationMasterApi;
    internal long[] TimeLaunched;
    internal ContextMenuManager ContextMenuManager;
    public bool ReadOnly = false;

    internal static OfflineCharacterData Data => Utils.GetCurrentCharacterData();

    public AutoRetainer(IDalamudPluginInterface pi)
    {
        //PluginLoader.CheckAndLoad(pi, "https://love.puni.sh/plugins/AutoRetainer/blacklist.txt", delegate
        {
            P = this;
            ECommonsMain.Init(pi, this, Module.DalamudReflector);
            SvcEx.Init(pi);
            // 讓「呼叫了對方沒有的 IPC 方法」不再完全靜默。
            // 訂閱越早越好：事件只在 IPC **呼叫**當下才被查閱，在這裡訂閱就涵蓋往後所有呼叫。
            EzIpcFailureLog.Enable();
#if CUSTOMCS
            PluginLog.Warning($"Using custom FFXIVClientStructs");
            var gameVersion = DalamudReflector.TryGetDalamudStartInfo(out var ver) ? ver.GameVersion.ToString() : "unknown";
            InteropGenerator.Runtime.Resolver.GetInstance.Setup(Svc.SigScanner.SearchBase, gameVersion, new(Svc.PluginInterface.ConfigDirectory.FullName + "/cs.json"));
            FFXIVClientStructs.Interop.Generated.Addresses.Register();
            InteropGenerator.Runtime.Resolver.GetInstance.Resolve();
#endif
            PunishLibMain.Init(pi, Name, PunishOption.DefaultKoFi); // Default button
            var cnt = FFXIVInstanceMonitor.GetFFXIVCNT();
            PluginLog.Information($"FFXIV instances: {cnt}");
            if(FFXIVInstanceMonitor.AcquireLock() || cnt <= 1)
            {
                new TickScheduler(Load);
            }
            else
            {
                var shouldCreateWindow = !EzConfig.LoadConfiguration<Config>(EzConfig.DefaultSerializationFactory.DefaultConfigFileName).No2ndInstanceNotify;
                if(shouldCreateWindow)
                {
                    new SingletonNotifyWindow();
                }
                else
                {
                    for(var i = 0; i < 100; i++)
                    {
                        PluginLog.Fatal($"AutoRetainer's loading was skipped because it's second instance of the game and you have \"Do not warn about second game instance running from same directory\" option enabled.");
                    }
                }
            }
        }
        //);
    }

    internal void SetConfig(Config c)
    {
        config = c;
    }

    public void Load()
    {
        Loc.Load(Svc.ClientState.ClientLanguage);
        EzConfig.Migrate<Config>();
        config = EzConfig.Init<Config>();

        //windows
        WindowSystem = new();
        VenturePlanner = new();
        VentureBrowser = new();
        LogWindow = new();
        AutoRetainerWindow = new();
        MarketCooldownOverlay = new();
        new MultiModeOverlay();
        RetainerListOverlay = new();
        LoginOverlay = new LoginOverlay();
        SubmarineUnlockPlanUI = new();
        SubmarinePointPlanUI = new();

        // 🔴 訂閱順序＝每幀的呼叫順序:這一行必須排在下面 TaskManager = new(...) 之前。
        //    ECommons 的 NeoTaskManager 建構式自己就 Svc.Framework.Update += Tick
        //    (ECommons/Automation/NeoTaskManager/TaskManager.cs),而排程任務(TaskEntrustDuplicates
        //    的數量輸入框、TaskDiscardItems 的丟棄確認框、買燃料確認框…)全是掛在那條鏈上按窗的。
        //    守衛的解除點若排在它後面,每幀就變成「先按、後解除」:舊窗在兩幀之間被遊戲移出清單、
        //    同一輪任務又開出一扇重用同一塊位址的新窗時,解除掃描看到位址還在就把舊記號留著,
        //    新窗會被白白擋到逃生口(道具逐件迴圈＝每件多等 60 幀 ≈ 1 秒)。排在最前面才是
        //    「先解除、再按」,與這個守衛集中化之前(各任務在自己 tick 開頭 ReleaseGuardIfGone)一致。
        //    另一個理由:Dalamud 把一個外掛的整條 Framework.Update 鏈包在同一個 try/catch 裡
        //    (PluginErrorHandler.InvokeAndCatch),前面任何一個處理器丟例外會讓後面的處理器整幀被跳過;
        //    解除點掛在最前面就不會被別人的例外連累而漏掉一幀。
        Svc.Framework.Update += DialogGuardsTick;

        TaskManager = new(new(abortOnTimeout: true, timeLimitMS: 20000, showDebug: true));
        Memory = new();
        Svc.PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        Svc.PluginInterface.UiBuilder.OpenMainUi += () =>
        {
            AutoRetainerWindow.IsOpen = true;
        };
        Svc.PluginInterface.UiBuilder.OpenConfigUi += () =>
        {
            S.NeoWindow.IsOpen = true;
        };
        Svc.ClientState.Logout += Logout;
        Svc.Condition.ConditionChange += ConditionChange;
        EzCmd.Add("/autoretainer", CommandHandler, Loc.T("""
            Open plugin interface
            /ays - alias for /autoretainer
            /autoretainer e|enable → Enable plugin
            /autoretainer d|disable - Disable plugin
            /autoretainer t|toggle - toggle plugin
            /autoretainer m|multi - toggle MultiMode
            /autoretainer relog Character Name@WorldName - relog to the targeted character if configured
            /autoretainer b|browser - open venture browser
            /autoretainer expert - toggle expert settings
            /autoretainer debug - toggle debug menu and verbose output
            /autoretainer shutdown <hours> [minutes] [seconds] - schedule a game shutdown in this amount of time
            /autoretainer itemsell - begin selling items to NPC or retainer if possible
            /autoretainer het - enter nearby own house or apartment if possible
            /autoretainer reset - reset all pending tasks
            /autoretainer deliver - deliver expert delivery items
            """));
        EzCmd.Add("/ays", CommandHandler);
        Svc.Toasts.ErrorToast += Toasts_ErrorToast;
        Svc.Toasts.Toast += Toasts_Toast;
        Svc.Framework.Update += Tick;
        quickSellItems = new();
        StatisticsManager.Init();
        AutoGCHandin.Init();
        IPC.Init();
        VoyageMain.Init();

        MultiMode.Init();
        MultiModeDtr.Init();
        NotificationMasterApi = new(Svc.PluginInterface);
        ODMTaskManager = new(new(timeLimitMS: 60 * 1000, abortOnTimeout: true, showDebug: true));

        Safety.Check();

        API = new();
        ApiTest.Init();
        FPSManager.UnlockChillFrames();
        Utils.ResetEscIgnoreByWindows();
        Svc.PluginInterface.UiBuilder.Draw += FPSLimiter.FPSLimit;
        AutoCutsceneSkipper.Init(MiniTA.ProcessCutsceneSkip);
        EzSharedData.TryGet("AutoRetainer.Started", out TimeLaunched, CreationMode.CreateAndKeep, [DateTimeOffset.Now.ToUnixTimeMilliseconds()]);
        if(!C.NightModePersistent) C.NightMode = false;
        ContextMenuManager = new();
        PluginLog.Information($"AutoRetainer v{P.GetType().Assembly.GetName().Version} is ready.");
        if(!EzSharedData.TryGet<object>("AutoRetainer.WasLoaded", out _))
        {
            if(C.MultiAutoStart || C.AutoLogin != "")
            {
                MultiMode.PerformAutoStart();
            }
            if(C.DisplayOnStart)
            {
                AutoRetainerWindow.IsOpen = true;
            }
        }
        // 🔴 ECommons.IPC 的 IPCBase 預設 wrapper 是 SafeWrapper.None(例外會往外擲);
        // 我方一貫用 AnyException(靜默降級,回預設值)。這裡改回我方語意,不然「Lifestream 沒裝」
        // 會從「回 false」變成「擲例外」,行為在使用者那頭是靜默地變壞。
        // ⚠️ ECommonsIPC.X 是 lazy 屬性(field ??= new()),wrapper 在**第一次存取當下**就烘死了,
        // 所以這行必須早於任何 ECommonsIPC.* 的第一次存取。下面立刻取一次 Lifestream 強制建構,
        // 把「順序對不對」從執行期運氣變成這裡的既成事實。
        IPCBase.DefaultWrapper = SafeWrapper.AnyException;
        _ = ECommonsIPC.Lifestream;

        SingletonServiceManager.Initialize(typeof(AutoRetainerServiceManager));

    }

    private void Toasts_Toast(ref SeString message, ref ToastOptions options, ref bool isHandled)
    {
        if(Svc.Condition[ConditionFlag.OccupiedSummoningBell] && ProperOnLogin.PlayerPresent)
        {
            var text = message.GetText().Cleanup();
            //4330	57	33	0	False	リテイナーベンチャー「<Value>IntegerParameter(2)</Value> <Sheet(Item,IntegerParameter(1),0)/>」を依頼しました。
            //4330	57	33	0	False	Du hast deinen Gehilfen mit der Beschaffung von <SheetDe(Item,1,IntegerParameter(1),IntegerParameter(3),3,1)/> ( <Value>IntegerParameter(2)</Value>) beauftragt.
            //4330	57	33	0	False	Vous avez confié la tâche “<SheetFr(Item,12,IntegerParameter(1),2,1)/> ( <Value>IntegerParameter(2)</Value>)” à votre servant.
            // 台服(7.20 sqpack 實查):向僱員下達了「<Value>IntegerParameter(2)</Value>級 <Sheet(Item,IntegerParameter(1),0)/>」的探險委託。
            // 改讀遊戲自己的 LogMessage 表:取第一個巨集之前的固定文字當前綴,客戶端跑哪個語言就拿到哪個語言,
            // 不必再逐語言硬編。(台服前綴原本不在硬編名單內 ⇒ VentureBeginsAt 恆為 0,連帶讓
            //  SomethingNeedDoing 的 Lua 屬性 OfflineRetainerDataWrapper.VentureBeginsAt 也恆回 0。)
            // 🔴 讀不到時 Lang.LogMessageOpening 回的是字面備援而不是空字串 —— 空前綴會讓每一則 toast 都命中。
            if(text.StartsWithAny(Lang.LogMessageOpening(4330, "向僱員下達了").Cleanup())
                && Utils.TryGetCurrentRetainer(out var ret)
                && C.OfflineData.TryGetFirst(x => x.CID == SvcEx.PlayerState.ContentId, out var offlineData)
                && offlineData.RetainerData.TryGetFirst(x => x.Name == ret, out var offlineRetainerData))
            {
                offlineRetainerData.VentureBeginsAt = P.Time;
                DebugLog($"Recorded venture start time = {offlineRetainerData.VentureBeginsAt}");
            }
            //4578	57	33	0	False	Gil earned from market sales has been entrusted to your retainer.<If(Equal(IntegerParameter(1),1))>
            //The amount earned exceeded your retainer's gil limit. Excess gil has been discarded.<Else/></If>
            if(text.StartsWith(Svc.Data.GetExcelSheet<LogMessage>().GetRow(4578).Text.GetText(true).Cleanup()))
            {
                TaskWithdrawGil.forceCheck = true;
                DebugLog($"Forcing to check for gil");
            }
        }
    }

    private void CommandHandler(string command, string arguments)
    {
        if(arguments.EqualsIgnoreCase("debug"))
        {
            config.Verbose = !config.Verbose;
            DuoLog.Information($"Debug mode {(config.Verbose ? "enabled" : "disabled")}");
            S.NeoWindow.Reload();
        }
        else if(arguments.EqualsIgnoreCaseAny("e", "enable"))
        {
            SchedulerMain.EnablePlugin(PluginEnableReason.Auto);
        }
        else if(arguments.EqualsIgnoreCaseAny("d", "disable"))
        {
            SchedulerMain.DisablePlugin();
        }
        else if(arguments.EqualsIgnoreCaseAny("t", "toggle"))
        {
            Svc.Commands.ProcessCommand(SchedulerMain.PluginEnabled ? "/ays d" : "/ays e");
        }
        else if(arguments.EqualsIgnoreCaseAny("m", "multi"))
        {
            MultiMode.Enabled = !MultiMode.Enabled;
            MultiMode.OnMultiModeEnabled();
        }
        else if(arguments.StartsWithAny(StringComparison.OrdinalIgnoreCase, "m ", "multi "))
        {
            var arg2 = arguments.Split(" ")[1];
            if(arg2.EqualsIgnoreCaseAny("d", "disable"))
            {
                if(MultiMode.Enabled) MultiMode.Enabled = false;
            }
            else if(arg2.EqualsIgnoreCaseAny("e", "enable"))
            {
                if(!MultiMode.Enabled)
                {
                    MultiMode.Enabled = true;
                    MultiMode.OnMultiModeEnabled();
                }
            }
        }
        else if(arguments.EqualsIgnoreCaseAny("n", "night"))
        {
            C.NightMode = !C.NightMode;
            DuoLog.Information($"Night mode {(C.NightMode ? "enabled" : "disabled")}");
            if(C.NightMode)
            {
                if(!MultiMode.Enabled)
                {
                    MultiMode.Enabled = true;
                    MultiMode.OnMultiModeEnabled();
                }
            }
        }
        else if(arguments.StartsWithAny(StringComparison.OrdinalIgnoreCase, "n ", "night "))
        {
            var arg2 = arguments.Split(" ")[1];
            if(arg2.EqualsIgnoreCaseAny("d", "disable"))
            {
                C.NightMode = false;
            }
            else if(arg2.EqualsIgnoreCaseAny("e", "enable"))
            {
                C.NightMode = true;
                if(!MultiMode.Enabled)
                {
                    MultiMode.Enabled = true;
                    MultiMode.OnMultiModeEnabled();
                }
            }
            else if(arg2.EqualsIgnoreCaseAny("s", "set"))
            {
                C.NightMode = true;
            }
            DuoLog.Information($"Night mode {(C.NightMode ? "enabled" : "disabled")}");
        }
        else if(arguments.EqualsIgnoreCaseAny("s", "settings"))
        {
            S.NeoWindow.IsOpen = true;
        }
        else if(arguments.EqualsIgnoreCaseAny("b", "browser"))
        {
            VentureBrowser.IsOpen = !VentureBrowser.IsOpen;
        }
        else if(arguments.EqualsIgnoreCaseAny("l", "log"))
        {
            LogWindow.IsOpen = !LogWindow.IsOpen;
        }
        else if(arguments.StartsWith("relog "))
        {
            var target = C.OfflineData.Where(x => $"{x.Name}@{x.World}" == arguments[6..]).FirstOrDefault();
            if(target != null)
            {
                MultiMode.Relog(target, out _, RelogReason.Command);
            }
            else
            {
                Notify.Error($"Could not find target character");
            }
        }
        else if(arguments.EqualsIgnoreCase("het"))
        {
            TaskNeoHET.Enqueue(() => DuoLog.Error("Failed to find suitable house"));
        }
        else if(arguments.EqualsIgnoreCase("wet"))
        {
            if(TaskNeoHET.GetWorkshopEntrance() != null)
            {
                TaskNeoHET.TryEnterWorkshop(() => DuoLog.Error("Failed to enter workshop"));
            }
            else
            {
                TaskNeoHET.Enqueue(() => DuoLog.Error("Failed to find suitable house"), true);
            }
        }
        else if(arguments.EqualsIgnoreCaseAny("itemsell"))
        {
            if(!IsOccupied() && !P.TaskManager.IsBusy)
            {
                if(NpcSaleManager.GetValidNPC() != null && Data.GetIMSettings().IMEnableNpcSell)
                {
                    NpcSaleManager.EnqueueIfItemsPresent(true);
                }
                else if(Data.GetIMSettings().IMEnableAutoVendor && Utils.GetReachableRetainerBell(true) != null && Player.IsInHomeWorld)
                {
                    P.SkipNextEnable = true;
                    TaskInteractWithNearestBell.Enqueue(true);
                    P.TaskManager.Enqueue(() => TryGetAddonMaster<AddonMaster.RetainerList>(out var m) && m.IsAddonReady);
                    P.TaskManager.Enqueue(() =>
                    {
                        P.TaskManager.BeginStack();
                        Safe(Utils.EnqueueVendorItemsByRetainer);
                        P.TaskManager.InsertStack();
                    });
                    P.TaskManager.Enqueue(RetainerListHandlers.CloseRetainerList);
                }
            }
            else
            {
                DuoLog.Error($"No valid housing NPC or retainer bell were found, or AutoRetainer is busy, or sale function is disabled");
            }
        }
        else if(arguments.StartsWith("shutdown"))
        {
            var str = arguments.Split((char[])[' ', ',', ':', '-', '/', '.'], StringSplitOptions.RemoveEmptyEntries);
            if(str.Length <= 1)
            {
                Shutdown.ShutdownAt = 0;
                Shutdown.ForceShutdownAt = 0;
                Svc.Chat.Print("Shutdown timer cleared");
            }
            else
            {
                try
                {
                    var time = new TimeSpan();
                    time = time.Add(TimeSpan.FromHours(int.Parse(str[1])));
                    if(str.Length > 2) time = time.Add(TimeSpan.FromMinutes(int.Parse(str[2])));
                    if(str.Length > 3) time = time.Add(TimeSpan.FromSeconds(int.Parse(str[3])));
                    if(time.TotalSeconds < 10)
                    {
                        DuoLog.Error("Timer can't be less than 10 seconds");
                    }
                    else
                    {
                        Svc.Chat.Print($"Shutting down in {time}");
                        Shutdown.ShutdownAt = Environment.TickCount64 + (long)time.TotalMilliseconds;
                        Shutdown.ForceShutdownAt = Environment.TickCount64 + (long)time.TotalMilliseconds + 10 * 60 * 1000;
                    }
                }
                catch(Exception e)
                {
                    DuoLog.Error($"{e.Message}");
                    PluginLog.Error($"{e.StackTrace}");
                }
            }
        }
        else if(arguments.StartsWith("modifySoftVendorList"))
        {
            var s = C.DefaultIMSettings;
            if(s != null && int.TryParse(arguments.Split(" ")[1], out var num))
            {
                if(num > 0)
                {
                    var id = (uint)num;
                    if(!s.IMAutoVendorSoft.Contains(id))
                    {
                        s.IMAutoVendorSoft.Add(id);
                        PluginLog.Warning($"External addition to soft vendor list: {ExcelItemHelper.GetName(id)}");
                    }
                }
                else if(num < 0)
                {
                    var id = (uint)-num;
                    if(s.IMAutoVendorSoft.Contains(id))
                    {
                        s.IMAutoVendorSoft.Remove(id);
                        PluginLog.Warning($"External removal from soft vendor list: {ExcelItemHelper.GetName(id)}");
                    }
                }
            }
        }
        else if(arguments.EqualsIgnoreCase("reset"))
        {
            P.TaskManager.Abort();
            SchedulerMain.CharacterPostProcessLocked = false;
            Notify.Success("Reset completed");
        }
        else if(arguments.EqualsIgnoreCase("deliver"))
        {
            TaskDeliverItems.Enqueue();
        }
        else if(arguments.StartsWith("set"))
        {
            try
            {
                var field = arguments.Split(" ")[1];
                var value = arguments.Split(" ")[2];
                DuoLog.Information($"Attempting to set {field}={value}");
                if(C.GetFoP(field).GetType() == typeof(bool))
                {
                    C.SetFoP(field, bool.Parse(value));
                    DuoLog.Information($"Set bool {field}={value}");
                }
                else if(C.GetFoP(field).GetType() == typeof(int))
                {
                    C.SetFoP(field, int.Parse(value));
                    DuoLog.Information($"Set int {field}={value}");
                }
                else if(C.GetFoP(field).GetType() == typeof(uint))
                {
                    C.SetFoP(field, uint.Parse(value));
                    DuoLog.Information($"Set uint {field}={value}");
                }
                else if(C.GetFoP(field).GetType() == typeof(float))
                {
                    C.SetFoP(field, float.Parse(value));
                    DuoLog.Information($"Set float {field}={value}");
                }
                else if(C.GetFoP(field).GetType() == typeof(double))
                {
                    C.SetFoP(field, double.Parse(value));
                    DuoLog.Information($"Set double {field}={value}");
                }
                else if(C.GetFoP(field).GetType() == typeof(nint))
                {
                    C.SetFoP(field, nint.Parse(value));
                    DuoLog.Information($"Set nint {field}={value}");
                }
                else if(C.GetFoP(field).GetType() == typeof(long))
                {
                    C.SetFoP(field, long.Parse(value));
                    DuoLog.Information($"Set long {field}={value}");
                }
                else if(C.GetFoP(field).GetType() == typeof(ulong))
                {
                    C.SetFoP(field, ulong.Parse(value));
                    DuoLog.Information($"Set ulong {field}={value}");
                }
                else if(C.GetFoP(field).GetType() == typeof(string))
                {
                    C.SetFoP(field, value);
                    DuoLog.Information($"Set string {field}={value}");
                }
                else if(C.GetFoP(field).GetType().IsEnum)
                {
                    C.SetFoP(field, int.Parse(value));
                    DuoLog.Information($"Set enum {field}={value}");
                }
            }
            catch(Exception e)
            {
                e.LogDuo();
            }
        }
        else
        {
            AutoRetainerWindow.IsOpen = !AutoRetainerWindow.IsOpen;
        }
    }

    /// <summary>
    /// 只做一件事:每幀解除那些「窗已經不在了」的按下記號(<see cref="DialogGuards.Tick"/>)。
    /// </summary>
    /// <remarks>
    /// 🔴 刻意獨立成一個處理器、而且在 <see cref="Load"/> 的<b>最前面</b>訂閱(排在
    /// <c>TaskManager = new(...)</c> 之前),理由寫在訂閱處。原本這一行是 <see cref="Tick"/> 的第一個
    /// 敘述,但 <see cref="Tick"/> 自己排在 NeoTaskManager 的 Tick 後面,等於「先按、後解除」。
    /// </remarks>
    private void DialogGuardsTick(object _) => DialogGuards.Tick();

    private void Tick(object _)
    {
        // 🔴 解除按下記號的 DialogGuards.Tick() 已經移到 Load() 最前面獨立訂閱(DialogGuardsTick):
        //    這個 Tick 排在 NeoTaskManager 的 Tick 後面,擺在這裡等於「先按、後解除」。
        //    下面每一個模組 Tick 看到的都已經是「這一幀掃過、該解除的都解除了」的守衛狀態。
        MultiModeDtr.Tick();
        // 🔴 必須在下面兩個消費端(SchedulerMain.Tick 與僱員感知自動開鈴)之前、而且**無條件**跑,
        //    這樣兩邊看到的是同一幀的同一個答案,而且「開始讓路／恢復」兩個邊緣都各印得到一行。
        SchedulerMain.UpdateRetainerAutomationDeferral();
        if(!IPC.Suppressed)
        {
            if(SchedulerMain.PluginEnabled && Svc.Objects.LocalPlayer != null)
            {
                SchedulerMain.Tick();
                if(!C.SelectedRetainers.ContainsKey(SvcEx.PlayerState.ContentId))
                {
                    C.SelectedRetainers[SvcEx.PlayerState.ContentId] = [];
                }
            }
        }
        MiniTA.Tick();
        OfflineDataManager.Tick();
        AutoGCHandin.Tick();
        // ⚠️ 必須在 AutoGCHandin.Tick() 之後:循環會等 AutoGCHandin.Operation 落回 false 來判斷
        // 「這一輪繳完了」,先跑的話看到的是上一幀的舊值。
        GCExpertDeliveryLoop.Tick();
        MultiMode.Tick();
        NotificationHandler.Tick();
        // 「返航/探險時間到」的邊緣偵測。刻意跟 NotificationHandler 放在一起、而且無條件跑：
        // 它看的是 C.OfflineData 裡的絕對時間戳，所以不限當前角色，也不需要排程器啟用。
        TataruPraiseWatcher.Tick();
        NewYesAlreadyManager.Tick();
        AutoBuyFuelManager.Tick();
        Artisan.ArtisanTick();
        FPSManager.Tick();
        PriorityManager.Tick();
        TextAdvanceManager.Tick();
        Shutdown.Tick();
        BailoutManager.Tick();
        // Both of these recover from an aborted task queue, so they must be driven from the framework
        // update rather than from the queue they are watching.
        AutomoveManager.Tick();
        RetainerBulkOperation.Tick();
        if(Svc.Condition[ConditionFlag.OccupiedSummoningBell] && Utils.TryGetCurrentRetainer(out var name) && Utils.TryGetRetainerByName(name, out var retainer))
        {
            if(!retainer.VentureID.EqualsAny(0u, LastVentureID))
            {
                LastVentureID = retainer.VentureID;
                PluginLog.Debug($"Retainer {retainer.Name} current venture={LastVentureID}");
            }
        }
        else
        {
            if(LastVentureID != 0)
            {
                LastVentureID = 0;
                PluginLog.Debug($"Last venture ID reset");
            }
        }
        //if(C.RetryItemSearch) RetryItemSearch.Tick();
        if(SchedulerMain.PluginEnabled || MultiMode.Enabled || TaskManager.IsBusy)
        {
            if(EzThrottler.Throttle("CheckHTweaks"))
            {
                Utils.EnsureEnhancedLoginIsOff();
            }
            if(Svc.ClientState.TerritoryType == Prisons.Mordion_Gaol)
            {
                Process.GetCurrentProcess().Kill();
            }
            if(Svc.Condition[ConditionFlag.OccupiedSummoningBell])
            {
                if(!ConditionWasEnabled)
                {
                    ConditionWasEnabled = true;
                    DebugLog($"ConditionWasEnabled = true");
                }
            }
        }
        IsNextToBell = false;
        if(C.RetainerSense && Svc.Objects.LocalPlayer != null && Svc.Objects.LocalPlayer.HomeWorld.RowId == Svc.Objects.LocalPlayer.CurrentWorld.RowId)
        {
            // ⚠️ !SchedulerMain.RetainerAutomationDeferred:稀有品繳交循環在跑的時候不要自己去點鈴。
            //    這條路徑不看 SchedulerMain.PluginEnabled,所以光是擋住 SchedulerMain.Tick() 擋不到它 ——
            //    它會把 TaskInteractWithNearestBell 直接排進循環正在用的那條共用佇列。
            if(!IPC.Suppressed && !IsOccupied() && !C.OldRetainerSense && !TaskManager.IsBusy && !Utils.MultiModeOrArtisan && !Svc.Condition[ConditionFlag.InCombat] && !Svc.Condition[ConditionFlag.BoundByDuty] && !SchedulerMain.RetainerAutomationDeferred && Utils.IsAnyRetainersCompletedVenture())
            {
                var bell = Utils.GetReachableRetainerBell(true);
                if(bell == null || LastPosition != Svc.Objects.LocalPlayer.Position)
                {
                    LastPosition = Svc.Objects.LocalPlayer.Position;
                    LastMovementAt = Environment.TickCount64;
                }
                if(bell != null)
                {
                    IsNextToBell = true;
                }
                if(Environment.TickCount64 - LastMovementAt > C.RetainerSenseThreshold)
                {
                    if(bell != null)
                    {
                        IsNextToBell = true;
                        if(EzThrottler.Throttle("RetainerSense", 30000))
                        {
                            TaskInteractWithNearestBell.Enqueue();
                            TaskManager.Enqueue(() => { SchedulerMain.EnablePlugin(PluginEnableReason.Auto); return true; });
                        }
                    }
                }
            }
        }
        if(Utils.IsBusy && TryGetAddonByName<AtkUnitBase>("Trade", out var trade))
        {
            // 🔴 原本每幀無條件 Fire(-1)、零節流零狀態:拒絕之後 Trade 視窗「正在關閉中」的每一幀都再送一次,
            //    那幾幀 GetAddonByName 仍拿得到實例,再送就是攔不到的原生 AccessViolation。
            //    同一扇 Trade 只拒絕一次;窗消失後下一扇才會再被拒絕。其餘行為(不看 IsAddonReady、IsBusy 期間才做)不變。
            if(DialogGuards.TryPressOnce("Trade", (nint)trade, "DeclineTrade"))
            {
                Callback.Fire(trade, true, -1);
            }
        }
    }

    private void Toasts_ErrorToast(ref Dalamud.Game.Text.SeStringHandling.SeString message, ref bool isHandled)
    {
        if(!Svc.ClientState.IsLoggedIn)
        {
            //5800	60	8	0	False	Unable to execute command. Character is currently visiting the <Highlight>StringParameter(1)</Highlight> data center.
            //5800	60	8	0	False	他のデータセンター<Highlight>StringParameter(1)</Highlight>へ遊びに行っているため操作できません。
            //5800	60	8	0	False	Der Vorgang kann nicht ausgeführt werden, da der Charakter gerade das Datenzentrum <Highlight>StringParameter(1)</Highlight> bereist.
            //5800	60	8	0	False	Impossible d'exécuter cette commande. Le personnage se trouve dans un autre centre de traitement de données (<Highlight>StringParameter(1)</Highlight>).
            //5800	60	8	0	False	由於正前往<Highlight>StringParameter(1)</Highlight>遊玩，無法操作。（台服 exd-tc 7.20 實查）
            if(message.ToString().StartsWithAny(Lang.UnableToVisitWorld))
            {

                MultiMode.Enabled = false;
            }
        }
    }

    public void Dispose()
    {
        //if (PluginLoader.IsLoaded)
        {
            Safe(() => FFXIVInstanceMonitor.ReleaseLock());
            Safe(() => quickSellItems.Disable());
            Safe(() => quickSellItems.Dispose());
            Safe(() => Svc.PluginInterface.UiBuilder.Draw -= FPSLimiter.FPSLimit);
            Safe(() => Svc.PluginInterface.UiBuilder.Draw -= WindowSystem.Draw);
            Safe(() => Svc.ClientState.Logout -= Logout);
            Safe(() => Svc.Condition.ConditionChange -= ConditionChange);
            Safe(() => Svc.Framework.Update -= Tick);
            Safe(() => Svc.Framework.Update -= DialogGuardsTick);
            Safe(() => Svc.Toasts.ErrorToast -= Toasts_ErrorToast);
            Safe(() => Svc.Toasts.Toast -= Toasts_Toast);
            Safe(() => NewYesAlreadyManager.Unlock());
            Safe(() => TextAdvanceManager.UnlockTA());
            Safe(() => StatisticsManager.Shutdown());
            Safe(() => Memory.Dispose());
            Safe(() => IPC.Shutdown());
            Safe(() => API.Dispose());
            Safe(() => FPSManager.ForceRestore());
            Safe(() => PriorityManager.RestorePriority());
            Safe(() => VoyageMain.Shutdown());
            Safe(() => MultiModeDtr.Dispose());
            Safe(() => ContextMenuManager.Dispose());
            Safe(() => EzIpcFailureLog.Disable());
            PunishLibMain.Dispose();
            ECommonsMain.Dispose();
        }
        //PluginLoader.Dispose();
    }

    private void AddVenture(string name, uint ventureId)
    {
        if(API.Ready && API.GetOfflineCharacterData(Player.CID).RetainerData.TryGetFirst(x => x.Name == name, out var rdata))
        {
            var adata = API.GetAdditionalRetainerData(Player.CID, rdata.Name);
            if(adata.VenturePlan.List.TryGetFirst(x => x.ID == ventureId, out var v))
            {
                v.Num += 1;
            }
            else
            {
                adata.VenturePlan.List.Add(new(ventureId, 1));
            }
            API.WriteAdditionalRetainerData(Player.CID, rdata.Name, adata);
        }
    }

    private IEnumerable<string> ListRetainers()
    {
        if(API.Ready)
        {
            foreach(var x in API.GetOfflineCharacterData(Player.CID).RetainerData)
            {
                yield return x.Name;
            }
        }
    }

    internal HashSet<string> GetSelectedRetainers(ulong cid)
    {
        if(!config.SelectedRetainers.ContainsKey(cid))
        {
            config.SelectedRetainers.Add(cid, []);
        }
        return config.SelectedRetainers[cid];
    }

    internal static string LastLogMsg = string.Empty;
    internal static void DebugLog(string message)
    {
        //if (LastLogMsg != message)
        {
            PluginLog.Debug(message);
        }
    }

    public bool SkipNextEnable = false;

    private void ConditionChange(ConditionFlag flag, bool value)
    {
        if(flag == ConditionFlag.LoggingOut && value)
        {
            if(Player.Available)
            {
                PluginLog.Verbose($"Writing logout offline data...");
                OfflineDataManager.WriteOfflineData(true, true);
            }
        }
        if(flag == ConditionFlag.OccupiedSummoningBell)
        {
            OfflineDataManager.WriteOfflineData(true, true);
            if(!value)
            {
                ConditionWasEnabled = false;
                DebugLog("ConditionWasEnabled = false;");
            }
            if(!SkipNextEnable)
            {
                if(Svc.Targets.Target.IsRetainerBell())
                {
                    if(value)
                    {
                        if(Utils.MultiModeOrArtisan)
                        {
                            WasEnabled = false;
                            if(IsInteractionAutomatic)
                            {
                                IsInteractionAutomatic = false;
                                SchedulerMain.EnablePlugin(MultiMode.Enabled ? PluginEnableReason.MultiMode : PluginEnableReason.Artisan);
                            }
                        }
                        else
                        {
                            var bellBehavior = Utils.IsAnyRetainersCompletedVenture() ? C.OpenBellBehaviorWithVentures : C.OpenBellBehaviorNoVentures;
                            // CSFramework.Instance() 是 isPointer:true 的靜態位址，會合法回 null。
                            // 讀不到就當作「視窗非作用中」＝不取消開鈴動作，維持原本的行為
                            // （抑制鍵是額外的覆寫，拿不到狀態時不要擅自覆寫）。
                            var framework = CSFramework.Instance();
                            if(bellBehavior != OpenBellBehavior.Pause_AutoRetainer && IsKeyPressed(C.Suppress) && framework != null && !framework->WindowInactive)
                            {
                                bellBehavior = OpenBellBehavior.Do_nothing;
                                Notify.Info($"Open bell action cancelled");
                            }
                            if(SchedulerMain.PluginEnabled && bellBehavior == OpenBellBehavior.Pause_AutoRetainer)
                            {
                                WasEnabled = true;
                                SchedulerMain.DisablePlugin();
                            }
                            if(IsInteractionAutomatic)
                            {
                                IsInteractionAutomatic = false;
                                SchedulerMain.EnablePlugin(PluginEnableReason.Auto);
                            }
                            else
                            {
                                if(bellBehavior == OpenBellBehavior.Enable_AutoRetainer)
                                {
                                    SchedulerMain.EnablePlugin(PluginEnableReason.Access);
                                }
                                else if(bellBehavior == OpenBellBehavior.Disable_AutoRetainer)
                                {
                                    SchedulerMain.DisablePlugin();
                                }
                            }
                        }
                    }
                }
                else
                {
                    if(Svc.Targets.Target.IsRetainerBell() || Svc.Targets.PreviousTarget.IsRetainerBell())
                    {
                        if(WasEnabled)
                        {
                            DebugLog($"Enabling plugin because WasEnabled is true");
                            SchedulerMain.EnablePlugin(PluginEnableReason.Auto);
                            WasEnabled = false;
                        }
                        else if(!IsCloseActionAutomatic && C.AutoDisable && !Utils.MultiModeOrArtisan)
                        {
                            DebugLog($"Disabling plugin because AutoDisable is on");
                            SchedulerMain.DisablePlugin();
                        }
                    }
                }
            }
            SkipNextEnable = false;
            IsCloseActionAutomatic = false;
        }
        if(flag == ConditionFlag.Gathering)
        {
            VentureBrowser.Reset();
            OfflineDataManager.WriteOfflineData(true, true);
        }
    }

    private void Logout(int _, int __)
    {
        SchedulerMain.DisablePlugin();

        // 這個佇列裝的是「雇員名字」，而雇員名字只在該角色底下唯一。多角模式換角是走登出的，
        // 沒清乾淨的殘留項目會在下一個角色被按名字比對到（同名雇員在不同角色底下是可能的）。
        SchedulerMain.ClearPendingEntrustVendorPass("logout");

        if(!P.TaskManager.IsBusy)
        {
            MultiMode.LastLogin = 0;
        }

    }
}
