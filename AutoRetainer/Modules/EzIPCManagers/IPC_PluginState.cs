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

/// <summary>
/// 🔴 這裡的每一支端點都是<b>在呼叫端的執行緒上</b>執行的，不是在 framework 執行緒上。
/// 凡是會同步碰到原生記憶體、addon、<c>P.TaskManager.Tasks</c>（裸 <c>List&lt;T&gt;</c>）或
/// <see cref="AutoRetainer.Internal.InventoryManagement.RetainerRetrieve"/> 追蹤狀態的，
/// 一律經 <see cref="IpcFrameworkGate"/> 搬到 framework 執行緒上執行 ——
/// <b>已經在 framework 執行緒上呼叫時是就地執行，行為逐字不變</b>。
/// 純粹讀寫設定（<c>C.*</c>）與常數的端點不必經過閘門，維持原樣。
/// </summary>
public class IPC_PluginState
{
    public IPC_PluginState()
    {
        EzIPC.Init(this, $"{Svc.PluginInterface.InternalName}.PluginState");
    }

    [EzIPC]
    public bool IsBusy()
    {
        // 逾時回 true＝「就當我在忙」，也就是叫呼叫端別去碰傳喚鈴 —— fail-safe 的那一邊。
        return IpcFrameworkGate.Run(nameof(IsBusy), () => Utils.IsBusy, true,
            "the caller is told AutoRetainer is busy so that it keeps off the summoning bell");
    }

    /// <summary>Which retainers of which characters are ticked for automation, as a snapshot.</summary>
    /// <remarks>
    /// 🔴 這裡回的是<b>複本</b>，不是 <c>C.SelectedRetainers</c> 本尊。理由是這張表兩層都會被
    /// AutoRetainer 自己在 framework／繪製執行緒上改：外層在
    /// <c>AutoRetainer.cs</c> 的每幀 Tick 與 <c>P.GetSelectedRetainers</c> 會<b>新增鍵</b>、
    /// 角色排序 UI 會 <c>Remove</c>；內層的 <c>HashSet</c> 在僱員分頁的「啟用／停用選取的僱員」
    /// 會 <c>Add</c>／<c>Remove</c>。而 IPC 端點跑在<b>呼叫端的執行緒</b>上
    /// ⇒ 交出本尊等於讓對方在我們改動的當下走訪它，失敗形式是 <c>InvalidOperationException</c>
    /// 擲在<b>對方</b>的碼裡（看起來像對方的 bug），最壞是字典本身壞掉。
    /// <br/>
    /// ⚠️ 內層也複製（不是只換外層），否則對方走訪 <c>HashSet</c> 時的競態原封不動 ——
    /// 而「逐一列出某個角色勾了哪些僱員」正是這個端點唯一的用法。
    /// <br/>
    /// 📌 代價：透過回傳值寫回來不再會生效。這個端點的語意本來就是查詢
    /// （SomethingNeedDoing 對 Lua 公開它時的說明是 "Gets all enabled retainers"），
    /// 而且全艦隊的 C# 消費端（AutoDuty <c>IPCSubscriber.cs:60</c>、GatherBuddyReborn
    /// <c>IpcSubscribers.cs:650</c>、SomethingNeedDoing <c>External/AutoRetainer.cs:34</c>）
    /// <b>三個都只有宣告、沒有任何呼叫點</b>，更沒有人寫回。
    /// </remarks>
    [EzIPC]
    public Dictionary<ulong, HashSet<string>> GetEnabledRetainers()
    {
        // 🔴 複製這個動作本身也必須在 framework 執行緒上做，否則「拍快照」與「改動」就是同一個競態。
        return IpcFrameworkGate.Run(nameof(GetEnabledRetainers),
            () => C.SelectedRetainers.ToDictionary(x => x.Key, x => new HashSet<string>(x.Value)),
            new Dictionary<ulong, HashSet<string>>(),
            "the caller is told no retainer is enabled (an empty map)");
    }

    [EzIPC]
    public bool AreAnyRetainersAvailableForCurrentChara()
    {
        return IpcFrameworkGate.Run(nameof(AreAnyRetainersAvailableForCurrentChara),
            Utils.AnyRetainersAvailableCurrentChara, false, "the caller is told no retainer is ready");
    }

    [EzIPC]
    public void AbortAllTasks()
    {
        IpcFrameworkGate.Run(nameof(AbortAllTasks), P.TaskManager.Abort);
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
        IpcFrameworkGate.Run(nameof(EnableMultiMode), () => Svc.Commands.ProcessCommand("/autoretainer multi enable"));
    }

    [EzIPC]
    public int GetInventoryFreeSlotCount()
    {
        // 0 本來就是「讀不到或真的滿了」共用的回值（見 Utils.GetInventoryFreeSlotCount 的註解：
        // 讀不到的容器一律跳過，所以只可能少算），逾時沿用它不會引進新語意。
        return IpcFrameworkGate.Run(nameof(GetInventoryFreeSlotCount), Utils.GetInventoryFreeSlotCount, 0,
            "the caller is told there is no free inventory space");
    }

    [EzIPC]
    public void EnqueueHET(Action onFailure)
    {
        IpcFrameworkGate.Run(nameof(EnqueueHET), () => TaskNeoHET.Enqueue(onFailure));
    }

    [EzIPC]
    public bool CanAutoLogin()
    {
        return IpcFrameworkGate.Run(nameof(CanAutoLogin), Utils.CanAutoLogin, false,
            "the caller is told it cannot log in right now");
    }

    [EzIPC]
    public bool Relog(string charaNameWithWorld)
    {
        // 🔴 這裡只是把既有的判斷與入佇列搬到 framework 執行緒上，沒有新增任何自動觸發：
        //    觸發者仍然只有「呼叫端明確打了這支端點」這一件事。
        return IpcFrameworkGate.Run(nameof(Relog), () =>
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
        }, false, "the caller is told no relog was started");
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
        // P.Time 在 C.UseServerTime 時走 CSFramework.GetServerTime()，那是原生呼叫。
        return IpcFrameworkGate.Run<long?>(nameof(GetClosestRetainerVentureSecondsRemaining), () =>
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
        }, null, "the caller is told there is no known venture (the same answer as \"no data\")");
    }

    [EzIPC]
    public bool IsItemProtected(uint itemId)
    {
        // 逾時回 true＝「當它是受保護的」，也就是別動它 —— fail-safe 的那一邊。
        return IpcFrameworkGate.Run(nameof(IsItemProtected), () =>
        {
            // 🔴 沒登入、或這個角色還沒被記錄過時 Data 是 null，原本會直接把 NRE 擲進呼叫端。
            //    回 true 與上面的逾時值一致：兩者都是「我答不出來，所以別動這件道具」。
            //    ⚠️ 不可以回 false —— 那的意思是「這件沒被保護，儘管賣／丟」，對方會照做。
            var data = Data;
            if(data == null)
            {
                // EzThrottler 在這裡是安全的：這段已經被閘門搬到 framework 執行緒上跑了。
                if(EzThrottler.Throttle("IPCIsItemProtectedNoCharacterData", 60000))
                {
                    PluginLog.Information($"[IsItemProtected] There is no character data to check against (not logged in yet, or this character has never been seen), so item {itemId} cannot be looked up in a protect list. Answering \"protected\" so that callers leave it alone - that is deliberately not the same as the item actually being on the list.");
                }
                return true;
            }
            return data.GetIMSettings().IMProtectList.Contains(itemId);
        }, true, "the caller is told the item is protected");
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
    public void ResetRetainerRetrieveTracking()
        => IpcFrameworkGate.Run(nameof(ResetRetainerRetrieveTracking), RetainerRetrieve.ResetTracking);

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
    public bool RetrieveNextRetainerItemSlot()
        => IpcFrameworkGate.Run(nameof(RetrieveNextRetainerItemSlot), RetainerRetrieve.RetrieveNextSlot, false,
            "the caller is told nothing was retrieved");

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
        => IpcFrameworkGate.Run(nameof(RetrieveRetainerItemSlotById),
            () => RetainerRetrieve.RetrieveSlotById(itemId, hqOnly, includeCrystals),
            RetainerRetrieve.ResultRetainerUnavailable,
            "the caller is told the retainer's storage could not be read (-1), which is deliberately not the same answer as \"not present\" (0)");

    /// <summary>How many of <paramref name="itemId"/> the currently open retainer is holding, for callers
    /// that need to know when to stop asking. ⚠️ -1 is "unknown", not "none".</summary>
    [EzIPC]
    public int GetOpenRetainerItemQuantity(uint itemId, bool hqOnly, bool includeCrystals)
        => IpcFrameworkGate.Run(nameof(GetOpenRetainerItemQuantity),
            () => RetainerRetrieve.GetOpenQuantity(itemId, hqOnly, includeCrystals),
            RetainerRetrieve.ResultRetainerUnavailable,
            "the caller is told the quantity is unknown (-1), which is deliberately not the same answer as \"none\" (0)");

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
        // Utils.GetCurrentCharacterData() 讀 Player.CID，那是原生讀取。
        return IpcFrameworkGate.Run(nameof(GetRetainersWithEntrustPlan), () =>
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
        }, new List<string>(), "the caller is told there is no character data yet (an empty list)");
    }

    /// <summary>Enqueues AutoRetainer's own "walk up to the summoning bell, open it, pick this retainer,
    /// open their item storage" chain. Returns false without enqueuing anything when a precondition does
    /// not hold, so a caller can stop instead of waiting out a timeout on a flow that never started.</summary>
    /// <param name="retainerName">Must be a retainer of the currently logged-in character.</param>
    [EzIPC]
    public bool EnqueueOpenRetainerItemStorage(string retainerName)
    {
        return IpcFrameworkGate.Run(nameof(EnqueueOpenRetainerItemStorage), () =>
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
            // 🔴 清單還沒載入時 TryGetRetainerByName 對每個名字都回 false,與「這個雇員真的不存在」
            //    完全不可分。兩種情況要講成兩件事 —— 呼叫端看到「不存在」會去改設定,
            //    看到「還沒載入」才會知道再開一次鈴就好。
            //    ⚠️ 清單沒載入時**不擋**:這個門本來就會去開鈴,開完自然就載入了。
            if(!GCExpertDeliveryLoop.RetainerListLoaded)
            {
                PluginLog.Information($"[EnqueueOpenRetainerItemStorage] The game's retainer list is not loaded yet, so {retainerName} cannot be verified up front - the chain opens the bell, which loads it.");
            }
            else if(!Utils.TryGetRetainerByName(retainerName, out _))
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
        }, false, "the caller is told the chain was refused and nothing was enqueued");
    }

    /// <summary>Enqueues closing whatever retainer UI is open, back out to the world. Safe to call when
    /// nothing is open - the handler simply reports it had nothing to do.</summary>
    [EzIPC]
    public void EnqueueCloseRetainer()
    {
        IpcFrameworkGate.Run(nameof(EnqueueCloseRetainer), () =>
        {
            P.TaskManager.Enqueue(RetainerHandlers.CloseAgentRetainer, "CloseAgentRetainer");
            P.TaskManager.Enqueue(() => !IsOccupied(), "WaitUntilNotOccupiedAfterRetainerClose");
        });
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
        return IpcFrameworkGate.Run(nameof(EnqueueGCDeliverItems), () =>
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
        }, false, "the caller is told the flow was refused and nothing was enqueued");
    }

    #endregion
}
