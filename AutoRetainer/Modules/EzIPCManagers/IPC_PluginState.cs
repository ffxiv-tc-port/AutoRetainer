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

    // 取回指令的實作與「哪些格子的指令還在飛」的追蹤都在 RetainerRetrieve 裡。
    // 🔴 這裡刻意只留轉呼叫:追蹤狀態必須全外掛只有一份。稀有品繳交循環也會取回,
    //    如果 IPC 這邊各自留一份追蹤,同一個雇員就會有兩套「已經送過指令」的記憶,
    //    兩邊都會對彼此送過的格子重送 —— 而這正是那套追蹤當初要消滅的東西。

    /// <summary>Forgets which retainer slots already had a retrieve command fired at them, so the very next
    /// <see cref="RetrieveNextRetainerItemSlot"/> call considers every occupied slot again. Call this at the
    /// start of each sweep: anything the server refused (or dropped) is then re-offered immediately instead
    /// of waiting out the staleness timeout. Tracking also resets on its own when the retainer inventory
    /// closes or a different retainer is opened, so this is an optimisation, not a correctness requirement.</summary>
    [EzIPC]
    public void ResetRetainerRetrieveTracking() => RetainerRetrieve.ResetTracking();

    /// <summary>Fires a single retrieve-from-retainer command for the first occupied slot found in the
    /// currently open retainer's item storage (items and crystals), into the player's own bags - never
    /// routes through the armoury chest, same as AutoRetainer's own entrust/vendor tasks. Deliberately
    /// does not wait for the retrieve to land before returning, unlike AutoRetainer's own throttled tasks -
    /// callers (e.g. an SND macro looping this) are expected to control their own pacing between calls, in
    /// exchange for real speed instead of the ~500ms+confirm-per-item pace the built-in tasks use.
    ///
    /// Returns false once nothing is left to retrieve, the player's own inventory is nearly full, or every
    /// remaining occupied slot already has a command in flight - in the last case the caller should let the
    /// retainer inventory settle, then start a fresh round rather than treating it as "done".</summary>
    [EzIPC]
    public bool RetrieveNextRetainerItemSlot() => RetainerRetrieve.RetrieveNextSlot();

    /// <summary>Version of the specific-item retrieve surface below
    /// (<see cref="RetrieveRetainerItemSlotById"/> / <see cref="GetOpenRetainerItemQuantity"/>). Present from
    /// version 1 onwards; consumers should treat "the IPC call itself throws" as "not supported, use the UI
    /// path" and only rely on the methods below once this returns a version they understand.</summary>
    [EzIPC]
    public int GetRetainerItemRetrieveApiVersion() => 1;

    /// <summary>Fires one retrieve-from-retainer command at the first slot of the currently open retainer
    /// that holds <paramref name="itemId"/>, into the player's own bags. Always takes the <b>whole slot</b>,
    /// because the underlying command has no "retrieve N" form that avoids the game's own quantity dialog.</summary>
    /// <returns>The quantity the fired command was aimed at (always &gt;= 1) when a command was sent, otherwise
    /// 0 (proved absent), -1 (retainer storage could not be read), -2 (every matching slot already has a
    /// command in flight), -3 (player bags at or below the configured reserve), -4 (unique item the player
    /// already owns) or -5 (only present in the crystal container).
    /// 🔴 0 and -1 are deliberately different values: 0 means "proved absent", -1 means "could not look".</returns>
    [EzIPC]
    public int RetrieveRetainerItemSlotById(uint itemId, bool hqOnly, bool includeCrystals)
        => RetainerRetrieve.RetrieveSlotById(itemId, hqOnly, includeCrystals);

    /// <summary>How many of <paramref name="itemId"/> the currently open retainer is holding, for callers
    /// that need to know when to stop asking. ⚠️ -1 is "unknown", not "none".</summary>
    [EzIPC]
    public int GetOpenRetainerItemQuantity(uint itemId, bool hqOnly, bool includeCrystals)
        => RetainerRetrieve.GetOpenQuantity(itemId, hqOnly, includeCrystals);

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
