using AutoRetainer.Scheduler.Handlers;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace AutoRetainer.Scheduler.Tasks;

internal static unsafe class TaskDepositGil
{
    private static bool hasGilInt = false;
    internal static bool forceCheck = false;

    private static bool HasGil => hasGilInt || forceCheck;
    internal static int Gil => InventoryManager.Instance()->GetInventoryItemCount(1);
    internal static void Enqueue(int percent, bool isGilAmount = false)
    {
        Func<int, bool?> depFunc = isGilAmount ? RetainerHandlers.SetDepositGilAmountExact : RetainerHandlers.SetDepositGilAmount;
        hasGilInt = false;
        P.TaskManager.Enqueue(NewYesAlreadyManager.WaitForYesAlreadyDisabledTask);
        // 🔴 HasGil 只 latch 一次、不每一步現讀：GetInventoryItemCount 在換區與剛登入的短暫視窗內會整片回 0，
        // 而 HasGil == false 的語意是「跳過這一步並回報成功」，中途變 false 的話關窗那一步就沒了。
        // 讀不到就回 false，這一幀不做決定、下一幀重來（與 TaskWithdrawGil 同一個做法）。
        P.TaskManager.Enqueue(() =>
        {
            if(!Utils.IsInventoryStateReadable()) return false;
            hasGilInt = Gil > 0;
            return true;
        }, "DepositGil.ReadWalletGil");
        if(C.RetainerMenuDelay > 0)
        {
            TaskWaitSelectString.Enqueue(C.RetainerMenuDelay);
        }
        P.TaskManager.Enqueue(() => HasGil == false ? true : RetainerHandlers.SelectEntrustGil(), "DepositGil.SelectEntrustGil");
        P.TaskManager.Enqueue(() => HasGil == false ? true : GenericHandlers.Throttle(500), "DepositGil.Throttle500");
        P.TaskManager.Enqueue(() => HasGil == false ? true : GenericHandlers.WaitFor(500), "DepositGil.WaitFor500");
        P.TaskManager.Enqueue(() => HasGil == false ? true : RetainerHandlers.SwapBankMode(), "DepositGil.SwapBankMode");
        P.TaskManager.Enqueue(() => HasGil == false ? true : depFunc(percent), "DepositGil.SetAmount");
        // 🔑 歸因提示必須早於錢包變動；自成一步是因為 ProcessBankOrCancel 會被重試很多次。
        P.TaskManager.Enqueue(() => { if(HasGil) GilDeltaIPC.HintRetainerDeposit(); return true; }, "GilDeltaHintDeposit");
        P.TaskManager.Enqueue(() => HasGil == false ? true : RetainerHandlers.ProcessBankOrCancel(), "DepositGil.ProcessBankOrCancel");
        P.TaskManager.Enqueue(() => { forceCheck = false; return true; }, "DepositGil.ClearForceCheck");
    }
}
