namespace AutoRetainer.UI.NeoUI.InventoryManagementEntries.InventoryCleanupEntries;

/// <summary>
/// 丟棄清單。刻意做成**獨立**清單而不是沿用賣出清單 —— 見
/// <see cref="PluginData.InventoryManagementSettings.IMAutoDiscardList"/> 的註解。
/// </summary>
public class DiscardList : InventoryManagemenrBase
{
    public override string Name => Loc.T("Inventory Cleanup/Discard List");

    private DiscardList()
    {
        var s = InventoryCleanupCommon.SelectedPlan;
        Builder = InventoryCleanupCommon.CreateCleanupHeaderBuilder()
            .Section(Name)
            .TextWrapped(ImGuiColors.DalamudOrange, Loc.T("Discarding is permanent: discarded items can NOT be bought back or recovered. This list is deliberately separate from the sell lists, and is only ever processed when you press the button manually."))
            .TextWrapped(Loc.T("These items will be discarded when you manually press \"Discard now\". Items on the Protection List are never discarded, even if they are also listed here."))
            .Widget(() => InventoryManagementCommon.DrawListNew(s.IMAutoDiscardList))
            ;
    }
}
