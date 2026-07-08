using AutoRetainer.Internal.InventoryManagement;
using ECommons.GameHelpers;

namespace AutoRetainer.UI.NeoUI.InventoryManagementEntries.InventoryCleanupEntries;
public class GeneralSettings : InventoryManagemenrBase
{
    public override string Name { get; } = Loc.T("Inventory Cleanup/General Settings");

    private GeneralSettings()
    {
        Builder = InventoryCleanupCommon.CreateCleanupHeaderBuilder()
            .Section(Name)
            .Checkbox(Loc.T("Auto-open venture coffers"), () => ref InventoryCleanupCommon.SelectedPlan.IMEnableCofferAutoOpen, Loc.T("Multi Mode only. Before logging out, all coffers will be opened unless your inventory space is too low."))
            .Checkbox(Loc.T("Enable selling items to retainer"), () => ref InventoryCleanupCommon.SelectedPlan.IMEnableAutoVendor, Loc.T("When AutoRetainer checks resents retainers to ventures, items will be sold according to Inventory Cleanup plan."))
            .Checkbox(Loc.T("Enable selling items to housing NPC"), () => ref InventoryCleanupCommon.SelectedPlan.IMEnableNpcSell, Loc.T("When AutoRetainer enters a house, items will be sold according to the Inventory Cleanup plan. A housing vendor that supports item selling must be placed near the house entrance (not the workshop entrance)—you should be able to interact with the NPC immediately after entering."))
            .Indent()
            .Checkbox(Loc.T("Ignore NPC if retainer is available"), () => ref InventoryCleanupCommon.SelectedPlan.IMSkipVendorIfRetainer)
            .Widget(Loc.T("Sell now"), (x) =>
            {
                if(ImGuiEx.Button(x, Player.Interactable && InventoryCleanupCommon.SelectedPlan.IMEnableNpcSell && NpcSaleManager.GetValidNPC() != null && !IsOccupied() && !P.TaskManager.IsBusy))
                {
                    NpcSaleManager.EnqueueIfItemsPresent(true);
                }
            })
            .Unindent()
            .Checkbox(Loc.T("Auto-desynth items"), () => ref InventoryCleanupCommon.SelectedPlan.IMEnableItemDesynthesis)
            .Checkbox(Loc.T("Enable context menu integration"), () => ref InventoryCleanupCommon.SelectedPlan.IMEnableContextMenu)
            .Checkbox(Loc.T("Allow selling items from Armory Chest"), () => ref InventoryCleanupCommon.SelectedPlan.AllowSellFromArmory)
            .Checkbox(Loc.T("Demo mode"), () => ref InventoryCleanupCommon.SelectedPlan.IMDry, Loc.T("Do not sell items, instead print in chat what would be sold"))
            ;
    }
}
