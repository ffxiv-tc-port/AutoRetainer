using AutoRetainer.Internal;
using ECommons.Throttlers;

namespace AutoRetainer.Modules.Voyage.Tasks;

internal static unsafe class TaskRepairAll
{
    internal static volatile bool Abort = false;
    internal static string Name = "";
    internal static VoyageType Type = 0;
    internal static void EnqueueImmediate(List<int> indexes, string vesselName, VoyageType type)
    {
        P.TaskManager.BeginStack();
        try
        {
            VoyageUtils.Log($"Task enqueued: {nameof(TaskRepairAll)}");
            Name = vesselName;
            Type = type;
            Abort = false;
            var vesselIndex = VoyageUtils.GetVesselIndexByName(vesselName, type);
            P.TaskManager.Enqueue(() => Utils.TrySelectSpecificEntry(Lang.WorkshopRepair, () => EzThrottler.Throttle("RepairAllSelectRepair")), "RepairAllSelectRepair");
            foreach(var index in indexes)
            {
                if(index < 0 || index > 3) throw new ArgumentOutOfRangeException(nameof(index));
                P.TaskManager.Enqueue(() => VoyageScheduler.TryRepair(index), $"Repair {index}");
                P.TaskManager.Enqueue(() => Abort || VoyageScheduler.WaitForYesNoDisappear() == true, "WaitForYesNoDisappear", new(timeLimitMS: 5000, abortOnTimeout: false));
                // abortOnTimeout must stay false here for the same reason as the line above it:
                // P.TaskManager defaults to abortOnTimeout:true and Abort() clears the ENTIRE queue,
                // so one slot failing to report a repaired condition would also discard the trailing
                // CloseRepair step and leave CompanyCraftSupply/AirShipPartsMenu covering the voyage
                // menu forever (GetCurrentWorkshopPanelType only recognises SelectString, so the
                // scheduler then ticks without doing anything and without reporting an error).
                // Skipping one slot instead is harmless - the vessel simply stays damaged and the
                // next repair pass picks it up again.
                P.TaskManager.Enqueue(() => Abort || VoyageUtils.GetVesselComponent(vesselIndex, type, index)->Condition > 0, "WaitUntilRepairComplete", new(abortOnTimeout: false));
                P.TaskManager.EnqueueDelay(Utils.FrameDelay * 2, true);
            }
            P.TaskManager.Enqueue(VoyageScheduler.CloseRepair);
        }
        catch(Exception e) { e.Log(); }
        P.TaskManager.InsertStack();
        //P.TaskManager.Enqueue(() => Abort ? VoyageScheduler.SelectQuitVesselMenu() : true, "SelectQuitVesselMenu failed repair");
    }
}
