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
        ImGui.Separator();
        ImGui.Checkbox(Loc.T("Ask Tataru to remind you when deployables return or the expert delivery loop finishes (requires TataruPraise)"), ref C.TataruPraiseOnCompletion);
        ImGuiEx.HelpMarker(Loc.T("Fires the moment the return time AutoRetainer has on record passes - not when you actually go and collect. Covers every enabled submarine, airship and retainer venture across all of your characters, not just the one you are logged in on. Submarines and airships use the 「潛艇」 praise category, retainer ventures use 「僱員」. The expert delivery loop announces once more when a whole run finishes successfully, using the 「稀有品」 category - on the last character of a multi-character run, and never when the loop stops early or is stopped by hand. Timers that had already expired are silently marked as announced when the plugin loads, so logging in never sets off a burst. Does nothing at all if TataruPraise is not installed - no error, no message."));
    }
}
