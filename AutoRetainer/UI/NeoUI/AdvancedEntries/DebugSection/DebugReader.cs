using AutoRetainer.Internal;
using AutoRetainer.Modules.Voyage.Readers;
using AutoRetainer.Scheduler.Tasks;
using AutoRetainer.UiHelpers;
using ECommons.UIHelpers.AtkReaderImplementations;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace AutoRetainer.UI.NeoUI.AdvancedEntries.DebugSection;

internal unsafe class DebugReader : DebugSectionBase
{
    public override void Draw()
    {
        {
            if(TryGetAddonByName<AtkUnitBase>("FreeCompanyCreditShop", out var a) && IsAddonReady(a))
            {
                var reader = new ReaderFreeCompanyCreditShop(a);
                ImGuiEx.Text($"""
                    Rank: {reader.FCRank}
                    Credits: {reader.Credits}
                    Count: {reader.Count}
                    """);
                for(var i = 0; i < reader.Count; i++)
                {
                    var x = reader.Listings[i];
                    ImGuiEx.Text($"{x}");
                    if(ImGuiEx.HoveredAndClicked()) new FreeCompanyCreditShop(a).Buy(0);
                    var amount = Math.Floor((float)reader.Credits / (float)(x.Price));
                }

                if(ImGui.Button("Run task")) TaskRecursivelyBuyFuel.Enqueue();

                if(ImGui.CollapsingHeader("Dump nodes"))
                {
                    var dump = new System.Text.StringBuilder();
                    DumpNodeList(&a->UldManager, 0, dump);
                    if(ImGui.Button("Copy to clipboard"))
                    {
                        Copy(dump.ToString());
                    }
                    ImGuiEx.Text(dump.ToString());
                }
            }
        }

        {
            if(TryGetAddonByName<AtkUnitBase>("RetainerList", out var a) && IsAddonReady(a))
            {
                var reader = new ReaderRetainerList(a);
                foreach(var x in reader.Retainers)
                {
                    ImGuiEx.Text($"{x.Name}/act {x.IsActive}/gil {x.Gil}/lvl {x.Level}/inv {x.Inventory}");
                }
            }
        }
        {
            if(TryGetAddonByName<AtkUnitBase>("RetainerItemTransferList", out var a) && IsAddonReady(a))
            {
                var reader = new ReaderRetainerItemTransferList(a);
                foreach(var r in reader.Items)
                {
                    ImGuiEx.Text($"Item {r.ItemID}, isHQ = {r.IsHQ}");
                }
            }
        }
        {
            if(TryGetAddonByName<AtkUnitBase>("AirShipExploration", out var a) && IsAddonReady(a))
            {
                var reader = new ReaderAirShipExploration(a);
                ImGuiEx.Text($"Distance: {reader.Distance}");
                ImGuiEx.Text($"Fuel: {reader.Fuel}");
                foreach(var r in reader.Destinations)
                {
                    ImGuiEx.Text($"Destination {r.NameFull}, rank={r.RequiredRank}, status={r.StatusFlag}, canBeSelected={r.CanBeSelected}");
                }
            }
        }
        {
            if(TryGetAddonByName<AtkUnitBase>("SubmarineExplorationMapSelect", out var a) && IsAddonReady(a))
            {
                var reader = new ReaderSubmarineExplorationMapSelect(a);
                ImGuiEx.Text($"Current rank: {reader.SubmarineRank}");
                foreach(var r in reader.Maps)
                {
                    ImGuiEx.Text($"Map {r.Name}, rank={r.RequiredRank}");
                }
            }
        }
        {
            if(TryGetAddonByName<AtkUnitBase>("SelectString", out var a) && IsAddonReady(a))
            {
                var reader = new ReaderSelectString(a);
                foreach(var r in reader.Entries)
                {
                    ImGuiEx.Text($"{r.Text}");
                }
            }
        }
    }

    private static void DumpNodeList(AtkUldManager* uld, int depth, System.Text.StringBuilder sb)
    {
        var indent = new string(' ', depth * 2);
        for(var i = 0; i < uld->NodeListCount; i++)
        {
            var node = uld->NodeList[i];
            if(node == null) continue;
            var comp = node->GetComponent();
            string extra = "";
            if(comp != null)
            {
                extra += $" component@{(nint)comp:X}";
            }
            if(node->GetAsAtkTextNode() != null)
            {
                extra += $" text=\"{node->GetAsAtkTextNode()->NodeText.GetText()}\"";
            }
            sb.AppendLine($"{indent}[{i}] type={node->Type} id={node->NodeId} visible={node->IsVisible()}{extra}");
            if(depth < 3 && node->GetAsAtkComponentNode() != null && node->GetAsAtkComponentNode()->Component != null)
            {
                DumpNodeList(&node->GetAsAtkComponentNode()->Component->UldManager, depth + 1, sb);
            }
        }
    }
}
