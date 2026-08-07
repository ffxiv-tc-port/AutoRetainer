using ECommons.ExcelServices;

namespace AutoRetainer.UI.NeoUI.InventoryManagementEntries.InventoryCleanupEntries;
public class FastAddition : InventoryManagemenrBase
{
    public override string Name { get; } = Loc.T("Inventory Cleanup/Fast Addition and Removal");

    private FastAddition()
    {
        Builder = InventoryCleanupCommon.CreateCleanupHeaderBuilder()
        .Section(Name)
        .Widget(() =>
        {
            var selectedSettings = InventoryCleanupCommon.SelectedPlan;
            // 這三個鍵原本硬編 Shift/Ctrl/Alt，現在讀設定(快捷鍵設定頁)。提示文字必須一起從設定值組出來，
            // 否則使用者改了鍵、提示還停在舊的字面值。
            var addKey = C.FastListAddKey;
            var addHardKey = C.FastListAddHardKey;
            var removeKey = C.FastListRemoveKey;
            var addHeld = UIUtils.IsHotkeyHeld(addKey);
            var addHardHeld = UIUtils.IsHotkeyHeld(addHardKey);
            var removeHeld = UIUtils.IsHotkeyHeld(removeKey);
            ImGuiEx.TextWrapped(GradientColor.Get(EColor.RedBright, EColor.YellowBright), Loc.T("While this text is visible, hover over items while holding:"));
            ImGuiEx.Text(!addHeld ? ImGuiColors.DalamudGrey : ImGuiColors.DalamudRed, string.Format(Loc.T("{0} - add to Quick Venture Sell List"), UIUtils.HotkeyName(addKey)));
            ImGuiEx.Text(Loc.T("* Items that already in Unconditional Sell List WILL NOT BE ADDED to Quick Venture Sell List"));
            ImGuiEx.Text(!addHardHeld ? ImGuiColors.DalamudGrey : ImGuiColors.DalamudRed, string.Format(Loc.T("{0} - add to Unconditional Sell List"), UIUtils.HotkeyName(addHardKey)));
            ImGuiEx.Text(Loc.T("* Items that already in Quick Venture Sell List WILL BE MOVED to Unconditional Sell List"));
            ImGuiEx.Text(!removeHeld ? ImGuiColors.DalamudGrey : ImGuiColors.DalamudRed, string.Format(Loc.T("{0} - delete from either list"), UIUtils.HotkeyName(removeKey)));
            ImGuiEx.Text(Loc.T("\nItems that are protected are unaffected by these actions"));
            if(Svc.GameGui.HoveredItem > 0)
            {
                var id = (uint)(Svc.GameGui.HoveredItem % 1000000);
                if(addHeld)
                {
                    if(!selectedSettings.IMProtectList.Contains(id) && !selectedSettings.IMAutoVendorSoft.Contains(id) && !selectedSettings.IMAutoVendorHard.Contains(id))
                    {
                        selectedSettings.IMAutoVendorSoft.Add(id);
                        Notify.Success(string.Format(Loc.T("Added {0} to Quick Venture Sell List"), ExcelItemHelper.GetName(id)));
                        selectedSettings.IMAutoVendorHard.Remove(id);
                    }
                }
                if(addHardHeld)
                {
                    if(!selectedSettings.IMProtectList.Contains(id) && !selectedSettings.IMAutoVendorHard.Contains(id) && !selectedSettings.IMAutoVendorSoft.Contains(id))
                    {
                        selectedSettings.IMAutoVendorHard.Add(id);
                        Notify.Success(string.Format(Loc.T("Added {0} to Unconditional Sell List"), ExcelItemHelper.GetName(id)));
                        selectedSettings.IMAutoVendorSoft.Remove(id);
                    }
                }
                if(removeHeld)
                {
                    if(selectedSettings.IMAutoVendorSoft.Remove(id)) Notify.Info(string.Format(Loc.T("Removed {0} from Quick Venture Sell List"), ExcelItemHelper.GetName(id)));
                    if(selectedSettings.IMAutoVendorHard.Remove(id)) Notify.Info(string.Format(Loc.T("Removed {0} from Unconditional Sell List"), ExcelItemHelper.GetName(id)));
                }
            }
        });
        DisplayPriority = -10;
    }
}
