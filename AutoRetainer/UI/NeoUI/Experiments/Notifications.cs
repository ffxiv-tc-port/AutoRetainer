namespace AutoRetainer.UI.NeoUI.Experiments;
public class Notifications : ExperimentUIEntry
{
    public override string Name => Loc.T("Notifications");

    public override void Draw()
    {
        ImGui.Checkbox(Loc.T("Display overlay notification if one of retainers has completed a venture"), ref C.NotifyEnableOverlay);
        ImGui.Checkbox(Loc.T("Do not display overlay in duty or combat"), ref C.NotifyCombatDutyNoDisplay);
        ImGui.Checkbox(Loc.T("Include other characters"), ref C.NotifyIncludeAllChara);
        ImGui.Checkbox(Loc.T("Ignore other characters that have not been enabled in MultiMode"), ref C.NotifyIgnoreNoMultiMode);
        ImGui.Checkbox(Loc.T("Display notification in game chat"), ref C.NotifyDisplayInChatX);
        ImGuiEx.Text(Loc.T("If game is inactive: (requires NotificationMaster to be installed and enabled)"));
        ImGui.Checkbox(Loc.T("Send desktop notification on retainers available"), ref C.NotifyDeskopToast);
        ImGui.Checkbox(Loc.T("Flash taskbar"), ref C.NotifyFlashTaskbar);
        ImGui.Checkbox(Loc.T("Do not notify if AutoRetainer is enabled or MultiMode is running"), ref C.NotifyNoToastWhenRunning);
    }
}
