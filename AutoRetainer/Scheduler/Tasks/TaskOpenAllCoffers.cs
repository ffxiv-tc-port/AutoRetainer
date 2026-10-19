using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace AutoRetainer.Scheduler.Tasks;

public static unsafe class TaskOpenAllCoffers
{
    public static void Enqueue()
    {
        P.TaskManager.Enqueue(RecursivelyOpenCoffers, new(timeLimitMS: 10 * 60 * 1000, abortOnTimeout: false));
        P.TaskManager.Enqueue(() => Utils.AnimationLock == 0);
    }

    public static bool? RecursivelyOpenCoffers()
    {
        var invManager = InventoryManager.Instance();
        if(invManager->GetInventoryItemCount(32161) == 0)
        {
            return true;
        }
        if(Utils.GetInventoryFreeSlotCount() < Math.Max(5, C.UIWarningRetSlotNum))
        {
            return true;
        }
        if(ActionManager.Instance()->GetActionStatus(ActionType.Item, 32161) == 0 && Utils.AnimationLock == 0)
        {
            if(Utils.GenericThrottle && EzThrottler.Throttle("AutoOpenCoffers", 1000))
            {
                OpenCoffer();
            }
        }
        else
        {
            Utils.RethrottleGeneric();
        }
        return false;
    }

    public static void OpenCoffer()
    {
        // AgentInventoryContext.Instance() 是產生器產出的兩層可空取得器，合法回 null。
        // 拿不到就這次不開 —— 呼叫端每秒節流重試，下一輪還會再來。
        var ctx = AgentInventoryContext.Instance();
        if(ctx == null) return;
        ctx->UseItem(32161, (InventoryType)0x270F, 0, 0);
    }

}
