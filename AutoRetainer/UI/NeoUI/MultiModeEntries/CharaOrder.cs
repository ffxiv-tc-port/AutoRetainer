using AutoRetainerAPI.Configuration;

namespace AutoRetainer.UI.NeoUI.MultiModeEntries;
public class CharaOrder : NeoUIEntry
{
    public override string Path => Loc.T("Multi Mode/Exclusions and Order");

    private static string Search = "";
    private static ImGuiEx.RealtimeDragDrop<OfflineCharacterData> DragDrop = new("CharaOrder", x => x.Identity);

    public override bool NoFrame { get; set; } = true;

    public override void Draw()
    {
        C.OfflineData.RemoveAll(x => C.Blacklist.Any(z => z.CID == x.CID));
        var b = new NuiBuilder()
        .Section(Loc.T("Character Order"))
        .Widget(Loc.T(SharedText.CharacterSortingExplanation), (x) =>
        {
            ImGuiEx.TextWrapped(Loc.T(SharedText.CharacterSortingExplanation));
            ImGui.SetNextItemWidth(150f);
            ImGui.InputText(Loc.T("Search"), ref Search, 50);
            DragDrop.Begin();
            if(ImGui.BeginTable("CharaOrderTable", 3, ImGuiTableFlags.Borders | ImGuiTableFlags.NoSavedSettings | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit))
            {
                ImGui.TableSetupColumn("##ctrl");
                ImGui.TableSetupColumn(Loc.T("Character"), ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn(Loc.T("Toggles"));
                ImGui.TableHeadersRow();

                for(var index = 0; index < C.OfflineData.Count; index++)
                {
                    var chr = C.OfflineData[index];
                    ImGui.PushID(chr.Identity);
                    ImGui.TableNextRow();
                    DragDrop.SetRowColor(chr.Identity);
                    ImGui.TableNextColumn();
                    DragDrop.NextRow();
                    DragDrop.DrawButtonDummy(chr, C.OfflineData, index);
                    ImGui.TableNextColumn();
                    ImGuiEx.TextV((Search != "" && ($"{chr.Name}@{chr.World}").Contains(Search, StringComparison.OrdinalIgnoreCase)) ? ImGuiColors.ParsedGreen : (Search == "" ? null : ImGuiColors.DalamudGrey3), Censor.Character(chr.Name, chr.World));
                    ImGui.TableNextColumn();
                    if(ImGuiEx.ButtonCheckbox(FontAwesomeIcon.Users, ref chr.ExcludeRetainer, inverted: true))
                    {
                        chr.Enabled = false;
                        C.SelectedRetainers.Remove(chr.CID);
                    }
                    ImGuiEx.Tooltip(Loc.T("Enable retainers"));
                    ImGui.SameLine();
                    if(ImGuiEx.ButtonCheckbox(FontAwesomeIcon.Ship, ref chr.ExcludeWorkshop, inverted: true))
                    {
                        chr.WorkshopEnabled = false;
                        chr.EnabledSubs.Clear();
                        chr.EnabledAirships.Clear();
                    }
                    ImGuiEx.Tooltip(Loc.T("Enable deployables"));
                    ImGui.SameLine();
                    ImGuiEx.ButtonCheckbox(FontAwesomeIcon.DoorOpen, ref chr.ExcludeOverlay, inverted: true);
                    ImGuiEx.Tooltip(Loc.T("Display on login overlay"));
                    ImGui.SameLine();
                    ImGuiEx.ButtonCheckbox(FontAwesomeIcon.Coins, ref chr.NoGilTrack, inverted: true);
                    ImGuiEx.Tooltip(Loc.T("Count gil on this character towards total"));
                    ImGui.SameLine();
                    if(ImGuiEx.IconButton(FontAwesomeIcon.Trash, enabled: ImGuiEx.Ctrl))
                    {
                        new TickScheduler(() => C.OfflineData.Remove(chr));
                    }
                    ImGuiEx.Tooltip(Loc.T("Hold CTRL and click to delete stored character data. It will be recreated once you relog back."));
                    ImGui.SameLine();
                    if(ImGuiEx.IconButton("\uf057", enabled: ImGuiEx.Ctrl))
                    {
                        C.Blacklist.Add((chr.CID, chr.Name));
                    }
                    ImGuiEx.Tooltip(Loc.T("Hold CTRL and click to delete stored character data and prevent it from being ever created again, effectively excluding it from being processed by AutoRetainer entirely in any ways."));

                    ImGui.PopID();
                }

                ImGui.EndTable();
            }
            DragDrop.End();
        });


        if(C.Blacklist.Count != 0)
        {
            b = b.Section(Loc.T("Excluded Characters"))
                .Widget(() =>
                {
                    for(var i = 0; i < C.Blacklist.Count; i++)
                    {
                        var d = C.Blacklist[i];
                        ImGuiEx.TextV($"{d.Name} ({d.CID:X16})");
                        ImGui.SameLine();
                        if(ImGui.Button(Loc.T("Delete") + $"##bl{i}"))
                        {
                            C.Blacklist.RemoveAt(i);
                            C.SelectedRetainers.Remove(d.CID);
                            break;
                        }
                    }
                });
        }

        b.Draw();
    }
}
