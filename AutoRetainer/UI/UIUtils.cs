global using OverlayTextData = (System.Numerics.Vector2 Curpos, (bool Warning, string Text, bool Unknown, string Tooltip)[] Texts);
using AutoRetainerAPI.Configuration;
using AutoRetainer.Services.Lifestream;
using ECommons.GameHelpers;
using ECommons.Interop;
using Lumina.Excel.Sheets;

namespace AutoRetainer.UI;

internal static class UIUtils
{
    public static void DrawSortableEnumList<T>(string id, List<T> list) where T : struct, Enum
    {
        ref var dragDrop = ref Ref<ImGuiEx.RealtimeDragDrop<T>>.Get($"dsel{id}", () => new($"dsel{id}", x => x.ToString()));
        ImGui.PushID(id);
        if(ImGui.BeginCombo("##addNew", Loc.T("Add Entries..."), ImGuiComboFlags.HeightLarge))
        {
            foreach(var x in Enum.GetValues<T>())
            {
                if(!list.Contains(x))
                {
                    if(ImGui.Selectable(x.ToStringEx(), false, ImGuiSelectableFlags.DontClosePopups))
                    {
                        list.Add(x);
                    }
                }
            }
            ImGui.EndCombo();
        }
        dragDrop.Begin();
        for(var i = 0; i < list.Count; i++)
        {
            var x = list[i];
            ImGui.PushID(x.ToString());
            dragDrop.DrawButtonDummy(x, list, i);
            ImGui.SameLine();
            if(ImGuiEx.IconButton(FontAwesomeIcon.Trash))
            {
                new TickScheduler(() => list.Remove(x));
            }
            ImGui.SameLine();
            ImGuiEx.Text(x.ToStringEx());
            ImGui.PopID();
        }
        dragDrop.End();
        ImGui.PopID();
    }

    public static string ToStringEx<T>(this T obj) where T : Enum
    {
        return obj.ToString().Replace('_', ' ');
    }

    public static bool PushColIfPreferredCurrent(this OfflineCharacterData data)
    {
        var normalColor = Player.CID == data.CID ? EColor.CyanBright : ImGui.GetStyle().Colors[(int)ImGuiCol.Text];
        if(data.Preferred)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, GradientColor.Get(normalColor, ImGuiColors.ParsedGreen));
            return true;
        }
        else
        {
            ImGui.PushStyleColor(ImGuiCol.Text, normalColor);
            return true;
        }
    }

    public static void DrawSearch()
    {
        if(!C.NoCharaSearch)
        {
            ImGuiEx.SetNextItemFullWidth();
            ImGui.InputTextWithHint("##search", Loc.T("Search characters..."), ref Ref<string>.Get(Loc.T("SearchChara")), 50);
        }
    }

    public static void DrawDCV(this OfflineCharacterData data)
    {
        if(data.WorldOverride != null)
        {
            ImGui.PushFont(UiBuilder.IconFont);
            ImGuiEx.Text("\uf0ac");
            ImGui.PopFont();
            if(ImGuiEx.HoveredAndClicked(Loc.T("Visiting another data center. Right click to clear this status."), ImGuiMouseButton.Right))
            {
                data.WorldOverride = null;
            }
            ImGui.SameLine();
        }
    }

    public static void DrawTeleportIcons(ulong cid)
    {
        var offlineData = C.OfflineData.FirstOrDefault(x => x.CID == cid);
        if(offlineData == null) return;
        var data = LifestreamHousePath.Get(cid);
        if(offlineData.GetAllowFcTeleportForSubs() || offlineData.GetAllowFcTeleportForRetainers())
        {
            string error = null;
            if(data.FC == null)
            {
                error = Loc.T("Free company house is not registered in Lifestream");
            }
            else if(data.FC.PathToEntrance.Count == 0)
            {
                error = Loc.T("Free company house is registered in Lifestream but path to entrance is not set");
            }
            ImGui.PushFont(UiBuilder.IconFont);
            ImGuiEx.Text(error == null ? null : ImGuiColors.DalamudGrey3, "\uf1ad");
            ImGui.PopFont();
            ImGuiEx.Tooltip(error ?? string.Format(Loc.T("Free company house is registered in Lifestream and path is set. You will be teleported to Free company house for resending Deployables. If enabled, you will be teleported to Free company house for resending retainers as well.\nAddress: {0}, ward {1}, plot {2}"), Svc.Data.GetExcelSheet<Aetheryte>().GetRowOrDefault((uint)data.FC.ResidentialDistrict)?.Territory.Value.PlaceNameRegion.Value.Name, data.FC.Ward + 1, data.FC.Plot + 1));
            ImGui.SameLine(0, 3);
        }
        if(offlineData.GetAllowPrivateTeleportForRetainers())
        {
            string error = null;
            if(data.Private == null)
            {
                error = Loc.T("Private house is not registered in Lifestream.");
            }
            else if(data.Private.PathToEntrance.Count == 0)
            {
                error = Loc.T("Private house is registered in Lifestream but path to entrance is not set.");
            }
            ImGui.PushFont(UiBuilder.IconFont);
            ImGuiEx.Text(error == null ? null : ImGuiColors.DalamudGrey3, "\ue1b0");
            ImGui.PopFont();
            ImGuiEx.Tooltip(error ?? string.Format(Loc.T("Private house is registered in Lifestream and path is set. You will be teleported to Private house for resending Retainers.\nAddress: {0}, ward {1}, plot {2}"), Svc.Data.GetExcelSheet<Aetheryte>().GetRowOrDefault((uint)data.Private.ResidentialDistrict)?.Territory.Value.PlaceNameRegion.Value.Name, data.Private.Ward + 1, data.Private.Plot + 1));
            ImGui.SameLine(0, 3);
        }
    }

    public static void DrawOverlayTexts(List<OverlayTextData> overlayTexts, ref float statusTextWidth)
    {
        if(overlayTexts.Count > 0)
        {
            // Rows can have different column counts (the "C:" counter column is
            // conditional), so size the array to the longest row and compute
            // each column's max only over the rows that have that column.
            // Sizing and indexing everything from row 0 crashed with
            // IndexOutOfRangeException when lengths differed within one frame.
            var maxSizes = new float[overlayTexts.Max(x => x.Texts.Length)];
            for(var i = 0; i < maxSizes.Length; i++)
            {
                maxSizes[i] = overlayTexts.Where(x => i < x.Texts.Length).Select(x => ImGui.CalcTextSize(x.Texts[i].Text).X).Max();
            }
            foreach(var x in overlayTexts)
            {
                var cur = ImGui.GetCursorPos();
                for(var i = x.Texts.Length - 1; i >= 0; i--)
                {
                    var width = maxSizes[i..].Sum() + (maxSizes[i..].Length - 1) * ImGui.CalcTextSize("      ").X;
                    ImGui.SetCursorPos(new(x.Curpos.X - width, x.Curpos.Y));
                    if(statusTextWidth < width) statusTextWidth = width;
                    // 三種狀態要分得開：灰＝這個角色從來沒讀到過那個值（不是 0），
                    // 橘＝該處理了，預設色＝正常。灰色優先於橘色，因為「不知道」不該長得像警告。
                    var col = x.Texts[i].Unknown ? ImGuiColors.DalamudGrey3
                        : x.Texts[i].Warning ? ImGuiColors.DalamudOrange : (Vector4?)null;
                    ImGuiEx.Text(col, x.Texts[i].Text);
                    // 取樣時間這類「起疑才查」的長文字放 tooltip，不佔列上版面。
                    if(!x.Texts[i].Tooltip.IsNullOrEmpty()) ImGuiEx.Tooltip(x.Texts[i].Tooltip);
                }
                ImGui.SetCursorPos(cur);
            }
        }
    }

    /// <remarks>
    /// 把每日／每週配額三欄（理符受理限額 L／籌備委託品 D／限定神典石 T）加到角色列右側的狀態欄。
    /// 🔴 三態顯示：從沒讀到過一律畫灰色的 <c>?</c>，<b>絕不畫成 0</b>——理符受理限額的 0 與籌備委託品的 0
    /// 意義相反（一個代表沒得用、一個代表都用完了），把「不知道」畫成 0 會讓使用者直接跳過
    /// 該做的事。取樣時間屬於「起疑才查」，放 tooltip。
    /// </remarks>
    public static void AddAllowanceTexts(List<(bool Warning, string Text, bool Unknown, string Tooltip)> texts, OfflineCharacterData data)
    {
        if(!C.UIShowAllowances) return;
        texts.Add(BuildAllowance("L", data.LevequestAllowances, data.LevequestAllowancesUpdatedAt,
            data.LevequestAllowances >= C.UIWarningLeveAllowancesNum,
            Loc.T("Levequest allowances remaining (caps at 100)")));
        texts.Add(BuildAllowance("D", data.CustomDeliveryAllowances, data.CustomDeliveryAllowancesUpdatedAt,
            data.CustomDeliveryAllowances == 0,
            Loc.T("Custom delivery allowances left this week")));
        var tomeKnown = data.WeeklyTomestoneUpdatedAt != null && data.WeeklyTomestoneCount >= 0 && data.WeeklyTomestoneCap > 0;
        texts.Add((
            tomeKnown && data.WeeklyTomestoneCount >= data.WeeklyTomestoneCap - C.UIWarningTomestoneMargin,
            tomeKnown ? $"T: {data.WeeklyTomestoneCount}/{data.WeeklyTomestoneCap}" : "T: ?",
            !tomeKnown,
            BuildAllowanceTooltip(Loc.T("Limited tomestones acquired this week"), data.WeeklyTomestoneUpdatedAt, tomeKnown)));
    }

    private static (bool Warning, string Text, bool Unknown, string Tooltip) BuildAllowance(string prefix, int value, DateTime? sampledAt, bool warningWhenKnown, string label)
    {
        // 「讀到過」要值與時間戳兩邊都成立：只看值會把 -1 以外的任何殘留當成真值，
        // 只看時間戳則在欄位語意日後改變時失準。
        var known = sampledAt != null && value >= 0;
        return (known && warningWhenKnown, $"{prefix}: {(known ? value.ToString() : "?")}", !known,
            BuildAllowanceTooltip(label, sampledAt, known));
    }

    private static string BuildAllowanceTooltip(string label, DateTime? sampledAt, bool known)
    {
        if(!known) return $"{label}\n{Loc.T("Never read on this character yet. AutoRetainer fills this in automatically shortly after logging in.")}";
        return $"{label}\n{Loc.T("Sampled at")}: {sampledAt.Value:yyyy-MM-dd HH:mm}";
    }

    public static float CollapsingHeaderSpacingsWidth => ImGui.GetStyle().FramePadding.X * 2f + ImGui.GetStyle().ItemSpacing.X * 2 + ImGui.CalcTextSize("▲...").X;

    public static string GetCutCharaString(this OfflineCharacterData data, float statusTextWidth)
    {
        var chstr = Censor.Character(data.Name, data.World);
        var mod = false;
        while(ImGui.CalcTextSize(chstr).X > ImGui.GetContentRegionAvail().X - statusTextWidth - UIUtils.CollapsingHeaderSpacingsWidth && chstr.Length > 5)
        {
            mod = true;
            chstr = chstr[0..^1];
        }
        if(mod) chstr += "...";
        return chstr;
    }

    internal static void SliderIntFrameTimeAsFPS(string name, ref int frameTime, int min = 1)
    {
        var fps = 60;
        if(frameTime != 0)
        {
            fps = GetFPSFromMSPT(frameTime);
        }
        ImGuiEx.SliderInt(name, ref fps, min, 60, fps == 60 ? Loc.T("Unlimited") : null, ImGuiSliderFlags.AlwaysClamp);
        frameTime = fps == 60 ? 0 : (int)(1000f / fps);
    }

    public static int GetFPSFromMSPT(int frameTime)
    {
        return frameTime == 0 ? 60 : (int)(1000f / frameTime);
    }

    /// <summary>
    /// 判斷「按住修飾鍵」型的快捷鍵目前是否成立。這些功能改成可設定之前是硬編 ImGui 的
    /// KeyShift/KeyCtrl/KeyAlt，有兩個性質必須一併保留，否則就是靜默的行為回退：
    /// <list type="number">
    /// <item>ImGui 的修飾鍵狀態<b>不分左右</b>。LimitedKeys 沒有合併的 Shift/Ctrl/Alt(只有 Left*/Right*)，
    /// 所以選到左側修飾鍵時右側同樣算數——預設值就是左側，慣用右側 Shift 的人升級後不會失去功能。
    /// 想嚴格只認單邊就選右側的那一個。</item>
    /// <item>遊戲視窗失焦時 ImGui 收不到按鍵。這裡改用 winapi 的 GetAsyncKeyState(IsKeyPressed)，
    /// 它<b>連別的程式裡按的鍵都讀得到</b>，所以必須自己補上 WindowInactive 閘門，
    /// 否則「alt-tab 出去時剛好按著 Shift」會讓上一次 hover 的道具被加進清單。</item>
    /// </list>
    /// LimitedKeys.None ＝ 停用該動作，不是「不按任何鍵就觸發」。
    /// </summary>
    internal static unsafe bool IsHotkeyHeld(LimitedKeys key)
    {
        if(key == LimitedKeys.None) return false;
        var framework = CSFramework.Instance();
        if(framework == null || framework->WindowInactive) return false;
        return key switch
        {
            LimitedKeys.LeftShiftKey => IsKeyPressed(LimitedKeys.LeftShiftKey) || IsKeyPressed(LimitedKeys.RightShiftKey),
            LimitedKeys.LeftControlKey => IsKeyPressed(LimitedKeys.LeftControlKey) || IsKeyPressed(LimitedKeys.RightControlKey),
            LimitedKeys.LeftAltKey => IsKeyPressed(LimitedKeys.LeftAltKey) || IsKeyPressed(LimitedKeys.RightAltKey),
            _ => IsKeyPressed(key),
        };
    }

    /// <summary>
    /// 快捷鍵在提示文字裡的顯示名稱。未綁定時要在列上看得見「這個動作是停用的」，
    /// 不能只是把提示變灰——變灰看起來像「現在沒按著」。
    /// </summary>
    internal static string HotkeyName(LimitedKeys key) => key == LimitedKeys.None ? Loc.T("(unbound - disabled)") : key.ToString();

    internal static void QRA(string text, ref LimitedKeys key)
    {
        if(DrawKeybind(text, ref key))
        {
            P.quickSellItems.Toggle();
        }
        ImGui.SameLine();
        ImGuiEx.Text(Loc.T("+ right click"));
    }

    private static string KeyInputActive = null;
    internal static bool DrawKeybind(string text, ref LimitedKeys key)
    {
        var ret = false;
        ImGui.PushID(text);
        ImGuiEx.Text($"{text}:");
        ImGui.Dummy(new(20, 1));
        ImGui.SameLine();
        ImGuiEx.SetNextItemWidthScaled(200f);
        if(ImGui.BeginCombo("##inputKey", $"{key}", ImGuiComboFlags.HeightLarge))
        {
            if(text == KeyInputActive)
            {
                ImGuiEx.Text(ImGuiColors.DalamudYellow, Loc.T("Now press new key..."));
                foreach(var x in Enum.GetValues<LimitedKeys>())
                {
                    if(IsKeyPressed(x))
                    {
                        KeyInputActive = null;
                        key = x;
                        ret = true;
                        break;
                    }
                }
            }
            else
            {
                if(ImGui.Selectable(Loc.T("Auto-detect new key"), false, ImGuiSelectableFlags.DontClosePopups))
                {
                    KeyInputActive = text;
                }
                ImGuiEx.Text(Loc.T("Select key manually:"));
                ImGuiEx.SetNextItemFullWidth();
                ImGuiEx.EnumCombo("##selkeyman", ref key);
            }
            ImGui.EndCombo();
        }
        else
        {
            if(text == KeyInputActive)
            {
                KeyInputActive = null;
            }
        }
        if(key != LimitedKeys.None)
        {
            ImGui.SameLine();
            if(ImGuiEx.IconButton(FontAwesomeIcon.Trash))
            {
                key = LimitedKeys.None;
                ret = true;
            }
        }
        ImGui.PopID();
        return ret;
    }
}
