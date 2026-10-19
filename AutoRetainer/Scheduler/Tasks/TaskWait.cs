using AutoRetainer.Scheduler.Handlers;

namespace AutoRetainer.Scheduler.Tasks;

internal static class TaskWait
{
    internal static void Enqueue(int ms)
    {
        P.TaskManager.Enqueue(() => GenericHandlers.Throttle(ms), "Wait.Throttle");
        P.TaskManager.Enqueue(() => GenericHandlers.WaitFor(ms), "Wait.WaitFor");
    }
}
