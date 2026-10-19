using AutoRetainer.Internal;
using AutoRetainer.Modules.Voyage.PartSwapper;

namespace AutoRetainer.Modules.Voyage.Tasks;

internal static unsafe class TaskChangeComponents
{
    // Deliberately no Abort flag here, unlike TaskRepairAll.Abort (set by the error-toast handler on the
    // "out of repair materials" message and read by VoyageScheduler.TryRepair): component swapping
    // has no equivalent error message to drive one. A step hanging is handled by abortOnTimeout:false
    // below plus VoyageMain's stuck-window watchdog.
    internal static string Name = "";
    internal static VoyageType Type = 0;
    internal static void EnqueueImmediate(List<(int, uint)> indexes, string vesselName, VoyageType type)
    {
        P.TaskManager.BeginStack();
        try
        {
            VoyageUtils.Log($"Task enqueued: {nameof(TaskChangeComponents)}");
            Name = vesselName;
            Type = type;
            P.TaskManager.Enqueue(PartSwapperTasks.SelectChangeComponents, "SelectChangeComponents");
            foreach(var index in indexes)
            {
                if(index.Item1 < 0 || index.Item1 > 3) throw new ArgumentOutOfRangeException(nameof(index));
                // P.TaskManager defaults to abortOnTimeout:true and Abort() clears the ENTIRE queue.
                // ChangeComponent returns false until the swap shows up in the offline data, so a slot that
                // cannot be swapped would otherwise take CloseChangeComponents down with it and leave
                // CompanyCraftSupply covering the voyage menu forever. Skipping one slot is benign: the follow-up
                // task only applies the new plan when every part already matches.
                P.TaskManager.Enqueue(() => PartSwapperTasks.ChangeComponent(index.Item1, index.Item2, Name), $"Change {index}", new(abortOnTimeout: false));
                P.TaskManager.EnqueueDelay(Utils.FrameDelay * 2, true);
            }
            P.TaskManager.Enqueue(PartSwapperTasks.CloseChangeComponents, "CloseChangeComponents");
        }
        catch(Exception e) { e.Log(); }
        P.TaskManager.InsertStack();
    }
}
