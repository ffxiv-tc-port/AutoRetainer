using AutoRetainerAPI.Configuration;
using System.Collections.Frozen;

namespace AutoRetainer.UI.NeoUI.MultiModeEntries;
public class MultiModeContingency : NeoUIEntry
{
    private static readonly FrozenDictionary<WorkshopFailAction, string> WorkshopFailActionNames = new Dictionary<WorkshopFailAction, string>()
    {
        [WorkshopFailAction.StopPlugin] = "Halt all plugin operation",
        [WorkshopFailAction.ExcludeVessel] = "Exclude deployable from operation",
        [WorkshopFailAction.ExcludeChar] = "Exclude captain from multi mode rotation",
    }.ToFrozenDictionary();

    public override string Path => Loc.T("Multi Mode/Contingency");

    public override NuiBuilder Builder { get; init; } = new NuiBuilder()
        .Section(Loc.T("Contingency"))
        .TextWrapped(Loc.T("Here you can apply various fallback actions to perform in the case of some common failure states or potential operation errors."))
        .EnumComboFullWidth(null, Loc.T("Ceruleum Tanks Expended"), () => ref C.FailureNoFuel, (x) => x != WorkshopFailAction.ExcludeVessel, WorkshopFailActionNames, Loc.T("Applies selected fallback action in the case of insufficient Ceruleum Tanks to deploy vessel on a new voyage."))
        .EnumComboFullWidth(null, Loc.T("Unable to Repair Deployable"), () => ref C.FailureNoRepair, null, WorkshopFailActionNames, Loc.T("Applies selected fallback action in the case of insufficient Magitek Repair Materials to repair a vessel."))
        .EnumComboFullWidth(null, Loc.T("Inventory at Capacity"), () => ref C.FailureNoInventory, (x) => x != WorkshopFailAction.ExcludeVessel, WorkshopFailActionNames, Loc.T("Applies selected fallback action in the case of the captain's inventory having insufficient space to receive voyage rewards."))
        // FailureGeneric 沒有讀取端；欄位與使用者選的值刻意保留，接上讀取端會改變現行行為。
        .NoEffectEnumComboFullWidth(Loc.T("Critical Operation Failure"), () => ref C.FailureGeneric, (x) => x != WorkshopFailAction.ExcludeVessel, WorkshopFailActionNames,
            Loc.T("This option currently has no effect: when an unclassified error occurs, the action selected here is not applied (choosing to halt does not halt anything) and AutoRetainer carries on as usual. Too many interaction errors within one minute always exclude the current character (or turn Multi Mode off), regardless of this option."))
        .Widget(Loc.T("Jailed by the GM"), (x) =>
        {
            ImGui.BeginDisabled();
            ImGuiEx.SetNextItemFullWidth();
            if(ImGui.BeginCombo("##jailsel", Loc.T("Terminate the game"))) { ImGui.EndCombo(); }
            ImGui.EndDisabled();
        }, Loc.T("Applies selected fallback action in the case if you got jailed by the GM while plugin is running. Good luck!"));
}
