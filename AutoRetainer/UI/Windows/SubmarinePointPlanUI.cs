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

    // 「從 Submarine Tracker 匯入」的暫存狀態。只在繪製執行緒上讀寫。
    private string ImportSearch = "";
    private List<Item> ImportCandidates = [];
    private uint ImportItemId;
    private string ImportResult = "";

    /// <summary>一份計畫最多就是 5 個點位，和上面清單的上限同一個數。</summary>
    private const int ImportMaxPoints = 5;

    /// <summary>搜尋結果最多列幾筆。只是畫面上限，不影響比對範圍。</summary>
    private const int ImportMaxCandidates = 30;

    public SubmarinePointPlanUI() : base("Submersible Voyage Route Planner")
    {
        P.WindowSystem.AddWindow(this);
    }

    /// <summary>左欄清單：MapHeader 非 null＝地圖分隔標題列，否則是可選的點位。</summary>
    internal readonly record struct PointListEntry(string MapHeader, uint RowId, uint MapId, string Label);

    // 表在插件存活期間不變，取表語言也固定是客戶端語言，所以第一次畫時建好就一直用。只在繪製執行緒上讀寫。
    internal static List<PointListEntry> PointList => field ??= BuildPointList();

    internal static List<PointListEntry> BuildPointList()
    {
        var list = new List<PointListEntry>();
        foreach(var x in Svc.Data.GetExcelSheet<SubmarineExploration>())
        {
            if(x.Destination.GetText() == "")
            {
                if(x.Map.Value.Name.GetText() != "") list.Add(new($"{x.Map.Value.Name}:", x.RowId, x.Map.RowId, null));
                continue;
            }
            list.Add(new(null, x.RowId, x.Map.RowId, x.FancyDestination()));
        }
        return list;
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
            DrawSubmarineTrackerImport();

            ImGui.Separator();
            // 這艘潛水艇實際會跑到的點位。null＝算不出來或沒開裁切，下面的清單要畫「?」不要畫成「不跑」。
            List<uint> trimPreview = null;
            var trimEnabled = PointPlanRange.IsTrimEnabled(SelectedPlan);
            {
                var trim = trimEnabled;
                if(ImGui.Checkbox(Loc.T("Only pick the sectors this submersible can actually reach"), ref trim))
                {
                    PointPlanRange.SetTrimEnabled(SelectedPlan, trim);
                }
                ImGuiEx.Tooltip(Loc.T("Before deploying, the estimated exploration distance of this plan is compared with the submersible's range. Every combination of sectors is evaluated and the one that fits the range while covering the MOST sectors is used, so a sector in the middle of the list can be skipped to make room for two others.\n\nThe order of the list is your priority order: when several combinations reach the same number of sectors, the one using the sectors nearest the top wins. The travel order is optimized separately for the shortest route, so it does not have to match the order shown here.\n\nEvery submersible is evaluated on its own, so the same plan can send different submersibles to a different number of sectors, and a submersible will automatically pick up more sectors as it ranks up.\n\nIf anything cannot be calculated (submersible data unreadable, sheet lookup failed, not even the first sector is reachable) the plan is used unchanged, exactly as it behaves today."));
                if(trimEnabled)
                {
                    if(PointPlanRange.TryGetCurrentSubmarineInfo(out var info))
                    {
                        // 🔴 刻意呼叫與出航時同一支選擇函式：這裡自己另外推算的話，
                        // 演算法一改兩邊就會靜默不一致（顯示 2 點、實際跑 3 點）。
                        var havePreview = PointPlanRange.TryGetTrimPreview(SelectedPlan, info, out var kept, out var previewDistance);
                        if(havePreview) trimPreview = kept;
                        ImGuiEx.Text(string.Format(Loc.T("Current submersible: {0} (Rank {1}, range {2}) - would run {3} of {4} sectors"),
                            info.Name, info.Rank, info.Range, havePreview ? kept.Count.ToString() : "?", SelectedPlan.Points.Count));
                        if(havePreview)
                        {
                            ImGuiEx.Text(ImGuiColors.DalamudGrey, string.Format(Loc.T("Route: {0} (estimated exploration distance {1} of {2})"),
                                kept.Select(x => VoyageUtils.GetSubmarineExploration(x)?.Location.ToString() ?? $"?{x}").Join(" > "), previewDistance, info.Range));
                        }
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
                        foreach(var x in PointList)
                        {
                            if(x.MapHeader != null)
                            {
                                ImGui.Separator();
                                ImGuiEx.Text(x.MapHeader);
                                continue;
                            }
                            var disabled = !SelectedPlan.GetMapId().EqualsAny(0u, x.MapId) || SelectedPlan.Points.Count >= 5 && !SelectedPlan.Points.Contains(x.RowId);
                            if (disabled) ImGui.BeginDisabled();
                            var cont = SelectedPlan.Points.Contains(x.RowId);
                            if (ImGui.Selectable(x.Label, cont))
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
                            // 這艘船到底跑不跑這個點。⚠️「不知道」要在列上看得見，不能不畫 ——
                            // 什麼都不畫會被讀成「不跑」。用 ASCII 標記，理由同下面的 ">="。
                            if(trimEnabled)
                            {
                                if(trimPreview == null)
                                {
                                    ImGuiEx.Text(ImGuiColors.DalamudGrey, "[?]");
                                    ImGuiEx.Tooltip(Loc.T("Cannot tell whether this sector will be run: no submersible panel is open, or the calculation failed."));
                                }
                                else if(trimPreview.Contains(SelectedPlan.Points[i]))
                                {
                                    ImGuiEx.Text(ImGuiColors.HealerGreen, "[v]");
                                    ImGuiEx.Tooltip(Loc.T("The current submersible will run this sector."));
                                }
                                else
                                {
                                    ImGuiEx.Text(ImGuiColors.DalamudGrey, "[ ]");
                                    ImGuiEx.Tooltip(Loc.T("The current submersible will skip this sector: dropping it lets more of the other sectors fit into its range."));
                                }
                                ImGui.SameLine();
                            }
                            // 這個點要被跑到所需的航行距離（含它前面所有點）。算不到就畫「?」不畫 0 ——
                            // 把「不知道」畫成 0 會直接誤導使用者以為這個點不用航距。
                            var needed = i < ladder.Length ? ladder[i] : -1;
                            // 刻意用 ASCII 的 ">=" 而不是 U+2265 —— 遊戲字型缺字時會靜默畫成空白。
                            ImGuiEx.Text(ImGuiColors.DalamudGrey, needed >= 0 ? $">={needed}" : ">=?");
                            var pointRow = VoyageUtils.GetSubmarineExploration(SelectedPlan.Points[i]);
                            ImGuiEx.Tooltip(string.Format(Loc.T("Requires a submersible range of at least {0} to include this sector AND everything above it in one voyage.\nA submersible with less range can still reach this sector if one of the sectors above it is skipped instead.\nThis sector needs Rank {1}."),
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

    /// <summary>
    /// 問 Submarine Tracker「這件物品在哪撈」，把同一張地圖的點位填進目前的計畫。
    /// 只有按下按鈕那一幀才會寫 SelectedPlan.Points；GUID 與計畫名稱都不動。
    /// </summary>
    private void DrawSubmarineTrackerImport()
    {
        ImGuiEx.TextV(Loc.T("Import from Submarine Tracker: pick an item"));
        ImGuiEx.Tooltip(Loc.T("Asks Submarine Tracker which sectors drop the selected item, keeps the ones on this plan's map, and puts the first 5 into the list above.\n\nSubmarine Tracker orders them by loot tier, while AutoRetainer's own trimming picks whichever combination covers the MOST sectors within the submersible's range. The two do not optimize for the same thing, so look the result over before using it."));
        ImGui.SameLine();

        if(!SubmarineTrackerIPC.IsAvailable)
        {
            ImGuiEx.Text(ImGuiColors.DalamudGrey, string.Format(Loc.T("Submarine Tracker is not available (need API {0}, saw {1})"),
                SubmarineTrackerIPC.RequiredApiVersion,
                SubmarineTrackerIPC.LastSeenVersion < 0 ? "?" : SubmarineTrackerIPC.LastSeenVersion.ToString()));
            return;
        }

        ImGui.SetNextItemWidth(200f * ImGuiHelpers.GlobalScale);
        if(ImGui.InputTextWithHint("##stitemsearch", Loc.T("Item name"), ref ImportSearch, 100))
        {
            RebuildImportCandidates();
        }
        ImGui.SameLine();
        ImGuiEx.Text(ImportItemId == 0 ? ImGuiColors.DalamudGrey : null,
            ImportItemId == 0 ? Loc.T("no item picked") : $"{Svc.Data.GetExcelSheet<Item>().GetRowOrDefault(ImportItemId)?.Name}");
        ImGui.SameLine();

        // 計畫裡已經有點位時要按住 CTRL 才按得下去 —— 匯入是整份覆寫，不是附加。
        var disabled = ImportItemId == 0;
        if(disabled) ImGui.BeginDisabled();
        var pressed = SelectedPlan.Points.Count > 0
            ? ImGuiEx.ButtonCtrl(Loc.T("Import sectors"), Loc.T(" (Hold CTRL)"))
            : ImGui.Button(Loc.T("Import sectors"));
        if(disabled) ImGui.EndDisabled();
        if(pressed) RunSubmarineTrackerImport();

        if(ImportCandidates.Count > 0 && ImGui.BeginListBox("##stitemlist", new Vector2(320f * ImGuiHelpers.GlobalScale, 90f * ImGuiHelpers.GlobalScale)))
        {
            var picked = 0u;
            foreach(var item in ImportCandidates)
            {
                if(ImGui.Selectable($"{item.Name}##st{item.RowId}", item.RowId == ImportItemId))
                {
                    picked = item.RowId;
                }
            }
            ImGui.EndListBox();
            if(picked != 0)
            {
                ImportItemId = picked;
                ImportCandidates = [];
                ImportResult = "";
            }
        }

        if(ImportResult != "") ImGuiEx.Text(ImGuiColors.DalamudGrey, ImportResult);
    }

    /// <summary>
    /// 重建搜尋結果。只在搜尋字串改變的那一幀跑，不是每幀。
    /// 先用名稱過濾再問對方有沒有資料，使用者才不會挑完才被告知沒東西。
    /// </summary>
    private void RebuildImportCandidates()
    {
        ImportCandidates = [];
        if(ImportSearch.Length < 2) return;
        foreach(var item in Svc.Data.GetExcelSheet<Item>())
        {
            var name = item.Name.ToString();
            if(name == "" || !name.Contains(ImportSearch, StringComparison.OrdinalIgnoreCase)) continue;
            if(!SubmarineTrackerIPC.TryGetSectorsForItem(item.RowId, out var sectors) || sectors.Count == 0) continue;
            ImportCandidates.Add(item);
            if(ImportCandidates.Count >= ImportMaxCandidates) break;
        }
    }

    /// <summary>覆寫目前計畫的點位。每一條失敗路徑都會在畫面上留一句話，不做無聲的 no-op。</summary>
    private void RunSubmarineTrackerImport()
    {
        ImportResult = "";
        if(!SubmarineTrackerIPC.TryGetSectorsForItem(ImportItemId, out var sectors))
        {
            ImportResult = Loc.T("Submarine Tracker did not answer.");
            return;
        }

        var sheet = Svc.Data.GetExcelSheet<SubmarineExploration>();
        var rows = new List<SubmarineExploration>();
        foreach(var id in sectors)
        {
            // 對方的點位 id 來自它捆綁的外部資料檔，本地表不保證有這一列。
            // 台服的 129~159 是 Map=0 的空白列，沒有目的地名稱的列是地圖標題列 —— 兩種都不是點位。
            if(!sheet.TryGetRow(id, out var row)) continue;
            if(row.Map.RowId == 0 || row.Destination.GetText() == "") continue;
            rows.Add(row);
        }
        if(rows.Count == 0)
        {
            ImportResult = Loc.T("Submarine Tracker has no sector for this item.");
            return;
        }

        // 計畫還是空的時候沒有「目前地圖」，就採用排最前面那個點位的地圖。
        var mapId = SelectedPlan.GetMapId();
        if(mapId == 0) mapId = rows[0].Map.RowId;

        var points = rows.Where(x => x.Map.RowId == mapId).Select(x => x.RowId).Take(ImportMaxPoints).ToList();
        if(points.Count == 0)
        {
            ImportResult = string.Format(Loc.T("This item has no sector on {0}."), SelectedPlan.GetMap()?.Name);
            return;
        }

        SelectedPlan.Points.Clear();
        SelectedPlan.Points.AddRange(points);
        ImportResult = string.Format(Loc.T("Imported {0} sectors from Submarine Tracker."), points.Count);
    }
}
