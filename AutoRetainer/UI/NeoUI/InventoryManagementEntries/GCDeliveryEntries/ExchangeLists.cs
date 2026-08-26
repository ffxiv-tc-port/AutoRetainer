using AutoRetainerAPI.Configuration;
using ECommons.Configuration;
using ECommons.ExcelServices;
using Lumina.Excel.Sheets;
using System.Numerics;
using GrandCompany = ECommons.ExcelServices.GrandCompany;
using GrandCompanyRank = Lumina.Excel.Sheets.GrandCompanyRank;

namespace AutoRetainer.UI.NeoUI.InventoryManagementEntries.GCDeliveryEntries;
public sealed unsafe class ExchangeLists : InventoryManagemenrBase
{
    private ImGuiEx.RealtimeDragDrop<GCExchangeItem> DragDrop = new("GCELDD", x => x.ID);
    public override string Name { get; } = Loc.T("Grand Company Delivery/Exchange Lists");
    private GCExchangeCategoryTab? SelectedCategory = null;
    private GCExchangeCategoryTab? SelectedCategory2 = null;
    private GCExchangeRankTab? SelectedRank = null;
    private Guid SelectedPlanGuid = Guid.Empty;

    public override int DisplayPriority => -5;

    public override void Draw()
    {
        C.AdditionalGCExchangePlans.Where(x => x.GUID == Guid.Empty).Each(x => x.GUID = Guid.NewGuid());
        ImGuiEx.TextWrapped(Loc.T("""
            Select the items to be purchased automatically during Grand Company Expert Delivery operations.
            Purchase Logic:
            - The system will attempt to purchase the first available item from the list.
            - Purchases will continue until the quantity of that item in your inventory reaches the specified target amount.
            If no listed items are available for purchase, or they cannot fit into your inventory:
            - The system will purchase Ventures instead.
            - Venture purchases will continue until your Venture count reaches 65,000.
            Once the Venture cap is reached and no other purchases are possible:
            - Any excess Grand Company Seals will be discarded.
            """));

        var selectedPlan = C.AdditionalGCExchangePlans.FirstOrDefault(x => x.GUID == SelectedPlanGuid);
        ImGuiEx.InputWithRightButtonsArea(() =>
        {
            if(ImGui.BeginCombo("##selplan", selectedPlan?.DisplayName ?? Loc.T("Default Plan")))
            {
                if(ImGui.Selectable(Loc.T("Default Plan"), selectedPlan == null)) SelectedPlanGuid = Guid.Empty;
                ImGui.Separator();
                foreach(var x in C.AdditionalGCExchangePlans)
                {
                    ImGui.PushID(x.ID);
                    if(ImGui.Selectable(x.DisplayName)) SelectedPlanGuid = x.GUID;
                    ImGui.PopID();
                }
                ImGui.EndCombo();
            }
        }, () =>
        {
            if(ImGuiEx.IconButton(FontAwesomeIcon.Plus))
            {
                var newPlan = new GCExchangePlan();
                C.AdditionalGCExchangePlans.Add(newPlan);
                SelectedPlanGuid = newPlan.GUID;
            }
            ImGuiEx.Tooltip(Loc.T("Add new plan"));
            ImGui.SameLine(0, 1);
            if(ImGuiEx.IconButton(FontAwesomeIcon.Copy))
            {
                var clone = (selectedPlan ?? C.DefaultGCExchangePlan).DSFClone();
                clone.GUID = Guid.Empty;
                Copy(EzConfig.DefaultSerializationFactory.Serialize(clone));
            }
            ImGuiEx.Tooltip(Loc.T("Copy"));
            ImGui.SameLine(0, 1);
            if(ImGuiEx.IconButton(FontAwesomeIcon.Paste))
            {
                try
                {
                    var newPlan = EzConfig.DefaultSerializationFactory.Deserialize<GCExchangePlan>(Paste()) ?? throw new NullReferenceException();
                    C.AdditionalGCExchangePlans.Add(newPlan);
                    SelectedPlanGuid = newPlan.GUID;
                }
                catch(Exception e)
                {
                    e.Log();
                    Notify.Error(e.Message);
                }
            }
            ImGuiEx.Tooltip(Loc.T("Paste"));
            if(selectedPlan != null)
            {
                ImGui.SameLine(0, 1);
                if(ImGuiEx.IconButton(FontAwesomeIcon.ArrowsUpToLine, enabled: ImGuiEx.Ctrl && selectedPlan != null))
                {
                    C.DefaultGCExchangePlan = selectedPlan;
                    new TickScheduler(() => C.AdditionalGCExchangePlans.Remove(selectedPlan));
                }
                ImGuiEx.Tooltip(Loc.T(SharedText.MakePlanDefaultHint));
                ImGui.SameLine(0, 1);
                if(ImGuiEx.IconButton(FontAwesomeIcon.Trash, enabled: ImGuiEx.Ctrl && selectedPlan != null))
                {
                    new TickScheduler(() => C.AdditionalGCExchangePlans.Remove(selectedPlan));
                }
                ImGuiEx.Tooltip(Loc.T("Delete this plan. Hold CTRL and click."));
            }
        });

        if(SelectedPlanGuid == Guid.Empty)
        {
            DrawGCEchangeList(C.DefaultGCExchangePlan);
        }
        else
        {
            if(Data != null)
            {
                if(Data.ExchangePlan == SelectedPlanGuid)
                {
                    ImGuiEx.Text(ImGuiColors.ParsedGreen, UiBuilder.IconFont, FontAwesomeIcon.Check.ToIconString());
                    ImGui.SameLine();
                    ImGuiEx.Text(ImGuiColors.ParsedGreen, Loc.T("Used by current character"));
                    ImGui.SameLine();
                    if(ImGui.SmallButton(Loc.T("Unassign")))
                    {
                        Data.ExchangePlan = Guid.Empty;
                    }
                }
                else
                {
                    ImGuiEx.Text(ImGuiColors.DalamudOrange, UiBuilder.IconFont, FontAwesomeIcon.ExclamationTriangle.ToIconString());
                    ImGui.SameLine();
                    ImGuiEx.Text(ImGuiColors.DalamudOrange, Loc.T("Not used by current character"));
                    ImGui.SameLine();
                    if(ImGui.SmallButton(Loc.T("Assign")))
                    {
                        Data.ExchangePlan = selectedPlan.GUID;
                    }
                }
            }

            var planIndex = C.AdditionalGCExchangePlans.IndexOf(x => x.GUID == SelectedPlanGuid);
            if(planIndex == -1)
            {
                SelectedPlanGuid = Guid.Empty;
            }
            else
            {
                DrawGCEchangeList(C.AdditionalGCExchangePlans[planIndex]);
            }
        }
    }

    public void DrawGCEchangeList(GCExchangePlan plan)
    {
        ref string getFilter() => ref Ref<string>.Get($"{plan.ID}filter");
        ref bool onlySelected() => ref Ref<bool>.Get($"{plan.ID}onlySel");
        ref string getFilter2() => ref Ref<string>.Get($"{plan.ID}filter2");

        ImGui.PushID(plan.ID);
        plan.Validate();

        ImGuiEx.InputWithRightButtonsArea("GCPlanSettings", () =>
        {
            if(ReferenceEquals(plan, C.DefaultGCExchangePlan))
            {
                ImGui.BeginDisabled();
                var s = Loc.T("Default exchange plan can not be renamed");
                ImGui.InputText("##name", ref s, 1);
                ImGui.EndDisabled();
            }
            else
            {
                ImGui.InputTextWithHint($"##name", Loc.T("Name"), ref plan.Name, 100);
                ImGuiEx.Tooltip(Loc.T("Exchange plan name"));
            }
        }, () =>
        {
            ImGui.SetNextItemWidth(100f);
            ImGui.InputInt(Loc.T("Seals to keep"), ref plan.RemainingSeals.ValidateRange(0, 70000), 0, 0);
            ImGuiEx.HelpMarker(Loc.T("This amount of seals will be kept after purchase list is executed. However, this value will be capped to be no more than 20000 seals less than maximum possible, according to character's rank. "));
            ImGui.SameLine();
            ImGui.Checkbox(Loc.T("Finish by purchasing items"), ref plan.FinalizeByPurchasing);
            ImGuiEx.HelpMarker(Loc.T("If selected, after final exchange items will be purchased, otherwise - purchase will not be made until seals are capped again."));
        });

        ImGuiEx.SetNextItemFullWidth();
        if(ImGui.BeginCombo("##Add Items", Loc.T("Add Items"), ImGuiComboFlags.HeightLarge))
        {
            ImGuiEx.InputWithRightButtonsArea(() =>
            {
                ImGui.InputTextWithHint("##filter2", Loc.T("Search..."), ref getFilter2(), 100);
            }, () =>
            {
                ImGui.SetNextItemWidth(100f);
                ImGuiEx.EnumCombo("##cat2", ref SelectedCategory2, names: Loc.EnumNames<GCExchangeCategoryTab>(), nullName: Loc.T("All Categories"));
                ImGuiEx.Tooltip(Loc.T("Category"));
            });
            foreach(var x in Utils.SharedGCExchangeListings)
            {
                if(getFilter2().Length > 0
                    && !x.Value.Data.Name.GetText().Contains(getFilter2(), StringComparison.OrdinalIgnoreCase)
                    && !x.Value.Category.ToString().Equals(getFilter2(), StringComparison.OrdinalIgnoreCase)
                    && !Utils.GCRanks[x.Value.MinPurchaseRank].Equals(getFilter2(), StringComparison.OrdinalIgnoreCase)
                    ) continue;
                if(SelectedCategory2 != null && x.Value.Category != SelectedCategory2.Value) continue;
                var cont = plan.Items.Select(s => s.ItemID).ToArray();
                if(ThreadLoadImageHandler.TryGetIconTextureWrap(x.Value.Data.Icon, false, out var t))
                {
                    ImGui.Image(t.Handle, new(ImGui.GetTextLineHeight()));
                    ImGui.SameLine();
                }
                if(ImGui.Selectable(x.Value.Data.GetName() + $"##{x.Key}", cont.Contains(x.Key), ImGuiSelectableFlags.DontClosePopups))
                {
                    plan.Items.Add(new(x.Key, 0));
                }
            }
            ImGui.EndCombo();
        }
        if(ImGui.BeginPopup("Ex"))
        {
            if(ImGui.Selectable(Loc.T("Fill weapons and armor purchases optimally for extra FC points")))
            {
                List<GCExchangeItem> items = [];
                var qualifyingItems = Utils.SharedGCExchangeListings.Where(x => (x.Value.Category == GCExchangeCategoryTab.Weapons || x.Value.Category == GCExchangeCategoryTab.Armor) && x.Value.Data.GetRarity() == ItemRarity.Green).ToDictionary();
                plan.Items.RemoveAll(x => qualifyingItems.ContainsKey(x.ItemID));
                foreach(var item in qualifyingItems)
                {
                    items.Add(new(item.Key, 0));
                }
                items = items.OrderByDescending(x => (double)Svc.Data.GetExcelSheet<GCSupplyDutyReward>().GetRow(x.Data.Value.LevelItem.RowId).SealsExpertDelivery / (double)Utils.SharedGCExchangeListings[x.ItemID].Seals).ToList();
                foreach(var x in items)
                {
                    plan.Items.Add(x);
                    x.Quantity = Utils.SharedGCExchangeListings[x.ItemID].Data.IsUnique ? 1 : 999;
                }
            }
            ImGuiEx.Tooltip(Loc.T("Select this option to fill in your plan with all purchaseable weapons and gear items. By doing so, weapons and items will be purchased and handed right back to the Grand Company, maximizing amount of generated Free Company points. All these items will be placed at the end of the list and only purchased if nothing else is available."));
            if(ImGui.Selectable(Loc.T("Add all missing items")))
            {
                foreach(var x in Utils.SharedGCExchangeListings)
                {
                    if(!plan.Items.Any(i => i.ItemID == x.Key))
                    {
                        plan.Items.Add(new(x.Key, 0));
                    }
                }
            }
            if(ImGui.Selectable(Loc.T("Reset quantities to 0")))
            {
                plan.Items.Each(x => x.Quantity = 0);
                plan.Items.Each(x => x.QuantitySingleTime = 0);
            }
            if(ImGui.Selectable(Loc.T("Remove 0-quantity items")))
            {
                plan.Items.RemoveAll(x => x.Quantity == 0 && x.QuantitySingleTime == 0);
            }
            if(ImGuiEx.Selectable(Loc.T("Clear the list (Hold CTRL and click)"), enabled: ImGuiEx.Ctrl))
            {
                plan.Items.Clear();
            }
            ImGui.EndPopup();
        }
        if(ImGuiEx.IconButtonWithText(FontAwesomeIcon.AngleDoubleDown, Loc.T("Actions")))
        {
            ImGui.OpenPopup("Ex");
        }
        ImGui.SameLine();
        ImGuiEx.InputWithRightButtonsArea("Fltr2", () =>
        {
            ImGui.InputTextWithHint("##filter", Loc.T("Search..."), ref getFilter(), 100);
        }, () =>
        {
            ImGui.Checkbox(Loc.T("Only Selected"), ref onlySelected());
            ImGui.SameLine();
            ImGui.SetNextItemWidth(100f);
            ImGuiEx.EnumCombo("##cat", ref SelectedCategory, names: Loc.EnumNames<GCExchangeCategoryTab>(), nullName: Loc.T("All Categories"));
            ImGuiEx.Tooltip(Loc.T("Category"));
        });



        DragDrop.Begin();
        if(ImGuiEx.BeginDefaultTable("GCDeliveryList", ["##dragDrop", "~" + Loc.T("Item"), "GC", "Lv", Loc.T("Price"), Loc.T("Category"), Loc.T("Keep"), Loc.T("One-Time"), "##controls"]))
        {
            for(var i = 0; i < plan.Items.Count; i++)
            {
                var currentItem = plan.Items[i];
                var meta = Utils.SharedGCExchangeListings[currentItem.ItemID];
                if(onlySelected() && currentItem.Quantity == 0) continue;
                if(getFilter().Length > 0
                    && !meta.Data.Name.GetText().Contains(getFilter(), StringComparison.OrdinalIgnoreCase)
                    && !meta.Category.ToString().Equals(getFilter(), StringComparison.OrdinalIgnoreCase)
                    && !Utils.GCRanks[meta.MinPurchaseRank].Equals(getFilter(), StringComparison.OrdinalIgnoreCase)
                    ) continue;
                if(SelectedCategory != null && meta.Category != SelectedCategory.Value) continue;
                ImGui.PushID(currentItem.ID);
                ImGui.TableNextRow();
                DragDrop.SetRowColor(currentItem);
                ImGui.TableNextColumn();
                DragDrop.NextRow();
                if(ImGuiEx.IconButton(FontAwesomeIcon.AngleDoubleUp))
                {
                    new TickScheduler(() =>
                    {
                        plan.Items.Remove(currentItem);
                        plan.Items.Insert(0, currentItem);
                    });
                }
                ImGui.SameLine(0, 1);
                ImGuiEx.Tooltip(Loc.T("Move to the top"));
                DragDrop.DrawButtonDummy(currentItem, plan.Items, i);
                ImGui.TableNextColumn();
                if(ThreadLoadImageHandler.TryGetIconTextureWrap(meta.Data.Icon, false, out var t))
                {
                    ImGui.Image(t.Handle, new(ImGui.GetFrameHeight()));
                    ImGui.SameLine();
                }
                ImGuiEx.TextV($"{meta.Data.Name.GetText()}");
                ImGui.TableNextColumn();
                foreach(var c in Enum.GetValues<GrandCompany>().Where(x => x != GrandCompany.Unemployed))
                {
                    if(ThreadLoadImageHandler.TryGetIconTextureWrap(60870 + (int)c, false, out var ctex))
                    {
                        var trans = !meta.Companies.Contains(c);
                        if(trans) ImGui.PushStyleVar(ImGuiStyleVar.Alpha, 0.2f);
                        ImGui.Image(ctex.Handle, new(ImGui.GetFrameHeight()));
                        if(trans) ImGui.PopStyleVar();
                        ImGuiEx.Tooltip($"{c}" + (trans ? Loc.T(" (unavailable)") : ""));
                        ImGui.SameLine(0, 1);
                    }
                }
                ImGui.TableNextColumn();
                ImGuiEx.TextV($"{meta.Data.LevelItem.RowId}");
                ImGui.TableNextColumn();
                if(Svc.Data.GetExcelSheet<GrandCompanyRank>().TryGetRow(meta.MinPurchaseRank, out var rank) && ThreadLoadImageHandler.TryGetIconTextureWrap(rank.IconFlames, false, out var tex))
                {
                    ImGui.Image(tex.Handle, new(ImGui.GetFrameHeight()));
                    var rankName = Utils.GCRanks[meta.MinPurchaseRank];
                    ImGuiEx.Tooltip(rankName);
                    if(ImGuiEx.HoveredAndClicked()) getFilter() = rankName;
                    ImGui.SameLine();
                }
                ImGuiEx.TextV($"{meta.Seals}");
                ImGui.TableNextColumn();
                ImGuiEx.TextV($"{meta.Category}");
                if(ImGuiEx.HoveredAndClicked()) getFilter() = meta.Category.ToString();
                ImGui.TableNextColumn();
                if(currentItem.Data.Value.IsUnique)
                {
                    ImGuiEx.Checkbox(Loc.T("Unique"), ref currentItem.Quantity);
                }
                else
                {
                    ImGui.SetNextItemWidth(100f.Scale());
                    ImGui.InputInt("##qty", ref currentItem.Quantity.ValidateRange(0, int.MaxValue), 0, 0);
                }
                ImGuiEx.Tooltip(Loc.T("Select amount of items to keep in your inventory"));
                ImGui.TableNextColumn();
                ImGui.SetNextItemWidth(100f.Scale());
                ImGui.InputInt("##qtyonetime", ref currentItem.QuantitySingleTime.ValidateRange(0, currentItem.Data.Value.IsUnique ? 1 : int.MaxValue), 0, 0);
                ImGuiEx.Tooltip(Loc.T("Select amount of items to purchase once. Whenever purchase is made on any character using this plan, an amount will be subtracted from this value. Once it reaches 0, it will back to \"Keep\" amount."));
                ImGui.TableNextColumn();
                if(ImGuiEx.IconButton(FontAwesomeIcon.Clone))
                {
                    plan.Items.Insert(i + 1, currentItem.JSONClone());
                }
                ImGuiEx.Tooltip(Loc.T("Duplicate this listing."));
                ImGui.SameLine(0, 1);
                if(ImGuiEx.IconButton(FontAwesomeIcon.Trash))
                {
                    new TickScheduler(() => plan.Items.Remove(currentItem));
                }
                ImGuiEx.Tooltip(Loc.T("Deletes item from the list if there are multiple copies of it or sets it's amount to 0 if there is only one copy"));
                ImGui.PopID();
            }
            ImGui.EndTable();
        }
        DragDrop.End();
        ImGui.PopID();
    }
}