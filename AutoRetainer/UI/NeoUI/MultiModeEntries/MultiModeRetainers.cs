namespace AutoRetainer.UI.NeoUI.MultiModeEntries;
public class MultiModeRetainers : NeoUIEntry
{
    public override string Path => Loc.T("Multi Mode/Retainers");

    public override NuiBuilder Builder { get; init; } = new NuiBuilder()
        .Section(Loc.T("Multi Mode - Retainers"))
        .Checkbox(Loc.T("Wait For Venture Completion"), () => ref C.MultiModeRetainerConfiguration.MultiWaitForAll, Loc.T("AutoRetainer will wait for all retainers to return before cycling to the next character in multi mode operation."))
        .DragInt(60f, "Advance Relog Threshold", () => ref C.MultiModeRetainerConfiguration.AdvanceTimer.ValidateRange(0, 300), 0.1f, 0, 300)
        .SliderInt(100f, "Minimum inventory slots to continue operation", () => ref C.MultiMinInventorySlots.ValidateRange(2, 9999), 2, 30)
        .Checkbox(Loc.T("Synchronise Retainers (one time)"), () => ref MultiMode.Synchronize, Loc.T("AutoRetainer will wait until all enabled retainers have completed their ventures. After that this setting will be disabled automatically and all characters will be processed."))
        .Checkbox($"Enforce Full Character Rotation", () => ref C.CharEqualize, Loc.T("Recommended for users with > 15 characters, forces multi mode to make sure ventures are processed on all characters in order before returning to the beginning of the cycle."))
        .Indent()
        .Checkbox(Loc.T("Order characters by venture completion time"), () => ref C.LongestVentureFirst, Loc.T("Characters that have completed ventures longer time ago will be checked first"))
        .Checkbox(Loc.T("Order characters by retainer level and cap"), () => ref C.CappedLevelsLast, Loc.T("Characters with retainers that can be levelled up will be done first; then, characters with retainers at max level; and then characters with retainers less than max level and level capped."))
        .Unindent();
}
