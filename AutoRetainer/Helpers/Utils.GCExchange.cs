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

    /// <summary>
    /// 玩家所屬大國防聯軍的軍需官可換的項目；未加入聯軍＝空表。看玩家狀態不看目前區域，人在哪裡都能查、不擲例外
    /// （軍需官只賣自家聯軍，站在軍需官面前時兩者必然相同）。是否真的站在軍需官所在主城由呼叫端另判
    /// （<see cref="AutoGCHandin.IsValidGCTerritory"/>）。
    /// </summary>
    public static Dictionary<uint, GCExchangeListingMetadata> GetCurrentlyAvailableSharedExchangeListings()
    {
        var gc = (GrandCompany)AutoGCHandin.GetGC();
        if(!AvailableSharedExchangeListings.TryGetValue(gc, out var listings))
        {
            listings = BuildAvailableSharedExchangeListings(gc);
            AvailableSharedExchangeListings[gc] = listings;
        }
        return listings;
    }

    private static Dictionary<uint, GCExchangeListingMetadata> BuildAvailableSharedExchangeListings(GrandCompany gc)
        => SharedGCExchangeListings.Where(x => x.Value.Companies.Contains(gc)).ToDictionary();

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
}
