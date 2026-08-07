using AutoRetainer.Modules.Voyage;
using AutoRetainer.Modules.Voyage.VoyageCalculator;
using AutoRetainerAPI.Configuration;
using ECommons.GameHelpers;
using Lumina.Excel.Sheets;
using Newtonsoft.Json;

namespace AutoRetainer.UI.Windows;

internal unsafe class SubmarinePointPlanUI : Window
{
    internal string SelectedPlanGuid = Guid.Empty.ToString();
    internal string SelectedPlanName => VoyageUtils.GetSubmarinePointPlanByGuid(SelectedPlanGuid).GetPointPlanName();
    internal SubmarinePointPlan SelectedPlan => VoyageUtils.GetSubmarinePointPlanByGuid(SelectedPlanGuid);

    public SubmarinePointPlanUI() : base("Submersible Voyage Route Planner")
    {
        P.WindowSystem.AddWindow(this);
    }

    internal int GetAmountOfOtherPlanUsers(string guid)
    {
        var i = 0;
        C.OfflineData.Where(x => x.CID != Player.CID).Each(x => i += x.AdditionalSubmarineData.Count(a => a.Value.SelectedPointPlan == guid));
        return i;
    }

    public override void Draw()
    {
        if(C.SubmarinePointPlans.RemoveAll(x => x.Delete) > 0)
        {
            // 計畫刪掉了就把它留在「按航距裁切」集合裡的 GUID 一起清掉，避免無主的鍵越積越多。
            C.SubmarinePointPlansTrimToRange.RemoveWhere(guid => !C.SubmarinePointPlans.Any(x => x.GUID == guid));
        }
        ImGuiEx.InputWithRightButtonsArea(Loc.T("SUPSelector"), () =>
        {
            if(ImGui.BeginCombo("##supsel", SelectedPlanName, ImGuiComboFlags.HeightLarge))
            {
                foreach(var x in C.SubmarinePointPlans)
                {
                    if(ImGui.Selectable(x.GetPointPlanName() + $"##{x.GUID}"))
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
                var x = new SubmarinePointPlan
                {
                    Name = $""
                };
                C.SubmarinePointPlans.Add(x);
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
                var my = Data.AdditionalSubmarineData.Where(x => x.Value.SelectedPointPlan == SelectedPlanGuid);
                if(users == 0)
                {
                    if(!my.Any())
                    {
                        ImGuiEx.TextWrapped(Loc.T("This plan is not used by any submersibles."));
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
                    C.OfflineData.Each(x => x.AdditionalSubmarineData.Each(s => s.Value.SelectedPointPlan = SelectedPlanGuid));
                }
                ImGui.SameLine();
                if(ImGui.Button(Loc.T("Current character's submersibles")))
                {
                    Data.AdditionalSubmarineData.Each(s => s.Value.SelectedPointPlan = SelectedPlanGuid);
                }
                ImGui.SameLine();
                if(ImGui.Button(Loc.T("No submersibles")))
                {
                    C.OfflineData.Each(x => x.AdditionalSubmarineData.Where(s => s.Value.SelectedPointPlan == SelectedPlanGuid).Each(s => s.Value.SelectedPointPlan = Guid.Empty.ToString()));
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
                        SelectedPlan.CopyFrom(JsonConvert.DeserializeObject<SubmarinePointPlan>(Paste()));
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
            });

            ImGui.Separator();
            {
                var trim = PointPlanRange.IsTrimEnabled(SelectedPlan);
                if(ImGui.Checkbox(Loc.T("Only pick the sectors this submersible can actually reach"), ref trim))
                {
                    PointPlanRange.SetTrimEnabled(SelectedPlan, trim);
                }
                ImGuiEx.Tooltip(Loc.T("Before deploying, the estimated exploration distance of this plan is compared with the submersible's range. Sectors are dropped from the END of the list until the route fits, and the submersible is deployed on what is left.\n\nThe order of the list is your priority order: put the sectors you want most at the top. The travel order is optimized separately for the shortest route, so it does not have to match the order shown here.\n\nEvery submersible is evaluated on its own, so the same plan can send different submersibles to a different number of sectors, and a submersible will automatically pick up more sectors as it ranks up.\n\nIf anything cannot be calculated (submersible data unreadable, sheet lookup failed, not even the first sector is reachable) the plan is used unchanged, exactly as it behaves today."));
                if(trim)
                {
                    var ladder = PointPlanRange.GetRequiredRangeLadder(SelectedPlan);
                    if(PointPlanRange.TryGetCurrentSubmarineInfo(out var info))
                    {
                        int? reachable = 0;
                        for(var i = 0; i < ladder.Length; i++)
                        {
                            if(ladder[i] < 0) { reachable = null; break; }
                            if(ladder[i] > info.Range) break;
                            reachable = i + 1;
                        }
                        ImGuiEx.Text(string.Format(Loc.T("Current submersible: {0} (Rank {1}, range {2}) - would run {3} of {4} sectors"),
                            info.Name, info.Rank, info.Range, reachable?.ToString() ?? "?", SelectedPlan.Points.Count));
                        if(info.RangeMismatch)
                        {
                            ImGuiEx.Text(ImGuiColors.DalamudOrange, string.Format(Loc.T("Sheet-derived range {0} disagrees with the value read from the game ({1}). The sheet value is used."), info.SheetRange, info.NativeRange));
                        }
                    }
                    else
                    {
                        ImGuiEx.Text(ImGuiColors.DalamudGrey, Loc.T("Current submersible: unknown - open this window while a submersible panel is open to see how far it gets."));
                    }
                }
            }

            ImGuiEx.EzTableColumns("SubPlan",
            [
                delegate
                {
                    if(ImGui.BeginChild("col1"))
                    {
                        foreach(var x in Svc.Data.GetExcelSheet<SubmarineExploration>())
                        {
                            if(x.Destination.GetText() == "")
                            {
                                if(x.Map.Value.Name.GetText() != "")
                                {
                                    ImGui.Separator();
                                    ImGuiEx.Text($"{x.Map.Value.Name}:");
                                }
                                continue;
                            }
                            var disabled = !SelectedPlan.GetMapId().EqualsAny(0u, x.Map.RowId) || SelectedPlan.Points.Count >= 5 && !SelectedPlan.Points.Contains(x.RowId);
                            if (disabled) ImGui.BeginDisabled();
                            var cont = SelectedPlan.Points.Contains(x.RowId);
                            if (ImGui.Selectable(x.FancyDestination(), cont))
                            {
                                SelectedPlan.Points.Toggle(x.RowId);
                            }
                            if (disabled) ImGui.EndDisabled();
                        }
                    }
                    ImGui.EndChild();
                }, delegate
                {
                    if(ImGui.BeginChild("Col2"))
                    {
                        var map = SelectedPlan.GetMap();
                        if(map != null)
                        {
                            ImGuiEx.Text($"{map.Value.Name}:");
                        }
                        var toRem = -1;
                        var ladder = PointPlanRange.GetRequiredRangeLadder(SelectedPlan);
                        for (var i = 0; i < SelectedPlan.Points.Count; i++)
                        {
                            ImGui.PushID(i);
                            if(ImGui.ArrowButton($"##up", ImGuiDir.Up) && i > 0)
                            {
                                (SelectedPlan.Points[i-1], SelectedPlan.Points[i]) = (SelectedPlan.Points[i], SelectedPlan.Points[i-1]);
                            }
                            ImGui.SameLine();
                            if(ImGui.ArrowButton($"##down", ImGuiDir.Down) && i < SelectedPlan.Points.Count - 1)
                            {
                                (SelectedPlan.Points[i+1], SelectedPlan.Points[i]) = (SelectedPlan.Points[i], SelectedPlan.Points[i+1]);
                            }
                            ImGui.SameLine();
                            if (ImGuiEx.IconButton(FontAwesomeIcon.Trash))
                            {
                                toRem = i;
                            }
                            ImGui.SameLine();
                            // 這個點要被跑到所需的航行距離（含它前面所有點）。算不到就畫「?」不畫 0 ——
                            // 把「不知道」畫成 0 會直接誤導使用者以為這個點不用航距。
                            var needed = i < ladder.Length ? ladder[i] : -1;
                            // 刻意用 ASCII 的 ">=" 而不是 U+2265 —— 遊戲字型缺字時會靜默畫成空白。
                            ImGuiEx.Text(ImGuiColors.DalamudGrey, needed >= 0 ? $">={needed}" : ">=?");
                            var pointRow = VoyageUtils.GetSubmarineExploration(SelectedPlan.Points[i]);
                            ImGuiEx.Tooltip(string.Format(Loc.T("Requires a submersible range of at least {0} to include this sector (and everything above it) in the voyage.\nThis sector needs Rank {1}."),
                                needed >= 0 ? needed.ToString() : "?", pointRow == null ? "?" : pointRow.Value.RankReq.ToString()));
                            ImGui.SameLine();
                            ImGuiEx.Text($"{VoyageUtils.GetSubmarineExploration(SelectedPlan.Points[i])?.FancyDestination()}");
                            ImGui.PopID();
                        }
                        if(toRem > -1)
                        {
                            SelectedPlan.Points.RemoveAt(toRem);
                        }
                    }
                    ImGui.EndChild();
                }
            ]);
        }
    }
}
