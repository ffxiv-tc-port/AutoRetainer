using AutoRetainer.Internal.InventoryManagement;
using AutoRetainer.Scheduler.Tasks;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace AutoRetainer.UI.Overlays;

/// <summary>
/// Small floating button shown above a retainer's own item-storage window (as opposed to
/// <see cref="RetainerListOverlay"/>, which is shown above the retainer LIST and whose buttons
/// loop over every retainer). One click here only ever touches the single retainer currently open.
/// </summary>
internal unsafe class RetainerInventoryOverlay : Window
{
    private float height;
    private AtkUnitBase* trackedAddon;

    public RetainerInventoryOverlay() : base("AutoRetainer retainer inventory overlay", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoFocusOnAppearing, true)
    {
        P.WindowSystem.AddWindow(this);
        RespectCloseHotkey = false;
        IsOpen = true;
    }

    public override bool DrawConditions()
    {
        if(!C.UIBar) return false;
        foreach(var name in InventorySpaceManager.Addons)
        {
            if(TryGetAddonByName<AtkUnitBase>(name, out var addon) && IsAddonReady(addon))
            {
                trackedAddon = addon;
                Position = new(addon->X, addon->Y - height);
                return true;
            }
        }
        return false;
    }

    public override void Draw()
    {
        if(!P.TaskManager.IsBusy)
        {
            if(ImGuiEx.IconButtonWithText(FontAwesomeIcon.Download, Loc.T("Retrieve All")))
            {
                TaskRetrieveAllFromRetainer.Enqueue();
            }
            ImGuiEx.Tooltip(Loc.T("Retrieve every item from this retainer until your inventory is nearly full."));
        }
        else
        {
            ImGuiEx.Text(Loc.T("Busy..."));
        }
        height = ImGui.GetWindowSize().Y;
    }
}
