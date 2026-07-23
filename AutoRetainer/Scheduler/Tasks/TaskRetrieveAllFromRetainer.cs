using AutoRetainer.Internal.InventoryManagement;
using ECommons.ExcelServices;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace AutoRetainer.Scheduler.Tasks;

/// <summary>
/// Retrieves every item from the currently open retainer's inventory, one slot per pass,
/// until the player's own inventory has fewer than <see cref="Config.MultiMinInventorySlots"/>
/// free slots left (the same "near-full" threshold the rest of the plugin already uses).
/// </summary>
internal static unsafe class TaskRetrieveAllFromRetainer
{
    private static List<(uint ID, uint Quantity)> capturedRetainerState = [];

    public static void Enqueue()
    {
        capturedRetainerState = [];
        P.TaskManager.Enqueue(RetrieveNextSlot, "TaskRetrieveAllFromRetainer", new(timeLimitMS: 5 * 60 * 1000));
    }

    private static bool? RetrieveNextSlot()
    {
        if(!InventorySpaceManager.IsRetainerInventoryLoaded())
        {
            DuoLog.Warning("Retainer inventory is not open, stopping retrieve-all.");
            return true;
        }
        if(Utils.GetInventoryFreeSlotCount() <= C.MultiMinInventorySlots)
        {
            DuoLog.Information("Inventory is close to full, stopping retrieve-all.");
            return true;
        }
        if(!EzThrottler.Check("RetrieveAllTimeout") && Utils.GetCapturedInventoryState(Utils.RetainerInventoriesWithCrystals).SequenceEqual(capturedRetainerState))
        {
            //waiting for the previous retrieve to actually leave the retainer's inventory
            return false;
        }
        if(!EzThrottler.Throttle("RetrieveAllSlot", 500)) return false;

        foreach(var type in Utils.RetainerInventoriesWithCrystals)
        {
            var inv = InventoryManager.Instance()->GetInventoryContainer(type);
            if(inv == null) continue;
            for(var i = 0; i < inv->Size; i++)
            {
                var item = inv->GetInventorySlot(i);
                if(item->ItemId != 0 && item->Quantity > 0)
                {
                    InternalLog.Information($"[RetrieveAll] Retrieving slot {i}/{type} - {ExcelItemHelper.GetName(item->ItemId, true)} x{item->Quantity}");
                    capturedRetainerState = Utils.GetCapturedInventoryState(Utils.RetainerInventoriesWithCrystals);
                    EzThrottler.Throttle("RetrieveAllTimeout", 5000, true);
                    P.Memory.RetainerItemCommandDetour(InventorySpaceManager.AgentRetainerItemCommandModule, (uint)i, type, 0, RetainerItemCommand.RetrieveFromRetainer);
                    return false;
                }
            }
        }
        //nothing left to retrieve
        return true;
    }
}
