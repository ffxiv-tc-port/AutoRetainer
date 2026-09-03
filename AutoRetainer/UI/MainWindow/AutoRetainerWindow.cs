using AutoRetainer.Modules.Voyage;
using AutoRetainer.UI.MainWindow.MultiModeTab;
using AutoRetainerAPI;
using AutoRetainerAPI.Configuration;
using Dalamud.Interface.Components;
using ECommons.Configuration;
using ECommons.Funding;
using NightmareUI;

namespace AutoRetainer.UI.MainWindow;

internal unsafe class AutoRetainerWindow : Window
{
    private TitleBarButton LockButton;

    public AutoRetainerWindow() : base($"")
    {
        LockButton = new()
        {
            Click = OnLockButtonClick,
            Icon = C.PinWindow ? FontAwesomeIcon.Lock : FontAwesomeIcon.LockOpen,
            IconOffset = new(3, 2),
            ShowTooltip = () => ImGui.SetTooltip(Loc.T("Lock window position and size")),
        };
        SizeConstraints = new()
        {
            MinimumSize = new(250, 100),
            MaximumSize = new(9999, 9999)
        };
        P.WindowSystem.AddWindow(this);
        AllowPinning = false;
        TitleBarButtons.Add(new()
        {
            Click = (m) => { if(m == ImGuiMouseButton.Left) S.NeoWindow.IsOpen = true; },
            Icon = FontAwesomeIcon.Cog,
            IconOffset = new(2, 2),
            ShowTooltip = () => ImGui.SetTooltip(Loc.T("Open settings window")),
        });
        TitleBarButtons.Add(LockButton);
    }

    private Action<string> SomeAction;

    /// <summary>True while PreDraw has pushed <see cref="ImGuiStyleVar.Alpha"/> and PostDraw still owes a pop.
    /// Mirrors how Dalamud's own <c>Window</c> base class tracks its per-window opacity push.</summary>
    private bool PushedWindowAlpha = false;

    private void OnLockButtonClick(ImGuiMouseButton m)
    {
        SomeAction += (s) => { };
        SomeAction -= (s) => { };
        if(m == ImGuiMouseButton.Left)
        {
            C.PinWindow = !C.PinWindow;
            LockButton.Icon = C.PinWindow ? FontAwesomeIcon.Lock : FontAwesomeIcon.LockOpen;
        }
    }

    public override void PreDraw()
    {
        var prefix = SchedulerMain.PluginEnabled ? $" [{SchedulerMain.Reason}]" : "";
        var tokenRem = TimeSpan.FromMilliseconds(Utils.GetRemainingSessionMiliSeconds());
        WindowName = $"{P.Name} {P.GetType().Assembly.GetName().Version}{prefix} | {FormatToken(tokenRem)}###AutoRetainer";
        if(C.PinWindow)
        {
            ImGuiHelpers.SetNextWindowPosRelativeMainViewport(C.WindowPos);
            ImGui.SetNextWindowSize(C.WindowSize);
        }
        // Dalamud 的 Window 基底類別在 PreDraw() 裡推自己的每視窗不透明度(標題列右鍵選單那個
        // 滑桿)。這個 override 原本沒有呼叫 base,等於把那個功能對本視窗靜默關掉了一半
        // (ApplyConditionals 讀得到 internalAlpha 所以背景會變,但內容不會)。base.PostDraw()
        // 會依 base 自己的旗標決定要不要 pop,所以補呼叫 base 兩邊仍然成對。
        base.PreDraw();

        // 主視窗整體不透明度。推的是 ImGuiStyleVar.Alpha 而不是 Window.BgAlpha:
        // BgAlpha 走的是 SetNextWindowBgAlpha(),只換掉 WindowBg 那一格顏色的 alpha,
        // 僱員列、分頁列、按鈕這些自帶底色的元件完全不受影響 —— 使用者實機回報「其他
        // 選項沒有跟著變透明」就是這個原因。Alpha 是 ImGui 的全域乘數,GetColorU32()
        // 會把它乘進每一個取出的顏色,所以標題列、視窗背景、列底色、框線與文字會一起
        // 淡掉,與 Dalamud 標題列右鍵選單的「不透明度」滑桿是同一個機制。
        // 文字跟著淡是這個機制的本質,不是 bug;下限 20% 就是用來保底可讀性的。
        //
        // 🔴 push 與 pop 必須成對,否則整個 ImGui 樣式堆疊會壞掉。實際讀過
        // Dalamud/Interface/Windowing/Window.cs 的 DrawInternal 確認過:
        //   * PreDraw() 只在 !hasError 時呼叫,同時把區域變數 isErrorStylePushed 留在 false;
        //   * PostDraw() 在收尾處以 else(!isErrorStylePushed)呼叫 —— 判斷的是那個**區域變數**,
        //     不是重新讀 this.hasError,所以 Draw() 途中擲例外把 hasError 翻成 true 也不影響;
        //   * Draw() 的例外被 try/catch 攔住,兩者之間整段沒有任何 return;
        //   * 兩個提早 return(視窗未開啟、DrawConditions() 為 false)都發生在 PreDraw() **之前**;
        //   * 視窗收合時 ImGui.Begin() 回 false,但程式碼照樣往下走到 ImGui.End() 與 PostDraw()。
        // ⇒ PreDraw/PostDraw 在所有路徑成對,這也正是 Dalamud 自己推 internalAlpha 的位置。
        if(C.CustomWindowBgAlpha)
        {
            // 乘上現值而不是直接指定,才能疊在 base.PreDraw() 推的值與外層樣式調整之上。
            ImGui.PushStyleVar(ImGuiStyleVar.Alpha, ImGui.GetStyle().Alpha * (Math.Clamp(C.WindowBgAlphaPercent, 20, 100) / 100f));
            PushedWindowAlpha = true;
        }
    }

    public override void PostDraw()
    {
        // 後進先出:先 pop 掉本類別在 PreDraw 推的那一個,再讓 base 處理它自己那一個。
        if(PushedWindowAlpha)
        {
            ImGui.PopStyleVar();
            PushedWindowAlpha = false;
        }
        base.PostDraw();
    }

    private string FormatToken(TimeSpan time)
    {
        if(time.TotalMilliseconds > 0)
        {
            if(time.Days > 0)
            {
                return $"連線過期於 {time.Days} 天" + (time.Hours > 0 ? $" {time.Hours} 小時" : "") + "後";
            }
            else
            {
                if(time.Hours > 0)
                {
                    return $"連線過期於 {time.Hours} 小時後";
                }
                else
                {
                    return $"連線過期於不到一小時後";
                }
            }
        }
        else
        {
            return "連線已過期";
        }
    }
    public override void Draw()
    {
        //ImGuiEx.Text(GradientColor.Get(EColor.RedBright, EColor.YellowBright), "This version MUST NOT BE RUNNING UNATTENDED.");
        if(!C.AcceptedDisclamer)
        {
            new NuiBuilder()
                .Section(Loc.T("Disclaimer"))
                .TextWrapped(ImGuiColors.DalamudYellow, Loc.T("Please note that using AutoRetainer for RMT purposes is strictly prohibited. "))
                .TextWrapped(ImGuiColors.DalamudRed, Loc.T("To avoid unnecessary consequences please follow these rules when using AutoRetainer:"))
                .TextWrapped(Loc.T("1. Do not acknowledge your use of AutoRetainer in game chat;"))
                .TextWrapped(Loc.T("2. Do not leave AutoRetainer unattended for prolonged period of time;"))
                .TextWrapped(Loc.T("3. Ensure that your total playing+AutoRetainer usage time does not exceeds 16 hours per day; ensure that periods of inactivity exist between your retainer/submersible checking routine;"))
                .TextWrapped(Loc.T("4. Never reply back to players that are trying to do so-called bot checks by trading or chatting with you; instead, blacklist these players immediately;"))
                .TextWrapped(Loc.T("5. If questioned by game master, always claim that you are doing all actions manually and never admit plugin usage."))
                .TextWrapped(Loc.T("Failure to follow these guidelines may put your account at risk."))
                .TextWrapped(GradientColor.Get(ImGuiColors.DalamudYellow, ImGuiColors.DalamudRed), Loc.T("You may not use AutoRetainer for Real Money Trading or other commercial purposes. No support will be provided if you are using it for these purposes."))
                .Widget(() =>
                {
                    if(ImGuiEx.IconButtonWithText(FontAwesomeIcon.Check, Loc.T("Accept and continue")))
                    {
                        C.AcceptedDisclamer = true;
                        EzConfig.Save();
                    }
                })
                .Draw();
            return;
        }
        var e = SchedulerMain.PluginEnabledInternal;

        // 多角模式執行中這些控制項原本是灰的、要按住 CTRL 才點得動。使用者裁定改成永遠可介入:
        // 「整理包包時沒注意到被鎖住，一直重複點」。介入之後排程器不會卡住的理由見
        // SchedulerMain.SetEnabledByUser 的註解(關鍵是不要把 Reason 從 MultiMode 覆蓋掉)。
        if(ImGui.Checkbox(Loc.T($"Enable {P.Name}"), ref e))
        {
            P.WasEnabled = false;
            SchedulerMain.SetEnabledByUser(e, PluginEnableReason.Auto);
        }
        if(C.ShowDeployables && (VoyageUtils.Workshops.Contains(Svc.ClientState.TerritoryType) || VoyageScheduler.Enabled))
        {
            ImGui.SameLine();
            ImGui.Checkbox(Loc.T("Deployables"), ref VoyageScheduler.Enabled);
        }
        if(MultiMode.Active)
        {
            ImGuiComponents.HelpMarker(Loc.T(SharedText.MultiModeOverridesThisOption));
        }

        if(P.WasEnabled)
        {
            ImGui.SameLine();
            ImGuiEx.Text(GradientColor.Get(ImGuiColors.DalamudGrey, ImGuiColors.DalamudGrey3, 500), Loc.T("Paused"));
        }

        ImGui.SameLine();
        if(ImGui.Checkbox(Loc.T("Multi"), ref MultiMode.Enabled))
        {
            MultiMode.OnMultiModeEnabled();
        }
        if(C.ShowNightMode)
        {
            ImGui.SameLine();
            if(ImGui.Checkbox(Loc.T("Night"), ref C.NightMode))
            {
                MultiMode.BailoutNightMode();
            }
        }
        if(C.DisplayMMType)
        {
            ImGui.SameLine();
            ImGuiEx.SetNextItemWidthScaled(100f);
            ImGuiEx.EnumCombo("##mode", ref C.MultiModeType, Loc.EnumNames<MultiModeType>());
        }
        if(C.CharEqualize && MultiMode.Enabled)
        {
            ImGui.SameLine();
            if(ImGui.Button(Loc.T("Reset counters")))
            {
                MultiMode.CharaCnt.Clear();
            }
        }

        Svc.PluginInterface.GetIpcProvider<object>(ApiConsts.OnMainControlsDraw).SendMessage();

        if(IPC.Suppressed)
        {
            ImGuiEx.Text(ImGuiColors.DalamudRed, Loc.T("Plugin operation is suppressed by other plugin."));
            // 具名租約（AutoRetainer.AcquireSuppressionFor）：「是誰壓著」要在列上看得見，
            // tooltip 只補「還剩幾秒到期」。舊的無主布林沒有名字，所以清單可能是空的 —— 那時候不畫這一段。
            var suppressionOwners = SuppressionLeases.Snapshot();
            if(suppressionOwners.Count > 0)
            {
                ImGui.SameLine();
                ImGuiEx.Text(ImGuiColors.DalamudRed, $"[{string.Join(", ", suppressionOwners.Select(x => x.Owner))}]");
                if(ImGui.IsItemHovered())
                {
                    ImGuiEx.Tooltip(string.Join(Environment.NewLine,
                        suppressionOwners.Select(x => $"{x.Owner}: {Math.Max(0, x.RemainingMs) / 1000}s")));
                }
            }
            ImGui.SameLine();
            if(ImGui.SmallButton(Loc.T("Cancel")))
            {
                // 使用者的逃生口：舊的無主布林與所有具名租約一起清掉，
                // 否則按了「取消」卻還被別人的租約壓著＝按鈕看起來壞了。
                IPC.Suppressed = false;
                SuppressionLeases.ReleaseAll("使用者在主視窗按下取消");
            }
        }

        if(P.TaskManager.IsBusy)
        {
            ImGui.SameLine();
            if(ImGui.Button(string.Format(Loc.T("Abort {0} tasks"), P.TaskManager.NumQueuedTasks)))
            {
                P.TaskManager.Abort();
            }
            if(ImGui.IsItemHovered())
            {
                var lines = new List<string>();
                if(P.TaskManager.CurrentTask != null) lines.Add(P.TaskManager.CurrentTask.Name);
                lines.AddRange(P.TaskManager.Tasks.Select(x => x.Name));
                ImGuiEx.Tooltip(string.Join("\n", lines));
            }
        }

        ImGuiEx.EzTabBar("tabbar",
                        (Loc.T("Retainers"), MultiModeUI.Draw, null, true),
                        (Loc.T("Deployables"), WorkshopUI.Draw, null, true),
                        (Loc.T("Troubleshooting"), TroubleshootingUI.Draw, null, true),
                        (Loc.T("Statistics"), DrawStats, null, true)
                        );
        if(!C.PinWindow)
        {
            C.WindowPos = ImGui.GetWindowPos();
            C.WindowSize = ImGui.GetWindowSize();
        }
    }

    private void DrawStats()
    {
        NuiTools.ButtonTabs([[C.RecordStats ? new(Loc.T("Ventures"), S.VentureStats.DrawVentures) : null, new(Loc.T("Gil"), S.GilDisplay.Draw), new(Loc.T("FC Data"), S.FCData.Draw)]]);
    }

    public override void OnClose()
    {
        EzConfig.Save();
        S.VentureStats.Data.Clear();
        MultiModeUI.JustRelogged = false;
    }

    public override void OnOpen()
    {
        MultiModeUI.JustRelogged = true;
    }
}
