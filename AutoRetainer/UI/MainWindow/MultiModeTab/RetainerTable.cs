using AutoRetainer.Scheduler.Handlers;
using AutoRetainer.Scheduler.Tasks;
using AutoRetainerAPI;
using AutoRetainerAPI.Configuration;
using ECommons.GameHelpers;

namespace AutoRetainer.UI.MainWindow.MultiModeTab;
public static unsafe class RetainerTable
{
    public static void Draw(OfflineCharacterData data, List<OfflineRetainerData> retainerData, Dictionary<string, (Vector2 start, Vector2 end)> bars)
    {
        if(ImGui.BeginTable("##retainertable", 5, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.Borders))
        {
            ImGui.TableSetupColumn(Loc.T("Name"), ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn(Loc.T("Job"));
            ImGui.TableSetupColumn(Loc.T("Venture"));
            ImGui.TableSetupColumn(Loc.T("Slots"));
            ImGui.TableSetupColumn("");
            ImGui.TableHeadersRow();
            var retainers = P.GetSelectedRetainers(data.CID);
            foreach(var ret in retainerData)
            {
                if(ret.Level == 0 || ret.Name.ToString().IsNullOrEmpty()) continue;
                var adata = Utils.GetAdditionalData(data.CID, ret.Name);
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TableSetBgColor(ImGuiTableBgTarget.CellBg, 0);
                var start = ImGui.GetCursorPos();
                var selected = retainers.Contains(ret.Name.ToString());
                if(ImGui.Checkbox($"{Censor.Retainer(ret.Name)}", ref selected))
                {
                    if(selected)
                    {
                        retainers.Add(ret.Name.ToString());
                    }
                    else
                    {
                        retainers.Remove(ret.Name.ToString());
                    }
                }
                {
                    if(C.EntrustPlans.TryGetFirst(s => s.Guid == adata.EntrustPlan, out var plan))
                    {
                        ImGui.SameLine();
                        ImGui.PushFont(UiBuilder.IconFont);
                        Vector4? c = plan.ManualPlan ? ImGuiColors.DalamudOrange : null;
                        if(!C.EnableEntrustManager) c = ImGuiColors.DalamudRed;
                        ImGuiEx.Text(c, Lang.IconDuplicate);
                        ImGui.PopFont();
                        ImGuiEx.Tooltip($"{Loc.T("Entrust plan \"")}{plan.Name}{Loc.T("\" is active.")}" + (plan.ManualPlan ? Loc.T("\nThis is manual processing plan") : "") + (Utils.GetReachableRetainerBell(false) != null ? Loc.T("\nClick to Entrust.") : ""));
                        if(ImGui.IsItemClicked())
                        {
                            if(!Svc.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.OccupiedSummoningBell])
                                TaskInteractWithNearestBell.Enqueue();

                            P.TaskManager.Enqueue(() => RetainerListHandlers.SelectRetainerByName(ret.Name.ToString()));
                            TaskEntrustDuplicates.EnqueueNew(plan);
                            if(C.RetainerMenuDelay > 0)
                            {
                                TaskWaitSelectString.Enqueue(C.RetainerMenuDelay);
                            }
                            P.TaskManager.Enqueue(RetainerHandlers.SelectQuit);
                        }
                    }
                }
                if(adata.WithdrawGil)
                {
                    ImGui.SameLine();
                    ImGui.PushFont(UiBuilder.IconFont);
                    ImGuiEx.Text(Lang.IconGil);
                    ImGui.PopFont();
                }
                Svc.PluginInterface.GetIpcProvider<ulong, string, object>(ApiConsts.OnRetainerPostVentureTaskDraw).SendMessage(data.CID, ret.Name);
                if(adata.IsVenturePlannerActive())
                {
                    ImGui.SameLine();
                    ImGui.PushFont(UiBuilder.IconFont);
                    ImGuiEx.Text(Lang.IconPlanner);
                    ImGui.PopFont();
                    if(ImGui.IsItemHovered())
                    {
                        ImGui.BeginTooltip();
                        VentureUtils.BuildUnwrappedList(adata, data, ret);
                        ImGui.EndTooltip();
                    }
                }
                var end = ImGui.GetCursorPos();
                bars[$"{data.CID}{ret.Name}"] = (start, end);
                ImGui.TableNextColumn();
                ImGui.TableSetBgColor(ImGuiTableBgTarget.CellBg, 0);

                if(ThreadLoadImageHandler.TryGetIconTextureWrap(ret.Job == 0 ? 62143 : 062100 + ret.Job, true, out var t))
                {
                    ImGui.Image(t.Handle, new(24, 24));
                }
                else
                {
                    ImGui.Dummy(new(24, 24));
                }
                if(ret.Level > 0)
                {
                    ImGui.SameLine(0, 2);
                    var level = $"{Lang.CharLevel}{ret.Level}";
                    var add = "";
                    if(adata.Ilvl > -1 && !VentureUtils.IsDoL(ret.Job))
                    {
                        add += $"{Lang.CharItemLevel}{adata.Ilvl}";
                    }
                    if((adata.Gathering > -1 || adata.Perception > -1) && VentureUtils.IsDoL(ret.Job))
                    {
                        add += $"{Lang.CharPlant}{adata.Gathering}/{adata.Perception}";
                    }
                    var cap = ret.Level < Player.MaxLevel && data.GetJobLevel(ret.Job) == ret.Level;
                    if(cap) ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudRed);
                    ImGuiEx.TextV(level.ReplaceByChar(Lang.Digits.Normal, Lang.Digits.GameFont));
                    if(!cap && ret.Level < Player.MaxLevel)
                    {
                        ImGui.SameLine(0, 0);
                        ImGuiEx.TextV("/");
                        ImGui.SameLine(0, 0);
                        ImGuiEx.TextV(data.GetJobLevel(ret.Job).ToString().ReplaceByChar(Lang.Digits.Normal, Lang.Digits.GameFont));
                    }
                    if(cap) ImGui.PopStyleColor();
                    if(C.ShowAdditionalInfo && add != "")
                    {
                        ImGui.SameLine();
                        ImGuiEx.Text(add);
                    }
                }
                ImGui.TableNextColumn();
                ImGui.TableSetBgColor(ImGuiTableBgTarget.CellBg, 0);
                if(ret.VentureID != 0 && C.ShowAdditionalInfo)
                {
                    var parts = VentureUtils.GetVentureById(ret.VentureID).GetFancyVentureNameParts(data, ret, out _);
                    if(!parts.Name.IsNullOrEmpty())
                    {
                        var c = parts.YieldRate == 4 ? ImGuiColors.ParsedGreen : ImGui.GetStyle().Colors[(int)ImGuiCol.Text];
                        ImGuiEx.Text(c, $"{(parts.Level != 0 ? $"{Lang.CharLevel}{parts.Level} " : "")}{parts.Name}");
                        ImGui.SameLine();
                    }
                }
                ImGuiEx.Text($"{(!ret.HasVenture ? Loc.T("No Venture") : Utils.ToTimeString(ret.GetVentureSecondsRemaining(C.TimerAllowNegative)))}");
                ImGui.TableNextColumn();
                ImGui.TableSetBgColor(ImGuiTableBgTarget.CellBg, 0);
                DrawInventorySlots(ret);
                ImGui.TableNextColumn();
                ImGui.TableSetBgColor(ImGuiTableBgTarget.CellBg, 0);
                var n = $"{data.CID} {ret.Name} settings";
                if(ImGuiEx.IconButton(FontAwesomeIcon.Cogs, $"{data.CID} {ret.Name}"))
                {
                    ImGui.OpenPopup(n);
                }
                if(ImGuiEx.BeginPopupNextToElement(n))
                {
                    RetainerConfig.Draw(ret, data, adata);
                    ImGui.EndPopup();
                }
                ImGui.SameLine();
                if(ImGuiEx.IconButton(Lang.IconPlanner, $"{data.CID} {ret.Name} planner"))
                {
                    P.VenturePlanner.Open(data, ret);
                }
            }
            ImGui.EndTable();
        }
    }

    /// <summary>
    /// 僱員背包固定是 7 頁 × 25 格，遊戲沒有提供擴充的手段，所以這是常數。
    /// 真的哪天變了，下面的 clamp 會讓顯示停在 0 而不是跑出負數或超過上限。
    /// </summary>
    private const int RetainerInventoryCapacity = 7 * 25;

    /// <summary>
    /// 畫出這個僱員自己的背包剩餘格數，對應角色列右邊那個彙總的 <c>I:</c>。
    /// <br></br>
    /// 🔑 「沒有資料」跟「0 格」必須在列上就分得出來：
    /// <see cref="OfflineRetainerData.ItemCount"/> 是 -1 時畫灰色的 <c>?</c>，
    /// <b>絕不畫成 0</b> —— 把未知畫成 0 等於告訴使用者「這個僱員塞滿了」。
    /// 「為什麼不知道」「數字有多舊」這種長文字放 tooltip，但「不知道」本身留在列上。
    /// </summary>
    private static void DrawInventorySlots(OfflineRetainerData ret)
    {
        if(ret.ItemCount < 0)
        {
            ImGuiEx.Text(ImGuiColors.DalamudGrey, "?");
            ImGuiEx.Tooltip(Loc.T("No inventory data has been recorded for this retainer yet.\nIt comes from the retainer list, so logging in on this character once is enough - you do not need to open the retainer."));
            return;
        }

        var used = Math.Clamp(ret.ItemCount, 0, RetainerInventoryCapacity);
        var free = RetainerInventoryCapacity - used;
        Vector4? color = null;
        if(free == 0) color = ImGuiColors.DalamudRed;
        else if(free < C.UIWarningRetSlotNum) color = ImGuiColors.DalamudOrange;
        ImGuiEx.Text(color, free.ToString());
        ImGuiEx.Tooltip($"{Loc.T("Free inventory slots")}: {free}/{RetainerInventoryCapacity}\n" +
            $"{Loc.T("Items held")}: {ret.ItemCount}\n" +
            $"{Loc.T("Listed on market board")}: {ret.MBItems}\n\n" +
            Loc.T("Snapshot taken the last time this character was logged in."));
    }
}
