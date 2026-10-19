using AutoRetainer.Scheduler.Handlers;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace AutoRetainer.Scheduler.Tasks;

internal static unsafe class TaskWaitSelectString
{
    internal static void Enqueue(int ms)
    {
        P.TaskManager.Enqueue(() => { return TryGetAddonByName<AtkUnitBase>("SelectString", out _); }, "WaitSelectString.WaitAddon");
        P.TaskManager.Enqueue(() => GenericHandlers.Throttle(ms), "WaitSelectString.Throttle");
        P.TaskManager.Enqueue(() => GenericHandlers.WaitFor(ms), "WaitSelectString.WaitFor");
    }
}
