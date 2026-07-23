using AutoRetainer.Internal;
using AutoRetainer.Internal.InventoryManagement;
using AutoRetainer.Modules.Voyage;
using ECommons.EzIpcManager;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace AutoRetainer.Modules.EzIPCManagers;
public class IPC_PluginState
{
    public IPC_PluginState()
    {
        EzIPC.Init(this, $"{Svc.PluginInterface.InternalName}.PluginState");
    }

    [EzIPC]
    public bool IsBusy()
    {
        return Utils.IsBusy;
    }

    [EzIPC]
    public Dictionary<ulong, HashSet<string>> GetEnabledRetainers()
    {
        return C.SelectedRetainers;
    }

    [EzIPC]
    public bool AreAnyRetainersAvailableForCurrentChara()
    {
        return Utils.AnyRetainersAvailableCurrentChara();
    }

    [EzIPC]
    public void AbortAllTasks()
    {
        P.TaskManager.Abort();
    }

    [EzIPC]
    public void DisableAllFunctions()
    {
        MultiMode.Enabled = false;
        SchedulerMain.DisablePlugin();
        VoyageScheduler.Enabled = false;
    }
    [EzIPC]
    public bool GetMultiModeStatus()
    {
        return MultiMode.Enabled;
    }

    [EzIPC]
    public void EnableMultiMode()
    {
        Svc.Commands.ProcessCommand("/autoretainer multi enable");
    }

    [EzIPC]
    public int GetInventoryFreeSlotCount()
    {
        return Utils.GetInventoryFreeSlotCount();
    }

    [EzIPC]
    public void EnqueueHET(Action onFailure)
    {
        TaskNeoHET.Enqueue(onFailure);
    }

    [EzIPC]
    public bool CanAutoLogin()
    {
        return Utils.CanAutoLogin();
    }

    [EzIPC]
    public bool Relog(string charaNameWithWorld)
    {
        if(Utils.CanAutoLogin())
        {
            var target = C.OfflineData.Where(x => $"{x.Name}@{x.World}" == charaNameWithWorld).FirstOrDefault();
            if(target != null)
            {
                MultiMode.Relog(target, out var err, RelogReason.Command);
                return err == null;
            }
        }
        return false;
    }

    [EzIPC]
    public bool GetOptionRetainerSense()
    {
        return C.RetainerSense;
    }

    [EzIPC]
    public void SetOptionRetainerSense(bool value)
    {
        C.RetainerSense = value;
    }

    [EzIPC]
    public int GetOptionRetainerSenseThreshold()
    {
        return C.RetainerSenseThreshold;
    }

    [EzIPC]
    public void SetOptionRetainerSenseThreshold(int value)
    {
        C.RetainerSenseThreshold = value;
    }

    [EzIPC]
    public long? GetClosestRetainerVentureSecondsRemaining(ulong CID)
    {
        if(C.SelectedRetainers.TryGetValue(CID, out var enabledRetainers))
        {
            if(C.OfflineData.TryGetFirst(x => x.CID == CID, out var data))
            {
                var selectedRetainers = data.GetEnabledRetainers().Where(z => z.HasVenture).OrderBy(z => z.GetVentureSecondsRemaining());
                if(selectedRetainers.Any()) return selectedRetainers.First().GetVentureSecondsRemaining();
            }
        }
        return null;
    }

    [EzIPC]
    public bool IsItemProtected(uint itemId)
    {
        return Data.GetIMSettings().IMProtectList.Contains(itemId);
    }

    /// <summary>Fires a single retrieve-from-retainer command for the first occupied slot found in the
    /// currently open retainer's item storage (items and crystals), into the player's own bags - never
    /// routes through the armoury chest, same as AutoRetainer's own entrust/vendor tasks. Deliberately
    /// does not wait for the retrieve to land before returning, unlike AutoRetainer's own throttled tasks -
    /// callers (e.g. an SND macro looping this) are expected to control their own pacing between calls, in
    /// exchange for real speed instead of the ~500ms+confirm-per-item pace the built-in tasks use.
    /// Returns false once nothing is left to retrieve, or the player's own inventory is nearly full.</summary>
    [EzIPC]
    public unsafe bool RetrieveNextRetainerItemSlot()
    {
        if(!InventorySpaceManager.IsRetainerInventoryLoaded()) return false;
        if(Utils.GetInventoryFreeSlotCount() <= C.MultiMinInventorySlots) return false;

        foreach(var type in Utils.RetainerInventoriesWithCrystals)
        {
            var inv = InventoryManager.Instance()->GetInventoryContainer(type);
            if(inv == null) continue;
            for(var i = 0; i < inv->Size; i++)
            {
                var item = inv->GetInventorySlot(i);
                if(item->ItemId != 0 && item->Quantity > 0)
                {
                    P.Memory.RetainerItemCommandDetour(InventorySpaceManager.AgentRetainerItemCommandModule, (uint)i, type, 0, RetainerItemCommand.RetrieveFromRetainer);
                    return true;
                }
            }
        }
        return false;
    }
}
