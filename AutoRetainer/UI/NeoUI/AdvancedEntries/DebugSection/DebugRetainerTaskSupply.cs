using AutoRetainer.Scheduler.Tasks;
using ECommons.ExcelServices;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace AutoRetainer.UI.NeoUI.AdvancedEntries.DebugSection;
public unsafe class DebugRetainerTaskSupply : DebugSectionBase
{
    public override void Draw()
    {
        if(TryGetAddonByName<AtkUnitBase>("RetainerTaskSupply", out var addon) && addon->IsReady())
        {
            var count = TaskAssignHuntingVenture.TryGetListCount(addon, out var n) ? n : 0;
            for(var i = 0; i < count; i++)
            {
                if(!TaskAssignHuntingVenture.TryGetListedTaskId(addon, i, out var id))
                {
                    ImGuiEx.Text($"[{i}] ?");
                    continue;
                }
                ImGuiEx.Text($"{Svc.Data.GetExcelSheet<RetainerTask>().GetRow(id).GetVentureItem().Value.GetName()}");
            }
        }
        ref var vid = ref Ref<int>.Get("DTRSID");
        ImGui.InputInt("vid", ref vid);
        if(ImGui.Button("OpenAssignVentureWindow")) DuoLog.Information($"{TaskAssignHuntingVenture.OpenAssignVentureWindow((uint)vid)}");
    }
}
