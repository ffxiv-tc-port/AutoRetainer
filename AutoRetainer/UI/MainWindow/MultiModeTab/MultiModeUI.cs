using AutoRetainer.Internal;
using AutoRetainer.Services;
using AutoRetainerAPI.Configuration;

namespace AutoRetainer.UI.MainWindow.MultiModeTab;

internal static unsafe class MultiModeUI
{
    internal static bool JustRelogged = false;
    private static Dictionary<(ulong CID, string Name), (Vector2 start, Vector2 end)> bars = [];
    private static float StatusTextWidth = 0f;

    // 以下三個容器只給繪製執行緒用：前兩個每幀清空重填，StatusTexts 是逐角色的字串快取。
    private static readonly List<OverlayTextData> OverlayTexts = [];
    private static readonly List<OfflineCharacterData> SortedData = [];
    private static readonly Dictionary<ulong, StatusTextsEntry> StatusTexts = [];

    private static readonly string IconDoorOpen = FontAwesomeIcon.DoorOpen.ToIconString();
    private static readonly string IconUserCog = FontAwesomeIcon.UserCog.ToIconString();
    private static readonly string IconCheckCircle = FontAwesomeIcon.CheckCircle.ToIconString();

    internal static void Draw()
    {
        var overlayTexts = OverlayTexts;
        overlayTexts.Clear();
        // Snapshot once per frame: this flag decides whether each row gets the extra "C:" column, and it
        // can change mid-draw (per-character config popups mutate config inside the loop below;
        // MultiMode.Enabled is a bare static written from IPC/auto-disable). Evaluating it per row can
        // produce rows with different column counts within a single frame, which makes DrawOverlayTexts
        // throw IndexOutOfRangeException.
        var showCharaCnt = C.CharEqualize && MultiMode.Enabled;
        // 同一個理由（見上）：額度欄的四個開關也是一幀只讀一次，不要逐列去讀設定。
        var allowanceColumns = UIUtils.SnapshotAllowanceColumns();
        // 黑名單是空的時候這行本來就什麼都不移除；先判空只是不要每幀替每個角色配一個閉包。
        if(C.Blacklist.Count > 0) C.OfflineData.RemoveAll(x => C.Blacklist.Any(z => z.CID == x.CID));
        List<OfflineCharacterData> sortedData;
        JustRelogged = false;
        if(C.NoCurrentCharaOnTop)
        {
            sortedData = C.OfflineData.ApplyOrder(C.RetainersVisualOrders);
        }
        else
        {
            sortedData = SortedData;
            sortedData.Clear();
            var cdata = FindCharacter(Svc.PlayerState.ContentId);
            if(cdata != null)
            {
                sortedData.Add(cdata);
            }
            foreach(var x in C.OfflineData.ApplyOrder(C.RetainersVisualOrders))
            {
                if(x.CID != Svc.PlayerState.ContentId)
                {
                    sortedData.Add(x);
                }
            }
        }
        UIUtils.DrawSearch();
        for(var index = 0; index < sortedData.Count; index++)
        {
            var data = sortedData[index];
            if(data.World.IsNullOrEmpty() || data.ExcludeRetainer) continue;
            var search = Ref<string>.Get(Loc.T("SearchChara"));
            if(search != "" && !$"{data.Name}@{data.World}".Contains(search, StringComparison.OrdinalIgnoreCase)) continue;
            ImGui.PushID($"{data.CID}");
            var rCurPos = ImGui.GetCursorPos();
            var colen = false;
            if(data.Enabled)
            {
                ImGui.PushStyleColor(ImGuiCol.Button, 0xFF097000);
                colen = true;
            }
            if(ImGuiEx.IconButton(Lang.IconMultiMode))
            {
                data.Enabled = !data.Enabled;
            }
            if(colen) ImGui.PopStyleColor();
            ImGuiEx.Tooltip(Loc.T("Enable multi-mode for this character"));
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
            if(ImGui.IsItemClicked(ImGuiMouseButton.Right))
            {
                Copy($"/ays relog {data.Name}@{data.World}");
            }
            ImGuiEx.Tooltip(Loc.T("Left click - relog to this character\nRight click - copy relog command into clipboard"));
            ImGui.SameLine(0, 3);
            if(ImGuiEx.IconButton(IconUserCog))
            {
                ImGui.OpenPopup($"popup{data.CID}");
            }
            ImGuiEx.Tooltip(Loc.T("Configure Character"));
            ImGui.SameLine(0, 3);

            if(ImGui.BeginPopup($"popup{data.CID}"))
            {
                CharaConfig.Draw(data, true);
                ImGui.EndPopup();
            }
            data.DrawDCV();
            UIUtils.DrawTeleportIcons(data.CID);
            SharedUI.DrawLockout(data);

            var initCurpos = ImGui.GetCursorPos();
            var lowestRetainer = PickProgressRetainer(data, C.MultiModeRetainerConfiguration.MultiWaitForAll);
            if(lowestRetainer != default)
            {
                var prog = Math.Max(0, (3600 - lowestRetainer.GetVentureSecondsRemaining(false)) / 3600f);
                var pcol = prog == 1f ? (C.NoGradient ? 0xbb005000.ToVector4() : GradientColor.Get(0xbb500000.ToVector4(), 0xbb005000.ToVector4())) : 0xbb500000.ToVector4();
                ImGui.PushStyleColor(ImGuiCol.PlotHistogram, pcol);
                ImGui.ProgressBar(prog, new(ImGui.GetContentRegionAvail().X, ImGui.CalcTextSize("A").Y + ImGui.GetStyle().FramePadding.Y * 2), "");
                ImGui.PopStyleColor();
                ImGui.SetCursorPos(initCurpos);
            }
            var colpref = UIUtils.PushColIfPreferredCurrent(data);

            var anyRetainerDone = AnyEnabledRetainerDone(data);
            if(anyRetainerDone)
            {
                ImGui.PushFont(UiBuilder.IconFont);
                ImGuiEx.TextV(ImGuiColors.HealerGreen, IconCheckCircle);
                ImGui.PopFont();
                ImGuiEx.Tooltip(Loc.T("Some retainers have completed ventures"));
                ImGui.SameLine(0, 3);
            }
            if(ImGui.CollapsingHeader($"{data.GetCutCharaString(StatusTextWidth)}###workshop{data.CID}###chara{data.CID}"))
            {
                SetAsPreferred(data);
                if(colpref)
                {
                    ImGui.PopStyleColor();
                    colpref = false;
                }
                ImGui.PushID($"{data.CID}");

                var storePos = ImGui.GetCursorPos();
                var retainerData = data.RetainerData;
                foreach(var ret in retainerData)
                {
                    if(bars.TryGetValue((data.CID, ret.Name), out var v))
                    {
                        if(!ret.HasVenture || ret.Level == 0 || ret.Name.ToString().IsNullOrEmpty()) continue;
                        ImGui.SetCursorPos(v.start - ImGui.GetStyle().CellPadding with { Y = 0 });
                        ImGui.PushStyleColor(ImGuiCol.PlotHistogram, 0xbb500000);
                        ImGui.PushStyleColor(ImGuiCol.FrameBg, 0);
                        ImGui.ProgressBar(1f - Math.Min(1f, ret.GetVentureSecondsRemaining(false) / (60f * 60f)),
                            new(ImGui.GetContentRegionAvail().X, v.end.Y - v.start.Y - ImGui.GetStyle().CellPadding.Y), "");
                        ImGui.PopStyleColor(2);
                    }
                }
                ImGui.SetCursorPos(storePos);
                RetainerTable.Draw(data, retainerData, bars);
                ImGui.Dummy(new(2, 2));
                ImGui.PopID();
            }
            else
            {
                SetAsPreferred(data);
                if(colpref)
                {
                    ImGui.PopStyleColor();
                    colpref = false;
                }
            }
            ImGui.SameLine(0, 0);
            var texts = GetStatusTexts(data, allowanceColumns, showCharaCnt);
            overlayTexts.Add((new Vector2(ImGui.GetContentRegionMax().X - ImGui.GetStyle().FramePadding.X, rCurPos.Y + ImGui.GetStyle().FramePadding.Y), texts));
            ImGui.NewLine();
            ImGui.PopID();
        }
        StatusTextWidth = 0f;
        UIUtils.DrawOverlayTexts(overlayTexts, ref StatusTextWidth);

        if(C.Verbose && ImGui.CollapsingHeader("Debug"))
        {
            ImGuiEx.Text($"GetCurrentTargetCharacter: {MultiMode.GetCurrentTargetCharacter()}");
            //ImGuiEx.Text($"Yes Already: {YesAlready.IsEnabled()}");
            ImGuiEx.Text($"IsCurrentCharacterDone: {MultiMode.IsCurrentCharacterDone()}");
            ImGuiEx.Text($"NextInteraction: {Math.Max(0, MultiMode.NextInteractionAt - Environment.TickCount64)}");
            ImGuiEx.Text($"EnsureCharacterValidity: {MultiMode.EnsureCharacterValidity(true)}");
            ImGuiEx.Text($"IsInteractionAllowed: {MultiMode.IsInteractionAllowed()}");
            ImGuiEx.Text($"GetPreferredCharacter: {MultiMode.GetPreferredCharacter()}");
            ImGuiEx.Text($"IsAllRetainersHaveMoreThan15Mins: {MultiMode.IsAllRetainersHaveMoreThan15Mins()}");
            ImGuiEx.Text($"Target ?? Preferred: {MultiMode.GetCurrentTargetCharacter() ?? MultiMode.GetPreferredCharacter()}");
            //ImGuiEx.Text($"GetAutoAfkOpt: {MultiMode.GetAutoAfkOpt()}");
            //ImGuiEx.Text($"AutoAfkValue: {ConfigModule.Instance()->GetIntValue(145)}");
            ImGuiEx.Text($"LastLongin: {MultiMode.LastLogin:X16}");
            ImGuiEx.Text($"AnyRetainersAvailable: {MultiMode.AnyRetainersAvailable()}");
            ImGuiEx.Text($"IsAnySelectedRetainerFinishesWithin, 60: {MultiMode.IsAnySelectedRetainerFinishesWithin(60)}");
            ImGuiEx.Text($"IsAnySelectedRetainerFinishesWithin, 5*60: {MultiMode.IsAnySelectedRetainerFinishesWithin(5 * 60)}");
            foreach(var data in C.OfflineData)
            {
                ImGuiEx.Text($"Chatacter {data}\n  GetNeededVentureAmount: {data.GetNeededVentureAmount()}");
            }
        }
    }

    /// <summary>等同 <c>C.OfflineData.TryGetFirst(x =&gt; x.CID == cid, ...)</c>（取第一個符合的），但不配置列舉器。</summary>
    internal static OfflineCharacterData FindCharacter(ulong cid)
    {
        foreach(var x in C.OfflineData)
        {
            if(x.CID == cid) return x;
        }
        return null;
    }

    /// <summary>
    /// 等同 <c>data.GetEnabledRetainers().OrderBy(z =&gt; z.GetVentureSecondsRemaining())</c> 再取
    /// <c>LastOrDefault()</c>（<paramref name="last"/>，同值取最後一名）或 <c>FirstOrDefault()</c>（同值取第一名），但不配置集合。
    /// </summary>
    private static OfflineRetainerData PickProgressRetainer(OfflineCharacterData data, bool last)
    {
        if(!C.SelectedRetainers.TryGetValue(data.CID, out var enabled)) return null;
        OfflineRetainerData best = null;
        long bestKey = 0;
        foreach(var z in data.RetainerData)
        {
            if(!(enabled.Contains(z.Name) && z.HasVenture)) continue;
            var key = z.GetVentureSecondsRemaining();
            if(best == null || (last ? key >= bestKey : key < bestKey))
            {
                best = z;
                bestKey = key;
            }
        }
        return best;
    }

    /// <summary>等同 <c>data.GetEnabledRetainers().Any(...)</c> 的「有僱員探險已完成」，但不配置集合。</summary>
    private static bool AnyEnabledRetainerDone(OfflineCharacterData data)
    {
        if(!C.SelectedRetainers.TryGetValue(data.CID, out var enabled)) return false;
        foreach(var r in data.RetainerData)
        {
            if(!(enabled.Contains(r.Name) && r.HasVenture)) continue;
            if(r.HasVenture && r.Level != 0 && !r.Name.ToString().IsNullOrEmpty() && r.GetVentureSecondsRemaining(false) <= 0) return true;
        }
        return false;
    }

    /// <summary>
    /// 角色列右側的狀態欄字串。只有 V／I／C 三欄時，數值、警示判定與開關都沒變就沿用上一幀那一份；
    /// 開了額度欄就照舊每幀重組（那幾欄的 tooltip 帶取樣時間）。只給繪製執行緒用。
    /// </summary>
    private static (bool Warning, string Text, bool Unknown, string Tooltip)[] GetStatusTexts(OfflineCharacterData data, UIUtils.AllowanceColumns allowanceColumns, bool showCharaCnt)
    {
        var ventureWarning = data.Ventures < C.UIWarningRetVentureNum;
        var inventoryWarning = data.InventorySpace < C.UIWarningRetSlotNum;
        if(allowanceColumns != default)
        {
            List<(bool Warning, string Text, bool Unknown, string Tooltip)> texts = [
                (ventureWarning, $"V: {data.Ventures}", false, null),
                (inventoryWarning, $"I: {data.InventorySpace}", false, null)];
            UIUtils.AddAllowanceTexts(texts, data, allowanceColumns);
            if(showCharaCnt)
            {
                texts.Insert(0, (false, $"C: {MultiMode.CharaCnt.GetOrDefault(data.CID)}", false, null));
            }
            return [.. texts];
        }
        var charaCnt = showCharaCnt ? MultiMode.CharaCnt.GetOrDefault(data.CID) : 0;
        if(StatusTexts.TryGetValue(data.CID, out var e)
            && e.Ventures == data.Ventures && e.InventorySpace == data.InventorySpace
            && e.VentureWarning == ventureWarning && e.InventoryWarning == inventoryWarning
            && e.ShowCharaCnt == showCharaCnt && e.CharaCnt == charaCnt)
        {
            return e.Texts;
        }
        (bool Warning, string Text, bool Unknown, string Tooltip)[] built = showCharaCnt
            ? [(false, $"C: {charaCnt}", false, null), (ventureWarning, $"V: {data.Ventures}", false, null), (inventoryWarning, $"I: {data.InventorySpace}", false, null)]
            : [(ventureWarning, $"V: {data.Ventures}", false, null), (inventoryWarning, $"I: {data.InventorySpace}", false, null)];
        StatusTexts[data.CID] = new(data.Ventures, data.InventorySpace, ventureWarning, inventoryWarning, showCharaCnt, charaCnt, built);
        return built;
    }

    private readonly record struct StatusTextsEntry(uint Ventures, uint InventorySpace, bool VentureWarning, bool InventoryWarning, bool ShowCharaCnt, int CharaCnt, (bool Warning, string Text, bool Unknown, string Tooltip)[] Texts);

    internal static void SetAsPreferred(OfflineCharacterData x)
    {
        if(ImGui.IsItemClicked(ImGuiMouseButton.Right))
        {
            if(x.Preferred)
            {
                x.Preferred = false;
            }
            else
            {
                C.OfflineData.Each(x => x.Preferred = false);
                x.Preferred = true;
            }
        }
    }
}
