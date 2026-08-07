using AutoRetainer.Internal.InventoryManagement;
using AutoRetainer.Scheduler.Tasks;
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
            .Checkbox(Loc.T("Enable discarding items"), () => ref InventoryCleanupCommon.SelectedPlan.IMEnableItemDiscard, Loc.T("Permanently destroys items on the Discard List. Discarded items can NOT be bought back. This is never triggered automatically: you must press \"Discard now\" yourself every time."))
            .Indent()
            .Widget(Loc.T("Discard now"), (x) =>
            {
                var s = InventoryCleanupCommon.SelectedPlan;
                var ready = s.IMEnableItemDiscard && Player.Interactable && !IsOccupied() && !P.TaskManager.IsBusy;
                // 🔴 破壞性操作的第二道確認：必須按住 CTRL 才點得下去。
                if(ImGuiEx.Button(x, ready && ImGuiEx.Ctrl))
                {
                    TaskDiscardItems.Enqueue();
                }
                ImGuiEx.Tooltip(Loc.T("Hold CTRL and click. Items on the Discard List will be permanently destroyed. Turn on \"Demo mode\" first to print what would be discarded without destroying anything."));
                if(s.IMEnableItemDiscard)
                {
                    ImGui.SameLine();
                    // 「隨時掃視」的資訊放列上：現在按下去會丟幾件，不必先開 tooltip 才知道。
                    var cnt = TaskDiscardItems.CountDiscardable();
                    ImGuiEx.Text(cnt > 0 ? ImGuiColors.DalamudOrange : ImGuiColors.DalamudGrey, cnt > 0 ? Loc.T("{0} item(s) match").Replace("{0}", cnt.ToString()) : Loc.T("nothing matches"));
                }
            })
            .Unindent()
            .Checkbox(Loc.T("Enable context menu integration"), () => ref InventoryCleanupCommon.SelectedPlan.IMEnableContextMenu)
            .Checkbox(Loc.T("Allow selling items from Armory Chest"), () => ref InventoryCleanupCommon.SelectedPlan.AllowSellFromArmory)
            .Checkbox(Loc.T("Demo mode"), () => ref InventoryCleanupCommon.SelectedPlan.IMDry, Loc.T("Do not sell items, instead print in chat what would be sold"))
            ;
    }
}
