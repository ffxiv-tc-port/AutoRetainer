using AutoRetainer.Internal;
using AutoRetainer.Internal.InventoryManagement;
using AutoRetainer.Modules.GcHandin;
using AutoRetainer.Modules.Voyage;
using AutoRetainer.Scheduler.Handlers;
using AutoRetainer.Scheduler.Tasks;
using ECommons.EzIpcManager;
using ECommons.ExcelServices;
using ECommons.GameHelpers;
using ECommons.Throttlers;
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
            var inv = TryGetReadableContainer(type);
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

    #region Retrieve a specific item

    /// <summary>The retainer's containers cannot be walked right now, so nothing at all can be concluded -
    /// in particular this is <b>not</b> "the retainer does not have that item". Retainer inventory is only
    /// populated once the window has actually been opened, so a caller that has just switched retainers will
    /// see this until the data lands. Retry, or fall back to driving the UI.</summary>
    private const int RetrieveResultRetainerUnavailable = -1;

    /// <summary>Every matching slot already has a retrieve command in flight that has not been observed
    /// landing yet. The item <b>is</b> there - let it settle and call again, do not treat this as done.</summary>
    private const int RetrieveResultCommandInFlight = -2;

    /// <summary>The player's own bags are at or below AutoRetainer's configured reserve
    /// (<see cref="Config.MultiMinInventorySlots"/>), so nothing was retrieved.</summary>
    private const int RetrieveResultInventoryFull = -3;

    /// <summary>Found, but it is a unique item the player already owns a copy of somewhere (bags, armoury or
    /// equipped). The game refuses these silently and forever, so no command was sent.</summary>
    private const int RetrieveResultBlockedUnique = -4;

    /// <summary>Found, but only in the crystal container, and the caller did not ask for crystals. Distinct
    /// from "not present" on purpose - reporting 0 here would tell the caller the retainer is out of an item
    /// it is actually holding.</summary>
    private const int RetrieveResultInCrystals = -5;

    /// <summary>The retainer's containers were readable end to end and hold no such item.</summary>
    private const int RetrieveResultNotPresent = 0;

    /// <summary>Version of the specific-item retrieve surface below
    /// (<see cref="RetrieveRetainerItemSlotById"/> / <see cref="GetOpenRetainerItemQuantity"/>). Present from
    /// version 1 onwards; consumers should treat "the IPC call itself throws" as "not supported, use the UI
    /// path" and only rely on the methods below once this returns a version they understand.</summary>
    [EzIPC]
    public int GetRetainerItemRetrieveApiVersion() => 1;

    /// <summary>Fires one retrieve-from-retainer command at the first slot of the currently open retainer
    /// that holds <paramref name="itemId"/>, into the player's own bags. Same command path, same pacing
    /// characteristics and the same in-flight tracking as <see cref="RetrieveNextRetainerItemSlot"/> - the
    /// only difference is which slot gets picked. Call <see cref="ResetRetainerRetrieveTracking"/> when
    /// starting a fresh sweep.
    ///
    /// <para>The underlying command has no "retrieve N" form that does not go through the game's own quantity
    /// dialog, so this always takes the <b>whole slot</b>. The return value says how many that was, which is
    /// how a caller that wanted fewer finds out it got more.</para></summary>
    /// <param name="itemId">Item to look for.</param>
    /// <param name="hqOnly">Only match high-quality stacks. False matches either quality, which is what a
    /// caller restocking crafting materials wants.</param>
    /// <param name="includeCrystals">Whether the crystal container may be retrieved from. Off by default for
    /// callers because crystals are the one category the game is known to always ask a quantity for when
    /// entrusting, and an unanswered quantity dialog would stall the caller's loop; whether retrieving
    /// behaves the same has not been confirmed on this client.</param>
    /// <returns>The quantity the fired command was aimed at (always >= 1) when a command was sent, otherwise
    /// one of <see cref="RetrieveResultNotPresent"/> (0),
    /// <see cref="RetrieveResultRetainerUnavailable"/> (-1), <see cref="RetrieveResultCommandInFlight"/> (-2),
    /// <see cref="RetrieveResultInventoryFull"/> (-3), <see cref="RetrieveResultBlockedUnique"/> (-4) or
    /// <see cref="RetrieveResultInCrystals"/> (-5). 🔴 0 and -1 are deliberately different values: 0 means
    /// "proved absent", -1 means "could not look". Collapsing them into one falsey answer is how a caller
    /// ends up silently skipping a retainer that was merely still loading.</returns>
    [EzIPC]
    public unsafe int RetrieveRetainerItemSlotById(uint itemId, bool hqOnly, bool includeCrystals)
    {
        if(itemId == 0) return RetrieveResultNotPresent;
        if(!InventorySpaceManager.IsRetainerInventoryLoaded())
        {
            // The window closing also covers switching retainers - you cannot swap without closing it -
            // so anything we fired at the previous one is meaningless now.
            ClearRetrieveTracking();
            ReportRetainerWindowClosed(nameof(RetrieveRetainerItemSlotById));
            return RetrieveResultRetainerUnavailable;
        }
        DropRetrieveTrackingIfRetainerChanged();
        if(Utils.GetInventoryFreeSlotCount() <= C.MultiMinInventorySlots) return RetrieveResultInventoryFull;

        var now = Environment.TickCount64;
        // Every "why not" below is remembered rather than returned straight away, because a later container
        // may still hold a slot we can actually fire at; only once the whole walk comes up empty do these
        // decide what the caller is told.
        var unreadable = false;
        var inFlight = false;
        var blockedUnique = false;
        var inCrystals = false;

        foreach(var type in Utils.RetainerInventoriesWithCrystals)
        {
            var inv = TryGetReadableContainer(type);
            if(inv == null)
            {
                unreadable = true;
                ReportUnreadableRetainerContainer(type);
                continue;
            }
            for(var i = 0; i < inv->Size; i++)
            {
                var item = inv->GetInventorySlot(i);
                if(item == null)
                {
                    unreadable = true;
                    continue;
                }
                if(item->ItemId == 0 || item->Quantity <= 0)
                {
                    // Emptied - if we had fired at it, that command landed.
                    PendingRetrieves.Remove((type, i));
                    continue;
                }
                if(item->ItemId != itemId) continue;
                if(hqOnly && !item->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality)) continue;
                if(type == InventoryType.RetainerCrystals && !includeCrystals)
                {
                    inCrystals = true;
                    continue;
                }

                if(PendingRetrieves.TryGetValue((type, i), out var pending))
                {
                    if(pending.ItemId == item->ItemId && pending.Quantity == item->Quantity)
                    {
                        // Nothing observable has happened to this slot since we fired at it, so the command
                        // is still in flight - do not fire again. Unless it has gone stale, in which case
                        // assume the server refused it and offer the slot once more.
                        if(now - pending.SentAt < PendingRetrieveStaleMs)
                        {
                            PendingRetrievesSkipped++;
                            inFlight = true;
                            continue;
                        }
                        PendingRetrievesRetried++;
                    }
                    PendingRetrieves.Remove((type, i));
                }

                // Unique items the player already owns one of can never be retrieved; the game silently
                // rejects it every single time, so firing would just get the caller stuck on this slot.
                var data = ExcelItemHelper.Get(item->ItemId);
                if(data != null && data.Value.IsUnique && Utils.GetItemCount(Utils.PlayerEntireInventory, item->ItemId) > 0)
                {
                    blockedUnique = true;
                    continue;
                }

                // Snapshot before firing: the detour must be the last thing that touches this slot, and the
                // item pointer must not be read again afterwards.
                var quantity = item->Quantity;
                var pendingEntry = new PendingRetrieve(item->ItemId, item->Quantity, now);
                P.Memory.RetainerItemCommandDetour(InventorySpaceManager.AgentRetainerItemCommandModule, (uint)i, type, 0, RetainerItemCommand.RetrieveFromRetainer);
                PendingRetrieves[(type, i)] = pendingEntry;
                PendingRetrievesFired++;
                return quantity;
            }
        }

        if(inFlight) return RetrieveResultCommandInFlight;
        if(blockedUnique) return RetrieveResultBlockedUnique;
        if(inCrystals) return RetrieveResultInCrystals;
        // 🔴 Must come last and must not be folded into "not present": a container we could not walk cannot
        // be used to prove the item is absent.
        if(unreadable) return RetrieveResultRetainerUnavailable;
        return RetrieveResultNotPresent;
    }

    /// <summary>How many of <paramref name="itemId"/> the currently open retainer is holding, for callers
    /// that need to know when to stop asking.</summary>
    /// <returns>The total quantity (0 meaning "proved absent"), or
    /// <see cref="RetrieveResultRetainerUnavailable"/> (-1) when any part of the retainer's storage could not
    /// be walked. ⚠️ -1 is "unknown", not "none" - a partial total is deliberately not returned, because a
    /// number that is silently too low would make a caller finish early and leave items behind.</returns>
    [EzIPC]
    public unsafe int GetOpenRetainerItemQuantity(uint itemId, bool hqOnly, bool includeCrystals)
    {
        if(itemId == 0) return 0;
        if(!InventorySpaceManager.IsRetainerInventoryLoaded())
        {
            ReportRetainerWindowClosed(nameof(GetOpenRetainerItemQuantity));
            return RetrieveResultRetainerUnavailable;
        }

        var total = 0;
        var unreadable = false;
        foreach(var type in Utils.RetainerInventoriesWithCrystals)
        {
            if(type == InventoryType.RetainerCrystals && !includeCrystals) continue;
            var inv = TryGetReadableContainer(type);
            if(inv == null)
            {
                unreadable = true;
                ReportUnreadableRetainerContainer(type);
                continue;
            }
            for(var i = 0; i < inv->Size; i++)
            {
                var item = inv->GetInventorySlot(i);
                if(item == null)
                {
                    unreadable = true;
                    continue;
                }
                if(item->ItemId != itemId) continue;
                if(hqOnly && !item->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality)) continue;
                total += item->Quantity;
            }
        }
        if(unreadable) return RetrieveResultRetainerUnavailable;
        return total;
    }

    /// <summary>The container only when it can actually be walked.</summary>
    /// <remarks>🔴 A container whose <c>Items</c> array has not been allocated is not safe to index:
    /// <c>GetInventorySlot(i)</c> returns a small-offset fake pointer rather than null, so the read succeeds
    /// and hands back arbitrary memory as an <c>ItemId</c>. Acting on that would fire a retrieve at a slot
    /// chosen from garbage. ⚠️ Dereferencing null is a corrupted-state exception in .NET Core, so try/catch
    /// is not an alternative to checking first.</remarks>
    private static unsafe InventoryContainer* TryGetReadableContainer(InventoryType type)
    {
        var inv = InventoryManager.Instance()->GetInventoryContainer(type);
        if(inv == null || inv->Items == null) return null;
        return inv;
    }

    /// <summary>Says out loud that the retainer window is open yet part of its storage is unreadable, which
    /// is the state that makes "does this retainer have item X" unanswerable. Information level because that
    /// is the level users actually run at, and this is exactly the kind of thing that otherwise shows up only
    /// as "the direct retrieve never does anything and it silently uses the slow path forever".</summary>
    /// <summary>Says out loud that there is no open retainer inventory at all, which is the single condition
    /// that makes every query in this region answer "unavailable". Information level because that is the
    /// level users run at, and because this early return was previously silent: a caller that asked at the
    /// wrong moment looked exactly like a retainer that had nothing, and the only visible symptom was the
    /// caller quietly achieving nothing. Throttled, since callers poll these.</summary>
    private static void ReportRetainerWindowClosed(string caller)
    {
        if(EzThrottler.Throttle($"ARDirectRetrieveWindowClosed{caller}", 10000))
        {
            PluginLog.Information($"[{caller}] No retainer inventory is open, so the retainer's storage cannot be read or retrieved from. Callers are told \"unavailable\" (-1), which is deliberately not the same answer as \"the retainer does not have that item\" (0).");
        }
    }

    private static void ReportUnreadableRetainerContainer(InventoryType type)
    {
        if(EzThrottler.Throttle($"ARDirectRetrieveUnreadableContainer{type}", 30000))
        {
            PluginLog.Information($"[RetrieveRetainerItemSlotById] Retainer inventory window is open but container {type} could not be walked. Absence of an item cannot be proven while that is true, so callers are being told \"unavailable\" rather than \"not present\".");
        }
    }

    #endregion

    #region Drive the retainer / GC flows from outside

    // 這一區把 AutoRetainer 本來就有的任務鏈開一個對外的門,讓巨集不必自己去點 addon。
    //
    // 🔴 動機是安全而不是方便:從巨集驅動「鈴 → 雇員清單 → 選雇員 → 道具管理」需要一連串寫死的
    //    callback 參數與選單索引,那些東西離線驗不了、改版會**靜默**失效(addon 對型別不對的參數
    //    是不動作,不是報錯),而且選單項的文字在各語系不同。AutoRetainer 內部這條鏈本來就是
    //    正式流程每天在跑的,連選單文字都是查 Addon 表而不是寫死字串。與其在外面重造一份會爛的,
    //    不如把已經在跑的這條接出來。
    //
    // ⚠️ 這些是 Enqueue,不是同步動作:呼叫後任務進佇列,呼叫端要自己輪詢 IsBusy() 等它做完。

    /// <summary>Retainer names of the current character that have an entrust plan assigned, in the order
    /// AutoRetainer knows them. Empty when there is no character data yet.
    ///
    /// <para>Exposed because "which retainers should this run touch" is a question an outside caller
    /// cannot answer on its own: the per-retainer settings live in AutoRetainer's own config keyed by
    /// (character CID, retainer name), and reading the config file from outside is both racy and wrong
    /// while the game is running - the in-memory copy is the truth.</para></summary>
    [EzIPC]
    public List<string> GetRetainersWithEntrustPlan()
    {
        var result = new List<string>();
        var data = Utils.GetCurrentCharacterData();
        if(data == null) return result;

        foreach(var retainer in data.RetainerData)
        {
            var name = retainer.Name.ToString();
            if(name.IsNullOrEmpty()) continue;
            var adata = Utils.GetAdditionalData(data.CID, name);
            if(adata.EntrustPlan != Guid.Empty) result.Add(name);
        }
        return result;
    }

    /// <summary>Enqueues AutoRetainer's own "walk up to the summoning bell, open it, pick this retainer,
    /// open their item storage" chain. Returns false without enqueuing anything when a precondition does
    /// not hold, so a caller can stop instead of waiting out a timeout on a flow that never started.</summary>
    /// <param name="retainerName">Must be a retainer of the currently logged-in character.</param>
    [EzIPC]
    public bool EnqueueOpenRetainerItemStorage(string retainerName)
    {
        if(retainerName.IsNullOrEmpty())
        {
            PluginLog.Information($"[EnqueueOpenRetainerItemStorage] Refused: no retainer name given.");
            return false;
        }
        if(!Player.Available)
        {
            PluginLog.Information($"[EnqueueOpenRetainerItemStorage] Refused: player is not available.");
            return false;
        }
        if(Utils.IsBusy)
        {
            PluginLog.Information($"[EnqueueOpenRetainerItemStorage] Refused for {retainerName}: AutoRetainer is already busy.");
            return false;
        }
        if(!Utils.TryGetRetainerByName(retainerName, out _))
        {
            PluginLog.Information($"[EnqueueOpenRetainerItemStorage] Refused: {retainerName} is not a retainer of the current character.");
            return false;
        }
        // 這裡不檢查鈴在不在:任務鏈自己會等,而在工房裡它還會先走過去。檢查了反而會把
        // 「站得稍遠但走得到」誤判成不可行。
        TaskInteractWithNearestBell.Enqueue();
        TaskSelectRetainer.Enqueue(retainerName);
        P.TaskManager.Enqueue(RetainerHandlers.SelectEntrustItems, $"SelectEntrustItems({retainerName})");
        P.TaskManager.Enqueue(InventorySpaceManager.IsRetainerInventoryLoaded, $"WaitRetainerInventoryLoaded({retainerName})");
        PluginLog.Information($"[EnqueueOpenRetainerItemStorage] Enqueued open-item-storage chain for {retainerName}.");
        return true;
    }

    /// <summary>Enqueues closing whatever retainer UI is open, back out to the world. Safe to call when
    /// nothing is open - the handler simply reports it had nothing to do.</summary>
    [EzIPC]
    public void EnqueueCloseRetainer()
    {
        P.TaskManager.Enqueue(RetainerHandlers.CloseAgentRetainer, "CloseAgentRetainer");
        P.TaskManager.Enqueue(() => !IsOccupied(), "WaitUntilNotOccupiedAfterRetainerClose");
    }

    /// <summary>Enqueues the same "go to the Grand Company and hand in expert delivery items" flow the
    /// Deliver Items button runs: Lifestream navigates there if needed, then AutoRetainer's own GC
    /// continuation interacts with the NPC, opens the supply list on the expert delivery tab and turns
    /// automatic handin on.
    ///
    /// <para>⚠️ This is the full flow, which means it also runs the seal-spending purchase step the
    /// button runs - it is not a handin-only entry point.</para></summary>
    /// <returns>False when the character has no Grand Company, or something is already busy.</returns>
    [EzIPC]
    public bool EnqueueGCDeliverItems()
    {
        if(!Player.Available)
        {
            PluginLog.Information($"[EnqueueGCDeliverItems] Refused: player is not available.");
            return false;
        }
        if(GCContinuation.GetGCInfo() == null)
        {
            PluginLog.Information($"[EnqueueGCDeliverItems] Refused: character is not employed by a Grand Company.");
            return false;
        }
        if(Utils.IsBusy)
        {
            PluginLog.Information($"[EnqueueGCDeliverItems] Refused: AutoRetainer or Lifestream is already busy.");
            return false;
        }
        TaskDeliverItems.Enqueue();
        PluginLog.Information($"[EnqueueGCDeliverItems] Enqueued GC delivery flow.");
        return true;
    }

    #endregion
}
