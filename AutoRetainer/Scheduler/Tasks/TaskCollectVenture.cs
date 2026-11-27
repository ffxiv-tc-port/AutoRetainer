using AutoRetainer.Scheduler.Handlers;
using AutoRetainer.Modules.Statistics;

namespace AutoRetainer.Scheduler.Tasks;

internal static class TaskCollectVenture
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
        P.TaskManager.Enqueue(() => RetainerHandlers.EnforceSelectString(RetainerHandlers.SelectViewVentureReport), "EnforceSelectString/SelectViewVentureReport");
        P.TaskManager.Enqueue(RetainerHandlers.ClickResultConfirm);
        P.TaskManager.Enqueue(VentureUniqueDiag.Flush, "VentureUniqueDiag.Flush");
    }
}
