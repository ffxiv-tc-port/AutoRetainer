using AutoRetainer.Scheduler.Handlers;
using AutoRetainer.Modules.Statistics;

namespace AutoRetainer.Scheduler.Tasks;

internal static class TaskReassignVenture
{
    internal static void Enqueue()
    {
        P.TaskManager.Enqueue(NewYesAlreadyManager.WaitForYesAlreadyDisabledTask);
        if(C.RetainerMenuDelay > 0)
        {
            TaskWaitSelectString.Enqueue(C.RetainerMenuDelay);
        }
        P.TaskManager.Enqueue(VentureUniqueDiag.TakeSnapshot, "VentureUniqueDiag.TakeSnapshot");
        P.TaskManager.Enqueue(RetainerHandlers.SelectViewVentureReport, RetainerHandlers.ViewVentureReportConf);
        P.TaskManager.Enqueue(() => RetainerHandlers.EnforceSelectString(RetainerHandlers.SelectViewVentureReport));
        P.TaskManager.Enqueue(RetainerHandlers.ClickResultReassign);
        P.TaskManager.Enqueue(VentureUniqueDiag.Flush, "VentureUniqueDiag.Flush");
        P.TaskManager.Enqueue(RetainerHandlers.WaitForVentureListUpdate);
        P.TaskManager.EnqueueDelay(Utils.FrameDelay, true);
        //P.TaskManager.DelayNext(10, true);
        P.TaskManager.Enqueue(RetainerHandlers.ClickAskAssign);
    }
}
