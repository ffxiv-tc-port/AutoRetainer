using AutoRetainer.Internal;
using AutoRetainer.Internal.InventoryManagement;
using AutoRetainer.Modules.Voyage;
using ECommons.EzIpcManager;
using ECommons.ExcelServices;
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

    /// <summary>A retrieve command that has been fired at a retainer slot but whose effect has not been
    /// observed yet. Identified by the slot contents at the moment the command was sent, so that any
    /// observable change to the slot counts as "the command resolved, re-evaluate this slot".</summary>
    private readonly record struct PendingRetrieve(uint ItemId, int Quantity, long SentAt);

    /// <summary>Slots that already had a retrieve command fired and have not been observed changing yet.
    /// Only ever touched from the framework thread (callers reach this through Dalamud IPC, and SND runs
    /// its Lua on the framework thread), so no locking.</summary>
    private readonly Dictionary<(InventoryType Type, int Slot), PendingRetrieve> PendingRetrieves = [];

    /// <summary>Which retainer <see cref="PendingRetrieves"/> belongs to, so entries can never leak across
    /// a retainer switch.</summary>
    private ulong PendingRetrievesRetainerId;

    /// <summary>Diagnostics for the current round, reported by <see cref="ResetRetainerRetrieveTracking"/>.</summary>
    private int PendingRetrievesFired;
    private int PendingRetrievesSkipped;
    private int PendingRetrievesRetried;

    /// <summary>How long a fired command may go unobserved before its slot is offered again. A command the
    /// server refuses outright never changes the slot, so without this the slot would be skipped forever
    /// and its item silently left behind. Deliberately generous: the server drains a burst of retrieves at
    /// roughly one slot per 0.13s, so anything shorter would start re-firing at slots that are merely
    /// queued and reintroduce the very amplification this tracking exists to remove.</summary>
    private const long PendingRetrieveStaleMs = 10000;

    /// <summary>Forgets which retainer slots already had a retrieve command fired at them, so the very next
    /// <see cref="RetrieveNextRetainerItemSlot"/> call considers every occupied slot again. Call this at the
    /// start of each sweep: anything the server refused (or dropped) is then re-offered immediately instead
    /// of waiting out the staleness timeout. Tracking also resets on its own when the retainer inventory
    /// closes or a different retainer is opened, so this is an optimisation, not a correctness requirement.</summary>
    [EzIPC]
    public void ResetRetainerRetrieveTracking()
    {
        if(PendingRetrievesFired > 0 || PendingRetrievesSkipped > 0)
        {
            PluginLog.Information($"[RetrieveNextRetainerItemSlot] Round ended: {PendingRetrievesFired} commands fired, {PendingRetrievesSkipped} duplicate calls suppressed, {PendingRetrievesRetried} slots re-offered after going stale, {PendingRetrieves.Count} still unobserved.");
        }
        ClearRetrieveTracking();
    }

    private void ClearRetrieveTracking()
    {
        PendingRetrieves.Clear();
        PendingRetrievesFired = 0;
        PendingRetrievesSkipped = 0;
        PendingRetrievesRetried = 0;
    }

    /// <summary>Fires a single retrieve-from-retainer command for the first occupied slot found in the
    /// currently open retainer's item storage (items and crystals), into the player's own bags - never
    /// routes through the armoury chest, same as AutoRetainer's own entrust/vendor tasks. Deliberately
    /// does not wait for the retrieve to land before returning, unlike AutoRetainer's own throttled tasks -
    /// callers (e.g. an SND macro looping this) are expected to control their own pacing between calls, in
    /// exchange for real speed instead of the ~500ms+confirm-per-item pace the built-in tasks use.
    ///
    /// Because the retrieve is a real server round trip (~0.13s per slot measured on TC) while callers poll
    /// far faster than that, the scan would otherwise keep finding the same not-yet-emptied slot and fire at
    /// it several times over - measured 2.48 commands per slot actually retrieved. So every fired slot is
    /// remembered and skipped until its contents are observed to change, it goes stale
    /// (<see cref="PendingRetrieveStaleMs"/>), or <see cref="ResetRetainerRetrieveTracking"/> is called.
    ///
    /// Returns false once nothing is left to retrieve, the player's own inventory is nearly full, or every
    /// remaining occupied slot already has a command in flight - in the last case the caller should let the
    /// retainer inventory settle, then start a fresh round rather than treating it as "done".</summary>
    [EzIPC]
    public unsafe bool RetrieveNextRetainerItemSlot()
    {
        if(!InventorySpaceManager.IsRetainerInventoryLoaded())
        {
            // The window closing also covers switching retainers - you cannot swap without closing it -
            // so anything we fired at the previous one is meaningless now.
            ClearRetrieveTracking();
            return false;
        }
        DropRetrieveTrackingIfRetainerChanged();
        if(Utils.GetInventoryFreeSlotCount() <= C.MultiMinInventorySlots) return false;

        var now = Environment.TickCount64;
        foreach(var type in Utils.RetainerInventoriesWithCrystals)
        {
            var inv = InventoryManager.Instance()->GetInventoryContainer(type);
            if(inv == null) continue;
            for(var i = 0; i < inv->Size; i++)
            {
                var item = inv->GetInventorySlot(i);
                if(item == null) continue;
                if(item->ItemId == 0 || item->Quantity <= 0)
                {
                    // Emptied - if we had fired at it, that command landed.
                    PendingRetrieves.Remove((type, i));
                    continue;
                }

                if(PendingRetrieves.TryGetValue((type, i), out var pending))
                {
                    if(pending.ItemId == item->ItemId && pending.Quantity == item->Quantity)
                    {
                        // Nothing observable has happened to this slot since we fired at it, so the command
                        // is still in flight - do not fire again. Unless it has gone stale, in which case
                        // assume the server refused it and offer the slot once more; skipping forever would
                        // silently leave the item on the retainer.
                        if(now - pending.SentAt < PendingRetrieveStaleMs)
                        {
                            PendingRetrievesSkipped++;
                            continue;
                        }
                        PendingRetrievesRetried++;
                    }
                    // Either it went stale, or the contents changed (a partial stack merge, say), which means
                    // the previous command resolved. Re-evaluate the slot from scratch.
                    PendingRetrieves.Remove((type, i));
                }

                // Unique items (rare-marked gear etc.) the player already owns one of
                // anywhere - bags, armoury, or equipped - can never be retrieved; the
                // game silently rejects it every single time. Skip these instead of
                // returning true and getting a caller stuck retrying the same slot
                // forever, since nothing ever moves.
                var data = ExcelItemHelper.Get(item->ItemId);
                if(data != null && data.Value.IsUnique && Utils.GetItemCount(Utils.PlayerEntireInventory, item->ItemId) > 0)
                {
                    continue;
                }

                // Snapshot before firing: the detour must be the last thing that touches this slot, and the
                // item pointer must not be read again afterwards.
                var pendingEntry = new PendingRetrieve(item->ItemId, item->Quantity, now);
                P.Memory.RetainerItemCommandDetour(InventorySpaceManager.AgentRetainerItemCommandModule, (uint)i, type, 0, RetainerItemCommand.RetrieveFromRetainer);
                PendingRetrieves[(type, i)] = pendingEntry;
                PendingRetrievesFired++;
                return true;
            }
        }
        return false;
    }

    private unsafe void DropRetrieveTrackingIfRetainerChanged()
    {
        var manager = RetainerManager.Instance();
        if(manager == null) return;
        var id = manager->LastSelectedRetainerId;
        if(id != PendingRetrievesRetainerId)
        {
            ClearRetrieveTracking();
            PendingRetrievesRetainerId = id;
        }
    }
}
