namespace AutoRetainer.UI.NeoUI;
public class MiscTab : NeoUIEntry
{
    public override string Path => Loc.T("Miscellaneous");

    public override NuiBuilder Builder { get; init; } = new NuiBuilder()
        .Section(Loc.T("Statistics"))
        .Checkbox($"Record Venture Statistics", () => ref C.RecordStats)

        .Section(Loc.T("Automatic Grand Company Expert Delivery"))
        .Checkbox(Loc.T("Tray notification upon handin completion (requires NotificationMaster)"), () => ref C.GCHandinNotify)

        .Section(Loc.T("Performance"))

        .If(() => Utils.IsBusy)
        .Widget("", (x) => ImGui.BeginDisabled())
        .EndIf()

        .Checkbox($"Remove minimized FPS restrictions while plugin is operating", () => ref C.UnlockFPS)
        .Checkbox($"- Also remove general FPS restriction", () => ref C.UnlockFPSUnlimited)
        .Checkbox($"- Also pause ChillFrames plugin", () => ref C.UnlockFPSChillFrames)
        .Checkbox($"Raise FFXIV process priority while plugin is operating", () => ref C.ManipulatePriority, Loc.T("May result other programs slowdown"))

        .If(() => Utils.IsBusy)
        .Widget("", (x) => ImGui.EndDisabled())
        .EndIf();
}
