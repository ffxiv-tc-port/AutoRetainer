namespace AutoRetainer.UI.NeoUI.InventoryManagementEntries.InventoryCleanupEntries;
public class ProtectionList : InventoryManagemenrBase
{
    public override string Name { get; } = Loc.T("Inventory Cleanup/Protection List");

    private ProtectionList()
    {
        DisplayPriority = -1;
        Builder = InventoryCleanupCommon.CreateCleanupHeaderBuilder()
            .Section(Name)
            .TextWrapped(Loc.T("AutoRetainer won't sell, desynthese, discard or hand in to Grand Company these items, even if they are included in any other processing lists."))
            .Widget(() => InventoryManagementCommon.DrawListNew(InventoryCleanupCommon.SelectedPlan.IMProtectList));
    }

}