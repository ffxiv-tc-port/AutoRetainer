using AutoRetainer.Internal;
using AutoRetainer.Modules.Voyage.Readers;
using AutoRetainer.Modules.Voyage.VoyageCalculator;
using AutoRetainerAPI.Configuration;
using Dalamud.Game;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons.ExcelServices.TerritoryEnumeration;
using ECommons.GameHelpers;
using ECommons.Interop;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace AutoRetainer.Modules.Voyage;

/// <summary>
/// <see cref="VoyageUtils.SelectRoutePointSafe(string, out uint)"/> 的結果。
/// ⚠️ 零值刻意是 <see cref="Unknown"/> 而不是 <see cref="Selected"/> ——
/// 讓 default 落在樂觀值上，任何漏填的路徑都會靜默變成「成功」。
/// </summary>
internal enum RoutePointPickResult
{
    Unknown,
    /// <summary>真的按下去了。</summary>
    Selected,
    /// <summary>面板上找得到這個點，但遊戲判定不可選（航行距離不足、未解鎖、等級不足…）。</summary>
    NotSelectable,
    /// <summary>面板上根本沒有這個名字的點位（多半是海圖選錯，或計畫來自別的服務版本）。</summary>
    NotFound,
    /// <summary>航線面板當下不在或還沒 ready。</summary>
    PanelUnavailable,
}

internal static unsafe class VoyageUtils
{
    /// <remarks>
    /// CSFramework.Instance() 是 isPointer:true 的靜態位址，會合法回 null，裸解參考是攔不到的 AVE。
    /// 讀不到就當作「視窗非作用中」＝不啟用暫停指派（不擅自覆寫使用者原本的行為）。
    /// </remarks>
    internal static bool DontReassign
    {
        get
        {
            if(C.TempCollectB == LimitedKeys.None || !IsKeyPressed(C.TempCollectB)) return false;
            var framework = CSFramework.Instance();
            return framework != null && !framework->WindowInactive;
        }
    }

    internal static uint[] Workshops = [Houses.Company_Workshop_Empyreum, Houses.Company_Workshop_The_Goblet, Houses.Company_Workshop_Mist, Houses.Company_Workshop_Shirogane, Houses.Company_Workshop_The_Lavender_Beds];

    internal static bool ShouldEnterWorkshop()
    {
        return ((Data.WorkshopEnabled && Data.AreAnyEnabledVesselsReturnInNext(5 * 60, Data.ShouldWaitForAllWhenLoggedIn())) || (Utils.GetReachableRetainerBell(false) == null)) && Player.IsInHomeWorld;
    }

    internal static SubmarineUnlockPlan GetDefaultSubmarineUnlockPlan(bool New = true)
    {
        var ret = C.SubmarineUnlockPlans.FirstOrDefault(x => x.GUID == C.DefaultSubmarineUnlockPlan);
        if(ret == null && New) return new();
        return ret;
    }

    internal static bool IsNotEnoughSubmarinesEnabled(this OfflineCharacterData data)
    {
        return data.GetVesselData(VoyageType.Submersible).Count > data.GetVesselData(VoyageType.Submersible).Where(x => data.GetEnabledVesselsData(VoyageType.Submersible).Contains(x.Name)).Count();
    }

    internal static bool IsThereNotAssignedSubmarine(this OfflineCharacterData data)
    {
        return data.GetVesselData(VoyageType.Submersible).Where(x => data.GetEnabledVesselsData(VoyageType.Submersible).Contains(x.Name)).Any(x => x.ReturnTime == 0);
    }

    internal static bool AreAnySuboptimalBuildsFound(this OfflineCharacterData data)
    {
        var v = data.GetVesselData(VoyageType.Submersible).Where(x => data.GetEnabledVesselsData(VoyageType.Submersible).Contains(x.Name));
        foreach(var s in v)
        {
            var adata = data.GetAdditionalVesselData(s.Name, VoyageType.Submersible);
            if(adata.IsUnoptimalBuild(out _))
            {
                return true;
            }
        }
        return false;
    }

    internal static bool AreAnyInvalidRedeploysActive(this OfflineCharacterData data)
    {
        if(C.SubmarineUnlockPlans.Any(x => x.EnforcePlan))
        {
            var v = data.GetVesselData(VoyageType.Submersible).Where(x => data.GetEnabledVesselsData(VoyageType.Submersible).Contains(x.Name));
            foreach(var s in v)
            {
                var adata = data.GetAdditionalVesselData(s.Name, VoyageType.Submersible);
                if(adata.VesselBehavior == VesselBehavior.Redeploy)
                {
                    return true;
                }
            }
        }
        return false;
    }

    internal static bool IsUnoptimalBuild(this AdditionalVesselData adata, out string justification)
    {
        var conf = adata.GetSubmarineBuild().Trim();
        //PluginLog.Information($"{conf}");
        foreach(var x in C.UnoptimalVesselConfigurations)
        {
            if(adata.Level >= x.MinRank && adata.Level <= x.MaxRank)
            {
                if(x.ConfigurationsInvert)
                {
                    //PluginLog.Information($"{conf} vs {x.Configurations.Print()}={conf.EqualsIgnoreCaseAny(x.Configurations)}");
                    if(!conf.EqualsIgnoreCaseAny(x.Configurations))
                    {
                        justification = $"Build is not {x.Configurations.Print()}";
                        return true;
                    }
                }
                else
                {
                    foreach(var inv in x.Configurations)
                    {
                        if(conf.EqualsIgnoreCase(inv))
                        {
                            justification = $"Build is {conf}";
                            return true;
                        }
                    }
                }
            }
        }
        justification = default;
        return false;
    }

    internal static SubmarineExploration? GetSubmarineExploration(uint id)
    {
        return Svc.Data.GetExcelSheet<SubmarineExploration>().GetRowOrDefault(id);
    }

    internal static string GetSubmarineExplorationName(uint id)
    {
        return GetSubmarineExploration(id)?.ConvertDestination();
    }

    internal static string GetMapName(uint id)
    {
        return Svc.Data.GetExcelSheet<SubmarineMap>().GetRowOrDefault(id)?.Name.ToString();
    }

    internal static int? GetVesselIndex(string name, VoyageType type)
    {
        var housing = HousingManager.Instance();
        if(housing == null) return null;
        var w = housing->WorkshopTerritory;
        if(w == null) return null;
        var adata = GetAdditionalVesselData(Data, name, type);
        if(adata.IndexOverride > 0) return adata.IndexOverride - 1;
        if(type == VoyageType.Airship)
        {
            var v = w->Airship.Data;
            for(var i = 0; i < v.Length; i++)
            {
                var sub = v[i];
                if(GenericHelpers.Read(sub.Name) == name)
                {
                    return i;
                }
            }
        }
        if(type == VoyageType.Submersible)
        {
            var v = w->Submersible.Data;
            for(var i = 0; i < v.Length; i++)
            {
                var sub = v[i];
                if(GenericHelpers.Read(sub.Name) == name)
                {
                    return i;
                }
            }
        }
        return null;
    }

    internal static List<(uint point, string justification)> GetPrioritizedPointList(this SubmarineUnlockPlan plan)
    {
        var ret = new List<(uint point, string justification)>();
        if(plan.UnlockSubs)
        {
            foreach(var x in Unlocks.PointToUnlockPoint.Where(z => z.Value.Point < 9000 && z.Value.Sub))
            {
                if(!P.SubmarineUnlockPlanUI.IsMapExplored(x.Key, true) && P.SubmarineUnlockPlanUI.IsMapUnlocked(x.Key, true))
                {
                    ret.Add((x.Key, $"submarine slot from {VoyageUtils.GetSubmarineExplorationName(x.Key)}"));
                }
            }
            foreach(var unlock in Unlocks.PointToUnlockPoint.Where(x => x.Value.Sub))
            {
                var path = Unlocks.FindUnlockPath(unlock.Key);
                path.Reverse();
                foreach(var x in path)
                {
                    if(!ret.Any(z => z.point == x.Item2.Point) && !P.SubmarineUnlockPlanUI.IsMapUnlocked(x.Item1, true))
                    {
                        ret.Add((x.Item2.Point, $"{GetSubmarineExplorationName(x.Item1)} on the path to {GetSubmarineExplorationName(unlock.Key)} not unlocked"));
                    }
                }
            }
        }

        foreach(var x in Unlocks.PointToUnlockPoint.Where(z => z.Value.Point < 9000 && !plan.ExcludedRoutes.Contains(z.Key)))
        {
            if(ret.Count > 0 && Svc.Data.GetExcelSheet<SubmarineExploration>().GetRow(ret.First().point).Map.RowId != Svc.Data.GetExcelSheet<SubmarineExploration>().GetRow(x.Key).Map.RowId) break;
            if(!P.SubmarineUnlockPlanUI.IsMapUnlocked(x.Key, true) && P.SubmarineUnlockPlanUI.IsMapUnlocked(x.Value.Point, true) && !ret.Any(z => z.point == x.Value.Point))
            {
                ret.Add((x.Value.Point, $"{VoyageUtils.GetSubmarineExplorationName(x.Key)} not unlocked"));
            }
        }

        // 補跑「已解鎖但未探索」的點(使用者要求:全航線解鎖不只解鎖,還要跑過一次打勾)。
        // 🔴 預設關(C.UnlockRouteAlsoExploreUnexplored=false)=沿用既有行為;開啟才補跑。
        // 一律排在解鎖點之後(較低優先),只有沒有新點可解鎖時才會被選到。與上面同樣受單一海圖約束:
        // 一趟航行只能選同一張海圖上的點,清單累積起點所在海圖後,遇到別張海圖就中止(潛艇會逐圖清完)。
        if(C.UnlockRouteAlsoExploreUnexplored)
        {
            foreach(var x in Unlocks.PointToUnlockPoint.Where(z => z.Value.Point < 9000 && !plan.ExcludedRoutes.Contains(z.Key)))
            {
                if(ret.Count > 0 && Svc.Data.GetExcelSheet<SubmarineExploration>().GetRow(ret.First().point).Map.RowId != Svc.Data.GetExcelSheet<SubmarineExploration>().GetRow(x.Key).Map.RowId) break;
                if(P.SubmarineUnlockPlanUI.IsMapUnlocked(x.Key, true) && !P.SubmarineUnlockPlanUI.IsMapExplored(x.Key, true) && !ret.Any(z => z.point == x.Key))
                {
                    ret.Add((x.Key, $"{VoyageUtils.GetSubmarineExplorationName(x.Key)} unlocked but not explored"));
                }
            }
        }
        return ret;
    }

    internal static SubmarineUnlockPlan GetSubmarineUnlockPlanByGuid(string guid)
    {
        return C.SubmarineUnlockPlans.FirstOrDefault(x => x.GUID == guid);
    }

    internal static SubmarinePointPlan GetSubmarinePointPlanByGuid(string guid)
    {
        return C.SubmarinePointPlans.FirstOrDefault(x => x.GUID == guid);
    }

    internal static SubmarineMap? GetMap(this SubmarinePointPlan plan)
    {
        if(plan.Points.Count == 0) return null;
        return GetSubmarineExploration(plan.Points[0])?.Map.Value;
    }

    internal static string GetPointPlanName(this SubmarinePointPlan plan)
    {
        if(plan == null) return "No or unknown plan selected";
        if(plan.Name.Length > 0) return plan.Name;
        if(plan.Points.Count == 0) return $"Plan {plan.GUID}";
        // plan.Points 可以由使用者從剪貼簿貼進來(SubmarinePointPlanUI 的 Paste plan settings,
        // 走 JsonConvert 反序列化,沒有任何範圍驗證),所以點位 ID 不可信。而本方法被多個
        // ImGui.BeginCombo / Selectable 每幀呼叫 —— Dalamud 的 UiBuilder 攔到 Draw 例外後會
        // 把 this.Draw 設成 null,整個外掛的視窗在重開遊戲前都不會再畫出來。
        // 查無此列時顯示 "?<id>" 而不是靜默略過:讓「這個計畫有壞點位」在列上直接看得見。
        // Location 欄是扇區代號字母(A/B/.../AC),各語言版本一致;台服實測 exd-tc/7.20/
        // SubmarineExploration.csv 全部 160 列,非空的 Location 全數符合 [A-Z]{1,2}。
        // 這裡原本會對取表指定日文語言,但本艦隊的 Lumina
        // fork 在 ExcelModule.GetRawSheetCore() 開頭無條件執行 language = Language,語言參數是
        // 死參數(對所有客戶端皆然)——留著只會讓讀碼的人誤以為真的取到了日文表。移除後行為等價。
        var sheet = Svc.Data.GetExcelSheet<SubmarineExploration>();
        return $"{plan.GetMap()?.Name}: {plan.Points.Select(x => sheet.TryGetRow(x, out var row) ? row.Location.ToString() : $"?{x}").Join("→")}";
    }

    internal static uint GetMapId(this SubmarinePointPlan plan)
    {
        return GetMap(plan)?.RowId ?? 0;
    }

    internal static PanelType GetCurrentWorkshopPanelType()
    {
        if(TryGetAddonByName<AtkUnitBase>("SelectString", out var addon) && IsAddonReady(addon))
        {
            if(Utils.GetEntries((AddonSelectString*)addon).Any(x => x.EqualsIgnoreCaseAny(Lang.SubmarineManagement)))
            {
                return PanelType.TypeSelector;
            }
            // 🔴 NodeList[3] 既沒驗上界也沒判元素;GetAsAtkTextNode() 是 [MemberFunction],
            //    對 null 節點呼叫＝當場 AVE,而 &...->NodeText 是靜默的毒指標 0xC0。
            //    讀不到面板標題就回 Unknown ＝「認不出這是哪種面板」,與既有的「三種都比不中」
            //    同一條路徑(fail-closed:呼叫端不會據此把潛艇當飛空艇操作)。
            if(!Utils.TryGetNodeText(&addon->UldManager, 3, out var text)) return PanelType.Unknown;
            if(text.ContainsAny(StringComparison.OrdinalIgnoreCase, Lang.PanelSubmersible))
            {
                return PanelType.Submersible;
            }
            if(text.ContainsAny(StringComparison.OrdinalIgnoreCase, Lang.PanelAirship))
            {
                return PanelType.Airship;
            }
            return PanelType.Unknown;
        }
        return PanelType.None;
    }

    internal static void Log(string text)
    {
        DebugLog($"[Voyage] {text}");
    }

    internal static List<OfflineVesselData> GetVesselData(this OfflineCharacterData data, VoyageType type)
    {
        if(type == VoyageType.Airship) return data.OfflineAirshipData;
        if(type == VoyageType.Submersible) return data.OfflineSubmarineData;
        throw new ArgumentOutOfRangeException(nameof(type));
    }

    internal static HashSet<string> GetEnabledVesselsData(this OfflineCharacterData data, VoyageType type)
    {
        if(type == VoyageType.Airship) return data.EnabledAirships;
        if(type == VoyageType.Submersible) return data.EnabledSubs;
        throw new ArgumentOutOfRangeException(nameof(type));
    }

    /*internal static HashSet<string> GetFinalizeVesselsData(this OfflineCharacterData data, VoyageType type)
    {
        if (type == VoyageType.Airship) return data.FinalizeAirships;
        if (type == VoyageType.Submersible) return data.FinalizeSubs;
        throw new ArgumentOutOfRangeException(nameof(type));
    }*/

    internal static bool IsVoyagePanel(this IGameObject obj)
    {
        return obj?.Name.ToString().EqualsIgnoreCaseAny(Lang.PanelName) == true;
    }

    internal static bool IsVoyageCondition()
    {
        return Svc.Condition[ConditionFlag.OccupiedInEvent] || Svc.Condition[ConditionFlag.OccupiedInQuestEvent];
    }

    internal static bool IsInVoyagePanel()
    {
        if(IsVoyageCondition() && Svc.Targets.Target.IsVoyagePanel())
        {
            return true;
        }
        return false;
    }

    internal static bool TryGetNearestVoyagePanel(out IGameObject obj)
    {
        //Data ID: 2007820
        if(Svc.Objects.TryGetFirst(x => x.Name.ToString().EqualsIgnoreCaseAny(Lang.PanelName) && x.IsTargetable, out var o))
        {
            obj = o;
            return true;
        }
        obj = default;
        return false;
    }

    public static long GetRemainingSeconds(this OfflineVesselData data)
    {
        return data.ReturnTime - P.Time;
    }

    internal static AdditionalVesselData GetAdditionalVesselData(this OfflineCharacterData data, string name, VoyageType type)
    {
        if(type == VoyageType.Airship)
        {
            if(!data.AdditionalAirshipData.ContainsKey(name)) data.AdditionalAirshipData[name] = new();
            return data.AdditionalAirshipData[name];
        }
        if(type == VoyageType.Submersible)
        {
            if(!data.AdditionalSubmarineData.ContainsKey(name)) data.AdditionalSubmarineData[name] = new();
            return data.AdditionalSubmarineData[name];
        }
        throw new ArgumentOutOfRangeException(nameof(type));
    }

    internal static void WriteOfflineData()
    {
        //PluginLog.Debug($"WriteOfflineDataSub");
        // 這裡原本只判了 WorkshopTerritory，沒判 HousingManager 本身 —— 而它才是先解參考的那一個。
        var housing = HousingManager.Instance();
        if(housing == null) return;
        if(housing->WorkshopTerritory != null && C.OfflineData.TryGetFirst(x => x.CID == Player.CID, out var ocd))
        {
            ocd.WriteOfflineInventoryData();
            {
                var vessels = housing->WorkshopTerritory->Airship;
                var temp = new List<OfflineVesselData>();
                foreach(var x in vessels.Data)
                {
                    var name = x.Name.Read();
                    if(name != "")
                    {
                        temp.Add(new(name, x.ReturnTime));
                        var adata = Data.GetAdditionalVesselData(name, VoyageType.Airship);
                        adata.Level = x.RankId;
                        adata.CurrentExp = x.CurrentExp;
                        adata.NextLevelExp = x.NextLevelExp;
                    }
                }
                if(temp.Count > 0)
                {
                    Data.OfflineAirshipData = temp;
                }
            }
            {
                var vessels = housing->WorkshopTerritory->Submersible;
                var temp = new List<OfflineVesselData>();
                for(var i = 0; i < Math.Min(4, vessels.DataPointers.Length); i++)
                {
                    var vessel = vessels.DataPointers[i].Value;
                    if(vessel == null) continue;
                    var name = vessel->Name.Read();
                    if(name != "")
                    {
                        temp.Add(new(name, vessel->ReturnTime));
                        var adata = Data.GetAdditionalVesselData(name, VoyageType.Submersible);
                        adata.Level = vessel->RankId;
                        adata.NextLevelExp = vessel->NextLevelExp;
                        adata.CurrentExp = vessel->CurrentExp;
                        //PluginLog.Debug("Write offline sub data");
                        // 這裡用 Try 版而不是會丟例外的 GetVesselComponent，理由有兩個：
                        // 1. 本函式在航海面板裡每 100ms 跑一次，且沒有任何呼叫端接例外。
                        // 2. 這四個欄位會被持久化進 OfflineData。四個要嘛全寫要嘛全不寫——
                        //    只寫到一半或寫進 0，等於把「這一瞬間讀不到」固化成「這艘船沒有這個零件」，
                        //    之後的 UI 顯示與換零件計畫都會照著錯的值走。讀不到就留著上一輪的舊值。
                        if(TryGetVesselComponent(i, VoyageType.Submersible, 0, out var part1)
                            && TryGetVesselComponent(i, VoyageType.Submersible, 1, out var part2)
                            && TryGetVesselComponent(i, VoyageType.Submersible, 2, out var part3)
                            && TryGetVesselComponent(i, VoyageType.Submersible, 3, out var part4))
                        {
                            adata.Part1 = (int)part1->ItemId;
                            adata.Part2 = (int)part2->ItemId;
                            adata.Part3 = (int)part3->ItemId;
                            adata.Part4 = (int)part4->ItemId;
                        }
                        adata.Points = vessel->CurrentExplorationPoints.ToArray();
                    }
                }
                if(temp.Count > 0)
                {
                    Data.OfflineSubmarineData = temp;
                }
                Data.NumSubSlots = P.SubmarineUnlockPlanUI.GetNumUnlockedSubs() ?? Data.NumSubSlots;
                /*var curSub = CurrentSubmarine.Get();
                if (curSub != null)
                {
                    var adata = Data.GetAdditionalVesselData(Utils.Read(curSub->Name), VoyageType.Submersible);
                    adata.CurrentExp = curSub->CurrentExp;
                    adata.NextLevelExp = curSub->NextLevelExp;
                }*/
            }
        }
    }

    internal static bool IsRetainerBlockedByVoyage()
    {
        if(C.MultiModeType == MultiModeType.Retainers) return false;
        if(C.DisableRetainerVesselReturn == 0) return false;
        foreach(var x in C.OfflineData.Where(x => x.WorkshopEnabled).Where(x => !x.IsLockedOut()))
        {
            if(x.WorkshopEnabled && x.AreAnyEnabledVesselsReturnInNext(C.DisableRetainerVesselReturn * 60)) return true;
        }
        return false;
    }

    internal static string GetSubmarineBuild(this AdditionalVesselData data)
    {
        if(data.Part1 != 0 && data.Part2 != 0 && data.Part3 != 0 && data.Part4 != 0)
        {
            var str = Build.ToIdentifier((ushort)((Items)data.Part1).GetPartId())
                + Build.ToIdentifier((ushort)((Items)data.Part2).GetPartId())
                + Build.ToIdentifier((ushort)((Items)data.Part3).GetPartId())
                + Build.ToIdentifier((ushort)((Items)data.Part4).GetPartId());
            if(str.Length == 8) str = str.Replace("+", "") + "++";
            return " " + str;
        }
        return "";
    }

    internal static (string Text, int ModdedCount) GetSubmarineBuildDisplay(this AdditionalVesselData data)
    {
        if(data.Part1 != 0 && data.Part2 != 0 && data.Part3 != 0 && data.Part4 != 0)
        {
            var str = Build.ToIdentifier((ushort)((Items)data.Part1).GetPartId())
                + Build.ToIdentifier((ushort)((Items)data.Part2).GetPartId())
                + Build.ToIdentifier((ushort)((Items)data.Part3).GetPartId())
                + Build.ToIdentifier((ushort)((Items)data.Part4).GetPartId());
            var moddedCount = str.Count(c => c == '+');
            return (" " + str.Replace("+", ""), moddedCount);
        }
        return ("", 0);
    }

    internal static string GetPlanBuild(this LevelAndPartsData data)
    {
        if(data.Part1 != 0 && data.Part2 != 0 && data.Part3 != 0 && data.Part4 != 0)
        {
            var str = Build.ToIdentifier((ushort)((Items)data.Part1).GetPartId())
                    + Build.ToIdentifier((ushort)((Items)data.Part2).GetPartId())
                    + Build.ToIdentifier((ushort)((Items)data.Part3).GetPartId())
                    + Build.ToIdentifier((ushort)((Items)data.Part4).GetPartId());
            if(str.Length == 8) str = str.Replace("+", "") + "++";
            return " " + str;
        }
        return "";
    }

    internal static VoyageType? DetectAddonType(AtkUnitBase* addon)
    {
        // 🔴 原本先把 NodeText **整個 Utf8String 複製到區域變數**再取位址 ——
        //    複製本身就是對毒指標 0xC0 起頭的 0x68 位元組做讀取,炸在那一行,
        //    而不是在後面看起來有判空的 ReadSeString 裡面。判空要做在取值之前。
        //    取不到就回 null ＝「認不出型別」,與既有的「兩個字串都比不中」同一條路徑。
        if(addon == null) return null;
        if(!Utils.TryGetNodeText(&addon->UldManager, 3, out var text)) return null;
        if(text.ContainsAny(StringComparison.OrdinalIgnoreCase, Lang.PanelAirship))
        {
            return VoyageType.Airship;
        }
        if(text.ContainsAny(StringComparison.OrdinalIgnoreCase, Lang.PanelSubmersible))
        {
            return VoyageType.Submersible;
        }
        return null;
    }

    internal static List<int> GetIsVesselNeedsRepair(string name, VoyageType type, out List<string> log)
    {
        return GetIsVesselNeedsRepair(GetVesselIndexByName(name, type), type, out log);
    }

    internal static List<int> GetIsVesselNeedsRepair(int num, VoyageType type, out List<string> log)
    {
        log = [];
        var ret = new List<int>();

        for(var i = 0; i < 4; i++)
        {
            var slot = GetVesselComponent(num, type, i);
            log.Add($"index: {i}, id: {slot->ItemId}, cond: {slot->Condition}");
            if(slot->ItemId == 0)
            {
                PluginLog.Warning($"Item id for airship component was 0 ({i})");
                continue;
            }
            if(slot->Condition == 0)
            {
                ret.Add(i);
            }
        }
        return ret;
    }


    internal static bool TryGetVesselComponent(int vesselIndex, VoyageType type, int slotIndex, out InventoryItem* component)
    {
        component = null;
        int begin;
        InventoryType itype;
        if(type == VoyageType.Airship)
        {
            begin = 30 + vesselIndex * 5;
            itype = InventoryType.HousingInteriorPlacedItems1;
        }
        else if(type == VoyageType.Submersible)
        {
            begin = vesselIndex * 5;
            itype = InventoryType.HousingInteriorPlacedItems2;
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(type));
        }
        var index = begin + slotIndex;
        var container = FFXIVClientStructs.FFXIV.Client.Game.InventoryManager.Instance()->GetInventoryContainer(itype);
        if(container == null) return false;
        // GetInventorySlot 是虛擬函式（進遊戲原生碼），對超界索引的行為未經證實，所以先自己夾。
        // 正常情況下 index 一定在範圍內（vesselIndex 由 GetVesselIndexByName 產生，查不到會丟例外）。
        if(index < 0 || index >= container->Size) return false;
        component = container->GetInventorySlot(index);
        return component != null;
    }

    /// <remarks>
    /// 🔴 讀不到時**丟例外，不回 null**。八個呼叫端全部拿到指標就直接解參考，回 null 只是把同一個
    /// 解參考往上搬一層；而靜默回退更糟——<see cref="GetIsVesselNeedsRepair"/> 會把「讀不到」
    /// 算成「這個零件不用修」，那是**錯的但看起來合理**的答案，船會帶著壞掉的零件出航。
    /// 同檔的 <see cref="GetVesselIndexByName"/> 對它自己的「查不到」也是丟例外，行為一致。
    /// 需要「讀不到就安靜跳過」語意的呼叫端請改用 <see cref="TryGetVesselComponent"/>。
    /// </remarks>
    internal static InventoryItem* GetVesselComponent(int vesselIndex, VoyageType type, int slotIndex)
    {
        if(!TryGetVesselComponent(vesselIndex, type, slotIndex, out var component))
        {
            throw new InvalidOperationException($"Could not read vessel component: vessel={vesselIndex}, type={type}, slot={slotIndex}");
        }
        return component;
    }

    internal static int GetVesselIndexByName(string name, VoyageType type)
    {
        var index = 0;
        // 讀不到就讓 h 保持 null，落到函式尾端既有的 throw —— 與 WorkshopTerritory 為 null
        // 時的行為完全一致（不用三元：指標與 null 字面值之間沒有隱含轉換，CS0173）。
        WorkshopTerritory* h = null;
        var housing = HousingManager.Instance();
        if(housing != null) h = housing->WorkshopTerritory;
        if(h != null)
        {
            if(type == VoyageType.Airship)
            {
                foreach(var x in h->Airship.Data)
                {
                    if(x.Name.Read() == name)
                    {
                        return index;
                    }
                    else
                    {
                        index++;
                    }
                }
            }
            else if(type == VoyageType.Submersible)
            {
                foreach(var x in h->Submersible.Data)
                {
                    if(x.Name.Read() == name)
                    {
                        return index;
                    }
                    else
                    {
                        index++;
                    }
                }
            }
            else
            {
                throw new ArgumentOutOfRangeException(nameof(type));
            }
        }
        throw new Exception($"Could not retrieve airship's index: {name}");
    }

    internal static string Seconds2Time(long seconds)
    {
        var t = TimeSpan.FromSeconds(seconds);
        var dlm = ":";
        if(t.Days > 0)
        {
            return $"{t.Days} days {t.Hours:D2}{dlm}{t.Minutes:D2}{dlm}{t.Seconds:D2}";
        }
        else
        {
            return $"{t.Hours:D2}{dlm}{t.Minutes:D2}{dlm}{t.Seconds:D2}";
        }
    }

    internal static bool AnyEnabledVesselsAvailable(this OfflineCharacterData data)
    {
        return data.AnyEnabledVesselsAvailable(VoyageType.Airship) || data.AnyEnabledVesselsAvailable(VoyageType.Submersible);
    }

    internal static bool AnyEnabledVesselsAvailable(this OfflineCharacterData data, VoyageType type)
    {
        return data.GetVesselData(type).Any(x => data.GetEnabledVesselsData(type).Contains(x.Name) && data.IsVesselAvailable(x, type));
    }

    internal static OfflineVesselData GetOfflineVesselData(this OfflineCharacterData data, string name, VoyageType type)
    {
        if(type == VoyageType.Submersible)
        {
            return data.OfflineSubmarineData.FirstOrDefault(x => x.Name == name);
        }
        else if(type == VoyageType.Airship)
        {
            return data.OfflineAirshipData.FirstOrDefault(x => x.Name == name);
        }
        return null;
    }

    internal static bool IsVesselAvailable(this OfflineCharacterData data, OfflineVesselData x, VoyageType type, int advanceSeconds = 0)
    {
        return (x.ReturnTime != 0 && x.GetRemainingSeconds() < C.UnsyncCompensation + advanceSeconds)
            ||
            (x.ReturnTime == 0 && data.GetAdditionalVesselData(x.Name, type).VesselBehavior.EqualsAny(VesselBehavior.LevelUp, VesselBehavior.Unlock, VesselBehavior.Use_plan, VesselBehavior.Redeploy));
    }

    internal static bool IsVesselNotDeployed(this OfflineVesselData x)
    {
        return x.ReturnTime == 0;
    }

    internal static bool AreAnyEnabledVesselsNotDeployed(this OfflineCharacterData data)
    {
        return AreAnyEnabledVesselsNotDeployed(data, VoyageType.Airship) && AreAnyEnabledVesselsNotDeployed(data, VoyageType.Submersible);
    }

    internal static bool AreAnyEnabledVesselsNotDeployed(this OfflineCharacterData data, VoyageType type)
    {
        var v = data.GetVesselData(type).Where(x => data.IsVesselAvailable(x, type) && data.GetEnabledVesselsData(type).Contains(x.Name));
        if(v.Any(x => x.IsVesselNotDeployed())) return true;
        return false;
    }

    internal static string GetNextCompletedVessel(VoyageType type)
    {
        var data = Data;
        var v = data.GetVesselData(type).Where(x => data.IsVesselAvailable(x, type) && data.GetEnabledVesselsData(type).Contains(x.Name));
        if(v.Any())
        {
            return v.FirstOrDefault(x => x.ReturnTime != 0)?.Name ?? v.First().Name;
        }
        return null;
    }

    internal static bool AreAnyEnabledVesselsReturnInNext(this OfflineCharacterData data, int seconds, bool all = false, bool ignorePerCharaSetting = false)
    {
        return data.AreAnyEnabledVesselsReturnInNext(VoyageType.Airship, seconds, all, ignorePerCharaSetting) || data.AreAnyEnabledVesselsReturnInNext(VoyageType.Submersible, seconds, all, ignorePerCharaSetting);
    }

    internal static bool CheckVesselForWaitTreshold(this OfflineCharacterData data, VoyageType type, int seconds)
    {
        if(C.MultiModeWorkshopConfiguration.MaxMinutesOfWaiting == 0) return true;
        var completedVesselExists = false;
        var upcomingVesselExists = false;
        foreach(var x in data.GetVesselData(type))
        {
            if(x.GetRemainingSeconds() < seconds)
            {
                completedVesselExists = true;
            }
            else if(x.GetRemainingSeconds() < C.MultiModeWorkshopConfiguration.MaxMinutesOfWaiting * 60)
            {
                upcomingVesselExists = true;
            }
        }
        if(completedVesselExists && !upcomingVesselExists) return false;
        return true;
    }

    internal static bool AreAnyEnabledVesselsReturnInNext(this OfflineCharacterData data, VoyageType type, int seconds, bool all = false, bool ignorePerCharaSetting = false)
    {
        if((all || (!ignorePerCharaSetting && data.MultiWaitForAllDeployables)) && data.CheckVesselForWaitTreshold(type, seconds))
        {
            var v = data.GetVesselData(type).Where(x => data.GetEnabledVesselsData(type).Contains(x.Name));
            return v.Any() && v.All(x => data.IsVesselAvailable(x, type, seconds));
        }
        else
        {
            var v = data.GetVesselData(type).Where(x => data.IsVesselAvailable(x, type, seconds) && data.GetEnabledVesselsData(type).Contains(x.Name));
            if(v.Any())
            {
                return true;
            }
        }
        return false;
    }

    internal static bool? CanBeSelected(string FullName)
    {
        if(TryGetAddonByName<AtkUnitBase>("AirShipExploration", out var addon) && IsAddonReady(addon))
        {
            var reader = new ReaderAirShipExploration(addon);
            for(var i = 0; i < reader.Destinations.Count; i++)
            {
                var dest = reader.Destinations[i];
                if(dest.NameFull == FullName)
                {
                    return dest.CanBeSelected;
                }
            }
        }
        return null;
    }

    /// <summary>
    /// 依名稱點選航線上的一個點位。
    ///
    /// 🔴 這個方法對「找不到」與「遊戲判定不可選」兩種情形都是**靜默跳過**的，
    /// 呼叫端必須看回傳值，不能假設呼叫完就一定選上了。
    /// （2026-08-08 實機證據：點位計畫要求 O→J→M→R→Z 五點，M/R/Z 三點的 StatusFlag=3
    /// 被靜默略過，整趟照樣出航，使用者看到的是「設了 MROJZ 卻跑了 OJ」——
    /// 而 Debug 以外的等級一行訊息都沒有，只能靠猜。）
    ///
    /// 🔴 行為刻意維持原樣（跳過、繼續往下走），這裡只補回傳值讓呼叫端能記 log。
    /// </summary>
    /// <param name="statusFlag">遊戲給這個點位的狀態旗標；面板上找不到該點位時是 <see cref="uint.MaxValue"/>。</param>
    internal static RoutePointPickResult SelectRoutePointSafe(string FullOrShortName, out uint statusFlag)
    {
        statusFlag = uint.MaxValue;
        Log($"Requested selection of {FullOrShortName} point.");
        if(TryGetAddonByName<AtkUnitBase>("AirShipExploration", out var addon) && IsAddonReady(addon))
        {
            var reader = new ReaderAirShipExploration(addon);
            Log($"  Reader initialized with {reader.Destinations.Count} destinations: {reader.Destinations.Select(x => $"{x}").Join("\n")}");
            for(var i = 0; i < reader.Destinations.Count; i++)
            {
                var dest = reader.Destinations[i];
                Log($"  Comparing {i} {dest} with {FullOrShortName}");
                if(FullOrShortName.EqualsIgnoreCaseAny(dest.NameFull, dest.NameShort))
                {
                    statusFlag = dest.StatusFlag;
                    Log($"    Found {FullOrShortName}, CanBeSelected = {dest.CanBeSelected}");
                    if(dest.CanBeSelected)
                    {
                        return SelectRoutePointSafe(i) ? RoutePointPickResult.Selected : RoutePointPickResult.NotSelectable;
                    }
                    return RoutePointPickResult.NotSelectable;
                }
                else
                {
                    Log($"    Negative comparison result");
                }
            }
            return RoutePointPickResult.NotFound;
        }
        return RoutePointPickResult.PanelUnavailable;
    }

    /// <summary>
    /// 依索引點選航線上的一個點位。回傳值＝「有沒有真的送出選取」。
    /// ⚠️ 這裡會**重讀一次** reader 再判一次 CanBeSelected，所以呼叫端在上一幀讀到的
    /// 「可選」不保證這裡也成立 —— 兩者不一致時以這裡的回傳值為準。
    /// </summary>
    internal static bool SelectRoutePointSafe(int which)
    {
        Log($"Requested selection of point by ID={which}.");
        if(TryGetAddonByName<AtkUnitBase>("AirShipExploration", out var addon) && IsAddonReady(addon))
        {
            var reader = new ReaderAirShipExploration(addon);
            Log($"  Reader initialized with {reader.Destinations.Count} destinations: {reader.Destinations.Select(x => $"{x}").Join("\n")}");
            if(which >= reader.Destinations.Count) throw new ArgumentOutOfRangeException(nameof(which));
            var dest = reader.Destinations[which];
            Log($"  Destination {dest}");
            if(dest.CanBeSelected)
            {
                VoyageUtils.Log($"  Selecting {dest.NameFull} / {which}");
                P.Memory.SelectRoutePointUnsafe(which);
                return true;
            }
            else
            {
                VoyageUtils.Log($"  Can't select {dest.NameFull} / {which}, skipping");
            }
        }
        return false;
    }
}
