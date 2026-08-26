using AutoRetainer.Internal;
using AutoRetainer.Modules.Voyage.VoyageCalculator;
using AutoRetainerAPI.Configuration;

namespace AutoRetainer.Modules.Voyage.Tasks;

internal static unsafe class TaskDeployOnPointPlan
{
    internal static void Enqueue(string name, VoyageType type, SubmarinePointPlan unlock)
    {
        VoyageUtils.Log($"Task enqueued: {nameof(TaskDeployOnPointPlan)} (plan: {unlock})");
        TaskIntelligentRepair.Enqueue(name, type);
        P.TaskManager.Enqueue(TaskDeployOnBestExpVoyage.SelectDeploy);
        EnqueuePick(unlock);
        P.TaskManager.Enqueue(TaskDeployOnBestExpVoyage.Deploy);
        TaskDeployAndSkipCutscene.Enqueue(true);
    }
    internal static void EnqueuePick(SubmarinePointPlan unlock)
    {
        P.TaskManager.Enqueue(() => PickFromPlan(unlock), $"PickFromPlan({unlock})");
    }

    internal static void PickFromPlan(SubmarinePointPlan unlock)
    {
        // 這艘潛水艇的航行距離跑不完整份計畫時，從清單尾端往前砍到跑得完為止。
        // 未啟用、或任何一步算不出來時 GetEffectivePoints 會原樣回傳 unlock.Points（＝維持現狀）。
        var points = PointPlanRange.GetEffectivePoints(unlock, log: true);
        VoyageUtils.Log($"points: {points.Select(x => $"{x}").Join("\n")}");
        TaskPickSubmarineRoute.EnqueueImmediate(unlock.GetMapId(), points.ToArray());
    }
}
