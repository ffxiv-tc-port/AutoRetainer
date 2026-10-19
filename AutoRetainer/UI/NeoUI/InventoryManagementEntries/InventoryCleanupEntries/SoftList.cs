namespace AutoRetainer.UI.NeoUI.InventoryManagementEntries.InventoryCleanupEntries;
public class SoftList : InventoryManagemenrBase
{
    public override string Name => Loc.T("Inventory Cleanup/Quick Venture Sell List");
    private SoftList()
    {
        // 這一頁只建一次，選中的計畫之後會換：清單每幀現取，不在這裡抓住。
        Builder = InventoryCleanupCommon.CreateCleanupHeaderBuilder()
            .Section(Name)
            .TextWrapped(Loc.T("These items, when obtained from Quick Venture will be sold unless they have stacked with the same item."))
            .Widget(() => InventoryManagementCommon.DrawListNew(InventoryCleanupCommon.SelectedPlan.IMAutoVendorSoft));
    }
}
