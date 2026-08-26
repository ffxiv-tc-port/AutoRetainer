using AutoRetainerAPI.Configuration;
using Dalamud.Interface.Components;
using ECommons.ExcelServices;

namespace AutoRetainer.UI.Statistics;

public sealed class GilDisplayManager
{
    private GilDisplayManager() { }

    public void Draw()
    {
        ImGuiEx.SetNextItemWidthScaled(200f);
        ImGui.InputInt(Loc.T("Ignore characters/retainers with gil less than"), ref C.MinGilDisplay.ValidateRange(0, int.MaxValue));
        ImGuiComponents.HelpMarker(Loc.T("Ignored retainer gil still contributes to character/DC total. Character is ignored if their gil AND all retainers' gil is less than this value. Ignored characters do not contribute to DC total."));
        ref var filter = ref Ref<string>.Get();
        ImGui.Checkbox(Loc.T("Only display character total"), ref C.GilOnlyChars);
        ImGui.SameLine();
        ImGuiEx.SetNextItemFullWidth();
        ImGui.InputTextWithHint("##fltr", Loc.T("Filter..."), ref filter, 50);
        Dictionary<ExcelWorldHelper.Region, List<OfflineCharacterData>> data = [];
        foreach(var x in C.OfflineData)
        {
            if(ExcelWorldHelper.TryGet(x.World, out var world))
            {
                // 台服(陸行鳥 DC=151)的 WorldDCGroupType.Region 是 8,ExcelWorldHelper.Region
                // 列舉沒有這個具名值,但轉型本身沒問題——只是顯示時要用 GetRegionDisplayName()
                // 而不是直接 ToString(),否則裸數字「8」會取代地區名稱。
                var region = world.GetRegion();
                if(!data.ContainsKey(region))
                {
                    data[region] = [];
                }
                data[region].Add(x);
            }
        }
        var globalTotal = 0L;
        foreach(var x in data)
        {
            ImGuiEx.Text($"{x.Key.GetRegionDisplayName()}:");
            var dcTotal = 0L;
            foreach(var c in x.Value)
            {
                if(c.NoGilTrack) continue;
                if(filter != "" && !$"{c.Name}@{c.World}".Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
                FCData fcdata = null;
                var charTotal = c.Gil + c.RetainerData.Sum(s => s.Gil);
                foreach(var fc in C.FCData)
                {
                    if(S.FCData.GetHolderChara(fc.Key, fc.Value) == c && fc.Value.GilCountsTowardsChara)
                    {
                        fcdata = fc.Value;
                        charTotal += fcdata.Gil;
                        break;
                    }
                }
                if(charTotal > C.MinGilDisplay)
                {
                    if(!C.GilOnlyChars)
                    {
                        ImGuiEx.Text($"    {Censor.Character(c.Name, c.World)}: {c.Gil:N0}");
                        foreach(var r in c.RetainerData)
                        {
                            if(r.Gil > C.MinGilDisplay)
                            {
                                ImGuiEx.Text($"        {Censor.Retainer(r.Name)}: {r.Gil:N0}");
                            }
                        }
                        if(fcdata != null && fcdata.Gil > 0)
                        {
                            ImGuiEx.Text(ImGuiColors.DalamudYellow, $"        {Loc.T("Free Company ")}{fcdata.Name}: {fcdata.Gil:N0}");
                        }
                    }
                    ImGuiEx.Text(ImGuiColors.DalamudViolet, $"    {Censor.Character(c.Name, c.World)}{(fcdata != null && fcdata.Gil > 0 ? "+FC" : "")} total: {charTotal:N0}");
                    if(ImGuiEx.HoveredAndClicked(Loc.T("Click to relog")))
                    {
                        if(!MultiMode.Relog(c, out var error, Internal.RelogReason.Command))
                        {
                            Notify.Error(error);
                        }
                    }
                    dcTotal += charTotal;
                    ImGui.Separator();
                }
            }
            ImGuiEx.Text(ImGuiColors.DalamudOrange, $"{Loc.T("Data center total (")}{x.Key.GetRegionDisplayName()}): {dcTotal:N0}");
            globalTotal += dcTotal;
            ImGui.Separator();
            ImGui.Separator();
        }
        ImGuiEx.Text(ImGuiColors.DalamudOrange, $"{Loc.T("Overall total: ")}{globalTotal:N0}");
    }
}
