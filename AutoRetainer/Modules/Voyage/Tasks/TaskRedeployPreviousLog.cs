using AutoRetainer.Internal;
using AutoRetainerAPI.Configuration;
using Dalamud.Memory;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace AutoRetainer.Modules.Voyage.Tasks;

internal static unsafe class TaskRedeployPreviousLog
{
    internal static void Enqueue(string name, VoyageType type)
    {
        VoyageUtils.Log($"Task enqueued: {nameof(TaskRedeployPreviousLog)}");
        TaskIntelligentRepair.Enqueue(name, type);
        P.TaskManager.Enqueue(VoyageScheduler.SelectViewPreviousLog);
        P.TaskManager.Enqueue(VoyageScheduler.WaitUntilFinalizeDeployAddonExists);
        // 🔑 「再派出」也會把這一趟的收穫結算掉，所以跟「收回」同樣要送提示。
        if(type == VoyageType.Submersible)
            P.TaskManager.Enqueue(() => { GilDeltaIPC.HintSubmarineReturn(name); return true; }, "GilDeltaHintSubmarineReturn");
        P.TaskManager.Enqueue(VoyageScheduler.RedeployVessel);
        P.TaskManager.EnqueueDelay(10, true);
        P.TaskManager.Enqueue(CheckForFuel);
        P.TaskManager.Enqueue(VoyageScheduler.DeployVessel);
        P.TaskManager.Enqueue(VoyageScheduler.WaitForCutscene);
        P.TaskManager.Enqueue(VoyageScheduler.WaitForNoCutscene);
    }

    internal static bool? CheckForFuel()
    {
        if(TryGetAddonByName<AtkUnitBase>("AirShipExplorationDetail", out var addon) && IsAddonReady(addon))
        {
            if(AtkValueGuard.TryGetUtf8String(addon, 1, "AirShipExplorationDetail", out var fuel))
            {
                var values = MemoryHelper.ReadStringNullTerminated(fuel).Split("/");
                if(values.Length == 2 && uint.TryParse(values[0], out var req) && uint.TryParse(values[1], out var have))
                {
                    if(req > have)
                    {
                        P.TaskManager.Abort();
                        DuoLog.Warning($"[Voyage] You are out of fuel!");
                        if(C.FailureNoFuel == WorkshopFailAction.ExcludeChar)
                        {
                            Data.WorkshopEnabled = false;
                        }
                        else if(C.FailureNoFuel == WorkshopFailAction.StopPlugin)
                        {
                            MultiMode.SetEnabled(false, "潛水艇：燃料不足（設定為停止）", MultiModeChangeCause.AutoRetainer);
                            VoyageScheduler.Enabled = false;
                        }
                        P.TaskManager.BeginStack();
                        P.TaskManager.Enqueue(VoyageScheduler.CancelDeployVessel);
                        P.TaskManager.Enqueue(VoyageScheduler.WaitUntilFinalizeDeployAddonExists);
                        P.TaskManager.Enqueue(VoyageScheduler.FinalizeVessel);
                        P.TaskManager.Enqueue(VoyageScheduler.SelectQuitVesselMenu);
                        P.TaskManager.Enqueue(VoyageScheduler.SelectQuitVesselSelectorMenu);
                        P.TaskManager.Enqueue(VoyageScheduler.SelectExitMainPanel);
                        P.TaskManager.InsertStack();
                    }
                    else
                    {
                        return true;
                    }
                }
            }
        }
        return false;
    }
}
