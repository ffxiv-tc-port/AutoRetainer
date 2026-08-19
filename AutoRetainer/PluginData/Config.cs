using AutoRetainerAPI.Configuration;
using ECommons.Configuration;
using ECommons.ExcelServices;
using ECommons.Interop;

namespace AutoRetainer.PluginData;

[Serializable]
internal unsafe class Config
{
    public string CensorSeed = Guid.NewGuid().ToString();
    public Dictionary<ulong, HashSet<string>> SelectedRetainers = [];
    public bool EnableAssigningQuickExploration = false;
    public bool Verbose = false;
    public List<OfflineCharacterData> OfflineData = [];
    //public bool MultipleServiceAccounts = false;
    public bool NoNames = false;
    public int UnsyncCompensation = -5;
    public bool StatsUnifyHQ = false;
    public bool RecordStats = true;
    public bool AutoGCContinuation = false;
    public HashSet<ulong> WhitelistedAccounts = [];

    public bool ShouldSerializeEnableAutoGCHandin()
    {
        return false;
    }

    public bool GCHandinNotify = false;
    /// <summary>Grand Company handin: how long to wait for the item list row count to change after the
    /// refresh event was sent, before giving up on the active refresh and falling back to waiting for the
    /// game to rebuild the list on its own. This is a fuse, not the pacing source - the flow advances as
    /// soon as the row count changes, no matter who caused it. Do not set it near the fallback duration
    /// (~0.56s measured): timing out makes the plugin rescan a list that may still be stale, which can pick
    /// the item that was just handed in and abort the whole run. See AutoGCHandin.</summary>
    public int GCHandinListTimeoutMs = 1500;
    /// <summary>Grand Company handin: if the row count has not changed this long after a refresh event was
    /// sent, send another one (once the list addon is fully ready). Capped at 3 sends per item.</summary>
    public int GCHandinRefreshRetryMs = 150;

    #region Expert delivery loop

    // 稀有品繳交循環。⚠️ 這個功能**沒有啟用旗標**:只有按下按鈕才會跑,沒有任何事件觸發它,
    // 所以「預設狀態」就是「閒置」。下面全部是行為參數,不是開關。

    /// <summary>Only retainers carrying this entrust plan are visited by the expert delivery loop.
    /// <see cref="Guid.Empty"/> means nothing is selected, which the loop treats as "no retainers to visit"
    /// and stops - deliberately not "visit everyone".</summary>
    public Guid ExpertDeliveryLoopEntrustPlan = Guid.Empty;

    /// <summary>Pick retainers from <see cref="ExpertDeliveryLoopRetainerNames"/> instead of by entrust plan.</summary>
    public bool ExpertDeliveryLoopManualRetainers = false;

    /// <summary>Retainer names used when <see cref="ExpertDeliveryLoopManualRetainers"/> is on.
    /// 🔴 這是**第一版留下來的跨角色共用清單**,現在的角色是「還沒被按角色設定過的角色所使用的預設值」。
    /// 新的寫入一律進 <see cref="ExpertDeliveryLoopPerCharacter"/>;這個欄位刻意不清空也不刪除 ——
    /// 既有使用者的設定要無損留著,而且它對其他角色是無害的(名字對不上該角色的僱員就自然不會被選中)。</summary>
    public List<string> ExpertDeliveryLoopRetainerNames = [];

    /// <summary>Per-character overrides for the expert delivery loop, keyed by CID.
    /// 每個欄位都有「沒設定」的表示法,沒設定就退回上面那些全域值。詳見 <see cref="ExpertDeliveryLoopCharacterConfig"/>。</summary>
    public Dictionary<ulong, ExpertDeliveryLoopCharacterConfig> ExpertDeliveryLoopPerCharacter = [];

    /// <summary>Run the expert delivery loop across several characters, logging out and back in between them.
    /// ⚠️ 一樣沒有自動觸發:這只是決定「按下按鈕之後要跑幾個角色」。</summary>
    public bool ExpertDeliveryLoopMultiCharacter = false;

    /// <summary>CIDs the multi-character run visits, in the order they appear in <see cref="OfflineData"/>.
    /// 🔴 預設空清單 ＝ 多角色模式打開也不會跑 —— 「還沒挑」與「要跑全部」是兩回事,
    /// 而後者會在使用者還沒看清楚之前就開始換角色。</summary>
    public List<ulong> ExpertDeliveryLoopCharacters = [];

    /// <summary>Stop retrieving once the player's own bags are down to this many free slots, and go hand in
    /// what has been collected instead. ⚠️ The retrieve core independently refuses to go below
    /// <see cref="MultiMinInventorySlots"/>, so the reserve that actually applies is the larger of the two.</summary>
    public int ExpertDeliveryLoopReservedSlots = 5;

    /// <summary>Use a Priority Seal Allowance (item 14946) when no seal bonus is active.</summary>
    public bool ExpertDeliveryLoopUseSealAllowance = true;

    /// <summary>Stop the whole loop when a seal bonus is wanted but none could be applied. Off = carry on
    /// without the bonus, which is what someone who ran out mid-run almost always wants.</summary>
    public bool ExpertDeliveryLoopStopWithoutSealBonus = false;

    /// <summary>Lifestream teleport-panel favourite the loop travels to before looking for a summoning bell.
    /// 0 = not set, and then the loop only ever works with a bell that is already in reach.
    /// 🔴 This is deliberately the *only* way the loop travels to a bell. The generic-travel-command fallback
    /// that used to live here was removed: it lands wherever that command's own default is (measured: Ul'dah),
    /// and the flow then hunts for a bell in the wrong city. Stopping with an instruction beats travelling
    /// somewhere nobody asked for.</summary>
    public uint ExpertDeliveryLoopBellFavoriteId = 0;
    public byte ExpertDeliveryLoopBellFavoriteSub = 0;
    /// <summary>Display name of the chosen favourite, remembered so the UI can still name it when Lifestream
    /// is not loaded or the favourite has been removed.</summary>
    public string ExpertDeliveryLoopBellFavoriteName = "";

    /// <summary>Lifestream favourite the loop travels to before handing in, instead of letting AutoRetainer's
    /// own delivery flow pick the route. 0 = use the built-in flow.</summary>
    public uint ExpertDeliveryLoopGCFavoriteId = 0;
    public byte ExpertDeliveryLoopGCFavoriteSub = 0;
    public string ExpertDeliveryLoopGCFavoriteName = "";

    /// <summary>How long one handin round may take before the loop gives up. The handin itself is gated on
    /// AutoRetainer no longer being busy; this is only the fuse for "it never became not-busy".</summary>
    public int ExpertDeliveryLoopHandinTimeoutMinutes = 10;

    /// <summary>How long one character switch may take before the multi-character run gives up.
    /// ⚠️ 這段包含登出、標題畫面、選角、登入排隊與登入後的場景安定延遲 —— 世界排隊時它可以很長,
    /// 所以預設放寬到十分鐘,而不是照「換個畫面應該幾秒」去抓。</summary>
    public int ExpertDeliveryLoopRelogTimeoutMinutes = 10;

    /// <summary>Tray notification when a multi-character run stops early - on an error or by hand.
    /// <para>🔴 預設**開**,而且刻意不沿用 <see cref="GCHandinNotify"/>(完成時通知,預設關):
    /// 「整批做完了」與「一批五個角色死在第二個」是兩件重要程度完全不同的事。後者不出聲的話,
    /// 使用者要半小時後回來才發現,而那時候已經分不出是哪一段出的事 —— 這是這條流程最痛的情境。</para>
    /// <para>⚠️ 整批完成的通知仍然走 <see cref="GCHandinNotify"/>,這個開關不影響它。
    /// 兩者一樣都需要 NotificationMaster 才會真的響。</para></summary>
    public bool ExpertDeliveryLoopNotifyOnFailure = true;

    #endregion

    internal bool BypassSanctuaryCheck = false;
    public bool MultiHETOnEnable = true;
    public bool UseServerTime = true;
    public bool NoTheme = false;
    public Dictionary<string, AdditionalRetainerData> AdditionalData = [];
    public bool AutoDisable = true;
    public List<(ulong CID, string Name)> Blacklist = [];
    public bool HideOverlayIcons = false;
    public bool UnsafeProtection = false;
    public bool CharEqualize = false;
    public bool LongestVentureFirst = false;
    public bool CappedLevelsLast = false;
    public bool TimerAllowNegative = false;
    public bool MarketCooldownOverlay = false;

    public bool LoginOverlay = false;
    public float LoginOverlayScale = 1f;
    public float LoginOverlayBPadding = 1.35f;
    public bool LoginOverlayAllSearch = false;

    public OpenBellBehavior OpenBellBehaviorNoVentures = OpenBellBehavior.Enable_AutoRetainer;
    public OpenBellBehavior OpenBellBehaviorWithVentures = OpenBellBehavior.Enable_AutoRetainer;
    public TaskCompletedBehavior TaskCompletedBehaviorAuto = TaskCompletedBehavior.Stay_in_retainer_list_and_keep_plugin_enabled;
    public TaskCompletedBehavior TaskCompletedBehaviorManual = TaskCompletedBehavior.Stay_in_retainer_list_and_keep_plugin_enabled;
    public TaskCompletedBehavior TaskCompletedBehaviorAccess = TaskCompletedBehavior.Stay_in_retainer_list_and_keep_plugin_enabled;
    //public bool AutoPause = true;
    public bool Stay5 = true;
    public bool NoCurrentCharaOnTop = false;

    public int ExtraFrameDelay = 0;

    public bool _dontReassign = false;
    public bool OldRetainerSense = false;
    public bool RetainerSense = false;
    public int RetainerSenseThreshold = 10000;
    public bool MultiModeUIBar = false;
    public bool UIBar = true;

    public LimitedKeys Suppress = LimitedKeys.LeftControlKey;
    public LimitedKeys TempCollectB = LimitedKeys.LeftShiftKey;

    public int RetainerMenuDelay = 0;
    public List<VenturePlan> SavedPlans = [];
    public bool MultiWaitOnLoginScreen = false;
    public UnavailableVentureDisplay UnavailableVentureDisplay = UnavailableVentureDisplay.Hide;

    public bool ShowAdditionalInfo = true;
    public bool RetryItemSearch = false;
    public bool ArtisanIntegration = false;
    public bool DisplayMMType = false;
    public List<SubmarineUnlockPlan> SubmarineUnlockPlans = [];
    public bool HideAirships = false;
    public int DisableRetainerVesselReturn = 0;
    public List<SubmarinePointPlan> SubmarinePointPlans = [];
    /// <summary>
    /// 這些 GUID 的點計畫在出航前會先算航行距離，跑不到的點從清單尾端往前砍掉。
    /// 🔴 刻意用「新增鍵 + 預設空集合」而不是在 SubmarinePointPlan 上改預設值：
    /// EzConfig 反序列化是 ObjectCreationHandling.Replace，既有使用者的 JSON 只要有那個鍵就會
    /// 覆蓋欄位初始值，所以「改預設值」對既有使用者一律無效且無聲。空集合＝所有既有計畫維持原行為。
    /// </summary>
    public HashSet<string> SubmarinePointPlansTrimToRange = [];
    public int MultiMinInventorySlots = 2;
    public bool IgnoreEsc = false;

    public int UIWarningRetSlotNum = 20;
    public int UIWarningRetVentureNum = 50;
    public int UIWarningDepTanksNum = 300;
    public int UIWarningDepRepairNum = 100;
    public int UIWarningDepSlotNum = 20;
    public int TargetMSPTIdle = 0;
    public int TargetMSPTRunning = 0;
    public bool NoFPSLockWhenActive = true;
    public bool ExtraFPSLockRange = false;
    public bool FpsLockOnlyShutdownTimer = false;
    public bool ShutdownMakesNightMode = false;

    public bool ShowDeployables = false;
    public int BailoutTimeout = 5;
    public bool EnableBailout = true;
    public bool EnableCharaSelectBailout = true;

    public bool NightMode = false;
    public bool NightModePersistent = false;
    public bool ShowNightMode = false;
    public bool NightModeRetainers = false;
    public bool NightModeDeployables = true;
    internal bool NightModeFPSLimit = true;

    internal bool ExtraDebug = false;

    public bool OldStatusIcons = false;
    public int MinGilDisplay = 10000;
    public bool GilOnlyChars = false;

    public bool MultiAutoStart = false;
    public string AutoLogin = "";
    public int AutoLoginDelay = 10;
    public int PostLoginSceneSettleDelay = 0;
    public bool MultiDisableOnRelog = false;
    public bool MultiNoPreferredReset = false;
    public bool MultiPreferredCharLast = true;
    public bool VoyageDisableCalcParallel = false;
    public bool VoyageDisableCalcMultithreading = false;
    public Dictionary<ulong, FCData> FCData = [];
    public bool UpdateStaleFCData = false;
    public bool DisplayOnlyWalletFC = false;

    public bool LeastMBSFirst = false;

    public string DefaultSubmarineUnlockPlan = "";
    public bool AcceptedDisclamer = false;
    public bool AllowManualPostprocess = false;
    public bool AllowSimpleTeleport = false;

    public List<EntrustPlan> EntrustPlans = [];
    public bool DontLogout = false;

    public TeleportOptions GlobalTeleportOptions = new();
    public bool SharedHET = false;
    public bool SkipItemConfirmations = false;
    public ulong LastLoggedInChara = 0;

    internal bool DontReassign
    {
        get
        {
            if(_dontReassign) return true;
            if(C.TempCollectB == LimitedKeys.None || !IsKeyPressed(C.TempCollectB)) return false;
            // CSFramework.Instance() 是 isPointer:true 的靜態位址，會合法回 null。
            // 讀不到就當作「視窗非作用中」＝不啟用暫停指派。
            var framework = CSFramework.Instance();
            return framework != null && !framework->WindowInactive;
        }
        set
        {
            _dontReassign = value;
        }
    }

    public LimitedKeys SellKey = LimitedKeys.None;
    public LimitedKeys EntrustKey = LimitedKeys.None;
    public LimitedKeys RetrieveKey = LimitedKeys.None;
    public LimitedKeys SellMarketKey = LimitedKeys.None;

    // 「hover 道具 ＋ 按住修飾鍵 ＝ 立刻改清單」的快捷鍵。背包清理(FastAddition)與存放管理(EntrustManager)共用，
    // 兩者不會同時被繪製，而這兩個功能的觸發條件本來就包含「該區塊正在顯示」。
    // 🔴 預設值＝改成可設定之前的硬編值(Shift/Ctrl/Alt)，升級的使用者不會感覺到差別。
    // 🔴 None ＝ 停用該動作，不是「不按任何鍵就觸發」——後者會變成 hover 到就進清單。
    //    語意由 UIUtils.IsHotkeyHeld 保證(它對 None 一律回 false)。
    public LimitedKeys FastListAddKey = LimitedKeys.LeftShiftKey;
    public LimitedKeys FastListAddHardKey = LimitedKeys.LeftControlKey;
    public LimitedKeys FastListRemoveKey = LimitedKeys.LeftAltKey;

    // 「立即丟棄」按鈕的第二道確認鍵(原本硬編 CTRL)。
    // 🔴 None ＝ 停用該按鈕。破壞性且不可買回的操作不允許退化成「不按任何鍵就能一鍵按下」。
    public LimitedKeys DiscardNowKey = LimitedKeys.LeftControlKey;

    public bool NotifyEnableOverlay = false;
    public bool NotifyCombatDutyNoDisplay = true;
    public bool NotifyIncludeAllChara = true;
    public bool NotifyIgnoreNoMultiMode = false;
    public bool NotifyDisplayInChatX = false;
    public bool NotifyDeskopToast = false;
    public bool NotifyFlashTaskbar = false;
    public bool NotifyNoToastWhenRunning = true;
    public bool UnlockFPS = true;
    public bool UnlockFPSUnlimited = false;
    public bool UnlockFPSChillFrames = false;

    public bool ManipulatePriority = false;

    public bool SubsAutoResend2 = true;
    //public bool SubsAutoRepair = true;
    //public bool SubsOnlyFinalize = false;
    //public bool SubsAutoEnable = false;
    //public bool SubsRepairFinalize = true;
    public MultiModeType MultiModeType = MultiModeType.Everything;
    public bool NoErrorCheckPlanner2 = true;
    public WorkshopFailAction FailureNoFuel = WorkshopFailAction.ExcludeChar;
    public WorkshopFailAction FailureNoRepair = WorkshopFailAction.ExcludeVessel;
    public WorkshopFailAction FailureNoInventory = WorkshopFailAction.ExcludeChar;
    public WorkshopFailAction FailureGeneric = WorkshopFailAction.StopPlugin;
    internal bool SimpleTweaksCompat = true;
    public bool FinalizeBeforeResend = false;
    public bool AlertNotAllEnabled = true;
    public bool AlertNotDeployed = true;
    public List<UnoptimalVesselConfiguration> UnoptimalVesselConfigurations = [];

    public MultiModeCommonConfiguration MultiModeRetainerConfiguration = new()
    {
        AdvanceTimer = 60,
        MultiWaitForAll = false,
    };
    public MultiModeCommonConfiguration MultiModeWorkshopConfiguration = new()
    {
        MultiWaitForAll = false,
        AdvanceTimer = 120,
    };

    public List<LevelAndPartsData> LevelAndPartsData = [];
    public bool EnableAutomaticSubRegistration = false;
    public bool EnableAutomaticComponentsAndPlanChange = false;

    public bool StatusBarMSI = false;
    public int StatusBarIconWidth = 96;

    [Obsolete] public bool IMEnableCofferAutoOpen = false;
    [Obsolete] public bool IMEnableAutoVendor = false;
    [Obsolete] public bool IMEnableContextMenu = false;
    [Obsolete] public bool IMSkipVendorIfRetainer = false;
    [Obsolete] public List<uint> IMAutoVendorHard = [];
    [Obsolete] public List<uint> IMAutoVendorHardIgnoreStack = [];
    [Obsolete] public List<uint> IMAutoVendorSoft = [];
    [Obsolete] public List<uint> IMProtectList = [];
    [Obsolete] public int IMAutoVendorHardStackLimit = 20;
    [Obsolete] public bool IMDry = false;
    [Obsolete] public bool IMEnableItemDesynthesis = false;
    [Obsolete] public bool IMEnableNpcSell = false;
    [Obsolete] public bool AllowSellFromArmory = false;

    public InventoryManagementSettings DefaultIMSettings = new();
    public List<InventoryManagementSettings> AdditionalIMSettings = [];
    public bool IMMigrated = false;

    public Vector2 WindowSize;
    public Vector2 WindowPos;
    public bool PinWindow = false;
    public bool DisplayOnStart = false;

    public bool ResolveConnectionErrors = false;
    public int ConnectionErrorsRetry = 10;
    public bool ConnectionErrorsBlacklist = true;
    public bool EnableEntrustManager = true;
    public bool EnableEntrustChat = false;
    /// <summary>Minimum spacing between two entrust commands, in milliseconds. This is a floor, not the
    /// pacing source - the flow already waits for each item to actually leave the inventory before sending
    /// the next one, so lowering this does not allow two commands to be in flight at once. See
    /// TaskEntrustDuplicates for the measurements the default is derived from.</summary>
    public int EntrustIntervalMS = 150;

    public bool HETWhenDisabled = false;
    public bool UseTitleScreenButton = false;
    public bool NoCharaSearch = false;
    public bool NoTeleportHetWhenNextToBell = false;
    public bool NoGradient = false;
    public bool No2ndInstanceNotify = false;

    public bool FCChestGilCheck = false;
    public int FCChestGilCheckCd = 24;
    public Dictionary<ulong, long> FCChestGilCheckTimes = [];

    public bool AutoBuyFuelEnabled = false;
    public int AutoBuyFuelThreshold = 500;
    public int AutoBuyFuelTarget = 999;
    public Dictionary<ulong, long> AutoBuyFuelCheckTimes = [];
    public Dictionary<ExcelWorldHelper.Region, long> LockoutTime = [];
    public GCExchangePlan DefaultGCExchangePlan = new();
    public List<GCExchangePlan> AdditionalGCExchangePlans = [];

    public bool EnableRetainerSort = false;
    public List<RetainersVisualOrder> RetainersVisualOrders = [];
    public bool EnableDeployablesSort = false;
    public List<DeployablesVisualOrder> DeployablesVisualOrders = [];
}
