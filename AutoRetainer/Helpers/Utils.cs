using AutoRetainer.Internal;
using AutoRetainer.Modules.Voyage;
using AutoRetainer.Scheduler.Handlers;
using AutoRetainer.Scheduler.Tasks;
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

public static unsafe class Utils
{
    /// <summary>
    /// 安全地讀取按鈕的啟用狀態，無法判定時回傳 null。
    /// <para>
    /// 🔴 ClientStructs 的 <c>AtkComponentButton.IsEnabled</c> 實作是
    /// <c>AtkComponentBase.OwnerNode-&gt;AtkResNode.NodeFlags.HasFlag(...)</c>，
    /// 對 <c>OwnerNode</c> <b>零 null 檢查</b>。按鈕還沒建構完成（或已被遊戲拆掉）時
    /// <c>OwnerNode</c> 是 null，直接讀 <c>IsEnabled</c> 會丟 AccessViolationException。
    /// AVE 在 .NET Core 是 corrupted-state exception，<b><c>try/catch</c> 攔不到</b>，
    /// 結果是整個遊戲當場崩潰。
    /// </para>
    /// <para>
    /// ⚠️ <c>AtkComponentBase</c> 有<b>兩個</b>指標欄位：<c>AtkResNode</c>(0xA0) 與 <c>OwnerNode</c>(0xA8)。
    /// <c>IsEnabled</c> 解的是 <c>OwnerNode</c>，所以檢查 <c>AtkResNode</c> <b>不算守衛</b>。
    /// </para>
    /// </summary>
    public static bool? GetButtonEnabled(AtkComponentButton* button)
    {
        if(button == null) return null;
        if(button->AtkComponentBase.OwnerNode == null) return null;
        return button->IsEnabled;
    }

    /// <summary>
    /// <see cref="GetButtonEnabled"/> 的自動化用版本：按鈕還沒準備好時一律視為「不可按」，
    /// 讓呼叫端走既有的等待／重試路徑，而不是中止整條任務佇列。
    /// </summary>
    public static bool IsButtonEnabled(AtkComponentButton* button) => GetButtonEnabled(button) == true;

    public static int FrameDelay => 10 + C.ExtraFrameDelay;
    // TC(台服)客戶端在 Dalamud 13.0.0.16 之後回報 ClientLanguage 7(TraditionalChinese),
    // 舊版回報 4(ChineseSimplified)。用數值比較才能同時相容 CI 釘的 13.0.0.6(列舉沒有 7 這個名字)與執行期新版。
    public static bool IsCN => (int)Svc.ClientState.ClientLanguage is 4 or 5 or 7;
    public static int FCPoints => *(int*)((nint)AgentModule.Instance()->GetAgentByInternalId(AgentId.FreeCompanyCreditShop) + 256);
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
            var world = ExcelWorldHelper.Get(data.WorldOverride ?? data.World);
            if(world != null)
            {
                return DateTimeOffset.Now.ToUnixTimeSeconds() < C.LockoutTime.SafeSelect(world.Value.GetRegion(), 0);
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

    /// <remarks>
    /// 讀不到的容器一律跳過、繼續累加。唯一的呼叫端 <c>AutoGCHandin</c> 把結果當**排序鍵**用
    /// （<c>ThenByDescending</c>），少算一個容器只會換到另一個一樣合法的順序。
    /// ⚠️ 解參考 null 在 .NET Core 是 corrupted-state exception，try/catch 攔不到，只能靠事前檢查。
    /// </remarks>
    public static int CountItemsInInventory(uint id, bool? hq, IEnumerable<InventoryType> inventories)
    {
        var ret = 0;
        foreach(var inventory in inventories)
        {
            var inv = InventoryManager.Instance()->GetInventoryContainer(inventory);
            if(inv == null || inv->Items == null) continue;
            for(var i = 0; i < inv->Size; i++)
            {
                var slot = inv->Items[i];
                var itemId = slot.ItemId;
                var itemHq = slot.Flags.HasFlag(InventoryItem.ItemFlags.HighQuality);
                if((hq == null || itemHq == hq) && itemId == id)
                {
                    ret += (int)slot.Quantity;
                }
            }
        }
        return ret;
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

    public static GCExchangePlan GetGCExchangePlanWithOverrides()
    {
        if(C.AdditionalGCExchangePlans.TryGetFirst(x => x.GUID == Data.ExchangePlan, out var plan))
        {
            return plan;
        }
        return C.DefaultGCExchangePlan;
    }

    public static bool IsProtected(this Item item)
    {
        return Data.GetIMSettings().IMProtectList.Contains(item.RowId);
    }

    public static Dictionary<uint, GCExchangeListingMetadata> GetCurrentlyAvailableSharedExchangeListings()
    {
        var gc = Svc.ClientState.TerritoryType switch
        {
            MainCities.New_Gridania => GrandCompany.TwinAdder,
            MainCities.Uldah_Steps_of_Nald => GrandCompany.ImmortalFlames,
            MainCities.Limsa_Lominsa_Upper_Decks => GrandCompany.Maelstrom,
            _ => throw new InvalidOperationException("Could not determite accessed grand company")
        };
        return SharedGCExchangeListings.Where(x => x.Value.Companies.Contains(gc)).ToDictionary();
    }

    public static Dictionary<uint, GCExchangeListingMetadata> SharedGCExchangeListings
    {
        get
        {
            if(field == null)
            {
                field = [];
                Dictionary<uint, List<GCExchangeListingMetadata>> listings = [];
                foreach(var x in Svc.Data.GetExcelSheet<GCScripShopCategory>())
                {
                    var items = Svc.Data.GetSubrowExcelSheet<GCScripShopItem>();
                    if(x.RowId < items.Count && x.GrandCompany.RowId > 0)
                    {
                        var list = listings.GetOrCreate(x.GrandCompany.RowId, []);
                        var sub = items[x.RowId];
                        foreach(var entry in sub)
                        {
                            if(!entry.Item.RowId.EqualsAny(0u, 6017u, 6018u, 6019u) && entry.Item.ValueNullable != null)
                            {
                                list.Add(new()
                                {
                                    Category = (GCExchangeCategoryTab)(x.SubCategory - 1),
                                    ItemID = entry.Item.RowId,
                                    MinPurchaseRank = entry.RequiredGrandCompanyRank.RowId,
                                    Seals = entry.CostGCSeals,
                                });
                            }
                        }
                    }
                }
                foreach(var listing in listings)
                {
                    foreach(var x in listing.Value)
                    {
                        field.TryAdd(x.ItemID, x);
                        for(uint i = 1; i <= 3; i++)
                        {
                            if(listings[i].Contains(x))
                            {
                                field[x.ItemID].Companies.Add((GrandCompany)i);
                            }
                        }
                    }
                }
            }
            return field;
        }
    }

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
        for(var i = 0; i < GameRetainerManager.Count; i++)
        {
            var ret = GameRetainerManager.Retainers[i];
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

    public static InventoryType[] RetainerInventories => [InventoryType.RetainerPage1, InventoryType.RetainerPage2, InventoryType.RetainerPage3, InventoryType.RetainerPage4, InventoryType.RetainerPage5, InventoryType.RetainerPage6, InventoryType.RetainerPage7];

    public static InventoryType[] RetainerInventoriesWithCrystals => [.. RetainerInventories, InventoryType.RetainerCrystals];

    public static InventoryType[] PlayerInvetories => [InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4];

    public static InventoryType[] PlayerInvetoriesWithCrystals => [.. PlayerInvetories, InventoryType.Crystals];

    public static InventoryType[] PlayerArmory => [InventoryType.ArmoryOffHand, InventoryType.ArmoryHead, InventoryType.ArmoryBody, InventoryType.ArmoryHands, InventoryType.ArmoryWaist, InventoryType.ArmoryLegs, InventoryType.ArmoryFeets, InventoryType.ArmoryEar, InventoryType.ArmoryNeck, InventoryType.ArmoryWrist, InventoryType.ArmoryRings, InventoryType.ArmorySoulCrystal, InventoryType.ArmoryMainHand];

    public static InventoryType[] PlayerEntireInventory => [.. PlayerInvetories, .. PlayerArmory, InventoryType.EquippedItems];

    public static InventoryType[] RetainerEntireInventory => [.. RetainerInventoriesWithCrystals, InventoryType.RetainerMarket, InventoryType.RetainerEquippedItems];

    public static InventoryType[] GetAllowedInventories(this EntrustPlan plan)
    {
        return plan.AllowEntrustFromArmory ? [.. PlayerInvetoriesWithCrystals, .. PlayerArmory] : PlayerInvetoriesWithCrystals;
    }

    /// <summary>
    /// Snapshots the contents of the given inventories. Counterpart of
    /// <see cref="MatchesCapturedInventoryState"/>, which must walk the containers in the same order.
    /// </summary>
    /// <remarks>
    /// 🔴 拿不到的容器／格位一律「跳過」，不是中止整個快照。這條路徑每次雇員存入送出指令都會走到
    /// （<c>TaskEntrustDuplicates</c> 的閘門與 <c>NpcSaleManager</c> 的等待判定），而
    /// <c>NpcSaleManager</c> 是拿本函式的兩次輸出互相 <c>SequenceEqual</c>：中止會讓新的一次回傳短少
    /// 的清單，跟舊快照比對不相等 → 把「狀態根本沒變」誤判成「已變動」→ 閘門提前放行。
    /// 跳過則兩次都跳過同樣的容器，比對結果仍然是相等，行為維持不變。
    /// 同檔的 <see cref="MatchesCapturedInventoryState"/> 與 <c>NpcSaleManager.SellHardListItemsTask</c>
    /// 都已經是這個寫法，本函式先前是唯一漏掉的一個。
    /// ⚠️ 解參考 null 在 .NET Core 是 corrupted-state exception，try/catch 攔不到，只能靠事前檢查。
    /// </remarks>
    public static List<(uint ID, uint Quantity)> GetCapturedInventoryState(IEnumerable<InventoryType> inventoryTypes)
    {
        var ret = new List<(uint ID, uint Quantity)>();
        foreach(var type in inventoryTypes)
        {
            var inv = InventoryManager.Instance()->GetInventoryContainer(type);
            if(inv == null) continue;
            for(var i = 0; i < inv->Size; i++)
            {
                var item = InventoryManager.Instance()->GetInventorySlot(type, i);
                if(item == null) continue;
                ret.Add((item->ItemId, (uint)item->Quantity));
            }
        }
        return ret;
    }

    /// <summary>
    /// Whether the given inventories still hold exactly what <see cref="GetCapturedInventoryState"/>
    /// recorded. Equivalent to <c>GetCapturedInventoryState(types).SequenceEqual(captured)</c> but walks
    /// the containers in place and bails at the first difference, so it allocates nothing. The callers
    /// that poll this run once per frame, where a fresh ~200 element list every frame is pure waste.
    /// </summary>
    public static bool MatchesCapturedInventoryState(IEnumerable<InventoryType> inventoryTypes, List<(uint ID, uint Quantity)> captured)
    {
        if(captured == null) return false;
        var index = 0;
        foreach(var type in inventoryTypes)
        {
            var inv = InventoryManager.Instance()->GetInventoryContainer(type);
            if(inv == null) return false;
            for(var i = 0; i < inv->Size; i++)
            {
                if(index < 0 || index >= captured.Count) return false;
                var item = InventoryManager.Instance()->GetInventorySlot(type, i);
                if(item == null) return false;
                var recorded = captured[index];
                if(recorded.ID != item->ItemId || recorded.Quantity != (uint)item->Quantity) return false;
                index++;
            }
        }
        return index == captured.Count;
    }

    /// <summary>
    /// Request all unique items from select inventories
    /// </summary>
    /// <param name="inventoryTypes"></param>
    /// <returns></returns>
    /// <remarks>
    /// 讀不到的容器／格位一律跳過。唯一的呼叫端 <c>TaskEntrustDuplicates</c> 拿這份集合決定「這輪要存入哪些道具」，
    /// 少一個 ID 只代表那件道具這輪不處理（下輪重掃會補回來），不會做出多餘的動作。
    /// ⚠️ 解參考 null 在 .NET Core 是 corrupted-state exception，try/catch 攔不到，只能靠事前檢查。
    /// </remarks>
    public static HashSet<uint> GetItemsInInventory(IEnumerable<InventoryType> inventoryTypes)
    {
        var ret = new HashSet<uint>();
        foreach(var type in inventoryTypes)
        {
            var inv = InventoryManager.Instance()->GetInventoryContainer(type);
            if(inv == null) continue;
            for(var i = 0; i < inv->Size; i++)
            {
                var item = InventoryManager.Instance()->GetInventorySlot(type, i);
                if(item == null) continue;
                if(item->ItemId != 0)
                {
                    ret.Add(item->ItemId);
                }
            }
        }
        return ret;
    }

    /// <summary>
    /// Gets total item count of certain item across all inventories
    /// </summary>
    /// <param name="inventoryTypes"></param>
    /// <param name="itemId"></param>
    /// <returns></returns>
    /// <remarks>
    /// 讀不到的容器／格位一律跳過、繼續累加，也就是**只可能少算不可能多算**。三個呼叫端全部是拿結果去
    /// 跟門檻比大小後才「做事」（<c>SchedulerMain</c> 的 <c>&gt; toKeep</c> 與 <c>&gt; 0</c> 決定要不要排訪問、
    /// <c>IPC_PluginState</c> 的 <c>&gt; 0</c> 決定要不要跳過獨占道具），少算一律讓判斷倒向「不做事」。
    /// ⚠️ 解參考 null 在 .NET Core 是 corrupted-state exception，try/catch 攔不到，只能靠事前檢查。
    /// </remarks>
    public static int GetItemCount(IEnumerable<InventoryType> inventoryTypes, uint itemId)
    {
        var ret = 0;
        foreach(var type in inventoryTypes)
        {
            var inv = InventoryManager.Instance()->GetInventoryContainer(type);
            if(inv == null) continue;
            for(var i = 0; i < inv->Size; i++)
            {
                var item = InventoryManager.Instance()->GetInventorySlot(type, i);
                if(item == null) continue;
                if(item->ItemId == itemId)
                {
                    ret += (int)item->Quantity;
                }
            }
        }
        return ret;
    }

    /// <summary>
    /// Whether the given container can be walked at all right now. A container that is not currently
    /// loaded (retainer pages while no retainer is open, say) comes back as a null pointer, or as a
    /// container whose backing item array has not been allocated.
    /// </summary>
    /// <remarks>
    /// 🔴 這是「能不能讀」不是「有沒有東西」。呼叫端如果需要**證明某件道具不存在**，光看
    /// <see cref="ContainsItem"/> 回 false 是不夠的——讀不到的容器也會回 false。要先用本方法確認讀得到。
    /// </remarks>
    public static bool IsInventoryReadable(this InventoryType type)
    {
        var inv = InventoryManager.Instance()->GetInventoryContainer(type);
        return inv != null && inv->Items != null;
    }

    /// <remarks>
    /// ⚠️ 讀不到的容器回 <c>false</c>——語意是「在**讀得到的格位裡**沒找到」，**不是**「這件道具不存在」。
    /// 需要後者的呼叫端請先過 <see cref="IsInventoryReadable"/>。
    /// </remarks>
    public static bool ContainsItem(this InventoryType type, uint item, bool? isHq = null)
    {
        var im = InventoryManager.Instance();
        var inv = im->GetInventoryContainer(type);
        if(inv == null || inv->Items == null) return false;
        for(var i = 0; i < inv->Size; i++)
        {
            var slot = inv->Items[i];
            if(slot.ItemId == item && (isHq == null || isHq == slot.Flags.HasFlag(InventoryItem.ItemFlags.HighQuality)))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Gets amount of items that can fit into inventories.
    /// </summary>
    /// <remarks>
    /// Use this overload unless you are actually going to show the diagnostics to someone. The
    /// <c>debugData</c> overload builds one interpolated string per scanned slot - up to ~175 for a
    /// retainer's item pages, several of them with an Excel name lookup - and callers used to build
    /// them unconditionally and then throw them away, which is a large amount of garbage for a method
    /// that gets called inside per-item scan loops.
    /// </remarks>
    public static uint GetAmountThatCanFit(IEnumerable<InventoryType> inventoryTypes, uint itemId, bool isHq)
    {
        return GetAmountThatCanFitInternal(inventoryTypes, itemId, isHq, null);
    }

    /// <summary>
    /// Gets amount of items that can fit into inventories, along with a per-slot explanation of how the
    /// number was reached. Only call this when the explanation is going to be read - see the remarks on
    /// the three-argument overload.
    /// </summary>
    /// <param name="inventoryTypes"></param>
    /// <param name="itemId"></param>
    /// <param name="isHq"></param>
    /// <param name="debugData"></param>
    /// <returns></returns>
    public static uint GetAmountThatCanFit(IEnumerable<InventoryType> inventoryTypes, uint itemId, bool isHq, out List<string> debugData)
    {
        debugData = [];
        return GetAmountThatCanFitInternal(inventoryTypes, itemId, isHq, debugData);
    }

    /// <param name="debugData">Null to skip building the per-slot explanation entirely. Every use below is
    /// through <c>?.</c>, which short-circuits argument evaluation, so a null collector means the
    /// interpolated strings are never built in the first place.</param>
    /// <remarks>
    /// 🔴 本函式讀不到容器時一律 <c>return 0</c>，**不是** <c>continue</c>——跟同檔其他幾個累加型函式相反。
    /// 理由是這裡「跳過一個容器」會讓結果**偏高**，而不是偏低：
    /// <list type="number">
    /// <item>水晶分支跳過的若正好是那個裝著未滿堆疊的容器，迴圈會落到結尾的
    /// <c>return data.Value.StackSize</c>，直接回報一整堆的容量——這是可能回傳的最大值。</item>
    /// <item>一般分支跳過的若正好是那個裝著獨占道具的容器，就錯過 <c>if(data.Value.IsUnique) return 0</c>，
    /// 於是把「一件都放不下」算成一個正數。</item>
    /// </list>
    /// 而回傳值會被拿去夾 <c>toEntrust</c>（<c>TaskEntrustDuplicates</c>）與軍票兌換數量（<c>GCContinuation</c>），
    /// 高估＝送出遊戲一定會拒絕的數量。回 0 是保守失敗（這輪不動作），而且兩個呼叫端都是輪詢重跑的，
    /// 容器一旦載入下一輪就自動恢復，不會卡死。
    /// ⚠️ 解參考 null 在 .NET Core 是 corrupted-state exception，try/catch 攔不到，只能靠事前檢查。
    /// </remarks>
    private static uint GetAmountThatCanFitInternal(IEnumerable<InventoryType> inventoryTypes, uint itemId, bool isHq, List<string> debugData)
    {
        uint ret = 0;
        var data = ExcelItemHelper.Get(itemId);
        if(data == null) return 0;
        if(data.Value.IsUnique)
        {
            // 讀不到的容器不能用來證明「身上沒有這件獨占道具」（ContainsItem 對讀不到的容器也是回 false），
            // 所以讀不到就當作「可能已經有了」→ 回 0。
            if(inventoryTypes.ContainsAny(Utils.PlayerEntireInventory))
            {
                if(Utils.PlayerEntireInventory.Any(i => !i.IsInventoryReadable() || i.ContainsItem(itemId, null))) return 0;
            }
            if(inventoryTypes.ContainsAny(Utils.RetainerEntireInventory))
            {
                if(Utils.RetainerEntireInventory.Any(i => !i.IsInventoryReadable() || i.ContainsItem(itemId, null))) return 0;
            }
        }
        if(data.Value.ItemUICategory.RowId == 59)//crystal special handling
        {
            foreach(var type in inventoryTypes)
            {
                var inv = InventoryManager.Instance()->GetInventoryContainer(type);
                if(inv == null) return 0;
                for(var i = 0; i < inv->Size; i++)
                {
                    var item = InventoryManager.Instance()->GetInventorySlot(type, i);
                    if(item == null) return 0;
                    if(item->ItemId == itemId)
                    {
                        ret += (uint)(data.Value.StackSize - item->Quantity);
                        debugData?.Add($"[TED] [CrystalDebugData] in {type} slot {i} found incomplete stack: {ExcelItemHelper.GetName(itemId, true)} q={item->Quantity} canFit={ret}");
                        return ret;
                    }
                }
            }
            return data.Value.StackSize;
        }
        else
        {
            foreach(var type in inventoryTypes)
            {
                if(type.EqualsAny(InventoryType.Crystals, InventoryType.RetainerCrystals)) continue;
                var inv = InventoryManager.Instance()->GetInventoryContainer(type);
                if(inv == null) return 0;
                for(var i = 0; i < inv->Size; i++)
                {
                    var item = InventoryManager.Instance()->GetInventorySlot(type, i);
                    if(item == null) return 0;
                    if(item->ItemId == itemId && item->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality) == isHq && !item->Flags.HasFlag(InventoryItem.ItemFlags.Collectable))
                    {
                        if(data.Value.IsUnique) return 0;
                        debugData?.Add($"[TED] [DebugData] in {type} slot {i} found incomplete stack: {ExcelItemHelper.GetName(itemId, true)} q={item->Quantity} canFit={ret}");
                        ret += (uint)(data.Value.StackSize - item->Quantity);
                    }
                    else if(item->ItemId == 0)
                    {
                        debugData?.Add($"[TED] [DebugData] in {type} slot {i} is empty, canFit={data.Value.StackSize}");
                        ret += data.Value.StackSize;
                    }
                }
            }
        }
        return ret;
    }

    public static bool IsItemSellableByHardList(Number item, Number quantity)
    {
        if(Data.GetIMSettings().IMProtectList.Contains(item)) return false;
        if(Data.GetIMSettings().IMAutoVendorHard.Contains(item))
        {
            if(Data.GetIMSettings().IMAutoVendorHardIgnoreStack.Contains(item)) return true;
            return quantity < Data.GetIMSettings().IMAutoVendorHardStackLimit;
        }
        else
        {
            return false;
        }
    }

    public static bool? WaitForScreen()
    {
        return IsScreenReady();
    }

    internal static void ExtraLog(string s)
    {
        // 外掛自訂等級只能比全域記錄等級更嚴格,不能更寬鬆(ScopedPluginLogService 的行為)。
        // 使用者開啟「額外記錄」開關就是想看到這些訊息,若寫 Debug,一般記錄等級下永遠不會顯示。
        if(C.ExtraDebug) PluginLog.Information(s);
    }

    internal static bool ContainsAllItems<T>(this IEnumerable<T> a, IEnumerable<T> b)
    {
        return !b.Except(a).Any();
    }

    internal static float Random { get; private set; } = 1f;
    internal static void RegenerateRandom()
    {
        Random = (float)new Random().NextDouble();
        DebugLog($"Random regenerated: {Random}");
    }

    internal static bool MultiModeOrArtisan => MultiMode.Active || (SchedulerMain.PluginEnabled && SchedulerMain.Reason == PluginEnableReason.Artisan);
    internal static bool IsBusy => P.TaskManager.IsBusy || AutoGCHandin.Operation || S.LifestreamIPC.IsBusy();
    internal static AtkValue ZeroAtkValue = new() { Type = 0, Int = 0 };

    internal static IEnumerable<string> GetEObjNames(params uint[] values)
    {
        foreach(var x in values)
        {
            yield return Svc.Data.GetExcelSheet<EObjName>().GetRow(x).Singular.GetText();
        }
    }

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

    internal static bool TryGetCharacterIndex(string name, uint world, out int index)
    {
        index = GetCharacterNames().IndexOf((name, (ushort)world));
        return index >= 0;
    }

    internal static List<CharaData> GetCharacterNames()
    {
        List<CharaData> ret = [];
        /*var data = CSFramework.Instance()->UIModule->GetRaptureAtkModule()->AtkModule.GetStringArrayData(1);
        if (data != null)
        {
            for (int i = 60; i < data->AtkArrayData.Size; i++)
            {
                if (data->StringArray[i] == null) break;
                var item = data->StringArray[i];
                if (item != null)
                {
                    var str = MemoryHelper.ReadSeStringNullTerminated((nint)item).GetText();
                    if (str == "") break;
                    ret.Add(str);
                }
            }
        }*/
        var agent = AgentLobby.Instance();
        if(agent->AgentInterface.IsAgentActive())
        {
            var charaSpan = agent->LobbyData.CharaSelectEntries.AsSpan();
            for(var i = 0; i < charaSpan.Length; i++)
            {
                var s = charaSpan[i];
                ret.Add(($"{s.Value->Name.Read()}", s.Value->HomeWorldId));
            }
        }
        return ret;
    }

    internal static string FancyDigits(this int n)
    {
        return n.ToString().ReplaceByChar(Lang.Digits.Normal, Lang.Digits.GameFont);
    }

    internal static int GetJobLevel(this OfflineCharacterData data, uint job)
    {
        var d = Svc.Data.GetExcelSheet<ClassJob>().GetRowOrDefault(job);
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

    internal static OfflineCharacterData GetCurrentCharacterData()
    {
        return C.OfflineData.FirstOrDefault(x => x.CID == Player.CID);
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
        return TryGetAddonByName<AtkUnitBase>("_TitleMenu", out var title)
            && IsAddonReady(title)
            && title->UldManager.NodeListCount > 3
            && title->UldManager.NodeList[7]->IsVisible()
            && title->UldManager.NodeList[3]->Color.A == 0xFF
            && !TryGetAddonByName<AtkUnitBase>("TitleDCWorldMap", out _)
            && !TryGetAddonByName<AtkUnitBase>("TitleConnect", out _);
    }

    internal static OfflineCharacterData GetOfflineCharacterDataFromAdditionalRetainerDataKey(string key)
    {
        var cid = ulong.Parse(key.Split(" ")[0].Replace("#", ""), System.Globalization.NumberStyles.HexNumber);
        return C.OfflineData.FirstOrDefault(x => x.CID == cid);
    }

    internal static OfflineRetainerData GetOfflineRetainerDataFromAdditionalRetainerDataKey(string key)
    {
        return GetOfflineCharacterDataFromAdditionalRetainerDataKey(key).RetainerData.FirstOrDefault(x => x.Name == key.Split(" ")[1]);
    }

    internal static uint GetNextPlannedVenture(this AdditionalRetainerData data)
    {
        var index = data.GetNextPlannedVentureIndex();
        if(index == -1)
        {
            return 0;
        }
        else
        {
            return data.VenturePlan.ListUnwrapped[index];
        }
    }

    internal static int GetNextPlannedVentureIndex(this AdditionalRetainerData data)
    {
        if(data.VenturePlan.ListUnwrapped.Count == 0)
        {
            return -1;
        }
        else
        {
            if(data.VenturePlanIndex >= data.VenturePlan.ListUnwrapped.Count)
            {
                if(data.VenturePlan.PlanCompleteBehavior == PlanCompleteBehavior.Restart_plan)
                {
                    return 0;
                }
                else
                {
                    return -1;
                }
            }
            else
            {
                return (int)data.VenturePlanIndex;
            }
        }
    }

    internal static bool IsLastPlannedVenture(this AdditionalRetainerData data)
    {
        return data.VenturePlanIndex >= data.VenturePlan.ListUnwrapped.Count;
    }

    internal static bool IsVenturePlannerActive(this AdditionalRetainerData data)
    {
        return data.EnablePlanner && data.VenturePlan.ListUnwrapped.Count > 0;
    }

    internal static DateTime DateFromTimeStamp(uint timeStamp)
    {
        const long timeFromEpoch = 62135596800;
        return timeStamp == 0u
            ? DateTime.MinValue
            : new DateTime((timeStamp + timeFromEpoch) * TimeSpan.TicksPerSecond, DateTimeKind.Utc);
    }

    internal static bool IsAnyRetainersCompletedVenture()
    {
        if(!ProperOnLogin.PlayerPresent) return false;
        if(C.OfflineData.TryGetFirst(x => x.CID == Svc.ClientState.LocalContentId, out var data))
        {
            var selectedRetainers = data.GetEnabledRetainers().Where(z => z.HasVenture);
            return selectedRetainers.Any(z => z.GetVentureSecondsRemaining() <= 10);
        }
        return false;
    }

    internal static bool IsAllCurrentCharacterRetainersHaveMoreThan5Mins()
    {
        if(C.OfflineData.TryGetFirst(x => x.CID == Svc.ClientState.LocalContentId, out var data))
        {
            foreach(var z in data.GetEnabledRetainers())
            {
                if(z.GetVentureSecondsRemaining() < 5 * 60) return false;
            }
        }
        return true;
    }

    internal static string GetActivePlayerInventoryName()
    {
        {
            if(TryGetAddonByName<AtkUnitBase>("InventoryLarge", out var addon) && addon->IsVisible)
            {
                return "InventoryLarge";
            }
        }
        {
            if(TryGetAddonByName<AtkUnitBase>("InventoryExpansion", out var addon) && addon->IsVisible)
            {
                return "InventoryExpansion";
            }
        }
        return "Inventory";
    }
    internal static (string Name, int EntrustDuplicatesIndex) GetActiveRetainerInventoryName()
    {
        if(TryGetAddonByName<AtkUnitBase>("InventoryRetainerLarge", out var addon) && addon->IsVisible)
        {
            return ("InventoryRetainerLarge", 8);
        }
        return ("InventoryRetainer", 5);
    }

    internal static IGameObject GetNearestRetainerBell(out float Distance)
    {
        var currentDistance = float.MaxValue;
        IGameObject currentObject = null;
        foreach(var x in Svc.Objects)
        {
            if(x.IsTargetable && (x.ObjectKind == ObjectKind.Housing || x.ObjectKind == ObjectKind.EventObj) && x.Name.ToString().EqualsIgnoreCaseAny(Lang.BellName))
            {
                var distance = Vector3.Distance(Svc.ClientState.LocalPlayer.Position, x.Position);
                if(distance < currentDistance)
                {
                    currentDistance = distance;
                    currentObject = x;
                }
            }
        }
        Distance = currentDistance;
        return currentObject;
    }

    internal static IGameObject GetReachableRetainerBell(bool extend)
    {
        if(Player.Object is null) return null;

        foreach(var x in Svc.Objects)
        {
            if((x.ObjectKind == ObjectKind.Housing || x.ObjectKind == ObjectKind.EventObj) && x.Name.ToString().EqualsIgnoreCaseAny(Lang.BellName))
            {
                var distance = extend && VoyageUtils.Workshops.Contains(Svc.ClientState.TerritoryType) ? 20f : GetValidInteractionDistance(x);
                if(Vector3.Distance(x.Position, Svc.ClientState.LocalPlayer.Position) < distance && x.IsTargetable)
                {
                    return x;
                }
            }
        }
        return null;
    }

    // Finds the Company Workshop's "adventurer doll" NPC, used to reach the
    // Free Company Credit Shop. Matched by name substring since it carries a
    // per-instance numeric suffix (e.g. "冒險人偶014號") that rules out an
    // exact-name match like the bell/panel use.
    internal static IGameObject GetNearestAdventurerDoll(out float Distance)
    {
        var currentDistance = float.MaxValue;
        IGameObject currentObject = null;
        foreach(var x in Svc.Objects)
        {
            if(x.IsTargetable && x.Name.ToString().ContainsAny(StringComparison.OrdinalIgnoreCase, Lang.AdventurerDollNamePart))
            {
                var distance = Vector3.Distance(Svc.ClientState.LocalPlayer.Position, x.Position);
                if(distance < currentDistance)
                {
                    currentDistance = distance;
                    currentObject = x;
                }
            }
        }
        Distance = currentDistance;
        return currentObject;
    }

    internal static IGameObject GetReachableAdventurerDoll()
    {
        if(Player.Object is null) return null;
        foreach(var x in Svc.Objects)
        {
            if(x.Name.ToString().ContainsAny(StringComparison.OrdinalIgnoreCase, Lang.AdventurerDollNamePart))
            {
                if(Vector3.Distance(x.Position, Svc.ClientState.LocalPlayer.Position) < GetValidInteractionDistance(x) && x.IsTargetable)
                {
                    return x;
                }
            }
        }
        return null;
    }



    internal static bool AnyRetainersAvailableCurrentChara()
    {
        if(C.OfflineData.TryGetFirst(x => x.CID == Svc.ClientState.LocalContentId, out var data))
        {
            return data.GetEnabledRetainers().Any(z => z.GetVentureSecondsRemaining() <= C.UnsyncCompensation);
        }
        return false;
    }

    internal static AdditionalRetainerData GetAdditionalData(ulong cid, string name)
    {
        var key = GetAdditionalDataKey(cid, name, true);
        return C.AdditionalData[key];
    }

    internal static string GetAdditionalDataKey(ulong cid, string name, bool create = true)
    {
        var key = $"#{cid:X16} {name}";
        if(create && !C.AdditionalData.ContainsKey(key))
        {
            C.AdditionalData[key] = new();
        }
        return key;
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
    internal static void RethrottleGeneric(int num)
    {
        FrameThrottler.Throttle("AutoRetainerGenericThrottle", num, true);
    }
    internal static void RethrottleGeneric()
    {
        FrameThrottler.Throttle("AutoRetainerGenericThrottle", Utils.FrameDelay, true);
    }

    internal static bool TrySelectSpecificEntry(string text, Func<bool> Throttler = null)
    {
        return TrySelectSpecificEntry(new string[] { text }, Throttler);
    }

    internal static bool TrySelectSpecificEntry(IEnumerable<string> text, Func<bool> Throttler = null)
    {
        return TrySelectSpecificEntry((x) => x.StartsWithAny(text), Throttler);
        /*if (TryGetAddonByName<AddonSelectString>("SelectString", out var addon) && IsAddonReady(&addon->AtkUnitBase))
        {
            var entry = GetEntries(addon).FirstOrDefault(x => x.EqualsAny(text));
            if (entry != null)
            {
                var index = GetEntries(addon).IndexOf(entry);
                if (index >= 0 && IsSelectItemEnabled(addon, index) && (Throttler?.Invoke() ?? GenericThrottle))
                {
                    ClickSelectString.Using((nint)addon).SelectItem((ushort)index);
                    DebugLog($"TrySelectSpecificEntry: selecting {entry}/{index} as requested by {text.Print()}");
                    return true;
                }
            }
        }
        else
        {
            RethrottleGeneric();
        }
        return false;*/
    }

    internal static bool TrySelectSpecificEntry(Func<string, bool> inputTextTest, Func<bool> Throttler = null)
    {
        if(TryGetAddonByName<AddonSelectString>("SelectString", out var addon) && IsAddonReady(&addon->AtkUnitBase))
        {
            if(new AddonMaster.SelectString(addon).Entries.TryGetFirst(x => inputTextTest(x.Text), out var entry))
            {
                if((Throttler?.Invoke() ?? GenericThrottle))
                {
                    entry.Select();
                    DebugLog($"TrySelectSpecificEntry: selecting {entry}");
                    return true;
                }
            }
        }
        else
        {
            RethrottleGeneric();
        }
        return false;
    }

    internal static List<string> GetEntries(AddonSelectString* addon)
    {
        var list = new List<string>();
        for(var i = 0; i < addon->PopupMenu.PopupMenu.EntryCount; i++)
        {
            list.Add(MemoryHelper.ReadSeStringNullTerminated((nint)addon->PopupMenu.PopupMenu.EntryNames[i].Value).GetText());
        }
        return list;
    }

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

    internal static float GetValidInteractionDistance(IGameObject bell)
    {
        if(bell.ObjectKind == ObjectKind.Housing)
        {
            return 6.5f;
        }
        else if(Inns.List.Contains(Svc.ClientState.TerritoryType))
        {
            return 4.75f;
        }
        else
        {
            return 4.6f;
        }
    }

    internal static float GetAngleTo(Vector2 pos)
    {
        return (MathHelper.GetRelativeAngle(Svc.ClientState.LocalPlayer.Position.ToVector2(), pos) + Svc.ClientState.LocalPlayer.Rotation.RadToDeg()) % 360;
    }

    internal static bool IsApartmentEntrance(this IGameObject obj)
    {
        return obj.Name.ToString().EqualsIgnoreCase(Lang.ApartmentEntrance);
    }

    internal static IGameObject GetNearestEntrance(out float Distance)
    {
        var currentDistance = float.MaxValue;
        IGameObject currentObject = null;

        foreach(var x in Svc.Objects)
        {
            if(x.IsTargetable && x.Name.ToString().EqualsIgnoreCaseAny([.. Lang.Entrance/*, Lang.ApartmentEntrance*/]))
            {
                var distance = Vector3.Distance(Svc.ClientState.LocalPlayer.Position, x.Position);
                if(distance < currentDistance)
                {
                    currentDistance = distance;
                    currentObject = x;
                }
            }
        }
        Distance = currentDistance;
        if(Distance > 20) return null;
        return currentObject;
    }

    internal static IGameObject GetEntranceAtLocation(Vector3 pos)
    {
        foreach(var x in Svc.Objects)
        {
            if(x.IsTargetable && x.Name.ToString().EqualsIgnoreCaseAny(Lang.Entrance))
            {
                var distance = Vector3.Distance(pos, x.Position);
                if(distance < 1f)
                {
                    return x;
                }
            }
        }
        return null;
    }

    internal static AtkUnitBase* GetSpecificYesno(Predicate<string> compare)
    {
        for(var i = 1; i < 100; i++)
        {
            try
            {
                var addon = (AtkUnitBase*)Svc.GameGui.GetAddonByName("SelectYesno", i).Address;
                if(addon == null) return null;
                if(IsAddonReady(addon))
                {
                    var textNode = addon->UldManager.NodeList[15]->GetAsAtkTextNode();
                    var text = GenericHelpers.ReadSeString(&textNode->NodeText).GetText();
                    if(compare(text))
                    {
                        PluginLog.Verbose($"SelectYesno {text} addon {i} by predicate");
                        return addon;
                    }
                }
            }
            catch(Exception e)
            {
                e.Log();
                return null;
            }
        }
        return null;
    }

    internal static AtkUnitBase* GetSpecificYesno(params string[] s)
    {
        for(var i = 1; i < 100; i++)
        {
            try
            {
                var addon = (AtkUnitBase*)Svc.GameGui.GetAddonByName("SelectYesno", i).Address;
                if(addon == null) return null;
                if(IsAddonReady(addon))
                {
                    var textNode = addon->UldManager.NodeList[15]->GetAsAtkTextNode();
                    var text = textNode->NodeText.GetText().Cleanup();
                    if(text.ContainsAny(s.Select(x => x.Cleanup())))
                    {
                        PluginLog.Verbose($"SelectYesno {s.Print()} addon {i}");
                        return addon;
                    }
                }
            }
            catch(Exception e)
            {
                e.Log();
                return null;
            }
        }
        return null;
    }

    internal static bool TryMatch(this string s, string pattern, out Match match)
    {
        var m = Regex.Match(s, pattern);
        if(m.Success)
        {
            match = m;
            return true;
        }
        else
        {
            match = null;
            return false;
        }
    }

    internal static bool IsCurrentRetainerEnabled()
    {
        return TryGetCurrentRetainer(out var ret) && C.SelectedRetainers.TryGetValue(Svc.ClientState.LocalContentId, out var rets) && rets.Contains(ret);
    }

    internal static bool TryGetCurrentRetainer(out string name)
    {
        if(Svc.Condition[ConditionFlag.OccupiedSummoningBell] && ProperOnLogin.PlayerPresent && Svc.Objects.Where(x => x.ObjectKind == ObjectKind.Retainer).OrderBy(x => Vector3.Distance(Svc.ClientState.LocalPlayer.Position, x.Position)).TryGetFirst(out var obj))
        {
            name = obj.Name.ToString();
            return true;
        }
        name = default;
        return false;
    }

    internal static uint GetVenturesAmount()
    {
        return (uint)InventoryManager.Instance()->GetInventoryItemCount(21072);
    }

    internal static bool IsInventoryFree()
    {
        return GetInventoryFreeSlotCount() >= C.MultiMinInventorySlots;
    }

    /// <summary>Whether the player's own inventory can be read right now at all.
    ///
    /// 🔴 <see cref="GetInventoryFreeSlotCount"/> skips containers it cannot read, so "not loaded yet"
    /// and "completely full" both come back as 0 - they are indistinguishable at the call site. That is
    /// fine for the callers that only ever *withhold* an action, but not for the ones whose false branch
    /// disables the plugin or writes <c>Enabled = false</c> into a character's saved data: those act
    /// destructively on the zero and the user has to undo it by hand.
    ///
    /// The containers are genuinely unreadable while zoning and for a short window after login, both of
    /// which the retainer/multi-mode flow hits constantly (it relogs between characters by design), so
    /// gate any such decision on this first and simply re-check on a later frame when it returns false.
    /// ⚠️ Deliberately NOT folded into <see cref="IsInventoryFree"/>: that would answer "yes, free" for
    /// an inventory nobody has read, which is the permissive direction and would let the venture loop
    /// start blind.</summary>
    internal static bool IsInventoryStateReadable()
    {
        if(!Player.Available) return false;
        if(Svc.Condition[ConditionFlag.BetweenAreas] || Svc.Condition[ConditionFlag.BetweenAreas51]) return false;
        var c = InventoryManager.Instance();
        if(c == null) return false;
        InventoryType[] types = [InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4];
        foreach(var x in types)
        {
            var inv = c->GetInventoryContainer(x);
            if(inv == null || inv->Items == null || inv->Size <= 0) return false;
        }
        return true;
    }

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

    internal static string GetAddonText(uint num)
    {
        return Svc.Data.GetExcelSheet<Addon>().GetRow(num).Text.ToString();
    }

    internal static bool IsRetainerBell(this IGameObject o)
    {
        return o != null &&
            (o.ObjectKind == ObjectKind.EventObj || o.ObjectKind == ObjectKind.Housing)
            && o.Name.ToString().EqualsIgnoreCaseAny(Lang.BellName);
    }

    internal static long GetVentureSecondsRemaining(this GameRetainerManager.Retainer ret, bool allowNegative = true)
    {
        var x = ret.VentureCompleteTimeStamp - P.Time;
        return allowNegative ? x : Math.Max(0, x);
    }

    internal static long GetVentureSecondsRemaining(this OfflineRetainerData ret, bool allowNegative = true)
    {
        var x = ret.VentureEndsAt - P.Time;
        return allowNegative ? x : Math.Max(0, x);
    }

    internal static bool TryGetRetainerByName(string name, out GameRetainerManager.Retainer retainer)
    {
        if(!GameRetainerManager.Ready)
        {
            retainer = default;
            return false;
        }
        for(var i = 0; i < GameRetainerManager.Count; i++)
        {
            var r = GameRetainerManager.Retainers[i];
            if(r.Name.ToString() == name)
            {
                retainer = r;
                return true;
            }
        }
        retainer = default;
        return false;
    }

    /// <remarks>
    /// 讀不到的容器一律跳過、繼續累加，也就是**只可能少算空格不可能多算**。所有呼叫端都是拿結果去跟下限比
    /// （<c>&gt;= MultiMinInventorySlots</c>、<c>&lt; Max(5, UIWarningRetSlotNum)</c> 等），少算一律讓判斷倒向
    /// 「背包不夠、先不要動作」。
    /// ⚠️ 解參考 null 在 .NET Core 是 corrupted-state exception，try/catch 攔不到，只能靠事前檢查。
    /// </remarks>
    internal static int GetInventoryFreeSlotCount()
    {
        InventoryType[] types = [InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4];
        var c = InventoryManager.Instance();
        var slots = 0;
        foreach(var x in types)
        {
            var inv = c->GetInventoryContainer(x);
            if(inv == null || inv->Items == null) continue;
            for(var i = 0; i < inv->Size; i++)
            {
                if(inv->Items[i].ItemId == 0)
                {
                    slots++;
                }
            }
        }
        return slots;
    }



    internal static bool TryParseRetainerName(string s, out string retainer)
    {
        retainer = default;
        if(!GameRetainerManager.Ready)
        {
            return false;
        }
        for(var i = 0; i < GameRetainerManager.Count; i++)
        {
            var r = GameRetainerManager.Retainers[i];
            var rname = r.Name.ToString();
            if(s.Contains(rname) && (retainer == null || rname.Length > retainer.Length))
            {
                retainer = rname;
            }
        }
        return retainer != default;
    }

    private static bool PopupContains(string source, string name)
    {
        if(Svc.Data.Language == ClientLanguage.Japanese)
        {
            return source.Contains($"（{name}）");
        }
        else if(Svc.Data.Language == ClientLanguage.French)
        {
            return source.Contains($"Menu de {name}");
        }
        else if(Svc.Data.Language == ClientLanguage.German)
        {
            return source.Contains($"Du hast {name}");
        }
        else
        {
            return source.Contains($"Retainer: {name}");
        }
    }

    internal static IGameObject GetNearestWorkshopEntrance(out float Distance)
    {
        Utils.ExtraLog($"GetNearestWorkshopEntrance: Begin");
        var currentDistance = float.MaxValue;
        IGameObject currentObject = null;
        foreach(var x in Svc.Objects)
        {
            Utils.ExtraLog($"GetNearestWorkshopEntrance: Scanning object table: object={x}, targetable={x.IsTargetable}");
            if(x.IsTargetable && x.Name.ToString().EqualsIgnoreCaseAny(Lang.AdditionalChambersEntrance))
            {
                var distance = Vector3.Distance(Svc.ClientState.LocalPlayer.Position, x.Position);
                Utils.ExtraLog($"GetNearestWorkshopEntrance: check passed, object={x}, targetable={x.IsTargetable}, distance={distance}");
                if(distance < currentDistance)
                {
                    Utils.ExtraLog($"GetNearestWorkshopEntrance: distance is less than current {currentDistance}, assigning from {currentObject}, object={x}, targetable={x.IsTargetable}, distance={distance}");
                    currentDistance = distance;
                    currentObject = x;
                }
            }
        }
        Distance = currentDistance;
        Utils.ExtraLog($"GetNearestWorkshopEntrance: End with distance={currentDistance}, obj={currentObject}");
        return currentObject;
    }
}
