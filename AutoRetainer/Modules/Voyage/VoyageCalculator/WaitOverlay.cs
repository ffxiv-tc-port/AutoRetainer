namespace AutoRetainer.Modules.Voyage.VoyageCalculator;

internal class WaitOverlay : Window
{
    public WaitOverlay() : base("WaitOverlay", ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoCollapse, true)
    {
        IsOpen = true;
        Position = Vector2.Zero;
        RespectCloseHotkey = false;
    }

    internal volatile bool IsProcessing = false;
    internal long StartTime = 0;
    internal int Frame = 0;

    public override bool DrawConditions()
    {
        return IsProcessing;
    }

    public override void PreDraw()
    {
        // Dalamud 的 Window 基底類別在 PreDraw() 裡推每視窗不透明度(標題列右鍵選單的
        // 「不透明度」滑桿)。沒有呼叫 base 會讓那個內建功能對本視窗靜默半失效。
        base.PreDraw();
        ImGui.SetNextWindowSize(ImGuiHelpers.MainViewport.Size);
    }

    public override void Draw()
    {
        if(ImGui.GetFrameCount() - Frame > 1) StartTime = Environment.TickCount64;
        Frame = ImGui.GetFrameCount();
        CImGui.igBringWindowToDisplayFront(CImGui.igGetCurrentWindow());
        ImGui.Dummy(new(ImGuiHelpers.MainViewport.Size.X, ImGuiHelpers.MainViewport.Size.Y / 3));
        ImGuiEx.ImGuiLineCentered("Waitoverlay1", () => ImGuiEx.Text(Loc.T("Calculating optimized path. Please wait.")));
        ImGuiEx.ImGuiLineCentered("Waitoverlay2", () => ImGuiEx.Text(Loc.T("This can take several minutes.")));
        ImGuiEx.Text("");
        var span = TimeSpan.FromMilliseconds(Environment.TickCount64 - StartTime);
        ImGuiEx.ImGuiLineCentered("Waitoverlay4", () => ImGuiEx.Text($"{span.Minutes:D2}:{span.Seconds:D2}"));
        ImGuiEx.Text("");
        ImGuiEx.Text("");
        ImGuiEx.ImGuiLineCentered("Waitoverlay3", () =>
        {
            if(ImGui.Button(Loc.T("Hide this overlay")))
            {
                IsProcessing = false;
            }
        });
    }
}
