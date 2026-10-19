using AutoRetainer.Scheduler.Handlers;

namespace AutoRetainer.Scheduler.Tasks;

internal static class TaskWithdrawGil
{
    private static bool hasGilInt = false;
    internal static bool forceCheck = false;

    private static bool HasGil => hasGilInt || forceCheck;
    internal static void Enqueue(int percent)
    {
        hasGilInt = false;
        P.TaskManager.Enqueue(NewYesAlreadyManager.WaitForYesAlreadyDisabledTask);
        P.TaskManager.Enqueue(() =>
        {
            var g = CurrentRetainerHasGil();
            if(g != null)
            {
                hasGilInt = g.Value;
                return true;
            }
            return false;
        }, "WithdrawGil.ReadRetainerGil");
        if(C.RetainerMenuDelay > 0)
        {
            TaskWaitSelectString.Enqueue(C.RetainerMenuDelay);
        }
        P.TaskManager.Enqueue(() => HasGil == false ? true : RetainerHandlers.SelectEntrustGil(), "WithdrawGil.SelectEntrustGil");
        P.TaskManager.Enqueue(() => HasGil == false ? true : GenericHandlers.Throttle(500), "WithdrawGil.Throttle500");
        P.TaskManager.Enqueue(() => HasGil == false ? true : GenericHandlers.WaitFor(500), "WithdrawGil.WaitFor500");
        P.TaskManager.Enqueue(() => HasGil == false ? true : RetainerHandlers.SetWithdrawGilAmount(percent), "WithdrawGil.SetAmount");
        // 🔑 歸因提示必須早於錢包變動：順序反了那筆錢已經被 GilDelta 用推論規則歸完類了。
        //    自成一步而不是塞進下一步：ProcessBankOrCancel 會被重試很多次，提示只送一次。
        P.TaskManager.Enqueue(() => { if(HasGil) GilDeltaIPC.HintRetainerWithdraw(); return true; }, "GilDeltaHintWithdraw");
        P.TaskManager.Enqueue(() => HasGil == false ? true : RetainerHandlers.ProcessBankOrCancel(), "WithdrawGil.ProcessBankOrCancel");
        P.TaskManager.Enqueue(() => { forceCheck = false; return true; }, "WithdrawGil.ClearForceCheck");
    }

    private static bool? CurrentRetainerHasGil()
    {
        if(Utils.TryGetCurrentRetainer(out var name) && Utils.TryGetRetainerByName(name, out var ret))
        {
            return ret.Gil > 0;
        }
        return null;
    }
}
