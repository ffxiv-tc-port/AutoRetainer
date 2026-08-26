using AutoRetainer.Internal;
using AutoRetainer.Modules.Voyage.PartSwapper;

namespace AutoRetainer.Modules.Voyage.Tasks;

internal static unsafe class TaskChangeComponents
{
    // NOTE: this class used to declare a volatile Abort flag mirroring TaskRepairAll.Abort, but
    // nothing ever read it and nothing ever set it - the safety valve was copied without being
    // wired up, which is worse than not having one because it reads like protection that exists.
    // TaskRepairAll.Abort is real: Toasts_ErrorToast sets it on the "out of repair materials"
    // message and VoyageScheduler.TryRepair reads it. There is no equivalent error message for
    // component swapping, so the flag has been removed rather than given a fake writer. The
    // failure it was presumably meant to cover (a step hanging and killing the queue) is handled
    // by abortOnTimeout:false below plus VoyageMain's stuck-window watchdog.
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
                // ChangeComponent returns false until the swap shows up in the offline data, so any
                // slot that cannot be swapped (part gone from the inventory, ContextIconMenu entry
                // not matching) used to take CloseChangeComponents down with it and leave
                // CompanyCraftSupply covering the voyage menu forever. Skipping one slot is benign:
                // PartSwapperScheduler's follow-up task only applies the new plan when every part
                // already matches, so a partial swap simply leaves the previous plan in place.
                P.TaskManager.Enqueue(() => PartSwapperTasks.ChangeComponent(index.Item1, index.Item2, Name), $"Change {index}", new(abortOnTimeout: false));
                P.TaskManager.EnqueueDelay(Utils.FrameDelay * 2, true);
            }
            P.TaskManager.Enqueue(PartSwapperTasks.CloseChangeComponents, "CloseChangeComponents");
        }
        catch(Exception e) { e.Log(); }
        P.TaskManager.InsertStack();
    }
}
