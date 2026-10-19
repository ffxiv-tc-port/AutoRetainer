using AutoRetainer.Internal;
using AutoRetainer.Modules.Voyage;
using AutoRetainer.Modules.Voyage.Tasks;
using AutoRetainer.Modules.Voyage.VoyageCalculator;
using AutoRetainer.Scheduler.Tasks;
using AutoRetainer.Services;
using AutoRetainer.UI.MainWindow.MultiModeTab;
using AutoRetainerAPI.Configuration;
using Dalamud.Game;
using Dalamud.Interface.Components;
using ECommons.MathHelpers;
using Lumina.Excel.Sheets;


namespace AutoRetainer.UI.MainWindow;

internal static unsafe class WorkshopUI
{
    private static float StatusTextWidth = 0;
    private static List<(ulong cid, ulong frame, Vector2 start, Vector2 end, float percent)> Bars = [];

    // 以下容器只給繪製執行緒用：前三個每幀（或每個角色列）清空重填，StatusTexts 是逐角色的字串快取。
    private static readonly List<OverlayTextData> OverlayTexts = [];
    private static readonly List<OfflineCharacterData> SortedData = [];
    private static readonly List<OfflineVesselData> ProgressVessels = [];
    private static readonly Dictionary<ulong, StatusTextsEntry> StatusTexts = [];

    private static readonly string IconDoorOpen = FontAwesomeIcon.DoorOpen.ToIconString();
    private static readonly string IconUserCog = FontAwesomeIcon.UserCog.ToIconString();
    private static readonly string IconCheckCircle = FontAwesomeIcon.CheckCircle.ToIconString();

    internal static void Draw()
    {
        var overlayTexts = OverlayTexts;
        overlayTexts.Clear();
        List<OfflineCharacterData> sortedData;
        if(C.NoCurrentCharaOnTop)
        {
            sortedData = C.OfflineData.ApplyOrder(C.DeployablesVisualOrders);
        }
        else
        {
            sortedData = SortedData;
            sortedData.Clear();
            var cdata = MultiModeUI.FindCharacter(Svc.PlayerState.ContentId);
            if(cdata != null)
            {
                sortedData.Add(cdata);
            }
            foreach(var x in C.OfflineData.ApplyOrder(C.DeployablesVisualOrders))
            {
                if(x.CID != Svc.PlayerState.ContentId)
                {
                    sortedData.Add(x);
                }
            }
        }
        UIUtils.DrawSearch();
        foreach(var data in sortedData)
        {
            if(!(data.OfflineAirshipData.Count + data.OfflineSubmarineData.Count > 0 && !data.ExcludeWorkshop)) continue;
            var search = Ref<string>.Get("SearchChara");
            if(search != "" && !$"{data.Name}@{data.World}".Contains(search, StringComparison.OrdinalIgnoreCase)) continue;
            ImGui.PushID($"Player{data.CID}");
            var rCurPos = ImGui.GetCursorPos();
            float pad = 0;
            ImGui.PushFont(UiBuilder.IconFont);
            ImGuiEx.ButtonCheckbox(GetWorkshopToggleLabel(data.CID), ref data.WorkshopEnabled, 0xFF097000);
            ImGui.PopFont();
            ImGuiEx.Tooltip(Loc.T("Enable submersibles in multi mode on this character"));
            ImGui.SameLine(0, 3);
            if(ImGuiEx.IconButton(IconDoorOpen))
            {
                if(MultiMode.Relog(data, out var error, RelogReason.ConfigGUI))
                {
                    Notify.Success(Loc.T("Relogging..."));
                }
                else
                {
                    Notify.Error(error);
                }
            }
            ImGui.SameLine(0, 3);
            if(ImGuiEx.IconButton(IconUserCog))
            {
                ImGui.OpenPopup($"popup{data.CID}");
            }
            ImGuiEx.Tooltip(Loc.T("Configure Character"));
            ImGui.SameLine(0, 3);

            if(ImGui.BeginPopup($"popup{data.CID}"))
            {
                CharaConfig.Draw(data, false);
                ImGui.EndPopup();
            }

            if(data.NumSubSlots > data.GetVesselData(VoyageType.Submersible).Count)
            {
                ImGui.PushFont(UiBuilder.IconFont);
                ImGuiEx.TextV(ImGuiColors.DalamudYellow, "\uf6e3");
                ImGui.PopFont();
                if(ImGui.IsItemHovered()) ImGuiEx.Tooltip($"{Loc.T("You can construct new submersible (")}{data.GetVesselData(VoyageType.Submersible).Count}/{data.NumSubSlots})");
                ImGui.SameLine(0, 3);
            }

            if(data.IsNotEnoughSubmarinesEnabled())
            {
                ImGui.PushFont(UiBuilder.IconFont);
                ImGuiEx.TextV(ImGuiColors.DalamudOrange, "\ue4ac");
                ImGui.PopFont();
                ImGuiEx.Tooltip(Loc.T("Some of your submersibles are not enabled"));
                ImGui.SameLine(0, 3);
            }

            if(data.IsThereNotAssignedSubmarine())
            {
                ImGui.PushFont(UiBuilder.IconFont);
                ImGuiEx.TextV(ImGuiColors.DalamudOrange, "\ue4ab");
                ImGui.PopFont();
                ImGuiEx.Tooltip(Loc.T("Some of your submersibles are not undertaking voyage"));
                ImGui.SameLine(0, 3);
            }

            if(data.AreAnySuboptimalBuildsFound())
            {
                ImGui.PushFont(UiBuilder.IconFont);
                ImGuiEx.TextV(ImGuiColors.DalamudOrange, "\uf0ad");
                ImGui.PopFont();
                ImGuiEx.Tooltip(Loc.T("Unoptimal configurations are found"));
                ImGui.SameLine(0, 3);
            }

            if(data.AreAnyInvalidRedeploysActive())
            {
                ImGui.PushFont(UiBuilder.IconFont);
                ImGuiEx.TextV(ImGuiColors.DalamudRed, FontAwesomeIcon.ArrowsSpin.ToIconString());
                ImGui.PopFont();
                ImGuiEx.Tooltip(Loc.T("Redeploy is active while some unlock plans are set as enforced."));
                ImGui.SameLine(0, 3);
            }

            if(C.OldStatusIcons)
            {
                if(C.MultiModeWorkshopConfiguration.MultiWaitForAll)
                {
                    ImGui.PushFont(UiBuilder.IconFont);
                    ImGuiEx.TextV("\uf252");
                    ImGui.PopFont();
                    ImGuiEx.Tooltip(Loc.T(SharedText.WaitForDeployablesEnabledGlobally));
                    ImGui.SameLine(0, 3);
                }
                else if(data.MultiWaitForAllDeployables)
                {
                    ImGui.PushFont(UiBuilder.IconFont);
                    ImGuiEx.TextV("\uf252");
                    ImGui.PopFont();
                    ImGuiEx.Tooltip(Loc.T(SharedText.WaitForDeployablesEnabledForCharacter));
                    ImGui.SameLine(0, 3);
                }
            }

            data.DrawDCV();
            UIUtils.DrawTeleportIcons(data.CID);
            SharedUI.DrawLockout(data);

            var initCurpos = ImGui.GetCursorPos();
            var pickLast = (C.MultiModeWorkshopConfiguration.MultiWaitForAll || data.MultiWaitForAllDeployables) && !data.AreAnyEnabledVesselsReturnInNext(C.MultiModeWorkshopConfiguration.MaxMinutesOfWaiting * 60);
            var lst = CollectProgressVessels(data);
            //if (EzThrottler.Throttle("log")) PluginLog.Information($"{lst.Select(x => x.Name).Print()}");
            var lowestVessel = PickByRemainingSeconds(lst, pickLast);
            if(lowestVessel != default)
            {
                var prog = 1f - lowestVessel.GetRemainingSeconds() / (60f * 60f * 24f);
                prog.ValidateRange(0f, 1f);
                var pcol = prog == 1f ? (C.NoGradient ? 0xbb005000.ToVector4() : GradientColor.Get(0xbb500000.ToVector4(), 0xbb005000.ToVector4())) : 0xbb500000.ToVector4();
                ImGui.PushStyleColor(ImGuiCol.PlotHistogram, pcol);
                ImGui.ProgressBar(prog, new(ImGui.GetContentRegionAvail().X, ImGui.CalcTextSize("A").Y + ImGui.GetStyle().FramePadding.Y * 2), "");
                ImGui.PopStyleColor();
                ImGui.SetCursorPos(initCurpos);
            }

            var colpref = UIUtils.PushColIfPreferredCurrent(data);

            var anyVesselDone = false;
            foreach(var v in lst)
            {
                if(v.ReturnTime != 0 && v.GetRemainingSeconds() <= 0)
                {
                    anyVesselDone = true;
                    break;
                }
            }
            if(anyVesselDone)
            {
                ImGui.PushFont(UiBuilder.IconFont);
                ImGuiEx.TextV(ImGuiColors.HealerGreen, IconCheckCircle);
                ImGui.PopFont();
                ImGuiEx.Tooltip(Loc.T("Some voyages have returned"));
                ImGui.SameLine(0, 3);
            }
            // ImGuiEx.CollapsingHeader 不帶顏色時就是直接呼叫這一支；改成內插只是不要每幀串出新字串。
            if(ImGui.CollapsingHeader($"{data.GetCutCharaString(StatusTextWidth)}###workshop{data.CID}"))
            {
                MultiModeUI.SetAsPreferred(data);
                if(colpref) ImGui.PopStyleColor();
                pad = ImGui.GetStyle().FramePadding.Y;
                DrawTable(data);
            }
            else
            {
                MultiModeUI.SetAsPreferred(data);
                if(colpref) ImGui.PopStyleColor();
            }

            ImGui.SameLine(0, 0);
            var texts = GetStatusTexts(data);
            overlayTexts.Add((new Vector2(ImGui.GetContentRegionMax().X - ImGui.GetStyle().FramePadding.X, rCurPos.Y + ImGui.GetStyle().FramePadding.Y), texts));
            ImGui.NewLine();

            ImGui.PopID();
        }
        StatusTextWidth = 0f;
        UIUtils.DrawOverlayTexts(overlayTexts, ref StatusTextWidth);
        Bars.RemoveAll(x => x.frame != Svc.PluginInterface.UiBuilder.FrameCount);

        ImGuiEx.LineCentered("WorkshopUI planner button", () =>
        {
            if(ImGui.Button(Loc.T("Open Voyage Route Planner")))
            {
                P.SubmarinePointPlanUI.IsOpen = true;
            }
            ImGui.SameLine();
            if(ImGui.Button(Loc.T("Open Voyage Unlockable Planner")))
            {
                P.SubmarineUnlockPlanUI.IsOpen = true;
            }
        });

        if(C.Verbose)
        {
            if(ImGui.CollapsingHeader(Loc.T("Public debug")))
            {
                try
                {
                    if(!P.TaskManager.IsBusy)
                    {
                        /*if (ImGui.Button("Resend currently selected submarine on previous voyage"))
                        {
                            TaskDeployOnPreviousVoyage.Enqueue();
                        }*/
                        if(ImGui.Button(Loc.T("Select best path")))
                        {
                            TaskCalculateAndPickBestExpRoute.Enqueue();
                        }
                        if(ImGui.Button(Loc.T("Select best path with 1 unlock included")))
                        {
                            TaskCalculateAndPickBestExpRoute.Enqueue(VoyageUtils.GetSubmarineUnlockPlanByGuid(Data.GetAdditionalVesselData(CurrentSubmarine.GetCurrentNameOrThrow(), VoyageType.Submersible).SelectedUnlockPlan) ?? new());
                        }
                        if(ImGui.Button(Loc.T("Select unlock path (up to 5)")))
                        {
                            TaskDeployOnUnlockRoute.EnqueuePickOrCalc(VoyageUtils.GetSubmarineUnlockPlanByGuid(Data.GetAdditionalVesselData(CurrentSubmarine.GetCurrentNameOrThrow(), VoyageType.Submersible).SelectedUnlockPlan) ?? new(), UnlockMode.MultiSelect);
                        }
                        if(ImGui.Button(Loc.T("Select unlock path (only 1)")))
                        {
                            TaskDeployOnUnlockRoute.EnqueuePickOrCalc(VoyageUtils.GetSubmarineUnlockPlanByGuid(Data.GetAdditionalVesselData(CurrentSubmarine.GetCurrentNameOrThrow(), VoyageType.Submersible).SelectedUnlockPlan) ?? new(), UnlockMode.SpamOne);
                        }
                        if(ImGui.Button(Loc.T("Select point planner path")))
                        {
                            var plan = VoyageUtils.GetSubmarinePointPlanByGuid(Data.GetAdditionalVesselData(CurrentSubmarine.GetCurrentNameOrThrow(), VoyageType.Submersible).SelectedPointPlan);
                            if(plan != null)
                            {
                                TaskDeployOnPointPlan.EnqueuePick(plan);
                            }
                            else
                            {
                                DuoLog.Error(Loc.T("No plan selected!"));
                            }
                        }
                        foreach(var x in Data.OfflineSubmarineData)
                        {
                            if(ImGui.Button(string.Format(Loc.T("Repair {0} submarine's broken components"), x.Name)))
                            {
                                if(VoyageUtils.GetCurrentWorkshopPanelType() == PanelType.Submersible)
                                {
                                    TaskSelectVesselByName.Enqueue(x.Name, VoyageType.Submersible);
                                    TaskIntelligentRepair.Enqueue(x.Name, VoyageType.Submersible);
                                    P.TaskManager.Enqueue(VoyageScheduler.SelectQuitVesselMenu);
                                }
                                else
                                {
                                    Notify.Error(Loc.T("You are not in a submersible menu"));
                                }
                            }
                        }
                        if(ImGui.Button(Loc.T("Approach bell")))
                        {
                            TaskInteractWithNearestBell.Enqueue(false);
                        }

                        if(ImGui.Button(Loc.T("Approach panel")))
                        {
                            TaskInteractWithNearestPanel.Enqueue(false);
                        }

                        /*if (ImGui.Button("Redeploy current vessel on previous voyage"))
                        {
                            TaskRedeployPreviousLog.Enqueue();
                        }*/

                        //if (ImGui.Button($"Deploy current submarine on best experience route")) TaskDeployOnBestExpVoyage.Enqueue();

                    }
                    else
                    {
                        ImGuiEx.Text(EColor.RedBright, string.Format(Loc.T("Currently executing: {0}"), P.TaskManager.CurrentTask?.Name));
                    }
                }
                catch(Exception e)
                {
                    ImGuiEx.TextWrapped(e.ToString());
                }
            }
        }
    }

    /// <summary>
    /// 等同 <c>啟用中的飛空艇.Union(啟用中的潛水艇).Where(ReturnTime != 0)</c>，內容與順序相同
    /// （Union 的去重同樣用 <c>EqualityComparer.Default</c>），填進重複使用的緩衝區；下一個角色列會覆蓋它。
    /// </summary>
    private static List<OfflineVesselData> CollectProgressVessels(OfflineCharacterData data)
    {
        var list = ProgressVessels;
        list.Clear();
        AddEnabledVessels(list, data, VoyageType.Airship);
        AddEnabledVessels(list, data, VoyageType.Submersible);
        list.RemoveAll(x => x.ReturnTime == 0);
        return list;
    }

    private static void AddEnabledVessels(List<OfflineVesselData> list, OfflineCharacterData data, VoyageType type)
    {
        foreach(var x in data.GetVesselData(type))
        {
            if(data.GetEnabledVesselsData(type).Contains(x.Name) && !list.Contains(x)) list.Add(x);
        }
    }

    /// <summary>
    /// 等同 <c>OrderBy(z =&gt; z.GetRemainingSeconds())</c> 之後的 <c>LastOrDefault()</c>（<paramref name="last"/>，同值取最後一艘）
    /// 或 <c>FirstOrDefault()</c>（同值取第一艘）。
    /// </summary>
    private static OfflineVesselData PickByRemainingSeconds(List<OfflineVesselData> vessels, bool last)
    {
        OfflineVesselData best = null;
        long bestKey = 0;
        foreach(var z in vessels)
        {
            var key = z.GetRemainingSeconds();
            if(best == null || (last ? key >= bestKey : key < bestKey))
            {
                best = z;
                bestKey = key;
            }
        }
        return best;
    }

    /// <summary>角色列右側 R／C／I 三欄的字串；數值與警示判定都沒變就沿用上一幀那一份。只給繪製執行緒用。</summary>
    private static (bool Warning, string Text, bool Unknown, string Tooltip)[] GetStatusTexts(OfflineCharacterData data)
    {
        var repairWarning = data.RepairKits < C.UIWarningDepRepairNum;
        var tanksWarning = data.Ceruleum < C.UIWarningDepTanksNum;
        var slotWarning = data.InventorySpace < C.UIWarningDepSlotNum;
        if(StatusTexts.TryGetValue(data.CID, out var e)
            && e.RepairKits == data.RepairKits && e.Ceruleum == data.Ceruleum && e.InventorySpace == data.InventorySpace
            && e.RepairWarning == repairWarning && e.TanksWarning == tanksWarning && e.SlotWarning == slotWarning)
        {
            return e.Texts;
        }
        (bool Warning, string Text, bool Unknown, string Tooltip)[] built = [
            (repairWarning, $"R: {data.RepairKits}", false, null),
            (tanksWarning, $"C: {data.Ceruleum}", false, null),
            (slotWarning, $"I: {data.InventorySpace}", false, null)];
        StatusTexts[data.CID] = new(data.RepairKits, data.Ceruleum, data.InventorySpace, repairWarning, tanksWarning, slotWarning, built);
        return built;
    }

    private readonly record struct StatusTextsEntry(int RepairKits, int Ceruleum, uint InventorySpace, bool RepairWarning, bool TanksWarning, bool SlotWarning, (bool Warning, string Text, bool Unknown, string Tooltip)[] Texts);

    // 以下快取只給繪製執行緒用。內容都是原本那一行運算式第一次算出來的，輸入不變就沿用同一個字串。
    private sealed class VesselTexts
    {
        public string SettingsPopup;
        public string SettingsButton;
        public long TimeSeconds;
        public string TimeText;
    }

    private static readonly Dictionary<(ulong CID, string Name), VesselTexts> VesselTextCache = [];
    private static readonly Dictionary<ulong, string> WorkshopToggleLabels = [];
    private static readonly Dictionary<string, string> VesselCheckboxLabels = [];
    private static readonly Dictionary<int, string> VesselLevelTexts = [];
    private static readonly Dictionary<int, string> VesselExpTexts = [];
    private static readonly Dictionary<(int, int, int, int), (string Text, int ModdedCount)> BuildDisplays = [];
    private static readonly Dictionary<ulong, string> RouteTexts = [];
    private static readonly string CogsIcon = FontAwesomeIcon.Cogs.ToIconString();

    private static VesselTexts GetVesselTexts(ulong cid, string name)
    {
        if(!VesselTextCache.TryGetValue((cid, name), out var t))
        {
            t = new()
            {
                SettingsPopup = $"{cid} {name} settings",
                SettingsButton = $"{cid} {name}",
                TimeSeconds = long.MinValue,
            };
            VesselTextCache[(cid, name)] = t;
        }
        return t;
    }

    private static string GetTimeText(VesselTexts t, long seconds)
    {
        if(t.TimeText == null || t.TimeSeconds != seconds)
        {
            t.TimeText = VoyageUtils.Seconds2Time(seconds);
            t.TimeSeconds = seconds;
        }
        return t.TimeText;
    }

    private static string GetWorkshopToggleLabel(ulong cid)
    {
        if(!WorkshopToggleLabels.TryGetValue(cid, out var s))
        {
            s = $"\uf21a##{cid}";
            WorkshopToggleLabels[cid] = s;
        }
        return s;
    }

    private static string GetVesselCheckboxLabel(string name)
    {
        if(name == null) return "##sub";
        if(!VesselCheckboxLabels.TryGetValue(name, out var s))
        {
            s = $"{name}##sub";
            VesselCheckboxLabels[name] = s;
        }
        return s;
    }

    private static string GetVesselLevelText(int level)
    {
        if(!VesselLevelTexts.TryGetValue(level, out var s))
        {
            s = Lang.CharLevel + $"{level}".ReplaceByChar(Lang.Digits.Normal, Lang.Digits.GameFont);
            VesselLevelTexts[level] = s;
        }
        return s;
    }

    private static string GetVesselExpText(int lvlf)
    {
        if(!VesselExpTexts.TryGetValue(lvlf, out var s))
        {
            s = $".{lvlf:D2}".ReplaceByChar(Lang.Digits.Normal, Lang.Digits.GameFont);
            VesselExpTexts[lvlf] = s;
        }
        return s;
    }

    /// <summary>與 <c>adata.GetSubmarineBuildDisplay()</c> 相同；結果只取決於四個零件。</summary>
    private static (string Text, int ModdedCount) GetBuildDisplay(AdditionalVesselData adata)
    {
        var key = (adata.Part1, adata.Part2, adata.Part3, adata.Part4);
        if(!BuildDisplays.TryGetValue(key, out var r))
        {
            r = adata.GetSubmarineBuildDisplay();
            BuildDisplays[key] = r;
        }
        return r;
    }

    /// <summary>航點代號串（例：ABCDE）；結果只取決於航點位元組與遊戲資料表。7 個以內的航點以長度＋位元組當鍵快取，其餘照算。</summary>
    private static string GetRouteText(byte[] points)
    {
        if(points == null || points.Length > 7) return BuildRouteText(points);
        var key = (ulong)points.Length << 56;
        for(var i = 0; i < points.Length; i++) key |= (ulong)points[i] << (i * 8);
        if(!RouteTexts.TryGetValue(key, out var s))
        {
            s = BuildRouteText(points);
            RouteTexts[key] = s;
        }
        return s;
    }

    private static string BuildRouteText(byte[] adataPoints)
    {
        List<string> points = [];
        foreach(var x in adataPoints)
        {
            if(x != 0)
            {
                // Location 欄是扇區代號字母(A/B/.../AC),各語言版本一致(非空的 Location 全數符合 [A-Z]{1,2})。
                // 刻意不指定取表語言:本艦隊的 Lumina fork 在 ExcelModule.GetRawSheetCore() 開頭無條件執行 language = Language,
                // 語言參數是死參數,寫了只會讓讀碼的人誤以為真的取到了日文表。
                var d = Svc.Data.GetExcelSheet<SubmarineExploration>().GetRowOrDefault(x);
                if(d != null && d.Value.Location.ToString().Length > 0)
                {
                    points.Add(d.Value.Location.ToString());
                }
            }
        }
        return points.Join("");
    }

    /// <summary>與 <c>planPoints.SequenceEqual(adataPoints.Where(x =&gt; x != 0).Select(x =&gt; (uint)x))</c> 相同，但不配置。</summary>
    private static bool PlanMatchesPoints(List<uint> planPoints, byte[] adataPoints)
    {
        if(adataPoints == null) throw new ArgumentNullException("source");
        var i = 0;
        foreach(var b in adataPoints)
        {
            if(b == 0) continue;
            if(i >= planPoints.Count || planPoints[i] != b) return false;
            i++;
        }
        return i == planPoints.Count;
    }

    private static string data1 = "";
    private static VoyageType data2 = VoyageType.Airship;

    private static void DrawTable(OfflineCharacterData data)
    {
        var storePos = ImGui.GetCursorPos();
        foreach(var v in Bars)
        {
            if(v.cid != data.CID) continue;
            ImGui.SetCursorPos(v.start - ImGui.GetStyle().CellPadding with { Y = 0 });
            ImGui.PushStyleColor(ImGuiCol.PlotHistogram, 0xbb500000);
            ImGui.PushStyleColor(ImGuiCol.FrameBg, 0);
            ImGui.ProgressBar(1f - Math.Min(1f, v.percent),
                new(ImGui.GetContentRegionAvail().X, v.end.Y - v.start.Y - ImGui.GetStyle().CellPadding.Y), "");
            ImGui.PopStyleColor(2);
        }
        ImGui.SetCursorPos(storePos);
        if(ImGui.BeginTable("##retainertable", 4, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.Borders))
        {
            ImGui.TableSetupColumn(Loc.T("Name"), ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn(Loc.T("Structure"));
            ImGui.TableSetupColumn(Loc.T("Voyage"));
            ImGui.TableSetupColumn("");
            ImGui.TableHeadersRow();
            for(var i = 0; i < data.OfflineAirshipData.Count; i++)
            {
                var vessel = data.OfflineAirshipData[i];
                DrawRow(data, vessel, VoyageType.Airship);
            }
            for(var i = 0; i < data.OfflineSubmarineData.Count; i++)
            {
                var vessel = data.OfflineSubmarineData[i];
                DrawRow(data, vessel, VoyageType.Submersible);
            }
            ImGui.EndTable();
        }
    }

    private static void DrawRow(OfflineCharacterData data, OfflineVesselData vessel, VoyageType type)
    {
        if(type == VoyageType.Airship && data.GetEnabledVesselsData(type).Count == 0 && C.HideAirships) return;
        ImGui.PushID($"{data.CID}/{vessel.Name}/{type}");
        var enabled = type == VoyageType.Airship ? data.EnabledAirships : data.EnabledSubs;
        var adata = data.GetAdditionalVesselData(vessel.Name, type);

        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.TableSetBgColor(ImGuiTableBgTarget.CellBg, 0);
        var start = ImGui.GetCursorPos();
        ImGui.PushFont(UiBuilder.IconFont);
        ImGuiEx.TextV(type == VoyageType.Airship ? "\ue22d" : "\uf21a");
        ImGui.PopFont();
        ImGui.SameLine();
        var enabledCount = 0;
        foreach(var x in data.OfflineSubmarineData)
        {
            if(data.EnabledSubs.Contains(x.Name)) enabledCount++;
        }
        foreach(var x in data.OfflineAirshipData)
        {
            if(data.EnabledAirships.Contains(x.Name)) enabledCount++;
        }
        var disabled = enabledCount >= 4 && !enabled.Contains(vessel.Name);
        if(disabled) ImGui.BeginDisabled();
        ImGuiEx.CollectionCheckbox(GetVesselCheckboxLabel(vessel.Name), vessel.Name, enabled);
        if(disabled) ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.PushFont(UiBuilder.IconFont);
        if(adata.VesselBehavior == VesselBehavior.Finalize)
        {
            ImGuiEx.Text(Lang.IconAnchor);
        }
        else if(adata.VesselBehavior == VesselBehavior.Redeploy)
        {
            ImGuiEx.Text(Lang.IconResend);
        }
        else if(adata.VesselBehavior == VesselBehavior.LevelUp)
        {
            ImGuiEx.Text(Lang.IconLevelup);
        }
        else if(adata.VesselBehavior == VesselBehavior.Unlock)
        {
            ImGuiEx.Text(Lang.IconUnlock);
            ImGui.SameLine();
            if(adata.UnlockMode == UnlockMode.WhileLevelling)
            {
                ImGuiEx.Text(Lang.IconLevelup);
            }
            else if(adata.UnlockMode == UnlockMode.SpamOne)
            {
                ImGuiEx.Text(Lang.IconRepeat);
            }
            else if(adata.UnlockMode == UnlockMode.MultiSelect)
            {
                ImGuiEx.Text(Lang.IconPath);
            }
            else
            {
                ImGuiEx.Text(Lang.IconWarning);
            }
        }
        else if(adata.VesselBehavior == VesselBehavior.Use_plan)
        {
            var plan = VoyageUtils.GetSubmarinePointPlanByGuid(adata.SelectedPointPlan);
            var valid = plan != null && plan.Points.Count.InRange(1, 5, true);
            var fast = plan != null && PlanMatchesPoints(plan.Points, adata.Points);
            ImGuiEx.Text(valid ? (fast ? EColor.GreenBright : null) : EColor.RedBright, Lang.IconPlanner);
            ImGui.PushFont(UiBuilder.DefaultFont);
            // ImGuiEx.Tooltip 自己只在懸停時畫；先判懸停只是不要每幀替每個航點查表、解析 SeString。
            if(valid && ImGui.IsItemHovered()) ImGuiEx.Tooltip(plan.Points.Select(x => $"{VoyageUtils.GetSubmarineExploration(x)?.FancyDestination()}").Print("\n"));
            ImGui.PopFont();
        }
        else
        {
            ImGuiEx.Text(Lang.IconWarning);
        }
        ImGui.PopFont();
        if(adata.IndexOverride > 0)
        {
            ImGui.SameLine();
            ImGuiEx.Text(ImGuiColors.DalamudGrey3, string.Format(Loc.T("Index override: {0}"), adata.IndexOverride));
        }
        var end = ImGui.GetCursorPos();
        var p = vessel.GetRemainingSeconds() / (60f * 60f * 24f);
        if(vessel.ReturnTime != 0) Bars.Add((data.CID, Svc.PluginInterface.UiBuilder.FrameCount, start, end, vessel.ReturnTime == 0 ? 0 : p.ValidateRange(0f, 1f)));
        ImGui.TableNextColumn();
        ImGui.TableSetBgColor(ImGuiTableBgTarget.CellBg, 0);
        if(adata.Level > 0)
        {
            var lvlf = 0;
            if(adata.CurrentExp > 0 && adata.NextLevelExp > 0)
            {
                lvlf = (int)(adata.CurrentExp * 100f / adata.NextLevelExp);
            }
            ImGuiEx.TextV(GetVesselLevelText(adata.Level));
            ImGui.SameLine(0, 0);
            ImGuiEx.Text(ImGuiColors.DalamudGrey3, GetVesselExpText(lvlf));
            ImGui.SameLine(0, 0);
            var (buildText, moddedCount) = GetBuildDisplay(adata);
            var buildColor = adata.IsUnoptimalBuild(out var justification) ? ImGuiColors.DalamudOrange
                : moddedCount == 4 ? ImGuiColors.HealerGreen
                : moddedCount > 0 ? ImGuiColors.DalamudYellow
                : (Vector4?)null;
            ImGuiEx.Text(buildColor, buildText);
            if(justification != null)
            {
                ImGuiEx.Tooltip(justification);
            }
        }
        ImGui.TableNextColumn();
        ImGui.TableSetBgColor(ImGuiTableBgTarget.CellBg, 0);

        if(vessel.ReturnTime == 0)
        {
            ImGuiEx.Text(Loc.T("No voyage"));
        }
        else
        {
            ImGuiEx.Text(GetRouteText(adata.Points));
            ImGui.SameLine();
            var texts = GetVesselTexts(data.CID, vessel.Name);
            if(C.TimerAllowNegative)
            {
                ImGuiEx.Text(GetTimeText(texts, vessel.GetRemainingSeconds()));
            }
            else
            {
                ImGuiEx.Text(vessel.GetRemainingSeconds() > 0 ? GetTimeText(texts, vessel.GetRemainingSeconds()) : Loc.T("Voyage completed"));
            }

        }
        ImGui.TableNextColumn();
        ImGui.TableSetBgColor(ImGuiTableBgTarget.CellBg, 0);
        var names = GetVesselTexts(data.CID, vessel.Name);
        var n = names.SettingsPopup;
        if(ImGuiEx.IconButton(CogsIcon, names.SettingsButton))
        {
            ImGui.OpenPopup(n);
        }
        if(ImGuiEx.BeginPopupNextToElement(n))
        {
            ImGui.CollapsingHeader($"{vessel.Name} - {Censor.Character(data.Name)} {Loc.T("Configuration")}  ##conf", ImGuiTreeNodeFlags.DefaultOpen | ImGuiTreeNodeFlags.Bullet | ImGuiTreeNodeFlags.OpenOnArrow);
            ImGuiEx.Text(Loc.T("Vessel behavior:"));
            ImGuiEx.EnumCombo("##vbeh", ref adata.VesselBehavior, Loc.EnumNames<VesselBehavior>());
            if(adata.VesselBehavior == VesselBehavior.Unlock)
            {
                ImGuiEx.Text(Loc.T("Unlock mode:"));
                ImGuiEx.EnumCombo("##umode", ref adata.UnlockMode, Lang.UnlockModeNames);
                var currentPlan = VoyageUtils.GetSubmarineUnlockPlanByGuid(adata.SelectedUnlockPlan) ?? VoyageUtils.GetDefaultSubmarineUnlockPlan(false);
                var isDefault = VoyageUtils.GetSubmarineUnlockPlanByGuid(adata.SelectedUnlockPlan) == null;
                var text = Environment.TickCount64 % 2000 > 1000 ? Loc.T("Unlocking every point") : Loc.T("No or unknown plan selected");
                if(ImGui.BeginCombo("##uplan", (currentPlan?.Name ?? text) + (isDefault ? " (default)" : ""), ImGuiComboFlags.HeightLarge))
                {
                    if(ImGui.Button(Loc.T("Open editor")))
                    {
                        P.SubmarineUnlockPlanUI.IsOpen = true;
                        P.SubmarineUnlockPlanUI.SelectedPlanGuid = adata.SelectedUnlockPlan;
                    }
                    ImGui.SameLine();
                    if(ImGui.Button(Loc.T("Clear plan")))
                    {
                        adata.SelectedUnlockPlan = Guid.Empty.ToString();
                    }
                    foreach(var x in C.SubmarineUnlockPlans)
                    {
                        if(ImGui.Selectable($"{x.Name}##{x.GUID}"))
                        {
                            adata.SelectedUnlockPlan = x.GUID;
                        }
                    }
                    ImGui.EndCombo();
                }
            }
            if(adata.VesselBehavior == VesselBehavior.Use_plan)
            {
                var currentPlan = VoyageUtils.GetSubmarinePointPlanByGuid(adata.SelectedPointPlan);
                if(ImGui.BeginCombo("##uplan", currentPlan.GetPointPlanName(), ImGuiComboFlags.HeightLarge))
                {
                    if(ImGui.Button(Loc.T("Open editor")))
                    {
                        P.SubmarinePointPlanUI.IsOpen = true;
                        P.SubmarinePointPlanUI.SelectedPlanGuid = adata.SelectedPointPlan;
                    }
                    ImGui.SameLine();
                    if(ImGui.Button(Loc.T("Clear plan")))
                    {
                        adata.SelectedPointPlan = Guid.Empty.ToString();
                    }
                    foreach(var x in C.SubmarinePointPlans)
                    {
                        if(ImGui.Selectable($"{x.GetPointPlanName()}##{x.GUID}"))
                        {
                            adata.SelectedPointPlan = x.GUID;
                        }
                    }
                    ImGui.EndCombo();
                }
            }
            ImGui.Separator();
            ImGuiEx.SetNextItemWidthScaled(150f);
            ImGuiEx.SliderInt(Loc.T("Index override"), ref adata.IndexOverride, 0, 4, adata.IndexOverride == 0 ? Loc.T("Disabled") : $"{adata.IndexOverride}");
            ImGuiComponents.HelpMarker(Loc.T("If your vessel order in AutoRetainer is different than in voyage panel menu, you must use this feature to set correct index to incorrectly ordered vessels. Make sure that index is matching order in control panel."));
            if(ImGui.CollapsingHeader(Loc.T("I have recently renamed this vessel")))
            {
                // 下面的委派捕獲這個區塊裡的副本：直接捕獲參數會讓每一列每幀一進方法就先配一個閉包物件。
                var renameType = type;
                var renameData = data;
                var renameVessel = vessel;
                if(ImGui.BeginCombo("##selprev", Loc.T("Select previous vessel name"), ImGuiComboFlags.HeightLarge))
                {
                    var datas = ((Func<Dictionary<string, AdditionalVesselData>>)delegate
                    {
                        if(renameType == VoyageType.Airship) return renameData.AdditionalAirshipData;
                        if(renameType == VoyageType.Submersible) return renameData.AdditionalSubmarineData;
                        throw new ArgumentOutOfRangeException(nameof(type));
                    })();
                    foreach(var x in datas)
                    {
                        var d = data.GetVesselData(type).Any(z => z.Name == x.Key);
                        if(d) ImGui.BeginDisabled();
                        if(ImGui.Selectable($"{x.Key}"))
                        {
                            new TickScheduler(() =>
                            {
                                var copyTo = renameVessel.Name;
                                var newData = x.Value.JSONClone();
                                var toDelete = x.Key;
                                datas[copyTo] = x.Value;
                                datas.Remove(toDelete);
                                Notify.Success(string.Format(Loc.T("Moved data from {0} to {1}"), toDelete, copyTo));
                            });
                        }
                        if(d) ImGui.EndDisabled();
                    }
                    ImGui.EndCombo();
                }
            }
            if(C.Verbose)
            {
                if(ImGui.Button(Loc.T("Fake ready"))) vessel.ReturnTime = (uint)P.Time;
                if(ImGui.Button(Loc.T("Fake ready+"))) vessel.ReturnTime += 60u * (ImGui.GetIO().KeyCtrl ? 10u : 1u) * (ImGui.GetIO().KeyShift ? 10u : 1u);
                if(ImGui.Button(Loc.T("Fake ready-"))) vessel.ReturnTime -= 60u * (ImGui.GetIO().KeyCtrl ? 10u : 1u) * (ImGui.GetIO().KeyShift ? 10u : 1u);
                if(ImGui.Button(Loc.T("Fake unready"))) vessel.ReturnTime = (uint)(P.Time + 9999);
            }
            ImGui.EndPopup();
        }
        ImGui.PopID();
    }
}
