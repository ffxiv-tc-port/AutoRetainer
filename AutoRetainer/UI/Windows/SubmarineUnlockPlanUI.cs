using AutoRetainer.Modules.Voyage;
using AutoRetainer.Modules.Voyage.VoyageCalculator;
using AutoRetainerAPI.Configuration;
using ECommons.GameHelpers;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;
using Newtonsoft.Json;

namespace AutoRetainer.UI.Windows;

internal unsafe class SubmarineUnlockPlanUI : Window
{
    internal string SelectedPlanGuid = Guid.Empty.ToString();
    internal string SelectedPlanName => VoyageUtils.GetSubmarineUnlockPlanByGuid(SelectedPlanGuid)?.Name ?? Loc.T("No or unknown plan selected");
    internal SubmarineUnlockPlan SelectedPlan => VoyageUtils.GetSubmarineUnlockPlanByGuid(SelectedPlanGuid);

    public SubmarineUnlockPlanUI() : base("Submersible Voyage Unlockable Planner")
    {
        P.WindowSystem.AddWindow(this);
    }

    internal Dictionary<uint, bool> RouteUnlockedCache = [];
    internal Dictionary<uint, bool> RouteExploredCache = [];
    internal int NumUnlockedSubs = 0;

    internal bool IsMapUnlocked(uint map, bool bypassCache = false)
    {
        if(!IsSubDataAvail()) return false;
        var throttle = $"Voyage.MapUnlockedCheck.{map}";
        if(!bypassCache && RouteUnlockedCache.TryGetValue(map, out var val) && !EzThrottler.Check(throttle))
        {
            return val;
        }
        else
        {
            EzThrottler.Throttle(throttle, 2500, true);
            RouteUnlockedCache[map] = HousingManager.IsSubmarineExplorationUnlocked((byte)map);
            return RouteUnlockedCache[map];
        }
    }

    internal bool IsMapExplored(uint map, bool bypassCache = false)
    {
        if(!IsSubDataAvail()) return false;
        var throttle = $"Voyage.MapExploredCheck.{map}";
        if(!bypassCache && RouteExploredCache.TryGetValue(map, out var val) && !EzThrottler.Check(throttle))
        {
            return val;
        }
        else
        {
            EzThrottler.Throttle(throttle, 2500, true);
            RouteExploredCache[map] = HousingManager.IsSubmarineExplorationExplored((byte)map);
            return RouteExploredCache[map];
        }
    }

    internal int? GetNumUnlockedSubs()
    {
        if(!IsSubDataAvail()) return null;
        NumUnlockedSubs = 1 + Unlocks.PointToUnlockPoint.Where(x => x.Value.Sub).Where(x => IsMapExplored(x.Key)).Count();
        return NumUnlockedSubs;
    }

    internal bool IsSubDataAvail()
    {
        // 原本三行都判了 WorkshopTerritory，卻沒有一行判 HousingManager 本身。
        var housing = HousingManager.Instance();
        if(housing == null) return false;
        var workshop = housing->WorkshopTerritory;
        if(workshop == null) return false;
        if(workshop->Submersible.Data.Length == 0) return false;
        if(workshop->Submersible.Data[0].Name[0] == 0) return false;
        return true;
    }

    internal int GetAmountOfOtherPlanUsers(string guid)
    {
        var i = 0;
        C.OfflineData.Where(x => x.CID != Player.CID).Each(x => i += x.AdditionalSubmarineData.Count(a => a.Value.SelectedUnlockPlan == guid));
        return i;
    }

    public override void Draw()
    {
        C.SubmarineUnlockPlans.RemoveAll(x => x.Delete);
        ImGuiEx.InputWithRightButtonsArea(Loc.T("SUPSelector"), () =>
        {
            if(ImGui.BeginCombo("##supsel", SelectedPlanName, ImGuiComboFlags.HeightLarge))
            {
                foreach(var x in C.SubmarineUnlockPlans)
                {
                    if(ImGui.Selectable(x.Name + $"##{x.GUID}"))
                    {
                        SelectedPlanGuid = x.GUID;
                    }
                }
                ImGui.EndCombo();
            }
        }, () =>
        {
            if(ImGui.Button(Loc.T("New plan")))
            {
                var x = new SubmarineUnlockPlan();
                x.Name = $"Plan {x.GUID}";
                C.SubmarineUnlockPlans.Add(x);
                SelectedPlanGuid = x.GUID;
            }
        });
        ImGui.Separator();
        if(SelectedPlan == null)
        {
            ImGuiEx.Text(Loc.T("No or unknown plan is selected"));
        }
        else
        {
            if(Data != null)
            {
                var users = GetAmountOfOtherPlanUsers(SelectedPlanGuid);
                var my = Data.AdditionalSubmarineData.Where(x => x.Value.SelectedUnlockPlan == SelectedPlanGuid);
                if(users == 0)
                {
                    if(!my.Any())
                    {
                        ImGuiEx.TextWrapped(Loc.T(SharedText.PlanNotUsedByAnySubmersibles));
                    }
                    else
                    {
                        ImGuiEx.TextWrapped($"{Loc.T("This plan is used by")} {my.Select(X => X.Key).Print()}.");
                    }
                }
                else
                {
                    if(!my.Any())
                    {
                        ImGuiEx.TextWrapped($"{Loc.T("This plan is used by")} {users} {Loc.T("submersibles of your other characters.")}");
                    }
                    else
                    {
                        ImGuiEx.TextWrapped($"{Loc.T("This plan is used by")} {my.Select(X => X.Key).Print()} {Loc.T("and")} {users} {Loc.T("more submersibles on other characters.")}");
                    }
                }
            }
            if(C.DefaultSubmarineUnlockPlan == SelectedPlanGuid)
            {
                ImGuiEx.Text(Loc.T("This plan is set as default."));
                ImGui.SameLine();
                if(ImGui.SmallButton(Loc.T("Reset"))) C.DefaultSubmarineUnlockPlan = "";
            }
            else
            {
                if(ImGui.SmallButton(Loc.T("Set this plan as default"))) C.DefaultSubmarineUnlockPlan = SelectedPlanGuid;
            }
            ImGuiEx.TextV(Loc.T("Name: "));
            ImGui.SameLine();
            ImGuiEx.SetNextItemFullWidth();
            ImGui.InputText($"##planname", ref SelectedPlan.Name, 100);
            ImGuiEx.LineCentered($"planbuttons", () =>
            {
                ImGuiEx.TextV(Loc.T("Apply this plan to:"));
                ImGui.SameLine();
                if(ImGui.Button(Loc.T("ALL submersibles")))
                {
                    C.OfflineData.Each(x => x.AdditionalSubmarineData.Each(s => s.Value.SelectedUnlockPlan = SelectedPlanGuid));
                }
                ImGui.SameLine();
                if(ImGui.Button(Loc.T("Current character's submersibles")))
                {
                    Data.AdditionalSubmarineData.Each(s => s.Value.SelectedUnlockPlan = SelectedPlanGuid);
                }
                ImGui.SameLine();
                if(ImGui.Button(Loc.T("No submersibles")))
                {
                    C.OfflineData.Each(x => x.AdditionalSubmarineData.Where(s => s.Value.SelectedUnlockPlan == SelectedPlanGuid).Each(s => s.Value.SelectedUnlockPlan = Guid.Empty.ToString()));
                }
            });
            ImGuiEx.LineCentered($"planbuttons2", () =>
            {
                if(ImGui.Button(Loc.T("Copy plan settings")))
                {
                    Copy(JsonConvert.SerializeObject(SelectedPlan));
                }
                ImGui.SameLine();
                if(ImGui.Button(Loc.T("Paste plan settings")))
                {
                    try
                    {
                        SelectedPlan.CopyFrom(JsonConvert.DeserializeObject<SubmarineUnlockPlan>(Paste()));
                    }
                    catch(Exception ex)
                    {
                        DuoLog.Error(string.Format(Loc.T("Could not import plan: {0}"), ex.Message));
                        ex.Log();
                    }
                }
                ImGui.SameLine();
                if(ImGuiEx.ButtonCtrl(Loc.T("Delete this plan")))
                {
                    SelectedPlan.Delete = true;
                }
                ImGui.SameLine();
                if(ImGui.Button(Loc.T("Help")))
                {
                    Svc.Chat.Print(Loc.T("Here is the list of all points that can be unlocked. Whenever a plugin needs to select something to unlock, a first available destination will be chosen from this list. Please note that you can NOT simply specify end point of unlocking, you need to select ALL destinations on your way."));
                }
            });
            if(ImGui.BeginChild("Plan"))
            {
                if(!IsSubDataAvail())
                {
                    ImGuiEx.TextWrapped(Loc.T("Access submarine list to retrieve data."));
                }
                ImGui.Checkbox($"{Loc.T("Unlock submarine slots. Current slots: ")}{GetNumUnlockedSubs()?.ToString() ?? Loc.T("Unknown")}/4", ref SelectedPlan.UnlockSubs);
                ImGuiEx.TextWrapped(Loc.T("Unlocking slots is always prioritized over unlocking routes."));
                ImGui.Checkbox(Loc.T("Enforce Spam one destination mode in Deep sea site."), ref SelectedPlan.EnforceDSSSinglePoint);
                ImGui.Checkbox(Loc.T("Set this plan as enforced."), ref SelectedPlan.EnforcePlan);
                ImGuiEx.HelpMarker(Loc.T("Any point selected for unlock in this map will be executed by every single eligible submarine until everything is actually unlocked"));
                if(ImGui.BeginTable("##planTable", 3, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
                {
                    ImGui.TableSetupColumn(Loc.T("Zone"), ImGuiTableColumnFlags.WidthStretch);
                    ImGui.TableSetupColumn(Loc.T("Map"));
                    ImGui.TableSetupColumn(Loc.T("Unlocked by"));
                    ImGui.TableHeadersRow();
                    foreach(var x in Unlocks.PointToUnlockPoint)
                    {
                        if(x.Value.Point < 9000)
                        {
                            ImGui.PushID($"{x.Key}");
                            ImGui.TableNextRow();
                            ImGui.TableNextColumn();
                            var data = Svc.Data.GetExcelSheet<SubmarineExploration>().GetRowOrDefault(x.Key);
                            if(data != null)
                            {
                                try
                                {
                                    var col = IsMapUnlocked(x.Key);
                                    ImGuiEx.CollectionCheckbox($"{data?.FancyDestination()}", x.Key, SelectedPlan.ExcludedRoutes, true);
                                    if(col) ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.ParsedGreen);
                                    if(col) ImGui.PopStyleColor();
                                    ImGui.TableNextColumn();
                                    ImGuiEx.TextV($"{data?.Map.ValueNullable?.Name}");
                                    ImGui.TableNextColumn();
                                    var notEnabled = !SelectedPlan.ExcludedRoutes.Contains(x.Key) && SelectedPlan.ExcludedRoutes.Contains(x.Value.Point);
                                    ImGuiEx.TextV(notEnabled ? ImGuiColors.DalamudRed : null, $"{Svc.Data.GetExcelSheet<SubmarineExploration>().GetRowOrDefault(x.Value.Point)?.FancyDestination()}");
                                }
                                catch(Exception e)
                                {
                                    e.Log();
                                }
                            }
                            ImGui.PopID();
                        }
                    }
                    ImGui.EndTable();
                }
                if(ImGui.CollapsingHeader(Loc.T("Display current point exploration order")))
                {
                    // 點位 ID 來自解鎖計畫,而計畫可以整份從剪貼簿貼入(本檔 Paste plan settings)。
                    // 裸 GetRow 查無此列會擲例外,而這裡在 Draw 裡 —— Dalamud 攔到之後會把整個
                    // 外掛的 Draw 委派設為 null。未知點位畫成 "?<id>" 讓問題在列上看得見。
                    var explorationSheet = Svc.Data.GetExcelSheet<SubmarineExploration>();
                    ImGuiEx.Text(SelectedPlan.GetPrioritizedPointList().Select(x => $"{(explorationSheet.TryGetRow(x.point, out var row) ? row.Destination.ToString() : $"?{x.point}")} ({x.justification})").Join("\n"));
                }
            }
            ImGui.EndChild();
        }
    }
}
