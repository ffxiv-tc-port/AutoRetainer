namespace AutoRetainer.UI.NeoUI;
public class MainSettings : NeoUIEntry
{
    public override string Path => Loc.T("General");

    public override NuiBuilder Builder { get; init; } = new NuiBuilder()
        .Section(Loc.T("Delays"))
        .Widget(100f, Loc.T("Time Desynchronization Compensation"), (x) => ImGuiEx.SliderInt(x, ref C.UnsyncCompensation.ValidateRange(-60, 0), -10, 0), Loc.T("Additional amount of seconds that will be subtracted from venture ending time to help mitigate possible issues of time desynchronization between the game and your PC."))
        .Widget(100f, Loc.T("Additional Interaction Delay, frames"), (x) => ImGuiEx.SliderInt(x, ref C.ExtraFrameDelay.ValidateRange(-10, 100), 0, 50), Loc.T("The lower this value is the faster plugin will use actions. When dealing with low FPS or high latency you may want to increase this value. If you want the plugin to operate faster you may decrease it."))
        .Widget(Loc.T("Extra Logging"), (x) => ImGui.Checkbox(x, ref C.ExtraDebug), Loc.T("This option enables excessive logging for debugging purposes. It will spam your log and cause performance issues while enabled. This option will disable itself upon plugin reload or game restart."))

            .Section(Loc.T("Operation"))
        .Widget(Loc.T("Assign + Reassign"), (x) =>
        {
            if(ImGui.RadioButton(x, C.EnableAssigningQuickExploration && !C._dontReassign))
            {
                C.EnableAssigningQuickExploration = true;
                C.DontReassign = false;
            }
        }, Loc.T("Automatically assigns enabled retainers to a Quick Venture if they have none already in progress and reassigns current venture."))
        .Widget(Loc.T("Collect"), (x) =>
        {
            if(ImGui.RadioButton(x, !C.EnableAssigningQuickExploration && C._dontReassign))
            {
                C.EnableAssigningQuickExploration = false;
                C.DontReassign = true;
            }
        }, Loc.T("Only collect venture rewards from the retainer, and will not reassign them.\nHold CTRL when interacting with the Summoning Bell to apply this mode temporarily."))
        .Widget(Loc.T("Reassign"), (x) =>
        {
            if(ImGui.RadioButton(Loc.T("Reassign"), !C.EnableAssigningQuickExploration && !C._dontReassign))
            {
                C.EnableAssigningQuickExploration = false;
                C.DontReassign = false;
            }
        }, Loc.T("Only reassign ventures that retainers are undertaking."))
        .Widget(Loc.T("RetainerSense"), (x) => ImGui.Checkbox(x, ref C.RetainerSense), Loc.T("AutoRetainer will automatically enable itself when the player is within interaction range of a Summoning Bell. You must remain stationary or the activation will be cancelled."))
        .Widget(200f, Loc.T("Activation Time"), (x) => ImGuiEx.SliderIntAsFloat(x, ref C.RetainerSenseThreshold, 1000, 100000));


}
