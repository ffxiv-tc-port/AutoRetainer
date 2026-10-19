using AutoRetainer.Internal;
using AutoRetainerAPI.Configuration;

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
        SearchBar = DrawSearchBar;
    }

    // 標題畫面每幀都畫：搜尋列的委派建一次；服務帳號清單與下拉名稱只在帳號組成變了才重建。只給繪製執行緒用。
    private readonly Action SearchBar;
    private int[] ServiceAccounts = [-1];
    private Dictionary<int, string> ServiceAccountNames;
    private readonly List<int> ServiceAccountScratch = [];

    // 等同 [-1, .. C.OfflineData.Select(x => x.ServiceAccount).Distinct().Order()]。
    private void RefreshServiceAccounts()
    {
        var scratch = ServiceAccountScratch;
        scratch.Clear();
        foreach(var x in C.OfflineData)
        {
            if(!scratch.Contains(x.ServiceAccount)) scratch.Add(x.ServiceAccount);
        }
        scratch.Sort();
        var same = ServiceAccounts.Length == scratch.Count + 1;
        for(var i = 0; same && i < scratch.Count; i++)
        {
            same = ServiceAccounts[i + 1] == scratch[i];
        }
        if(same) return;
        ServiceAccounts = [-1, .. scratch];
        ServiceAccountNames = null;
    }

    private void DrawSearchBar()
    {
        ImGui.SetNextItemWidth(100f);
        ImGui.InputTextWithHint("##search", Loc.T("Search..."), ref Search, 50);
        if(ServiceAccounts.Length > 2)
        {
            ImGui.SameLine();
            ImGui.SetNextItemWidth(100f);
            ServiceAccountNames ??= ServiceAccounts.ToDictionary(x => x, x => x == -1 ? Loc.T("All service accounts") : $"{Loc.T("Service account ")}{x + 1}");
            ImGuiEx.Combo("##sacc", ref Ref<int>.Get("ServAcc", -1), ServiceAccounts, names: ServiceAccountNames);
        }
    }

    public override bool DrawConditions()
    {
        return C.LoginOverlay && Utils.CanAutoLogin();
    }

    private bool IsListed(OfflineCharacterData x) => !x.Name.IsNullOrEmpty() && (!x.ExcludeOverlay || (C.LoginOverlayAllSearch && Search != ""));

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
        var drawnCount = 0;
        foreach(var x in C.OfflineData)
        {
            if(IsListed(x)) drawnCount++;
        }
        LastDrawnCharaCount = drawnCount;
        if(!C.NoCharaSearch)
        {
            // 服務帳號清單只有搜尋列用得到，關掉搜尋列時不必每幀排序、去重。
            RefreshServiceAccounts();
            // 不給 id 的多載每幀都用 StackTrace 算一個 id（整條呼叫堆疊都要配置）；這裡只有一處，給固定 id。
            ImGuiEx.LineCentered("LoginOverlay.Search", SearchBar);
        }
        ImGui.SetWindowFontScale(C.LoginOverlayScale);
        //ImGui.PushFont(Svc.PluginInterface.UiBuilder.GetGameFontHandle(new GameFontStyle(GameFontFamilyAndSize.MiedingerMid18)).ImFont);
        foreach(var x in C.OfflineData)
        {
            if(!IsListed(x)) continue;
            if(sacc > -1 && x.ServiceAccount != sacc) continue;
            if(Search != "" && !$"{x.Name}@{x.World}".Contains(Search, StringComparison.OrdinalIgnoreCase)) continue;
            var n = UIUtils.CensoredCharacter(x);
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
            var multi = MultiMode.Enabled;
            if(ImGui.Checkbox(Loc.T("Multi Mode"), ref multi))
            {
                MultiMode.SetEnabled(multi, "登入畫面上的勾選框", MultiModeChangeCause.User);
                MultiMode.OnMultiModeEnabled();
            }
        });
    }
}
