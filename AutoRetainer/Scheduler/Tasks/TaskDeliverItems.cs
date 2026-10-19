using ECommons.GameHelpers;
using ECommons.IPC;
using ECommons.Singletons;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AutoRetainer.Scheduler.Tasks;
public static unsafe class TaskDeliverItems
{
    /// <returns>有沒有真的排進任務佇列。</returns>
    public static bool Enqueue()
    {
        var gcInfo = GCContinuation.GetGCInfo();
        if(gcInfo == null)
        {
            Notify.Error("Not employed by a Grand Company");
            return false;
        }
        if(ECommonsIPC.Lifestream.IsBusy())
        {
            Notify.Error("Lifestream is busy");
            return false;
        }
        if(Utils.IsBusy)
        {
            Notify.Error("AutoRetainer is busy");
            return false;
        }
        if(Vector3.Distance(gcInfo.Value.Position, Player.Position) > 1f)
        {
            P.TaskManager.Enqueue(() => ECommonsIPC.Lifestream.ExecuteCommand("gc " + Player.GrandCompany switch
            {
                ECommons.ExcelServices.GrandCompany.ImmortalFlames => "if",
                ECommons.ExcelServices.GrandCompany.Maelstrom => "m",
                ECommons.ExcelServices.GrandCompany.TwinAdder => "ta",
                _ => throw new ArgumentOutOfRangeException()
            }), "DeliverItems.TeleportToGrandCompany");
        }
        P.TaskManager.Enqueue(() => !ECommonsIPC.Lifestream.IsBusy(), "DeliverItems.WaitLifestreamIdle", new(timeLimitMS: 5 * 60 * 1000));
        P.TaskManager.Enqueue(() => GCContinuation.EnqueueInitiation(true), "DeliverItems.EnqueueInitiation");
        return true;
    }
}