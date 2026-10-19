namespace AutoRetainer.UI.NeoUI.InventoryManagementEntries.InventoryCleanupEntries;
public class HardList : InventoryManagemenrBase
{
    public override string Name => Loc.T("Inventory Cleanup/Unconditional Sell List");

    private HardList()
    {
        // 這一頁只建一次，選中的計畫之後會換（切換、設為預設、刪除、匯入設定）：每幀現取，不在這裡抓住。
        Builder = InventoryCleanupCommon.CreateCleanupHeaderBuilder()
            .Section(Name)
            .TextWrapped(Loc.T("These items will always be sold, regardless of their source, as long as their stack count does not exceeds specified amount that you can specify below. Additionally, only these items will ever be sold to an NPC."))
            .InputInt(150f, Loc.T("Maximum stack size to be sold"), () => ref InventoryCleanupCommon.SelectedPlan.IMAutoVendorHardStackLimit)
            .Widget(() =>
            {
                var s = InventoryCleanupCommon.SelectedPlan;
                InventoryManagementCommon.DrawListNew(s.IMAutoVendorHard, (x) =>
                {
                    ImGui.SameLine();
                    ImGui.PushFont(UiBuilder.IconFont);
                    ImGuiEx.CollectionButtonCheckbox(FontAwesomeIcon.Database.ToIconString(), x, s.IMAutoVendorHardIgnoreStack);
                    ImGui.PopFont();
                    ImGuiEx.Tooltip(Loc.T("Ignore stack setting for this item"));
                });
            });
    }
}
