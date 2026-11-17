using AutoRetainer.Internal;
using AutoRetainer.Modules.Voyage;
using AutoRetainer.Scheduler.Handlers;
using AutoRetainer.Scheduler.Tasks;
using AutoRetainer.Services;
using AutoRetainerAPI.Configuration;
using Dalamud.Game;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Memory;
using Dalamud.Utility;
using ECommons.Events;
using ECommons.ExcelServices;
using ECommons.ExcelServices.TerritoryEnumeration;
using ECommons.GameHelpers;
using ECommons.IPC;
using ECommons.MathHelpers;
using ECommons.Reflection;
using ECommons.Throttlers;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using Lumina.Text.ReadOnly;
using OtterGui.Text.EndObjects;
using System.Text.RegularExpressions;
using CharaData = (string Name, ushort World);
using GrandCompany = ECommons.ExcelServices.GrandCompany;

namespace AutoRetainer.Helpers;

public static unsafe partial class Utils
{
    public static int FrameDelay => 10 + C.ExtraFrameDelay;
    /// <summary>
    /// 部隊點數。⚠️ 取不到時回 <c>0</c> —— 0 同時代表「真的沒有點數」與「讀不到」,
    /// 這與呼叫端既有語意一致(<c>OfflineDataManager</c> 本來就用 <c>!= 0</c> 當「有資料才寫入」的閘門)。
    /// 要區分兩者請改用 <see cref="TryGetFCPoints"/>。
    /// </summary>
    public static int FCPoints => TryGetFCPoints(out var points) ? points : 0;

    /// <summary>
    /// 讀部隊點數,並回報「這次到底有沒有讀到」。
    /// </summary>
    /// <remarks>
    /// 兩層都有真實的 null 路徑（AgentModule.Instance()、GetAgentByInternalId），少了判空的失敗形式是攔不到的 AccessViolationException。
    /// ⚠️ <c>+256</c> 是未文件化的原生偏移；偏移錯的失敗形式是「數字不對」,不是崩潰。
    /// </remarks>
    public static bool TryGetFCPoints(out int points)
    {
        points = 0;
        var agentModule = AgentModule.Instance();
        if(agentModule == null) return false;
        var agent = agentModule->GetAgentByInternalId(AgentId.FreeCompanyCreditShop);
        if(agent == null) return false;
        points = *(int*)((nint)agent + 256);
        return true;
    }
    public static float AnimationLock => Player.AnimationLock;

    public static uint[] WeaponsUICategories
    {
        get
        {
            field ??= [..new List<uint>
                {
                    Range(1u, 33),
                    Range(105u, 111),
                    (uint[])[84, 87, 88, 89, 96, 97, 98, 99]
                }];
            return field;
        }
    } = null;

    public static uint[] ArmorsUICategories
    {
        get
        {
            field ??= [..new List<uint>
                {
                    Range(34u, 38),
                    Range(40u, 43)
                }];
            return field;
        }
    } = null;

    /// <summary>防具＋武器的 ItemUICategory 合集,快取一份。
    ///
    /// <para>內容與現場串接 <c>[.. ArmorsUICategories, .. WeaponsUICategories]</c> 完全相同 ——
    /// 兩個來源集合不相交,而且所有使用點都只做「有沒有在裡面」的成員判定,順序無關。</para>
    ///
    /// <para>存在的理由是那個現場串接**每呼叫一次就配一個新陣列**,而存放流程一輪要對背包裡
    /// 每一件道具問一次。</para></summary>
    public static uint[] GearUICategories
    {
        get
        {
            field ??= [.. ArmorsUICategories, .. WeaponsUICategories];
            return field;
        }
    } = null;

    extension(OfflineCharacterData data)
    {
        public string NameWithWorld => $"{data.Name}@{data.World}";
        public string NameWithWorldCensored => Censor.Character(data.NameWithWorld);

        public object? GetOrderValue(RetainersVisualOrder order)
        {
            return order switch
            {
                RetainersVisualOrder.Region_JP => ExcelWorldHelper.Get(data.World)?.GetRegion() != ExcelWorldHelper.Region.JP,
                RetainersVisualOrder.Region_NA => ExcelWorldHelper.Get(data.World)?.GetRegion() != ExcelWorldHelper.Region.NA,
                RetainersVisualOrder.Region_EU => ExcelWorldHelper.Get(data.World)?.GetRegion() != ExcelWorldHelper.Region.EU,
                RetainersVisualOrder.Region_OC => ExcelWorldHelper.Get(data.World)?.GetRegion() != ExcelWorldHelper.Region.OC,
                // 台服(陸行鳥)。ExcelWorldHelper.Region 列舉沒有具名的 TW 值(公開 API 不能隨便加成員,
                // 見 ECommons ExcelWorldHelper.cs 的說明),所以直接轉型比對,做法與 GetRegionDisplayName() 一致。
                RetainersVisualOrder.Region_TW => ExcelWorldHelper.Get(data.World)?.GetRegion() != (ExcelWorldHelper.Region)8,
                RetainersVisualOrder.DataCenter => ExcelWorldHelper.Get(data.World)?.DataCenter.RowId ?? 0,
                RetainersVisualOrder.Inventory_Slots => (int)data.InventorySpace,
                RetainersVisualOrder.Ventures => (int)data.Ventures,
                RetainersVisualOrder.World => data.World,
                RetainersVisualOrder.Name => data.Name,
                _ => null
            };
        }

        public object? GetOrderValue(DeployablesVisualOrder order)
        {
            return order switch
            {
                DeployablesVisualOrder.Region_JP => ExcelWorldHelper.Get(data.World)?.GetRegion() != ExcelWorldHelper.Region.JP,
                DeployablesVisualOrder.Region_NA => ExcelWorldHelper.Get(data.World)?.GetRegion() != ExcelWorldHelper.Region.NA,
                DeployablesVisualOrder.Region_EU => ExcelWorldHelper.Get(data.World)?.GetRegion() != ExcelWorldHelper.Region.EU,
                DeployablesVisualOrder.Region_OC => ExcelWorldHelper.Get(data.World)?.GetRegion() != ExcelWorldHelper.Region.OC,
                // 台服(陸行鳥)。ExcelWorldHelper.Region 列舉沒有具名的 TW 值(公開 API 不能隨便加成員,
                // 見 ECommons ExcelWorldHelper.cs 的說明),所以直接轉型比對,做法與 GetRegionDisplayName() 一致。
                DeployablesVisualOrder.Region_TW => ExcelWorldHelper.Get(data.World)?.GetRegion() != (ExcelWorldHelper.Region)8,
                DeployablesVisualOrder.DataCenter => ExcelWorldHelper.Get(data.World)?.DataCenter.RowId ?? 0,
                DeployablesVisualOrder.Inventory_Slots => (int)data.InventorySpace,
                DeployablesVisualOrder.Ceruleum => (int)data.Ceruleum,
                DeployablesVisualOrder.Repair_Kits => (int)data.RepairKits,
                DeployablesVisualOrder.World => data.World,
                DeployablesVisualOrder.Name => data.Name,
                _ => null
            };
        }

        public bool IsLockedOut()
        {
            var now = DateTimeOffset.Now.ToUnixTimeSeconds();
            if(!AnyLockoutActive(now)) return false;
            var world = ExcelWorldHelper.Get(data.WorldOverride ?? data.World);
            if(world != null)
            {
                return now < C.LockoutTime.SafeSelect(GetRegionCached(world.Value), 0);
            }
            return false;
        }

        public bool ShouldWaitForAllWhenLoggedIn()
        {
            return C.MultiModeWorkshopConfiguration.WaitForAllLoggedIn && (C.MultiModeWorkshopConfiguration.MultiWaitForAll || data.MultiWaitForAllDeployables);
        }

        public bool GetAllowFcTeleportForRetainers()
        {
            return data.IsTeleportEnabled() && data.GetIsTeleportEnabledForRetainers() && (data.TeleportOptionsOverride.RetainersFC ?? C.GlobalTeleportOptions.RetainersFC);
        }

        public bool GetAllowPrivateTeleportForRetainers()
        {
            return data.IsTeleportEnabled() && data.GetIsTeleportEnabledForRetainers() && (data.TeleportOptionsOverride.RetainersPrivate ?? C.GlobalTeleportOptions.RetainersPrivate);
        }

        public bool GetAllowApartmentTeleportForRetainers()
        {
            return data.IsTeleportEnabled() && data.GetIsTeleportEnabledForRetainers() && (data.TeleportOptionsOverride.RetainersApartment ?? C.GlobalTeleportOptions.RetainersApartment);
        }

        public bool GetAllowFcTeleportForSubs()
        {
            return data.IsTeleportEnabled() && (data.TeleportOptionsOverride.Deployables ?? C.GlobalTeleportOptions.Deployables);
        }

        public bool IsTeleportEnabled()
        {
            return data.TeleportOptionsOverride.Enabled ?? C.GlobalTeleportOptions.Enabled;
        }

        public bool GetIsTeleportEnabledForRetainers()
        {
            return data.TeleportOptionsOverride.Retainers ?? C.GlobalTeleportOptions.Retainers;
        }

        public bool GetAreTeleportSettingsOverriden()
        {
            return data.TeleportOptionsOverride.Deployables != null
                || data.TeleportOptionsOverride.Enabled != null
                || data.TeleportOptionsOverride.Retainers != null
                || data.TeleportOptionsOverride.RetainersApartment != null
                || data.TeleportOptionsOverride.RetainersFC != null
                || data.TeleportOptionsOverride.RetainersPrivate != null;
        }

        public InventoryManagementSettings GetIMSettings(bool raw = false)
        {
            if(C.AdditionalIMSettings.TryGetFirst(x => x.GUID == data.InventoryCleanupPlan, out var plan))
            {
                if(!raw && (plan.AdditionModeProtectList || plan.AdditionModeSoftSellList || plan.AdditionModeHardSellList))
                {
                    var newPlan = plan.DSFClone();
                    if(plan.AdditionModeProtectList)
                    {
                        foreach(var x in C.DefaultIMSettings.IMProtectList)
                        {
                            if(!newPlan.IMProtectList.Contains(x))
                            {
                                newPlan.IMProtectList.Add(x);
                            }
                        }
                    }
                    if(plan.AdditionModeSoftSellList)
                    {
                        foreach(var x in C.DefaultIMSettings.IMAutoVendorSoft)
                        {
                            if(!newPlan.IMAutoVendorSoft.Contains(x))
                            {
                                newPlan.IMAutoVendorSoft.Add(x);
                            }
                        }
                    }
                    if(plan.AdditionModeHardSellList)
                    {
                        foreach(var x in C.DefaultIMSettings.IMAutoVendorHard)
                        {
                            if(!newPlan.IMAutoVendorHard.Contains(x))
                            {
                                newPlan.IMAutoVendorHard.Add(x);
                                if(C.DefaultIMSettings.IMAutoVendorHardIgnoreStack.Contains(x))
                                {
                                    newPlan.IMAutoVendorHardIgnoreStack.Add(x);
                                }
                            }
                        }
                    }
                    return newPlan;
                }
                else
                {
                    return plan;
                }
            }
            else
            {
                return C.DefaultIMSettings;
            }
        }
    }

    // 多角模式每幀對每個角色問好幾次 IsLockedOut。沒有尚未到期的鎖區時它必回 false（SafeSelect 缺鍵回 0），
    // 所以先看這個、不查世界與地區。到期條目不會自動刪除，只看 Count 不夠。
    private static bool AnyLockoutActive(long now)
    {
        var lockouts = C.LockoutTime;
        if(lockouts == null) return false;
        foreach(var x in lockouts)
        {
            if(now < x.Value) return true;
        }
        return false;
    }

    private static Lumina.Excel.ExcelSheet<WorldDCGroupType> WorldDCGroupSheet;

    /// <summary>
    /// 與 <c>world.GetRegion()</c> 結果相同，不每次新建表。
    /// 資料中心不在表裡時交回原實作（回 0，每個世界只寫一次 Error）。
    /// </summary>
    private static ExcelWorldHelper.Region GetRegionCached(World world)
    {
        var sheet = WorldDCGroupSheet ??= Svc.Data.GetExcelSheet<WorldDCGroupType>();
        if(sheet.TryGetRow(world.DataCenter.RowId, out var dcg)) return (ExcelWorldHelper.Region)dcg.Region;
        return world.GetRegion();
    }

    public static List<OfflineCharacterData> ApplyOrder<TOrder>(this List<OfflineCharacterData> source, List<TOrder> orders)
    {
        if(typeof(TOrder) == typeof(RetainersVisualOrder) && (!C.EnableRetainerSort || C.RetainersVisualOrders.Count == 0)) return source;
        if(typeof(TOrder) == typeof(DeployablesVisualOrder) && (!C.EnableDeployablesSort || C.DeployablesVisualOrders.Count == 0)) return source;
        var ascending = true;
        IOrderedEnumerable<OfflineCharacterData> ordered = null;

        foreach(var order in orders)
        {
            object selector(OfflineCharacterData data)
            {
                return order switch
                {
                    RetainersVisualOrder retainers => data.GetOrderValue(retainers),
                    DeployablesVisualOrder deployables => data.GetOrderValue(deployables),
                    _ => null
                };
            }

            if(ordered == null)
            {
                if(ascending)
                {
                    ordered = source.OrderBy(selector);
                }
                else
                {
                    ordered = source.OrderByDescending(selector);
                }
            }
            else
            {
                if(ascending)
                {
                    ordered = ordered.ThenBy(selector);
                }
                else
                {
                    ordered = ordered.ThenByDescending(selector);
                }
            }
        }

        return ordered?.ToList() ?? [.. source];
    }

    extension(GCExchangePlan plan)
    {
        public string DisplayName
        {
            get
            {
                if(plan.Name != "") return plan.Name;
                var index = C.AdditionalGCExchangePlans.IndexOf(plan);
                if(index != -1) return $"Plan {index + 1}";
                return $"Plan {plan.GUID.ToString().Split("-")[0]}";
            }
        }

        public void Validate()
        {
            foreach(var x in plan.Items)
            {
                if(!SharedGCExchangeListings.ContainsKey(x.ItemID))
                {
                    new TickScheduler(() => plan.Items.Remove(x));
                }
                if(x.Data.ValueNullable != null && x.Data.Value.IsUnique) x.Quantity.ValidateRange(0, 1);
            }
        }
    }

    extension(InventoryManagementSettings plan)
    {
        public string DisplayName
        {
            get
            {
                if(plan.Name != "") return plan.Name;
                var index = C.AdditionalIMSettings.IndexOf(plan);
                if(index != -1) return $"Plan {index + 1}";
                return $"Plan {plan.GUID.ToString().Split("-")[0]}";
            }
        }
    }

    // 只給框架執行緒用。內容只取決於遊戲表（SharedGCExchangeListings 建一次後不再改），所以每個軍團建一次就好；
    // 呼叫端只讀，不可修改回傳的字典。lambda 放在另一個方法裡，快取命中時不配置閉包。
    private static readonly Dictionary<GrandCompany, Dictionary<uint, GCExchangeListingMetadata>> AvailableSharedExchangeListings = [];

    public static readonly string[] GCRanks = [
        "",
        "Private Third Class",
        "Private Second Class",
        "Private First Class",
        "Corporal",
        "Sergeant Third Class",
        "Sergeant Second Class",
        "Sergeant First Class",
        "Chief Sergeant",
        "Second Lieutenant",
        "First Lieutenant",
        "Captain",
        "Second Commander",
        "First Commander",
        "High Commander",
        "Rear Marshal",
        "Vice Marshal",
        "Marshal",
        "Grand Marshal",
        "Champion",
    ];

    public static bool ShouldSkipNPCVendor()
    {
        if(!Data.GetIMSettings().IMSkipVendorIfRetainer) return false;
        if(!Data.GetIMSettings().IMEnableAutoVendor) return false;
        if(C.MultiModeType == MultiModeType.Submersibles) return false;
        if(Data == null) return false;
        if(!Data.Enabled) return false;
        if(Data.GetEnabledRetainers().Length == 0) return false;
        return true;
    }

    private static bool IsNullOrEmpty(this string s)
    {
        return GenericHelpers.IsNullOrEmpty(s);
    }

    public static void EnsureEnhancedLoginIsOff()
    {
        /*try
        {
            if(Svc.PluginInterface.InstalledPlugins.Any(x => x.InternalName == "HaselTweaks" && x.IsLoaded))
            {
                if(DalamudReflector.TryGetDalamudPlugin("HaselTweaks", out var instance, out var context, false, true))
                {
                    var configWindow = ReflectionHelper.CallStatic(context.Assemblies, "HaselCommon.Service", [], "Get", ["HaselTweaks.Windows.PluginWindow"], []);
                    var tweaks = (System.Collections.IEnumerable)configWindow.GetFoP("Tweaks");
                    foreach(var x in tweaks)
                    {
                        if(x.GetFoP<string>("InternalName") == "EnhancedLoginLogout" && x.GetFoP<int>("Status") == 5)
                        {
                            configWindow.GetFoP("TweakManager").Call("UserDisableTweak", [x], true);
                            new PopupWindow(() =>
                            {
                                ImGuiEx.Text($"""
                                    Enhanced Login/Logout from HaselTweaks plugin has been detected.
                                    It is not compatible with AutoRetainer and has been disabled.
                                    """);
                            });
                        }
                    }
                }
            }
        }
        catch(Exception e)
        {
            e.Log();
        }*/
    }



    public static void EnqueueVendorItemsByRetainer()
    {
        var retainers = GameRetainerManager.Retainers;
        for(var i = 0; i < retainers.Count; i++)
        {
            var ret = retainers[i];
            if(ret.Available)
            {
                P.TaskManager.Enqueue(() => RetainerListHandlers.SelectRetainerByName(ret.Name.ToString()));
                TaskVendorItems.Enqueue();

                if(C.RetainerMenuDelay > 0)
                {
                    TaskWaitSelectString.Enqueue(C.RetainerMenuDelay);
                }
                P.TaskManager.Enqueue(RetainerHandlers.SelectQuit);
                P.TaskManager.Enqueue(RetainerHandlers.ConfirmCantBuyback);
                break;
            }
        }
    }

    public static long GetRemainingSessionMiliSeconds()
    {
        return P.TimeLaunched[0] + 3 * 24 * 60 * 60 * 1000 - DateTimeOffset.Now.ToUnixTimeMilliseconds();
    }

    public static bool? WaitForScreen()
    {
        return IsScreenReady();
    }

    internal static void ExtraLog(string s)
    {
        // 要使用者回報的診斷:開了「額外記錄」就是想看到這些訊息,所以寫 Information 不寫 Debug。
        if(C.ExtraDebug) PluginLog.Information(s);
    }

    internal static float Random { get; private set; } = 1f;
    internal static void RegenerateRandom()
    {
        Random = (float)new Random().NextDouble();
        DebugLog($"Random regenerated: {Random}");
    }

    internal static bool MultiModeOrArtisan => MultiMode.Active || (SchedulerMain.PluginEnabled && SchedulerMain.Reason == PluginEnableReason.Artisan);
    internal static bool IsBusy => P.TaskManager.IsBusy || AutoGCHandin.Operation || LifestreamIsBusy();

    private static Dalamud.Plugin.Ipc.ICallGateSubscriber<bool> LifestreamIsBusyGate;

    /// <summary>
    /// 等同 <c>ECommonsIPC.Lifestream.IsBusy()</c>（沒有提供端時 SafeWrapper 也是回 false），但先判 HasFunction：
    /// 沒裝 Lifestream 時不必每次擲 IpcNotReadyError 再吞掉——IsBusy 每幀被問好幾次。
    /// </summary>
    internal static bool LifestreamIsBusy()
    {
        LifestreamIsBusyGate ??= Svc.PluginInterface.GetIpcSubscriber<bool>("Lifestream.IsBusy");
        return LifestreamIsBusyGate.HasFunction && ECommonsIPC.Lifestream.IsBusy();
    }
    internal static AtkValue ZeroAtkValue = new() { Type = 0, Int = 0 };

    internal static float GetGCSealMultiplier()
    {
        var ret = 1f;
        if(Player.Available)
        {
            if(Player.Object.StatusList.TryGetFirst(x => x.StatusId == 414, out var s)) ret = 1f + (float)s.Param / 100f;
            if(Player.Object.StatusList.Any(x => x.StatusId == 1078)) ret = 1.15f;
        }
        return ret > 1f ? ret : 1f;
    }

    internal static string FancyDigits(this int n)
    {
        return n.ToString().ReplaceByChar(Lang.Digits.Normal, Lang.Digits.GameFont);
    }

    // 本 pin 的 GetExcelSheet 每次 new 一個 ExcelSheet；多角模式排序時對每個僱員都會呼叫。
    private static Lumina.Excel.ExcelSheet<ClassJob> ClassJobSheet;

    internal static int GetJobLevel(this OfflineCharacterData data, uint job)
    {
        var d = (ClassJobSheet ??= Svc.Data.GetExcelSheet<ClassJob>()).GetRowOrDefault(job);
        if(d != null)
        {
            try
            {
                return data.ClassJobLevelArray.SafeSelect(d.Value.ExpArrayIndex);
            }
            catch(Exception) { }
        }
        return 0;
    }

    internal static bool CanAutoLogin()
    {
        return CanAutoLoginFromTaskManager() && !P.TaskManager.IsBusy;
    }

    internal static bool CanAutoLoginFromTaskManager()
    {
        return !Svc.ClientState.IsLoggedIn
            && !Svc.Condition.Any()
            && IsTitleScreenReady();
    }

    internal static bool IsTitleScreenReady()
    {
        if(!TryGetAddonByName<AtkUnitBase>("_TitleMenu", out var title) || !IsAddonReady(title)) return false;
        // 兩個索引都走 GetNodeSafe（上界與元素都驗）：節點數不到 8 時 NodeList[7] 讀到的是相鄰記憶體，
        // 拿它呼叫 IsVisible() 是 try/catch 攔不到的 AVE。取不到＝還沒就緒，呼叫端下一幀再問。
        var node7 = GetNodeSafe(&title->UldManager, 7);
        var node3 = GetNodeSafe(&title->UldManager, 3);
        if(node7 == null || node3 == null) return false;

        return node7->IsVisible()
            && node3->Color.A == 0xFF
            && !TryGetAddonByName<AtkUnitBase>("TitleDCWorldMap", out _)
            && !TryGetAddonByName<AtkUnitBase>("TitleConnect", out _);
    }

    internal static DateTime DateFromTimeStamp(uint timeStamp)
    {
        const long timeFromEpoch = 62135596800;
        return timeStamp == 0u
            ? DateTime.MinValue
            : new DateTime((timeStamp + timeFromEpoch) * TimeSpan.TicksPerSecond, DateTimeKind.Utc);
    }

    public static string UpperCaseStr(ReadOnlySeString s, sbyte article = 0)
    {
        if(article == 1)
            return s.ToDalamudString().ToString();

        var sb = new StringBuilder(s.ToDalamudString().ToString());
        var lastSpace = true;
        for(var i = 0; i < sb.Length; ++i)
        {
            if(sb[i] == ' ')
            {
                lastSpace = true;
            }
            else if(lastSpace)
            {
                lastSpace = false;
                sb[i] = char.ToUpperInvariant(sb[i]);
            }
        }

        return sb.ToString();
    }

    internal static bool GenericThrottle => FrameThrottler.Throttle("AutoRetainerGenericThrottle", Utils.FrameDelay);
    /// <summary>此刻呼叫 <see cref="GenericThrottle"/> 會不會放行；只看、不消耗。</summary>
    internal static bool GenericThrottleOpen => FrameThrottler.Check("AutoRetainerGenericThrottle");
    internal static void RethrottleGeneric(int num)
    {
        FrameThrottler.Throttle("AutoRetainerGenericThrottle", num, true);
    }
    internal static void RethrottleGeneric()
    {
        FrameThrottler.Throttle("AutoRetainerGenericThrottle", Utils.FrameDelay, true);
    }

    private enum EntryMatch { Single, Many, Predicate }

    // 只給框架執行緒用（TrySelectSpecificEntry 的呼叫端都是任務回呼）：第 i 項上一次讀到的位元組與解出的文字。
    private static byte[][] SelectStringEntryBytes = [];
    private static string[] SelectStringEntryTexts = [];

    internal static void TryNotify(string s)
    {
        if(DalamudReflector.TryGetDalamudPlugin("NotificationMaster", out var instance, true, true))
        {
            Safe(delegate
            {
                instance.GetType().Assembly.GetType("NotificationMaster.TrayIconManager", true).GetMethod("ShowToast").Invoke(null, new object[] { s, P.Name });
            }, true);
        }
    }

    private static byte[] RetainerObjectNameBytes = [];
    private static string RetainerObjectName;

    internal static void ResetEscIgnoreByWindows()
    {
        P.SubmarinePointPlanUI.RespectCloseHotkey = !C.IgnoreEsc;
        P.SubmarineUnlockPlanUI.RespectCloseHotkey = !C.IgnoreEsc;
        P.AutoRetainerWindow.RespectCloseHotkey = !C.IgnoreEsc;
        P.VenturePlanner.RespectCloseHotkey = !C.IgnoreEsc;
        P.VentureBrowser.RespectCloseHotkey = !C.IgnoreEsc;
        P.LogWindow.RespectCloseHotkey = !C.IgnoreEsc;
    }

    internal static string ToTimeString(long seconds)
    {
        var t = TimeSpan.FromSeconds(seconds);
        var d = ":";
        return $"{t.Hours:D2}{d}{t.Minutes:D2}{d}{t.Seconds:D2}";
    }

    private const int ObjectNameBytes = 64;
}
