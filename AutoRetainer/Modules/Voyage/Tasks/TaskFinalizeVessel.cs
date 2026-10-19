using AutoRetainer.Internal;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace AutoRetainer.Modules.Voyage.Tasks;

internal static unsafe class TaskFinalizeVessel
{
    internal static void Enqueue(string name, VoyageType type, bool quit)
    {
        VoyageUtils.Log($"Task enqueued: {nameof(TaskFinalizeVessel)} name={name}, type={type}, quit={quit}");
        TaskSelectVesselByName.Enqueue(name, type);
        P.TaskManager.Enqueue(VoyageScheduler.WaitUntilFinalizeDeployAddonExists);
        // 🔑 結算收穫之前先送歸因提示。飛空艇不送：GilDelta 沒有對應的分類。
        if(type == VoyageType.Submersible)
            P.TaskManager.Enqueue(() => { GilDeltaIPC.HintSubmarineReturn(name); return true; }, "GilDeltaHintSubmarineReturn");
        P.TaskManager.Enqueue(VoyageScheduler.FinalizeVessel);
        P.TaskManager.Enqueue(() => TryGetAddonByName<AtkUnitBase>("SelectString", out var addon) && IsAddonReady(addon), "WaitForSelectStringAddon");
        TaskIntelligentRepair.Enqueue(name, type);
        if(quit) P.TaskManager.Enqueue(VoyageScheduler.SelectQuitVesselMenu);
    }
}
