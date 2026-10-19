namespace AutoRetainer.UI.NeoUI.Experiments;

internal class NightMode : ExperimentUIEntry
{
    public override string Name => Loc.T("Night Mode");
    public override void Draw()
    {
        ImGuiEx.TextWrapped(Loc.T("Night mode:\n") +
                Loc.T("- Wait on login screen option is forcefully enabled\n") +
                Loc.T("- Built-in FPS limiter restrictions forcefully applied\n") +
                Loc.T("- While unfocused and awaiting, game is limited to 0.2 FPS\n") +
                Loc.T("- It may look like game hung up, but let it up to 5 seconds to wake up after you reactivate game window.\n") +
                Loc.T("- By default, only Deployables are enabled in Night mode\n") +
                Loc.T("- After disabling Night mode, Bailout manager will activate to relog you back to the game."));
        if(ImGui.Checkbox(Loc.T("Activate night mode"), ref C.NightMode)) MultiMode.BailoutNightMode();
        ImGui.Checkbox(Loc.T("Show Night mode checkbox"), ref C.ShowNightMode);
        ImGui.Checkbox(Loc.T("Do retainers in Night mode"), ref C.NightModeRetainers);
        ImGui.Checkbox(Loc.T("Do deployables in Night mode"), ref C.NightModeDeployables);
        ImGui.Checkbox(Loc.T("Make night mode status persistent"), ref C.NightModePersistent);
        ImGui.Checkbox(Loc.T("Make shutdown command activate night mode instead of shutting down the game"), ref C.ShutdownMakesNightMode);
    }
}
