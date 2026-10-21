using AutoRetainer.Scheduler.Handlers;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace AutoRetainer.Scheduler.Tasks;

public static unsafe class TaskAssignHuntingVenture
{
    public static void Enqueue(uint ventureId)
    {
        P.TaskManager.Enqueue(NewYesAlreadyManager.WaitForYesAlreadyDisabledTask);
        if(C.RetainerMenuDelay > 0)
        {
            TaskWaitSelectString.Enqueue(C.RetainerMenuDelay);
        }
        P.TaskManager.Enqueue(RetainerHandlers.SelectAssignVenture);
        P.TaskManager.Enqueue(() => RetainerHandlers.GenericSelectByName(Lang.HuntingVentureNames), $"GenericSelectByName({Lang.HuntingVentureNames})");
        P.TaskManager.Enqueue(RetainerHandlers.WaitForVentureListUpdate);
        P.TaskManager.EnqueueDelay(Utils.FrameDelay, true);
        P.TaskManager.Enqueue(() => OpenAssignVentureWindow(ventureId));
        P.TaskManager.Enqueue(RetainerHandlers.ClickAskAssign);
    }

    public static bool OpenAssignVentureWindow(uint ventureId)
    {
        if(!FrameThrottler.Check("OpenAssignVentureWindow")) return false;
        if(TryGetAddonByName<AtkUnitBase>("RetainerTaskAsk", out _)) return true;
        if(TryGetAddonByName<AtkUnitBase>("RetainerTaskSupply", out var addon) && addon->IsReady())
        {
            if(!TryGetListCount(addon, out var count))
            {
                rethrottle();
                return false;
            }
            for(var i = 0; i < count; i++)
            {
                if(!TryGetListedTaskId(addon, i, out var id)) continue;
                if(id == ventureId)
                {
                    // RetainerTaskSupply 按下不關(開出 RetainerTaskAsk 才 return true):帶參數組,同一列 15 幀內不重送。
                    // 比中就結束這一輪(不管有沒有送出),不要掉到下面「切等級清單」那條路去對同一扇窗送別的事件。
                    if(Utils.GenericThrottle && DialogGuards.TryPressOnce("RetainerTaskSupply", (nint)addon, "OpenAssignVentureWindow", $"Select{i}", escapeIsRoutine: true))
                    {
                        rethrottle();
                        Callback.Fire(addon, true, 5, i, Callback.ZeroAtkValue);
                    }
                    return false;
                }
            }
            if(Svc.Data.GetExcelSheet<RetainerTask>().TryGetRow(ventureId, out var task))
            {
                var level = task.RetainerLevel;
                var levelIndex = (level - 1) / 5;
                if(!AtkValueGuard.TryGet(addon, 40, "RetainerTaskSupply", out var levels))
                {
                    rethrottle();
                    return false;
                }
                var listIndex = levels.Int - levelIndex - 1;
                if(listIndex >= 0 && Utils.GenericThrottle && DialogGuards.TryPressOnce("RetainerTaskSupply", (nint)addon, "OpenAssignVentureWindow.Level", $"Level{listIndex}", escapeIsRoutine: true))
                {
                    rethrottle();
                    Callback.Fire(addon, true, 4, listIndex, Callback.ZeroAtkValue);
                    return false;
                }
            }
        }
        else
        {
            rethrottle();
        }

        return false;

        void rethrottle() => FrameThrottler.Throttle("OpenAssignVentureWindow", Utils.FrameDelay, true);
    }

    // RetainerTaskSupply：[107]＝清單筆數，[42+i]＝第 i 筆資料的指標，開頭 4 位元組是 RetainerTask 列號。
    private const int ListCountValue = 107;
    private const int ListFirstValue = 42;

    /// <summary>清單筆數，夾在 AtkValuesCount 之內（被夾掉時記一次）。讀不到回 false＝這一輪不做。</summary>
    internal static bool TryGetListCount(AtkUnitBase* addon, out int count)
    {
        count = 0;
        if(!AtkValueGuard.TryGetCount(addon, ListCountValue, "RetainerTaskSupply", out var listed)) return false;
        var available = Math.Max(0, addon->AtkValuesCount - ListFirstValue);
        count = (int)Math.Min(listed, (uint)available);
        if(listed > (uint)available) AtkValueGuard.ReportOnce("RetainerTaskSupply", ListFirstValue + available, "out of range", $"listed={listed} AtkValuesCount={addon->AtkValuesCount}");
        return true;
    }

    /// <summary>第 <paramref name="i"/> 筆的 RetainerTask 列號。該格型別不對或指標為空（遊戲也當合法狀態）時回 false＝跳過該格。</summary>
    internal static bool TryGetListedTaskId(AtkUnitBase* addon, int i, out uint id)
    {
        id = 0;
        if(!AtkValueGuard.TryGetPointer(addon, ListFirstValue + i, "RetainerTaskSupply", out var ptr)) return false;
        id = *(uint*)ptr;
        return true;
    }
}
