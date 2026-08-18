using AutoRetainerAPI.Configuration;
using Dalamud.Interface.Components;
using PunishLib.ImGuiMethods;

namespace AutoRetainer.UI.MainWindow.MultiModeTab;
public class CharaConfig
{
    public static void Draw(OfflineCharacterData data, bool isRetainer)
    {
        ImGui.PushID(data.CID.ToString());
        SharedUI.DrawMultiModeHeader(data);
        var b = new NuiBuilder()

        .Section(Loc.T("General Character Specific Settings"))
        .Widget(() =>
        {
            SharedUI.DrawServiceAccSelector(data);
            SharedUI.DrawPreferredCharacterUI(data);
        });
        if(isRetainer)
        {
            b = b.Section(Loc.T("Retainers")).Widget(() =>
            {
                ImGuiEx.Text(Loc.T("Automatic Grand Company Expert Delivery:"));
                if(!AutoGCHandin.Operation)
                {
                    ImGuiEx.SetNextItemWidthScaled(200f);
                    ImGuiEx.EnumCombo("##gcHandin", ref data.GCDeliveryType, Loc.EnumNames<GCDeliveryType>());
                }
                else
                {
                    ImGuiEx.Text(Loc.T("Can't change this now"));
                }
            });
        }
        else
        {
            b = b.Section(Loc.T("Deployables")).Widget(() =>
            {
                ImGui.Checkbox(Loc.T("Wait For Voyage Completion"), ref data.MultiWaitForAllDeployables);
                ImGuiComponents.HelpMarker("""This setting works like the global option but applies to individual characters. When enabled, AutoRetainer will wait for all deployables to return before logging into the character. If you're already logged in for another reason, it will still resend completed submarines—unless the global setting "Wait even when already logged in" is also turned on.""");
            });
        }
        b = b.Section(Loc.T("Teleport overrides"), data.GetAreTeleportSettingsOverriden() ? ImGui.GetStyle().Colors[(int)ImGuiCol.FrameBg] with { X = 1f } : null, true)
        .Widget(() =>
        {
            ImGuiEx.Text(Loc.T("You can override teleport settings per character."));
            bool? demo = null;
            ImGuiEx.Checkbox(Loc.T("Options marked with this marker will use values from global configuration"), ref demo);
            ImGuiEx.Checkbox(Loc.T("Enabled"), ref data.TeleportOptionsOverride.Enabled);
            ImGui.Indent();
            ImGuiEx.Checkbox(Loc.T("Teleport for retainers..."), ref data.TeleportOptionsOverride.Retainers);
            ImGui.Indent();
            ImGuiEx.Checkbox(Loc.T("...to private house"), ref data.TeleportOptionsOverride.RetainersPrivate);
            ImGuiEx.Checkbox(Loc.T("...to free company house"), ref data.TeleportOptionsOverride.RetainersFC);
            ImGuiEx.Checkbox(Loc.T("...to apartment"), ref data.TeleportOptionsOverride.RetainersApartment);
            ImGui.Text(Loc.T("If all above are disabled or fail, will be teleported to inn."));
            ImGui.Unindent();
            ImGuiEx.Checkbox(Loc.T("Teleport to free company house for deployables"), ref data.TeleportOptionsOverride.Deployables);
            ImGui.Unindent();
            ImGuiGroup.EndGroupBox();
        }).Draw();
        SharedUI.DrawExcludeReset(data);
        ImGui.PopID();
    }
}
