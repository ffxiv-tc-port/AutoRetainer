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
            ImGuiComponents.HelpMarker(Loc.T("MultiMode also controls this option. You can always change it by hand and it takes effect immediately, but while MultiMode is running it will switch this back on by itself when it moves on to the next retainer or character - untick \"Multi\" as well if you want it to stay off."));
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
            ImGui.SameLine();
            if(ImGui.SmallButton(Loc.T("Cancel")))
            {
                IPC.Suppressed = false;
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
