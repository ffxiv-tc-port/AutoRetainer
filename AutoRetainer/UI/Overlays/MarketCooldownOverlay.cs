namespace AutoRetainer.UI.Overlays;

internal class MarketCooldownOverlay : Window
{
    public long UnlockAt = 0;

    public MarketCooldownOverlay() : base("AutoRetainer MarketCooldownOverlay", ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.AlwaysAutoResize)
    {
        P.WindowSystem.AddWindow(this);
        IsOpen = true;
        RespectCloseHotkey = false;
    }

    public override void PreDraw()
    {
        // Dalamud 的 Window 基底類別在 PreDraw() 裡推每視窗不透明度(標題列右鍵選單的
        // 「不透明度」滑桿)。沒有呼叫 base 會讓那個內建功能對本視窗靜默半失效。
        base.PreDraw();
        SizeConstraints = new()
        {
            MinimumSize = new(ImGuiHelpers.MainViewport.Size.X, 0),
            MaximumSize = new(0, float.MaxValue)
        };
    }

    public override void Draw()
    {
        CImGui.igBringWindowToDisplayBack(CImGui.igGetCurrentWindow());
        var percent = 1f - (float)(UnlockAt - Environment.TickCount64) / 2000f;
        ImGui.PushStyleColor(ImGuiCol.PlotHistogram, EColor.Green);
        ImGui.ProgressBar(percent, new(ImGui.GetContentRegionAvail().X, 20), $"");
        ImGui.PopStyleColor();
        Position = new(0, 0);
    }

    public override bool DrawConditions()
    {
        return Environment.TickCount64 < UnlockAt;
    }
}
