namespace AutoRetainer.UI.NeoUI;
public class LoginOverlay : NeoUIEntry
{
    public override string Path => Loc.T("Login Overlay");

    public override NuiBuilder Builder { get; init; } = new NuiBuilder()
            .Section(Loc.T("Login Overlay"))
            .Checkbox(Loc.T("Display Login Overlay"), () => ref C.LoginOverlay)
            .Widget(Loc.T("Login overlay scale multiplier"), (x) =>
            {
                ImGuiEx.SetNextItemWidthScaled(150f);
                if(ImGuiEx.SliderFloat(x, ref C.LoginOverlayScale.ValidateRange(0.1f, 5f), 0.2f, 2f)) P.LoginOverlay.bWidth = 0;
            })
            .Widget(Loc.T("Login overlay button padding"), (x) =>
            {
                ImGuiEx.SetNextItemWidthScaled(150f);
                if(ImGuiEx.SliderFloat(x, ref C.LoginOverlayBPadding.ValidateRange(0.5f, 5f), 1f, 1.5f)) P.LoginOverlay.bWidth = 0;
            })
        .Checkbox(Loc.T("Display hidden characters when searching"), () => ref C.LoginOverlayAllSearch);
}
