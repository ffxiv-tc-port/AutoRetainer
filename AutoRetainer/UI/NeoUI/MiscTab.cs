namespace AutoRetainer.UI.NeoUI;
public class MiscTab : NeoUIEntry
{
    public override string Path => Loc.T("Miscellaneous");

    public override NuiBuilder Builder { get; init; } = new NuiBuilder()
        .Section(Loc.T("Statistics"))
        .Checkbox(Loc.T("Record Venture Statistics"), () => ref C.RecordStats)

        .Section(Loc.T("Automatic Grand Company Expert Delivery"))
        .Checkbox(Loc.T("Tray notification upon handin completion (requires NotificationMaster)"), () => ref C.GCHandinNotify)
        .SliderInt(150f, Loc.T("Refresh resend delay, ms"), () => ref C.GCHandinRefreshRetryMs.ValidateRange(50, 1000), 50, 500, Loc.T("After handing an item in, AutoRetainer asks the Grand Company agent to rebuild the list immediately instead of waiting for the game to do it. If the list has not changed after this delay, it sends the request again (at most 3 times per item). Lowering it retries sooner; raising it sends fewer redundant requests. This is not a fixed wait - the flow continues the moment the list actually changes."))
        .SliderInt(150f, Loc.T("List refresh timeout, ms"), () => ref C.GCHandinListTimeoutMs.ValidateRange(300, 5000), 300, 3000, Loc.T("How long to keep waiting for the item list to change before giving up on the active refresh and falling back to waiting for the game to rebuild the list by itself. The fallback path always works, so this is only a fuse.\n\nDo not set this too low: when it fires, AutoRetainer rescans a list that may still be stale, which can pick the item that was just handed in and abort the whole run with \"item was not found in inventory\"."))

        .Section(Loc.T("Performance"))

        .If(() => Utils.IsBusy)
        .Widget("", (x) => ImGui.BeginDisabled())
        .EndIf()

        .Checkbox(Loc.T("Remove minimized FPS restrictions while plugin is operating"), () => ref C.UnlockFPS)
        .Checkbox(Loc.T("- Also remove general FPS restriction"), () => ref C.UnlockFPSUnlimited)
        .Checkbox(Loc.T("- Also pause ChillFrames plugin"), () => ref C.UnlockFPSChillFrames)
        .Checkbox(Loc.T("Raise FFXIV process priority while plugin is operating"), () => ref C.ManipulatePriority, Loc.T("May result other programs slowdown"))

        .If(() => Utils.IsBusy)
        .Widget("", (x) => ImGui.EndDisabled())
        .EndIf();
}
