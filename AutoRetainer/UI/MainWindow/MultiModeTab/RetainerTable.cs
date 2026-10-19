using AutoRetainer.Scheduler.Handlers;
using AutoRetainer.Scheduler.Tasks;
using AutoRetainerAPI;
using AutoRetainerAPI.Configuration;
using ECommons.GameHelpers;

namespace AutoRetainer.UI.MainWindow.MultiModeTab;
public static unsafe class RetainerTable
{
    public static void Draw(OfflineCharacterData data, List<OfflineRetainerData> retainerData, Dictionary<(ulong CID, string Name), (Vector2 start, Vector2 end)> bars)
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
            var postVentureTaskDraw = DrawEventProviders.RetainerPostVentureTaskDraw.GetIfSubscribed();
            foreach(var ret in retainerData)
            {
                if(ret.Level == 0 || ret.Name.ToString().IsNullOrEmpty()) continue;
                var texts = GetRowTexts(data.CID, ret.Name);
                var adata = GetAdditionalData(texts.AdditionalDataKey);
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
                    var plan = FindEntrustPlan(adata.EntrustPlan);
                    if(plan != null)
                    {
                        ImGui.SameLine();
                        ImGui.PushFont(UiBuilder.IconFont);
                        Vector4? c = plan.ManualPlan ? ImGuiColors.DalamudOrange : null;
                        if(!C.EnableEntrustManager) c = ImGuiColors.DalamudRed;
                        ImGuiEx.Text(c, Lang.IconDuplicate);
                        ImGui.PopFont();
                        // 提示句只在懸停時才組：每個僱員每幀都組字串、再查一次附近有沒有傳喚鈴並不划算。
                        if(ImGui.IsItemHovered()) ImGuiEx.Tooltip($"{Loc.T("Entrust plan \"")}{plan.Name}{Loc.T("\" is active.")}" + (plan.ManualPlan ? Loc.T("\nThis is manual processing plan") : "") + (UIUtils.IsRetainerBellReachableForUi() ? Loc.T("\nClick to Entrust.") : ""));
                        if(ImGui.IsItemClicked())
                        {
                            // 捕獲放在點擊分支裡的區域變數：捕獲迴圈變數會讓每一列每幀都先配一個閉包物件。
                            var clicked = ret;
                            if(!Svc.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.OccupiedSummoningBell])
                                TaskInteractWithNearestBell.Enqueue();

                            P.TaskManager.Enqueue(() => RetainerListHandlers.SelectRetainerByName(clicked.Name.ToString()));
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
                postVentureTaskDraw?.SendMessage(data.CID, ret.Name);
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
                bars[(data.CID, ret.Name)] = (start, end);
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
                    var cap = ret.Level < Player.MaxLevel && data.GetJobLevel(ret.Job) == ret.Level;
                    if(cap) ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudRed);
                    ImGuiEx.TextV(GameFontLevel(ret.Level));
                    if(!cap && ret.Level < Player.MaxLevel)
                    {
                        ImGui.SameLine(0, 0);
                        ImGuiEx.TextV("/");
                        ImGui.SameLine(0, 0);
                        ImGuiEx.TextV(GameFontNumber(data.GetJobLevel(ret.Job)));
                    }
                    if(cap) ImGui.PopStyleColor();
                    // 附加資訊只在開關開著時才畫，字串也只在那時才組。
                    if(C.ShowAdditionalInfo)
                    {
                        var add = "";
                        if(adata.Ilvl > -1 && !VentureUtils.IsDoL(ret.Job))
                        {
                            add += $"{Lang.CharItemLevel}{adata.Ilvl}";
                        }
                        if((adata.Gathering > -1 || adata.Perception > -1) && VentureUtils.IsDoL(ret.Job))
                        {
                            add += $"{Lang.CharPlant}{adata.Gathering}/{adata.Perception}";
                        }
                        if(add != "")
                        {
                            ImGui.SameLine();
                            ImGuiEx.Text(add);
                        }
                    }
                }
                ImGui.TableNextColumn();
                ImGui.TableSetBgColor(ImGuiTableBgTarget.CellBg, 0);
                if(ret.VentureID != 0 && C.ShowAdditionalInfo)
                {
                    var venture = VentureUtils.GetVentureById(ret.VentureID);
                    if(venture == null)
                    {
                        // 委託 ID 查不到對應資料列時把 ID 直接顯示出來，不要讓 Draw 擲例外。
                        ImGuiEx.Text($"?{ret.VentureID}");
                        ImGui.SameLine();
                    }
                    else
                    {
                        var parts = venture.Value.GetFancyVentureNameParts(data, ret, out _);
                        if(!parts.Name.IsNullOrEmpty())
                        {
                            var c = parts.YieldRate == 4 ? ImGuiColors.ParsedGreen : ImGui.GetStyle().Colors[(int)ImGuiCol.Text];
                            ImGuiEx.Text(c, $"{(parts.Level != 0 ? $"{Lang.CharLevel}{parts.Level} " : "")}{parts.Name}");
                            ImGui.SameLine();
                        }
                    }
                }
                ImGuiEx.Text(!ret.HasVenture ? Loc.T("No Venture") : GetTimeText(texts, ret.GetVentureSecondsRemaining(C.TimerAllowNegative)));
                ImGui.TableNextColumn();
                ImGui.TableSetBgColor(ImGuiTableBgTarget.CellBg, 0);
                DrawInventorySlots(ret);
                ImGui.TableNextColumn();
                ImGui.TableSetBgColor(ImGuiTableBgTarget.CellBg, 0);
                var n = texts.SettingsPopup;
                if(ImGuiEx.IconButton(CogsIcon, texts.SettingsButton))
                {
                    ImGui.OpenPopup(n);
                }
                if(ImGuiEx.BeginPopupNextToElement(n))
                {
                    RetainerConfig.Draw(ret, data, adata);
                    ImGui.EndPopup();
                }
                ImGui.SameLine();
                if(ImGuiEx.IconButton(Lang.IconPlanner, texts.PlannerButton))
                {
                    P.VenturePlanner.Open(data, ret);
                }
            }
            ImGui.EndTable();
        }
    }

    // 以下快取只給繪製執行緒用。內容都是原本那一行運算式在第一次遇到時算出來的，之後輸入不變就沿用同一個字串。
    private sealed class RowTexts
    {
        public string AdditionalDataKey;
        public string SettingsPopup;
        public string SettingsButton;
        public string PlannerButton;
        public long TimeSeconds;
        public string TimeText;
    }

    private static readonly Dictionary<(ulong CID, string Name), RowTexts> RowTextCache = [];
    private static readonly Dictionary<int, string> GameFontLevels = [];
    private static readonly Dictionary<int, string> GameFontNumbers = [];
    private static readonly string[] FreeSlotTexts = new string[RetainerInventoryCapacity + 1];
    private static readonly string CogsIcon = FontAwesomeIcon.Cogs.ToIconString();

    private static RowTexts GetRowTexts(ulong cid, string name)
    {
        if(!RowTextCache.TryGetValue((cid, name), out var t))
        {
            t = new()
            {
                AdditionalDataKey = Utils.GetAdditionalDataKey(cid, name, false),
                SettingsPopup = $"{cid} {name} settings",
                SettingsButton = $"{cid} {name}",
                PlannerButton = $"{cid} {name} planner",
                TimeSeconds = long.MinValue,
            };
            RowTextCache[(cid, name)] = t;
        }
        return t;
    }

    /// <summary>與 <c>Utils.GetAdditionalData(cid, name)</c> 相同（沒有就建一筆），只是鍵字串用快取的。</summary>
    private static AdditionalRetainerData GetAdditionalData(string key)
    {
        if(!C.AdditionalData.TryGetValue(key, out var adata))
        {
            adata = new();
            C.AdditionalData[key] = adata;
        }
        return adata;
    }

    private static string GetTimeText(RowTexts t, long seconds)
    {
        if(t.TimeText == null || t.TimeSeconds != seconds)
        {
            t.TimeText = Utils.ToTimeString(seconds);
            t.TimeSeconds = seconds;
        }
        return t.TimeText;
    }

    private static string GameFontLevel(int level)
    {
        if(!GameFontLevels.TryGetValue(level, out var s))
        {
            s = $"{Lang.CharLevel}{level}".ReplaceByChar(Lang.Digits.Normal, Lang.Digits.GameFont);
            GameFontLevels[level] = s;
        }
        return s;
    }

    private static string GameFontNumber(int value)
    {
        if(!GameFontNumbers.TryGetValue(value, out var s))
        {
            s = value.ToString().ReplaceByChar(Lang.Digits.Normal, Lang.Digits.GameFont);
            GameFontNumbers[value] = s;
        }
        return s;
    }

    /// <summary>等同 <c>C.EntrustPlans.TryGetFirst(s =&gt; s.Guid == guid, ...)</c>（取第一個符合的），但不配置閉包。</summary>
    private static EntrustPlan FindEntrustPlan(Guid guid)
    {
        foreach(var s in C.EntrustPlans)
        {
            if(s.Guid == guid) return s;
        }
        return null;
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
        ImGuiEx.Text(color, FreeSlotTexts[free] ??= free.ToString());
        if(ImGui.IsItemHovered()) ImGuiEx.Tooltip($"{Loc.T("Free inventory slots")}: {free}/{RetainerInventoryCapacity}\n" +
            $"{Loc.T("Items held")}: {ret.ItemCount}\n" +
            $"{Loc.T("Listed on market board")}: {ret.MBItems}\n\n" +
            Loc.T("Snapshot taken the last time this character was logged in."));
    }
}
