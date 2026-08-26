using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AutoRetainer.UI.NeoUI;
public sealed unsafe class UserInterface : NeoUIEntry
{
    public override string Path => Loc.T("User Interface");

    public override NuiBuilder Builder => new NuiBuilder()

        .Section(Loc.T("User Interface"))
        .Checkbox(Loc.T("Anonymise Retainers"), () => ref C.NoNames, Loc.T("Retainer names will be redacted from general UI elements. They will not be hidden in debug menus and plugin logs however. While this option is on, character and retainer numbers are not guaranteed to be equal in different sections of a plugin (for example, retainer 1 in retainers view is not guaranteed to be the same retainer as in statistics view)."))
        .Checkbox(Loc.T("Display Quick Menu in Retainer UI"), () => ref C.UIBar)
        .Checkbox(Loc.T("Display Extended Retainer Info"), () => ref C.ShowAdditionalInfo, Loc.T("Displays retainer item level/gathering/perception and the name of their current venture in the main UI."))
        .Widget(Loc.T("Do not close AutoRetainer windows on ESC key press"), (x) =>
        {
            if(ImGui.Checkbox(x, ref C.IgnoreEsc)) Utils.ResetEscIgnoreByWindows();
        })
        .Checkbox(Loc.T("Display only most significant icon in status bar"), () => ref C.StatusBarMSI)
        .SliderInt(120f, Loc.T("Status bar icon size"), () => ref C.StatusBarIconWidth, 32, 128)
        .Checkbox(Loc.T("Open AutoRetainer window on game start"), () => ref C.DisplayOnStart)
        .Checkbox(Loc.T("Skip item sell/trade confirmation while plugin is active"), () => ref C.SkipItemConfirmations)
        .Checkbox(Loc.T("Enable title screen button (requires plugin restart)"), () => ref C.UseTitleScreenButton)
        .Checkbox(Loc.T("Hide character search"), () => ref C.NoCharaSearch)
        .Checkbox(Loc.T("Don't flash background of characters that are complete"), () => ref C.NoGradient)
        .Checkbox(Loc.T("Adjust main window opacity"), () => ref C.CustomWindowBgAlpha, Loc.T("Makes the whole main AutoRetainer window see-through - the background, the tab bar, the retainer rows and the buttons all fade together, the same way Dalamud's own per-window opacity slider does. While this is off, the window looks exactly as it did before."))
        .If(() => C.CustomWindowBgAlpha)
        .SliderInt(120f, Loc.T("Main window opacity, %"), () => ref C.WindowBgAlphaPercent.ValidateRange(20, 100), 20, 100, Loc.T("100% is the normal look; the lower the value, the more the game shows through. The window's own text fades along with everything else, which is why the slider stops at 20%. Only the main window is affected - the settings window and the overlays are not."))
        .EndIf()
        .Checkbox(Loc.T("Do not warn about second game instance running from same directory"), () => ref C.No2ndInstanceNotify, "This will automatically skip AutoRetainer's loading on second instance of the game and you will have no way of loading it until you disable this option in primary instance")

        .Section(Loc.T("Character sorting in Retainer tab"))
        .Checkbox(Loc.T("Enable"), () => ref C.EnableRetainerSort)
        .TextWrapped(Loc.T(SharedText.VisualOrderOnlyNote))
        .Widget(() => UIUtils.DrawSortableEnumList("rorder", C.RetainersVisualOrders))

        .Section(Loc.T("Character sorting in Deployables tab"))
        .Checkbox(Loc.T("Enable"), () => ref C.EnableDeployablesSort)
        .TextWrapped(Loc.T(SharedText.VisualOrderOnlyNote))
        .Widget(() => UIUtils.DrawSortableEnumList("dorder", C.DeployablesVisualOrders));



}