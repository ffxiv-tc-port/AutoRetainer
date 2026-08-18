using ECommons.Configuration;
using ECommons.ExcelServices;
using ECommons.Reflection;
using ECommons.Throttlers;
using Lumina.Excel.Sheets;

namespace AutoRetainer.UI.NeoUI.InventoryManagementEntries;
public class EntrustManager : InventoryManagemenrBase
{
    public override string Name { get; } = Loc.T("Entrust Manager");
    private Guid SelectedGuid = Guid.Empty;
    private string Filter = "";

    public override void Draw()
    {
        ImGuiEx.TextWrapped(Loc.T("Use advanced entrust manager to entrust specific items to specific retainers. In this window you can configure specific plans; then, you can assign entrust plans to your retainers in retainer configuration window."));
        ImGui.Checkbox(Loc.T("Enable"), ref C.EnableEntrustManager);
        ImGui.Checkbox(Loc.T("Output entrusted items into chat"), ref C.EnableEntrustChat);
        ImGui.SetNextItemWidth(150f);
        ImGuiEx.SliderInt(Loc.T("Entrust interval, ms"), ref C.EntrustIntervalMS.ValidateRange(50, 1000), 50, 1000);
        ImGuiEx.HelpMarker(Loc.T("Minimum spacing between two entrust commands. The plugin already waits for each item to actually leave your inventory before sending the next one, so this is only a lower bound on top of that wait - it never allows two commands to be in flight at once.\n\nLowering it makes entrusting faster, but a value below the round trip to the server buys nothing, and a very low value may get commands dropped by the server. Raise it if you see items being skipped or \"no inventory change\" messages in the log."));
        var selectedPlan = C.EntrustPlans.FirstOrDefault(x => x.Guid == SelectedGuid);

        ImGuiEx.InputWithRightButtonsArea(() =>
        {
            if(ImGui.BeginCombo($"##select", selectedPlan?.Name ?? Loc.T("Select plan..."), ImGuiComboFlags.HeightLarge))
            {
                for(var i = 0; i < C.EntrustPlans.Count; i++)
                {
                    var plan = C.EntrustPlans[i];
                    ImGui.PushID(plan.Guid.ToString());
                    if(ImGui.Selectable(plan.Name, plan == selectedPlan))
                    {
                        SelectedGuid = plan.Guid;
                    }
                    ImGui.PopID();
                }
                ImGui.EndCombo();
            }
        }, () =>
        {
            if(ImGuiEx.IconButton(FontAwesomeIcon.Plus))
            {
                var plan = new EntrustPlan();
                C.EntrustPlans.Add(plan);
                SelectedGuid = plan.Guid;
                plan.Name = string.Format(Loc.T("Entrust plan {0}"), C.EntrustPlans.Count);
            }
            ImGui.SameLine();
            if(ImGuiEx.IconButton(FontAwesomeIcon.Trash, enabled: selectedPlan != null && ImGuiEx.Ctrl))
            {
                C.EntrustPlans.Remove(selectedPlan);
            }
            ImGuiEx.Tooltip(Loc.T("Hold CTRL and click"));
            ImGui.SameLine();
            if(ImGuiEx.IconButton(FontAwesomeIcon.Copy, enabled: selectedPlan != null))
            {
                Copy(EzConfig.DefaultSerializationFactory.Serialize(selectedPlan, false));
            }
            ImGui.SameLine();
            if(ImGuiEx.IconButton(FontAwesomeIcon.Paste, enabled: EzThrottler.Check("ImportPlan")))
            {
                try
                {
                    var plan = EzConfig.DefaultSerializationFactory.Deserialize<EntrustPlan>(Paste()) ?? throw new NullReferenceException();
                    plan.Guid = Guid.NewGuid();
                    if(plan.GetType().GetFieldPropertyUnions(ReflectionHelper.AllFlags).Any(x => x.GetValue(plan) == null)) throw new NullReferenceException();
                    C.EntrustPlans.Add(plan);
                    SelectedGuid = plan.Guid;
                    Notify.Success(Loc.T("Imported plan from clipboard"));
                    EzThrottler.Throttle("ImportPlan", 2000, true);
                }
                catch(Exception e)
                {
                    DuoLog.Error(e.Message);
                }
            }
        });
        if(selectedPlan != null)
        {
            ImGuiEx.SetNextItemFullWidth();
            ImGui.InputTextWithHint($"##name", Loc.T("Plan name"), ref selectedPlan.Name, 100);
            ImGui.Checkbox(Loc.T("Entrust Duplicates"), ref selectedPlan.Duplicates);
            ImGuiEx.HelpMarker(Loc.T("Mimics vanilla entrust duplicates option: entrusts any items that already present in retainer's inventory up until your retainer fills up it's stack of items. Does not affects crystals. Items and categories that are explicitly added into the list below will be excluded from being processed by this option."));
            ImGui.Indent();
            ImGui.Checkbox(Loc.T("Allow going over stack"), ref selectedPlan.DuplicatesMultiStack);
            ImGuiEx.HelpMarker(Loc.T("Allows entrust duplicates to create new stacks of items that already exist in the selected retainer."));
            ImGui.Unindent();
            ImGui.Checkbox(Loc.T("Allow entrusting from Armory Chest"), ref selectedPlan.AllowEntrustFromArmory);
            ImGui.Checkbox(Loc.T("Manual execution only"), ref selectedPlan.ManualPlan);
            ImGuiEx.HelpMarker(Loc.T("Mark this plan for manual execution only. This plan will only be processed upon manual \"Entrust Items\" button click and never automatically."));
            ImGui.Checkbox(Loc.T("Exclude items present in protection list"), ref selectedPlan.ExcludeProtected);
            ImGui.Separator();
            ImGuiEx.TreeNodeCollapsingHeader($"{Loc.T("Entrust categories (")}{selectedPlan.EntrustCategories.Count}{Loc.T(" selected)")}###ecats", () =>
            {
                ImGuiEx.TextWrapped(Loc.T("Here you can select item categories that will be entrusted as a whole. Individual items that are selected below will be excluded from these rules."));
                if(ImGui.BeginTable("EntrustTable", 3, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.NoSavedSettings | ImGuiTableFlags.BordersInner))
                {
                    ImGui.TableSetupColumn("##1");
                    ImGui.TableSetupColumn(Loc.T("Item name"), ImGuiTableColumnFlags.WidthStretch);
                    ImGui.TableSetupColumn(Loc.T("Amount to keep"));
                    ImGui.TableHeadersRow();
                    foreach(var x in Svc.Data.GetExcelSheet<ItemUICategory>())
                    {
                        if(x.Name == "" || x.RowId == 39) continue;
                        var contains = selectedPlan.EntrustCategories.Any(s => s.ID == x.RowId);
                        ImGui.TableNextRow();
                        ImGui.TableNextColumn();
                        if(ThreadLoadImageHandler.TryGetIconTextureWrap(x.Icon, true, out var icon))
                        {
                            ImGui.Image(icon.Handle, new(ImGui.GetFrameHeight()));
                        }
                        ImGui.TableNextColumn();
                        if(ImGui.Checkbox(x.Name.ToString(), ref contains))
                        {
                            if(contains)
                            {
                                selectedPlan.EntrustCategories.Add(new() { ID = x.RowId });
                            }
                            else
                            {
                                selectedPlan.EntrustCategories.RemoveAll(s => s.ID == x.RowId);
                            }
                        }
                        ImGui.TableNextColumn();
                        if(selectedPlan.EntrustCategories.TryGetFirst(s => s.ID == x.RowId, out var result))
                        {
                            ImGui.SetNextItemWidth(130f);
                            ImGui.InputInt($"##amtkeep{result.ID}", ref result.AmountToKeep);
                        }
                    }
                    ImGui.EndTable();
                }
            });
            ImGuiEx.TreeNodeCollapsingHeader($"{Loc.T("Entrust individual items (")}{selectedPlan.EntrustItems.Count}{Loc.T(" selected)")}###eitems", () =>
            {
                InventoryManagementCommon.DrawListNew(selectedPlan.EntrustItems, (x) =>
                {
                    var amount = selectedPlan.EntrustItemsAmountToKeep.SafeSelect(x);
                    ImGui.SameLine();
                    ImGui.SetNextItemWidth(130f);
                    if(ImGui.InputInt($"##amtkeepitem{x}", ref amount))
                    {
                        selectedPlan.EntrustItemsAmountToKeep[x] = amount;
                    }
                    ImGuiEx.Tooltip(Loc.T("Amount to keep in your inventory"));
                });
            });
            ImGuiEx.TreeNodeCollapsingHeader(Loc.T("Fast addition/removal"), () =>
            {
                // 與背包清理的「快速新增/移除」是同一個功能形狀，共用同一組可設定快捷鍵(快捷鍵設定頁)。
                // 兩個頁面不會同時被繪製，而觸發條件本來就含「該區塊正在顯示」，所以共用不會互相干擾。
                var addKey = C.FastListAddKey;
                var removeKey = C.FastListRemoveKey;
                var addHeld = UIUtils.IsHotkeyHeld(addKey);
                var removeHeld = UIUtils.IsHotkeyHeld(removeKey);
                ImGuiEx.TextWrapped(GradientColor.Get(EColor.RedBright, EColor.YellowBright), Loc.T(SharedText.HoverItemsWhileHolding));
                ImGuiEx.Text(!addHeld ? ImGuiColors.DalamudGrey : ImGuiColors.DalamudRed, string.Format(Loc.T("{0} - add to entrust plan"), UIUtils.HotkeyName(addKey)));
                ImGuiEx.Text(!removeHeld ? ImGuiColors.DalamudGrey : ImGuiColors.DalamudRed, string.Format(Loc.T("{0} - delete from entrust plan"), UIUtils.HotkeyName(removeKey)));
                if(Svc.GameGui.HoveredItem > 0)
                {
                    var id = (uint)(Svc.GameGui.HoveredItem % 1000000);
                    if(addHeld)
                    {
                        if(!selectedPlan.EntrustItems.Contains(id))
                        {
                            selectedPlan.EntrustItems.Add(id);
                            Notify.Success(string.Format(Loc.T("Added {0} to entrust plan {1}"), ExcelItemHelper.GetName(id), selectedPlan.Name));
                        }
                    }
                    if(removeHeld)
                    {
                        if(selectedPlan.EntrustItems.Contains(id))
                        {
                            selectedPlan.EntrustItems.Remove(id);
                            Notify.Success(string.Format(Loc.T("Removed {0} from entrust plan {1}"), ExcelItemHelper.GetName(id), selectedPlan.Name));
                        }
                    }
                }
            });
        }
    }
}
