namespace AutoRetainer.UI.NeoUI.MultiModeEntries;
public class MultiModeFPSLimiter : NeoUIEntry
{
    public override string Path => Loc.T("Multi Mode/FPS Limiter");

    public override NuiBuilder Builder { get; init; } = new NuiBuilder()
        .Section(Loc.T("FPS Limiter"))
        .TextWrapped(Loc.T("FPS Limiter is only active when Multi Mode is enabled"))
        .Widget(Loc.T("Target frame rate when idling"), (x) =>
        {
            ImGui.SetNextItemWidth(100f);
            UIUtils.SliderIntFrameTimeAsFPS(x, ref C.TargetMSPTIdle, C.ExtraFPSLockRange ? 1 : 10);
        })
        .Widget(Loc.T("Target frame rate when operating"), (x) =>
        {
            ImGui.SetNextItemWidth(100f);
            UIUtils.SliderIntFrameTimeAsFPS(x, ref C.TargetMSPTRunning, C.ExtraFPSLockRange ? 1 : 20);
        })
        .Checkbox(Loc.T("Release FPS lock when game is active"), () => ref C.NoFPSLockWhenActive)
        .Checkbox(Loc.T("Allow extra low FPS limiter values"), () => ref C.ExtraFPSLockRange, Loc.T("No support is provided if you enable this and run into ANY errors in Multi Mode"))
        .Checkbox(Loc.T("Limiter active only when shutdown timer is set"), () => ref C.FpsLockOnlyShutdownTimer);
}
