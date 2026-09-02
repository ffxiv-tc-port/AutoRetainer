using AutoRetainer.Internal;
using AutoRetainerAPI.Configuration;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace AutoRetainer.Modules.Voyage.Tasks;

internal static unsafe class TaskDeployOnBestExpVoyage
{
    internal static void Enqueue(string name, VoyageType type, SubmarineUnlockPlan unlock = null)
    {
        VoyageUtils.Log($"Task enqueued: {nameof(TaskCalculateAndPickBestExpRoute)} (plan: {unlock})");
        TaskIntelligentRepair.Enqueue(name, type);
        P.TaskManager.Enqueue(SelectDeploy);
        TaskCalculateAndPickBestExpRoute.Enqueue(unlock);
        P.TaskManager.Enqueue(Deploy);
        TaskDeployAndSkipCutscene.Enqueue(true);
    }

    internal static bool? SelectDeploy()
    {
        return Utils.TrySelectSpecificEntry(Lang.DeployOnSubaquaticVoyage, () => Utils.GenericThrottle && EzThrottler.Throttle("Voyage.SelectDeploy", 1000));
    }

    internal static bool? Deploy()
    {
        {
            if(TryGetAddonByName<AtkUnitBase>("AirShipExplorationDetail", out _)) return true;
        }
        {
            if(TryGetAddonByName<AtkUnitBase>("AirShipExploration", out var addon) && IsAddonReady(addon))
            {
                // 🔴 NodeList[2] 原本上界與元素都沒驗;GetAsAtkComponentButton() 是 [MemberFunction],
                //    對 null 節點呼叫等於把 this = 0 交給遊戲原生碼。
                //    取不到 → button 為 null → IsButtonEnabled 回 false → 走既有的「按鈕還沒能按」
                //    重試路徑(節流 500ms 後再來),不會誤觸發出航。
                var node = Utils.GetNodeSafe(&addon->UldManager, 2);
                var button = node == null ? null : node->GetAsAtkComponentButton();
                if(!Utils.IsButtonEnabled(button))
                {
                    EzThrottler.Throttle("Voyage.Deploy", 500, true);
                    return false;
                }
                else
                {
                    // 出航鈕按下後 Detail 開出前這扇窗是否關閉未證:帶參數組走 15 幀例行逃生口,補按仍受 500ms 節流。
                    if(Utils.GenericThrottle && EzThrottler.Throttle("Voyage.Deploy") && DialogGuards.TryPressOnce("AirShipExploration", (nint)addon, "Voyage.DeployButton", "Deploy", escapeIsRoutine: true))
                    {
                        Callback.Fire(addon, true, 0);
                        return false;
                    }
                }
            }
            else
            {
                Utils.RethrottleGeneric();
            }
        }
        return false;
    }
}
