using AutoRetainer.Internal.InventoryManagement;
using ECommons.GameHelpers;

namespace AutoRetainer.Scheduler.Tasks;

/// <summary>多角色模式收尾：把 <see cref="LowGearArmoryStow"/> 留在背包的裝備搬進兵裝庫。</summary>
/// <remarks>
/// 契約：不卡多角色模式。等不到可以搬的狀態最多 <see cref="ReadyWaitMs"/>、整段有時限且逾時照樣往下走；
/// 收尾那一顆任務一定跑，負責寫 D1，被拒時讓這個角色到遊戲或插件重開前都不再搬。
/// </remarks>
internal static unsafe class TaskStowLowGear
{
    private const long ReadyWaitMs = 15000;
    private static ArmoryStow.Batch Batch;
    private static long QueuedAt;
    private static string Context;

    internal static void Enqueue(string context)
    {
        P.TaskManager.Enqueue(() =>
        {
            Batch = null;
            QueuedAt = Environment.TickCount64;
            Context = context;
        }, "LowGearArmoryStow.Reset");
        P.TaskManager.Enqueue(Run, "LowGearArmoryStow.Run", new(timeLimitMS: 3 * 60 * 1000, abortOnTimeout: false));
        P.TaskManager.Enqueue(Finish, "LowGearArmoryStow.Finish");
    }

    private static bool? Run()
    {
        if(Batch == null)
        {
            var why = LowGearArmoryStow.WhyNotStow();
            if(why != null)
            {
                // 繳交模式不符的 Information 已由 LowGearArmoryStow 每個角色寫過一次。
                if(why == ArmoryStow.DeliveryModeNotUsingArmory) PluginLog.Debug($"[ArmoryStow] {LowGearArmoryStow.Owner} ({Context}): not moving this time ({why}).");
                else if(C.ArmoryStowLowGearEnabled) PluginLog.Information($"[ArmoryStow] {LowGearArmoryStow.Owner} ({Context}): not moving this time ({why}).");
                return true;
            }
            if(!Player.Available || IsOccupied() || !Utils.IsInventoryStateReadable())
            {
                if(Environment.TickCount64 - QueuedAt < ReadyWaitMs) return false;
                PluginLog.Information($"[ArmoryStow] {LowGearArmoryStow.Owner} ({Context}): still busy or the bags are unreadable after {ReadyWaitMs / 1000}s, not moving this time.");
                return true;
            }
            Batch = LowGearArmoryStow.NewBatch();
        }
        return ArmoryStow.Step(Batch);
    }

    private static bool? Finish()
    {
        var b = Batch;
        Batch = null;
        if(b == null) return true;
        if(b.State != ArmoryStow.BatchState.Done) b.EndReason = $"timed out while {b.State}";
        ArmoryStow.Report(b, Context);
        if(b.Rejected) LowGearArmoryStow.MarkRejected();
        return true;
    }
}
