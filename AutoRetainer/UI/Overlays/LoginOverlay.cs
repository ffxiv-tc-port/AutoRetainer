using AutoRetainer.Internal;

namespace AutoRetainer.UI.Overlays;

internal unsafe class LoginOverlay : Window
{
    internal float bWidth = 0f;
    private string Search = "";
    internal long LastDrawTick = 0;
    internal int LastDrawnCharaCount = 0;

    public LoginOverlay() : base("AutoRetainer login overlay", ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoFocusOnAppearing, true)
    {
        P.WindowSystem.AddWindow(this);
        RespectCloseHotkey = false;
        IsOpen = true;
    }

    public override bool DrawConditions()
    {
        return C.LoginOverlay && Utils.CanAutoLogin();
    }

    // ImGui persists this window's last position by name across sessions. If the window was
    // last closed/moved while off-screen (e.g. after a resolution or monitor layout change),
    // that stale position is restored forever and the overlay silently renders outside the
    // visible viewport. Reset to a safe default whenever it has no overlap with the viewport.
    private void EnsureOnScreen()
    {
        var pos = ImGui.GetWindowPos();
        var size = ImGui.GetWindowSize();
        var viewport = ImGui.GetMainViewport();
        var vpMin = viewport.Pos;
        var vpMax = viewport.Pos + viewport.Size;
        var noOverlap = pos.X + size.X < vpMin.X || pos.Y + size.Y < vpMin.Y || pos.X > vpMax.X || pos.Y > vpMax.Y;
        if(noOverlap)
        {
            ImGui.SetWindowPos(viewport.Pos + new Vector2(100, 100));
        }
    }

    public override void Draw()
    {
        LastDrawTick = Environment.TickCount64;
        EnsureOnScreen();
        var num = 1;
        ref var sacc = ref Ref<int>.Get("ServAcc", -1);
        int[] userServiceAccounts = [-1, .. C.OfflineData.Select(x => x.ServiceAccount).Distinct().Order()];
        LastDrawnCharaCount = C.OfflineData.Count(x => !x.Name.IsNullOrEmpty() && (!x.ExcludeOverlay || (C.LoginOverlayAllSearch && Search != "")));
        if(!C.NoCharaSearch)
        {
            ImGuiEx.LineCentered(() =>
            {
                ImGui.SetNextItemWidth(100f);
                ImGui.InputTextWithHint("##search", Loc.T("Search..."), ref Search, 50);
                if(userServiceAccounts.Count() > 2)
                {
                    ImGui.SameLine();
                    ImGui.SetNextItemWidth(100f);
                    ImGuiEx.Combo("##sacc", ref Ref<int>.Get("ServAcc", -1), userServiceAccounts, names: userServiceAccounts.ToDictionary(x => x, x => x == -1 ? Loc.T("All service accounts") : $"{Loc.T("Service account ")}{x + 1}"));
                }
            });
        }
        ImGui.SetWindowFontScale(C.LoginOverlayScale);
        //ImGui.PushFont(Svc.PluginInterface.UiBuilder.GetGameFontHandle(new GameFontStyle(GameFontFamilyAndSize.MiedingerMid18)).ImFont);
        foreach(var x in C.OfflineData.Where(x => !x.Name.IsNullOrEmpty() && (!x.ExcludeOverlay || (C.LoginOverlayAllSearch && Search != ""))))
        {
            if(sacc > -1 && x.ServiceAccount != sacc) continue;
            if(Search != "" && !$"{x.Name}@{x.World}".Contains(Search, StringComparison.OrdinalIgnoreCase)) continue;
            var n = Censor.Character(x.Name, x.World);
            var dim = ImGuiHelpers.GetButtonSize(n) * C.LoginOverlayScale;
            if(dim.X > bWidth)
            {
                bWidth = dim.X;
            }
            if(ImGui.Button(n, new(bWidth * C.LoginOverlayBPadding, dim.Y * C.LoginOverlayBPadding)))
            {
                MultiMode.Relog(x, out _, RelogReason.Overlay);
                //AutoLogin.Instance.Login(x.CurrentWorld, x.Name, ExcelWorldHelper.GetWorldByName(x.World).RowId, x.ServiceAccount);
            }
        }
        //ImGui.PopFont();
        ImGuiEx.LineCentered(Loc.T("LoginCenter"), delegate
        {
            if(ImGui.Checkbox(Loc.T("Multi Mode"), ref MultiMode.Enabled))
            {
                MultiMode.OnMultiModeEnabled();
            }
        });
    }
}
