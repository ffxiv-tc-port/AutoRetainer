using AutoRetainer.Internal.InventoryManagement;
using ECommons.ExcelServices;
using ECommons.GameFunctions;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using Lumina.Excel.Sheets;

namespace AutoRetainer.UI.NeoUI.AdvancedEntries.DebugSection;
public unsafe class DebugInventoryManagement : DebugSectionBase
{
    private int slot;
    private InventoryType Type;
    private HashSet<uint> Whitelist = [];

    public override void Draw()
    {
        if(ImGui.CollapsingHeader("Inventories"))
        {
            foreach(var x in Enum.GetValues<InventoryType>())
            {
                ImGuiEx.TreeNodeCollapsingHeader(x.ToString(), () =>
                {
                    // 這是除錯顯示，讀不到就顯示讀不到。Enum.GetValues 會列出所有容器型別，
                    // 其中大部分在任一時刻都是沒載入的（雇員頁面、部隊置物櫃等），所以 null 是常態不是異常。
                    var inv = InventoryManager.Instance()->GetInventoryContainer(x);
                    if(inv == null)
                    {
                        ImGuiEx.Text(EColor.RedBright, "Container not loaded");
                        return;
                    }
                    for(var i = 0; i < inv->Size; i++)
                    {
                        var slot = inv->GetInventorySlot(i);
                        if(slot == null)
                        {
                            ImGuiEx.Text(EColor.RedBright, $"{i}: <unreadable>");
                            continue;
                        }
                        ImGuiEx.Text($"{i}: {ExcelItemHelper.GetName(slot->ItemId)} x{slot->Quantity} {slot->Flags}");
                    }
                });
            }
        }
        if(ImGui.CollapsingHeader("Shop Sell test"))
        {
            ImGuiEx.EnumCombo($"type", ref Type);
            ImGui.InputInt("Slot", ref slot);
            // 🔴 slot 直接來自 InputInt，使用者可以打任何數字（含負數與遠超容器大小的值），
            // 而 GetInventorySlot 是虛擬函式、會進遊戲原生碼，對超界索引的行為未經證實。
            // 所以在呼叫之前就要自己夾好範圍，不能指望原生端會擋。
            var sellContainer = InventoryManager.Instance()->GetInventoryContainer(Type);
            var slotReadable = sellContainer != null && slot >= 0 && slot < sellContainer->Size;
            if(sellContainer == null)
            {
                ImGuiEx.Text(EColor.RedBright, "Container not loaded");
            }
            else if(!slotReadable)
            {
                ImGuiEx.Text(EColor.RedBright, $"Slot out of range (size {sellContainer->Size})");
            }
            else
            {
                var sellSlot = sellContainer->GetInventorySlot(slot);
                if(sellSlot == null) ImGuiEx.Text(EColor.RedBright, "Slot unreadable");
                else ImGuiEx.Text(ExcelItemHelper.GetName(sellSlot->ItemId));
            }
            if(slotReadable && ImGui.Button("Sell"))
            {
                // SellItemToShop 讀不到目標時會丟例外。這裡是除錯 UI，在 Draw() 中途讓例外逃出去
                // 會連帶弄壞整個視窗，所以接住並記錄就好。
                try
                {
                    P.Memory.SellItemToShop(Type, slot);
                }
                catch(Exception e)
                {
                    e.Log();
                }
            }
            if(ImGui.Button("Enqueue if present"))
            {
                NpcSaleManager.EnqueueIfItemsPresent();
            }
            ImGuiEx.Text($"Valid npc: {NpcSaleManager.GetValidNPC()}");
            if(ImGui.Button("Interact with target")) TargetSystem.Instance()->InteractWithObject(Svc.Targets.Target.Struct(), false);
            if(TryGetAddonMaster<AddonMaster.SelectIconString>(out var m))
            {
                foreach(var x in m.Entries)
                {
                    if(ImGui.Selectable(x.Text))
                    {
                        x.Select();
                    }
                }
            }
        }
        if(ImGui.CollapsingHeader("Vendor list"))
        {
            foreach(var x in Vendors)
            {
                ImGuiEx.Text(Whitelist.Contains(x) ? EColor.GreenBright : null, $"{x}: {Svc.Data.GetExcelSheet<ENpcResident>().GetRowOrDefault(x)?.Plural}");
                if(ImGui.IsItemHovered())
                {
                    if(ImGuiEx.Ctrl)
                    {
                        Whitelist.Add(x);
                    }
                    if(ImGuiEx.Shift) Whitelist.Remove(x);
                }
            }
            if(ImGui.Button(Loc.T("Copy"))) Copy(Whitelist.Print());
        }
    }

    public IEnumerable<uint> Vendors
    {
        get
        {
            foreach(var x in Svc.Data.GetSubrowExcelSheet<HousingEmploymentNpcList>())
            {
                for(var i = 0; i < x.Count; i++)
                {
                    var ret = x[i];
                    if(ret.RowId != 0) yield return ret.RowId;
                }
            }
        }
    }
}
