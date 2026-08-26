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
        // 🔴 原本下面六個步驟的閘門 HasGil 是「每一步都現讀一次金幣」，而
        // GetInventoryItemCount 在換區與剛登入的短暫視窗內會整片回 0
        // （ICE 實機事故同形狀：使用者身上有 999 個餌，BetweenAreas 那一毫秒讀到 0，
        //   流程就自己判定「沒餌了」而中止；觸發源是機甲行動把人傳走，跟該流程毫無關係）。
        // HasGil == false 的語意是「跳過這一步並回報成功」，所以只要假性歸零發生在鏈的
        // 中途，SelectEntrustGil 已經把 Bank 視窗打開了，後面的 SwapBankMode／設定金額／
        // 收尾的 ProcessBankOrCancel 會被一起跳過 —— 關窗那一步沒了，Bank 視窗就留在畫面上，
        // 後續整條雇員流程都會對著非預期的 modal 空轉。
        // 修法照抄同資料夾的 TaskWithdrawGil：把判斷 latch 一次而不是每步現讀，
        // 而且那一次讀取要等背包容器真的讀得到才做 —— 讀不到就回 false，
        // 這一幀不做決定、下一幀重來（TaskManager 有 20 秒上限會自行中止，不會卡死）。
        P.TaskManager.Enqueue(() =>
        {
            if(!Utils.IsInventoryStateReadable()) return false;
            hasGilInt = Gil > 0;
            return true;
        });
        if(C.RetainerMenuDelay > 0)
        {
            TaskWaitSelectString.Enqueue(C.RetainerMenuDelay);
        }
        P.TaskManager.Enqueue(() => HasGil == false ? true : RetainerHandlers.SelectEntrustGil());
        P.TaskManager.Enqueue(() => HasGil == false ? true : GenericHandlers.Throttle(500));
        P.TaskManager.Enqueue(() => HasGil == false ? true : GenericHandlers.WaitFor(500));
        P.TaskManager.Enqueue(() => HasGil == false ? true : RetainerHandlers.SwapBankMode());
        P.TaskManager.Enqueue(() => HasGil == false ? true : depFunc(percent));
        P.TaskManager.Enqueue(() => HasGil == false ? true : RetainerHandlers.ProcessBankOrCancel());
        P.TaskManager.Enqueue(() => { forceCheck = false; return true; });
    }
}
