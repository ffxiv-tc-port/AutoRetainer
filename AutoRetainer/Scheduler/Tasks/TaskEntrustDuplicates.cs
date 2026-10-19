using AutoRetainer.Internal.InventoryManagement;
using AutoRetainer.Scheduler.Handlers;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using ECommons.ExcelServices;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace AutoRetainer.Scheduler.Tasks;

internal static unsafe class TaskEntrustDuplicates
{
    internal static int RequestEntrustQuantity = 0;
    internal static List<(uint ID, uint Quantity)> CapturedInventoryState = [];
    internal static bool WasOpen = false;

    /// <summary>A slot we fired an entrust command at, identified by its contents at that moment.</summary>
    private record struct EntrustAttempt(uint ItemId, int Quantity, int Count, bool Warned);

    /// <summary>Per-slot attempt counts for the entrust flow currently running. The 5 second
    /// "InventoryTimeout" fallback exists so a command that never lands cannot wedge the flow, but on its
    /// own it only turns a wedge into a loop: the timeout fires, the scan re-runs, picks the same slot
    /// because nothing changed, fires again, and waits another 5 seconds - for up to the task's one hour
    /// limit, without a single line in the log. This bounds that.</summary>
    private static readonly Dictionary<(InventoryType Type, int Slot), EntrustAttempt> Attempts = [];

    private const int MaxEntrustAttempts = 3;

    /// <summary>Bounds for <see cref="Config.EntrustIntervalMS"/>.
    ///
    /// The lower bound is NOT about command spacing - the retry rate is already bounded elsewhere (every
    /// send re-arms the 5 second "InventoryTimeout" with rethrottle:true, and <see cref="IsSlotStuck"/>
    /// caps a slot at <see cref="MaxEntrustAttempts"/> tries). What it does bound is the full inventory scan
    /// below: while the retainer window is still opening, every pass that gets past this throttle rebuilds
    /// the item/count dictionaries; with no floor at all that would be one scan per frame.</summary>
    private const int MinEntrustIntervalMS = 50;
    private const int MaxEntrustIntervalMS = 1000;

    /// <summary>The configured spacing, clamped. Read per pass so changing the setting takes effect
    /// without a reload.
    ///
    /// Why the default is 150: the server round trip measured ~120ms per item, and the real "wait for the
    /// previous item to land" gate is the InventoryTimeout / captured-inventory-state comparison further
    /// down - this throttle is dead time layered on top of it. 150ms sits just above the observed round
    /// trip, so it is not the binding constraint in the normal case while still capping how often the scan
    /// below can run.</summary>
    private static int EntrustInterval => Math.Clamp(C.EntrustIntervalMS, MinEntrustIntervalMS, MaxEntrustIntervalMS);

    // Timing breakdown for the flow currently running. All wall-clock, all reported once per flow so the
    // cost of a slow entrust run can be attributed instead of guessed at.
    private static long FlowStartedAt;
    private static long FlowFirstSendAt;
    private static long LastSendAt;
    private static long LastLandedAt;
    private static long LastSendThrottleWaitMs;
    private static bool LastSendWasPartial;
    private static int FlowMoves;
    private static int FlowPartialMoves;
    private static long FlowRoundTripMs;
    private static long FlowPartialRoundTripMs;
    private static int FlowTimeouts;
    private static long FlowTimeoutMs;
    private static long FlowThrottleWaitMs;
    private static int FlowSkippedStuck;

    /// <summary>這一輪流程裡被「裝備類別只存放可繳交的稀有品」濾掉的道具 ID。
    ///
    /// <para>🔴 用集合而不是計數器,也刻意不逐件寫 log:itemList 每一輪(每 <see cref="EntrustInterval"/>
    /// 毫秒)都會整份重新建立,同一件裝備會被濾掉幾十次。用 ++ 會得到一個毫無意義的大數字,
    /// 逐件寫 log 則會把真正有用的行洗掉。統計留到 <see cref="ReportFlow"/> 一行講完。</para></summary>
    private static readonly HashSet<uint> FlowSkippedGear = [];

    /// <summary>已經提示過「裝備留在身上」的角色 ContentId。只在 framework 執行緒的任務回呼裡讀寫,
    /// 不需要鎖;外掛存活期間刻意不清空,清空就會退回每輪一行。</summary>
    private static readonly HashSet<ulong> GearSkipReported = [];

    private static void ResetFlowTracking()
    {
        Attempts.Clear();
        FlowStartedAt = Environment.TickCount64;
        FlowFirstSendAt = 0;
        LastSendAt = 0;
        LastLandedAt = 0;
        LastSendThrottleWaitMs = 0;
        LastSendWasPartial = false;
        FlowMoves = 0;
        FlowPartialMoves = 0;
        FlowRoundTripMs = 0;
        FlowPartialRoundTripMs = 0;
        FlowTimeouts = 0;
        FlowTimeoutMs = 0;
        FlowThrottleWaitMs = 0;
        FlowSkippedStuck = 0;
        FlowSkippedGear.Clear();
    }

    /// <summary>Written at Information so it survives a normal log level - these numbers are the whole
    /// point of the instrumentation and are useless if the user has to turn on verbose logging first.</summary>
    /// <param name="itemCounts">這一輪掃到的「玩家身上各道具的數量」,用來把被濾掉的裝備換算成件數。</param>
    private static void ReportFlow(Dictionary<uint, int> itemCounts)
    {
        if(FlowSkippedGear.Count > 0)
        {
            var kinds = 0;
            var pieces = 0;
            foreach(var id in FlowSkippedGear)
            {
                // 只算真的還留在身上的。multistack duplicates 那條路是拿僱員身上的東西加進清單的,
                // 玩家手上不一定有同一件,那種情況不該算進「留在身上」。
                var n = itemCounts.GetValueOrDefault(id);
                if(n <= 0) continue;
                kinds++;
                pieces += n;
            }
            // 每個僱員各跑一輪流程、每個換班週期又重來,同角色只有第一次值得 Information。
            if(pieces > 0)
            {
                var message = $"[TED] 「裝備類別只存放可繳交的稀有品」讓 {pieces} 件裝備({kinds} 種)留在身上沒有存進僱員 —— 它們不是稀有品繳交循環拿得走的東西(時裝、白色稀有度、不可分解的裝備都在此列)。";
                if(GearSkipReported.Add(Svc.PlayerState.ContentId)) PluginLog.Information(message);
                else PluginLog.Debug(message);
            }
            FlowSkippedGear.Clear();
        }
        if(FlowMoves == 0 && FlowTimeouts == 0 && FlowSkippedStuck == 0) return;
        var total = Environment.TickCount64 - FlowStartedAt;
        var plain = FlowMoves - FlowPartialMoves;
        var plainMs = FlowRoundTripMs - FlowPartialRoundTripMs;
        PluginLog.Information($"[TED-timing] flow done: total={total}ms moves={FlowMoves} (plain={plain} partial/InputNumeric={FlowPartialMoves}) " +
            $"roundtrip_total={FlowRoundTripMs}ms (plain_avg={(plain > 0 ? plainMs / plain : 0)}ms partial_avg={(FlowPartialMoves > 0 ? FlowPartialRoundTripMs / FlowPartialMoves : 0)}ms) " +
            $"throttle_wait_total={FlowThrottleWaitMs}ms timeouts={FlowTimeouts} timeout_total={FlowTimeoutMs}ms stuck_slots_skipped={FlowSkippedStuck} " +
            // Everything before the first command went out: waiting for SelectString, then opening the
            // retainer inventory. It is per-flow rather than per-item, so it does not shrink when the
            // interval does - which makes it the largest remaining component once the interval is lowered.
            $"startup={(FlowFirstSendAt == 0 ? -1 : FlowFirstSendAt - FlowStartedAt)}ms interval={EntrustInterval}ms");
        ResetFlowTracking();
    }

    /// <summary>延後批次開了僱員卻一件都沒存（空開）時，印一行：事前判斷命中哪一件，以及實際存放看這一件時的各項條件。
    /// 每次延後拜訪最多一行；只讀狀態，不影響存放。</summary>
    private static void ReportDeferredEmptyVisit(EntrustPlan plan, InventoryManagementSettings s, Dictionary<uint, int> itemList, Dictionary<uint, int> itemCounts)
    {
        if(DeferredPrecheck is not { } d) return;
        DeferredPrecheck = null;
        // 自動賣出命中的拜訪本來就不是為了存放而開，存 0 件不算空開。
        if(FlowMoves > 0 || d.Hit.Path == EntrustPrecheckPath.Vendor) return;
        try
        {
            var id = d.Hit.ItemId;
            var dupMode = plan.Duplicates ? (plan.DuplicatesMultiStack ? "multistack" : "fill") : "off";
            var flow = $"dupMode={dupMode} timeouts={FlowTimeouts} stuck={FlowSkippedStuck}";
            if(id == 0)
            {
                PluginLog.Information($"[EntrustVendorPass] Empty visit to {d.Retainer}: precheck={d.Hit.Path} {flow}");
                return;
            }
            var data = ExcelItemHelper.Get(id);
            string keep;
            if(plan.EntrustItems.Contains(id)) keep = $"EntrustItems:{plan.EntrustItemsAmountToKeep.SafeSelect(id)}";
            else if(data != null && plan.EntrustCategories.TryGetFirst(c => c.ID == data.Value.ItemUICategory.RowId, out var cat)) keep = $"category:{cat.AmountToKeep}";
            else keep = "none";
            var listKeep = itemList.TryGetValue(id, out var k) ? k.ToString() : "absent";
            var stackSize = data?.StackSize ?? 0;
            List<string> stacks = [];
            foreach(var type in Utils.RetainerInventoriesWithCrystals)
            {
                var inv = InventoryManager.Instance()->GetInventoryContainer(type);
                if(inv == null) continue;
                for(var i = 0; i < inv->Size; i++)
                {
                    var item = inv->GetInventorySlot(i);
                    if(item == null || item->ItemId != id) continue;
                    stacks.Add($"{(item->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality) ? "HQ" : "NQ")}{item->Quantity}/{stackSize}");
                }
            }
            var canFit = Utils.GetAmountThatCanFit(Utils.RetainerInventoriesWithCrystals, id, d.Hit.Hq);
            PluginLog.Information($"[EntrustVendorPass] Empty visit to {d.Retainer}: precheck={d.Hit.Path} item={id} \"{ExcelItemHelper.GetName(id)}\" hq={d.Hit.Hq} held={itemCounts.GetValueOrDefault(id)} keep={keep} listKeep={listKeep} unique={data?.IsUnique} retainerStacks=[{string.Join(",", stacks)}] canFit={canFit} exclusion={GetEntrustExclusion(id, plan, s)} {flow}");
        }
        catch(Exception e)
        {
            e.Log();
        }
    }

    /// <summary>延後批次重驗時，讓排程決定「開這個僱員」的是哪一條路徑。<c>default</c>＝沒有重驗（照樣開）。</summary>
    internal enum EntrustPrecheckPath { NotRevalidated, Duplicates, SharedEntrust, Vendor }

    /// <summary>重驗命中的第一件（判斷式在第一件就短路，後面的不會被看到）。HQ 是玩家那一格的品質。</summary>
    internal readonly record struct EntrustPrecheckHit(EntrustPrecheckPath Path, uint ItemId, bool Hq);

    /// <summary>這一輪存放流程是由延後批次開的，就記著是哪個僱員、為什麼開；只在框架執行緒的任務回呼裡讀寫。</summary>
    private static (string Retainer, EntrustPrecheckHit Hit)? DeferredPrecheck;

    /// <param name="deferredRetainer">只有延後批次給；其他入口（介面按鈕）不給，就不會印空開診斷。</param>
    public static void EnqueueNew(EntrustPlan plan, string deferredRetainer = null, EntrustPrecheckHit deferredHit = default)
    {
        P.TaskManager.Enqueue((Action)(() =>
        {
            WasOpen = false;
            ResetFlowTracking();
            DeferredPrecheck = deferredRetainer == null ? null : (deferredRetainer, deferredHit);
        }), "EntrustDuplicates.ResetFlowTracking");
        P.TaskManager.Enqueue(() => TryGetAddonByName<AtkUnitBase>("SelectString", out var addon) && IsAddonReady(addon), "EntrustDuplicates.WaitSelectString");
        P.TaskManager.Enqueue(() => RecursivelyEntrustItems(plan), "EntrustDuplicates.EntrustItems", new(timeLimitMS: 60 * 60 * 1000));
        P.TaskManager.Enqueue(() => !WasOpen || TaskVendorItems.CloseInventory() == true, "EntrustDuplicates.CloseInventory");
    }

    /// <summary>Whether this slot has already been fired at <see cref="MaxEntrustAttempts"/> times with no
    /// observable effect. Any change to the slot's contents means the previous attempt worked, so the
    /// counter starts over.</summary>
    private static bool IsSlotStuck(InventoryType type, int slot, uint itemId, int quantity)
    {
        if(!Attempts.TryGetValue((type, slot), out var attempt)) return false;
        if(attempt.ItemId != itemId || attempt.Quantity != quantity)
        {
            Attempts.Remove((type, slot));
            return false;
        }
        if(attempt.Count < MaxEntrustAttempts) return false;
        if(!attempt.Warned)
        {
            Attempts[(type, slot)] = attempt with { Warned = true };
            FlowSkippedStuck++;
            // Never skip silently: from the outside a skipped slot and a slot that was never eligible look
            // identical - a silently skipped slot just reads as "it is being slow".
            DuoLog.Warning($"存入「{ExcelItemHelper.GetName(itemId, true)}」連續 {attempt.Count} 次沒有生效({type} 第 {slot} 格,數量始終是 {quantity}),跳過這一格繼續處理其他道具。");
            PluginLog.Information($"[TED-stuck] skipping {type}/{slot} itemId={itemId} qty={quantity} after {attempt.Count} attempts with no inventory change");
        }
        return true;
    }

    private static void RecordSend(InventoryType type, int slot, uint itemId, int quantity, bool partial)
    {
        var now = Environment.TickCount64;
        var previous = Attempts.GetValueOrDefault((type, slot));
        var count = previous.ItemId == itemId && previous.Quantity == quantity ? previous.Count + 1 : 1;
        Attempts[(type, slot)] = new EntrustAttempt(itemId, quantity, count, false);
        LastSendAt = now;
        if(FlowFirstSendAt == 0) FlowFirstSendAt = now;
        LastSendWasPartial = partial;
        // How long we sat in our own EntrustItem throttle after the previous item landed. Not ~0: at 333ms
        // it measured ~200ms per item against a ~120ms round trip, i.e. the throttle can be the pacing
        // source rather than the server.
        LastSendThrottleWaitMs = LastLandedAt == 0 ? 0 : now - LastLandedAt;
    }

    /// <summary>「裝備類別只存放可繳交的稀有品」的判定:這件道具屬於裝備類別,而且**不是**
    /// 稀有品繳交循環會拿去繳交的東西 —— 時裝正好落在這裡(白色稀有度、不可分解)。
    ///
    /// <para>🔴 判定直接借用 <see cref="GCExpertDeliveryLoop.IsDeliverableGear(uint, InventoryManagementSettings)"/>,
    /// 不另外抄一份條件。存放端與取回端只要有一邊寬,使用者看到的就是「存進去卻取不回來」。</para>
    ///
    /// <para>⚠️ 借來的判定式含保護清單(IMProtectList)排除,所以這個選項開著的時候,保護清單裡的
    /// **裝備**也會留在身上,與 <see cref="EntrustPlan.ExcludeProtected"/> 各自獨立。方向是保守的
    /// (東西留在身上、不會多存),而且只在使用者主動開啟這個選項時才成立。</para>
    ///
    /// <para>📌 非裝備類別一律回 false,完全不受影響。</para></summary>
    private static bool IsNonDeliverableGear(uint itemId, InventoryManagementSettings imSettings)
    {
        if(itemId == 0) return false;
        var data = ExcelItemHelper.Get(itemId);
        if(data == null) return false;
        if(!data.Value.ItemUICategory.RowId.EqualsAny(Utils.GearUICategories)) return false;
        return !GCExpertDeliveryLoop.IsDeliverableGear(itemId, imSettings);
    }

    internal enum EntrustExclusion { None, Protected, ReservedFuel, NonDeliverableGear }

    /// <summary>這件道具在 <paramref name="plan"/> 下是否被存放規則整件排除(不看數量、不看僱員放不放得下)。
    ///
    /// <para>🔴 存放流程與排程的「這個僱員有沒有存放工作」(SchedulerMain)都只問這一支:事前判斷只要比這裡
    /// 多擋一件,那一輪就漏存;少擋一件只是多開一次僱員。</para>
    ///
    /// <para>📌 逐件點名在 EntrustItems 裡的道具不受裝備濾網影響:那份清單的語意就是「不管規則怎麼寫,這幾件我要存」。</para></summary>
    internal static EntrustExclusion GetEntrustExclusion(uint itemId, EntrustPlan plan, InventoryManagementSettings imSettings)
    {
        if(plan.ExcludeProtected && imSettings.IMProtectList.Contains(itemId)) return EntrustExclusion.Protected;
        if(AutoBuyFuelManager.IsFuelReservedForAutoBuy(itemId)) return EntrustExclusion.ReservedFuel;
        if(plan.EntrustOnlyDeliverableGear
            && !plan.EntrustItems.Contains(itemId)
            && IsNonDeliverableGear(itemId, imSettings)) return EntrustExclusion.NonDeliverableGear;
        return EntrustExclusion.None;
    }

    // 🔴 這支是任務回呼、每幀跑，而數量輸入框被送出之後有「正在關閉中」的幾幀：GetAddonByName 仍拿得到實例、
    // IsAddonReady 三關也全過，此時再 FireCallback 一次就是攔不到的原生 AccessViolation。
    // 200ms 的 EzThrottler 不是防護（EzThrottler 的 key 是全域且跨場景持久的，而且首次一定放行）。
    // 「這扇已經送出過了」的記號集中在 Helpers/DialogGuards.cs，解除點在 DialogGuards.Tick：每件要輸入數量的
    // 道具都會開一扇新的，中間那幾幀 addon 清單裡沒有 InputNumeric，記號就歸零，所以正常流程一件也不會被擋。
    private static bool? RecursivelyEntrustItems(EntrustPlan plan)
    {
        try
        {
            var s = Data.GetIMSettings();
            var allowedPlayerInventories = plan.GetAllowedInventories();
            if(TryGetAddonByName<AtkUnitBase>("InputNumeric", out var numeric))
            {
                if(IsAddonReady(numeric))
                {
                    var maxAmount = numeric->AtkValues[3].UInt;
                    var result = Math.Clamp(RequestEntrustQuantity, 1, maxAmount);
                    if(EzThrottler.Throttle("EntrustItemInputNumeric", 200)
                        && DialogGuards.TryPressOnce("InputNumeric", (nint)numeric, "EntrustItemInputNumeric"))
                    {
                        InternalLog.Information($"Processing input numeric: {result} (max: {maxAmount})");
                        Callback.Fire(numeric, true, (int)result);
                    }
                }
                return false;
            }
            var withinTimeoutWindow = !EzThrottler.Check("InventoryTimeout");
            if(withinTimeoutWindow && Utils.MatchesCapturedInventoryState(allowedPlayerInventories, CapturedInventoryState))
            {
                return false;
            }
            if(LastSendAt != 0)
            {
                // We got past the gate, so either the inventory changed (the entrust landed) or the 5 second
                // fallback expired with nothing having happened. Those two cost wildly different amounts of
                // time and have different fixes, so they are counted separately.
                var elapsed = Environment.TickCount64 - LastSendAt;
                var landed = withinTimeoutWindow || !Utils.MatchesCapturedInventoryState(allowedPlayerInventories, CapturedInventoryState);
                if(landed)
                {
                    FlowMoves++;
                    FlowRoundTripMs += elapsed;
                    FlowThrottleWaitMs += LastSendThrottleWaitMs;
                    if(LastSendWasPartial)
                    {
                        FlowPartialMoves++;
                        FlowPartialRoundTripMs += elapsed;
                    }
                }
                else
                {
                    FlowTimeouts++;
                    FlowTimeoutMs += elapsed;
                    PluginLog.Information($"[TED-timing] no inventory change {elapsed}ms after the last entrust command - fell through on the 5s fallback, the same slot will be retried (max {MaxEntrustAttempts} attempts)");
                }
                LastLandedAt = Environment.TickCount64;
                LastSendAt = 0;
            }
            // Sole user of the "EntrustItem" throttle key (EzThrottler keys are global and process
            // persistent). "EntrustItemInputNumeric" above and "InventoryTimeout" are separate keys, and
            // NpcSaleManager has its own "NpcInventoryTimeout".
            if(EzThrottler.Throttle("EntrustItem", EntrustInterval))
            {
                // Only build the [TED] diagnostics when someone has actually asked to see them. They are
                // not free: each line does an Excel row lookup plus a SeString to string conversion, and the
                // interpolated string is built at the call site before InternalLog's capture-level gate can drop it.
                var log = C.ExtraDebug;

                // ItemID -> amount to keep, as a Dictionary so the three scan loops below look it up in O(1).
                // First entry wins.
                Dictionary<uint, int> itemList = [];
                foreach(var x in plan.EntrustItems)
                {
                    var add = (x, plan.EntrustItemsAmountToKeep.SafeSelect(x));
                    if(plan.ExcludeProtected && s.IMProtectList.Contains(add.Item1)) continue;
                    if(!itemList.TryAdd(add.Item1, add.Item2)) continue;
                    if(log) InternalLog.Debug($"[TED] From EntrustItems added item: {ExcelItemHelper.GetName(add.Item1, true)} toKeep={add.Item2}");
                }
                foreach(var x in Utils.GetItemsInInventory(allowedPlayerInventories))
                {
                    if(plan.ExcludeProtected && s.IMProtectList.Contains(x)) continue;
                    var item = ExcelItemHelper.Get(x);
                    if(item == null) continue;
                    if(itemList.ContainsKey(item.Value.RowId)) continue;
                    if(plan.EntrustCategories.TryGetFirst(c => c.ID == item.Value.ItemUICategory.RowId, out var info))
                    {
                        var add = (item.Value.RowId, info.AmountToKeep);
                        itemList[add.RowId] = add.AmountToKeep;
                        if(log) InternalLog.Debug($"[TED] From EntrustCategories added item: {ExcelItemHelper.GetName(add.Item1, true)} toKeep={add.Item2}");
                    }
                }
                if(plan.Duplicates && plan.DuplicatesMultiStack)
                {
                    foreach(var type in Utils.RetainerInventoriesWithCrystals)
                    {
                        if(type.EqualsAny(InventoryType.Crystals, InventoryType.RetainerCrystals)) continue;
                        var inv = InventoryManager.Instance()->GetInventoryContainer(type);
                        if(inv == null) continue;
                        for(var i = 0; i < inv->Size; i++)
                        {
                            var item = InventoryManager.Instance()->GetInventorySlot(type, i);
                            if(item == null) continue;
                            if(item->ItemId != 0 && item->Quantity > 0)
                            {
                                if(plan.ExcludeProtected && s.IMProtectList.Contains(item->ItemId)) continue;
                                if(!itemList.TryAdd(item->ItemId, 0)) continue;
                                if(log) InternalLog.Debug($"[TED] From retainer multistack duplicate added: {ExcelItemHelper.GetName(item->ItemId, true)}");
                            }
                        }
                    }
                }
                // 燃料保留與「裝備類別只存放可繳交的稀有品」的排除(GetEntrustExclusion)。itemList 是上面三條建立路徑
                // (EntrustItems、EntrustCategories、multistack duplicates)的共同出口,也是底下「無條件存入」迴圈唯一的依據,
                // 所以在這裡拔掉一次就同時蓋住三條路。
                // ⚠️ 但這**不包括**下面 !DuplicatesMultiStack 的那條 duplicates 迴圈:它的條件是「不在 itemList 裡」,
                // 從 itemList 拿掉反而會讓它開始處理,所以那裡另外有一個明確的跳過。
                {
                    // 不能邊列舉邊移除,先收集再拔。
                    List<uint> drop = null;
                    foreach(var id in itemList.Keys)
                    {
                        var exclusion = GetEntrustExclusion(id, plan, s);
                        if(exclusion == EntrustExclusion.None) continue;
                        (drop ??= []).Add(id);
                        if(exclusion == EntrustExclusion.NonDeliverableGear) FlowSkippedGear.Add(id);
                    }
                    if(drop != null)
                    {
                        foreach(var id in drop) itemList.Remove(id);
                    }
                }

                // How many of each item the player is holding across the allowed containers. Built once
                // per tick instead of calling Utils.GetItemCount inside the slot loop below, which
                // rescanned every allowed container for every occupied slot - quadratic in slot count,
                // and paid in full even for slots the plan does not cover.
                Dictionary<uint, int> itemCounts = [];
                foreach(var type in allowedPlayerInventories)
                {
                    var inv = InventoryManager.Instance()->GetInventoryContainer(type);
                    if(inv == null) continue;
                    for(var i = 0; i < inv->Size; i++)
                    {
                        var item = InventoryManager.Instance()->GetInventorySlot(type, i);
                        if(item == null || item->ItemId == 0 || item->Quantity <= 0) continue;
                        itemCounts[item->ItemId] = itemCounts.GetValueOrDefault(item->ItemId) + item->Quantity;
                    }
                }
                //processing unconditional entrusts
                foreach(var type in allowedPlayerInventories)
                {
                    var inv = InventoryManager.Instance()->GetInventoryContainer(type);
                    if(inv == null) continue;
                    for(var i = 0; i < inv->Size; i++)
                    {
                        var item = InventoryManager.Instance()->GetInventorySlot(type, i);
                        if(item == null) continue;
                        if(item->ItemId != 0 && item->Quantity > 0)
                        {
                            if(plan.ExcludeProtected && s.IMProtectList.Contains(item->ItemId)) continue;
                            // Cheapest test first. A slot the plan does not cover must not pay for an item
                            // count, an Excel name lookup or a log string before we find that out.
                            if(!itemList.TryGetValue(item->ItemId, out var toKeep)) continue;
                            if(IsSlotStuck(type, i, item->ItemId, item->Quantity)) continue;
                            var itemCount = itemCounts.GetValueOrDefault(item->ItemId);
                            var toEntrust = itemCount - toKeep;
                            // Below the keep threshold: canFit can only ever lower toEntrust, so the outcome is already
                            // decided. Skipping here avoids a full ~175 slot scan of the retainer's pages for every slot that
                            // is merely being kept - those slots never empty out (stacked items leave a residual partial
                            // stack sitting at the keep threshold), so they would be rescanned on every pass for the whole run.
                            if(toEntrust <= 0) continue;
                            uint canFit;
                            if(log)
                            {
                                canFit = Utils.GetAmountThatCanFit(Utils.RetainerInventoriesWithCrystals, item->ItemId, item->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality), out var debugData);
                                InternalLog.Debug($"[TED] Item count for {ExcelItemHelper.GetName(item->ItemId, true)} = {itemCount}");
                                InternalLog.Debug($"[TED] For {ExcelItemHelper.GetName(item->ItemId, true)} toEntrust={toEntrust}, toKeep={toKeep}, canFit={canFit}\n{debugData.Print("\n")}");
                            }
                            else
                            {
                                canFit = Utils.GetAmountThatCanFit(Utils.RetainerInventoriesWithCrystals, item->ItemId, item->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality));
                            }
                            if(toEntrust > canFit) toEntrust = (int)canFit;
                            if(toEntrust > 0)
                            {
                                var toEntrustFromStack = Math.Min(item->Quantity, toEntrust);
                                if(toEntrustFromStack > 0)
                                {
                                    MoveSlotToRetainerInventoryUnsafe(item, (int)toEntrustFromStack, i, type, allowedPlayerInventories);
                                    return false;
                                }
                            }
                        }
                    }
                }
                if(plan.Duplicates && !plan.DuplicatesMultiStack)
                {
                    //and now processing duplicates
                    foreach(var type in Utils.RetainerInventoriesWithCrystals)
                    {
                        if(type.EqualsAny(InventoryType.Crystals, InventoryType.RetainerCrystals)) continue;
                        //find incomplete stacks, then query them from player inventory
                        var inv = InventoryManager.Instance()->GetInventoryContainer(type);
                        if(inv == null) continue;
                        for(var i = 0; i < inv->Size; i++)
                        {
                            var item = inv->GetInventorySlot(i);
                            if(item == null) continue;
                            // 見上面 itemList.Remove 那段：這條路徑的條件是「不在 itemList 裡」，
                            // 所以從 itemList 拔掉的排除對它是反效果，必須在這裡明確再擋一次。
                            // ⚠️ 目前裝備的 StackSize 都是 1,底下的 canFit 會算成 0 而自然跳過 ——
                            // 但那是**別的條件**碰巧擋住,不是裝備濾網在生效。不要拿它當保證。
                            var exclusion = GetEntrustExclusion(item->ItemId, plan, s);
                            if(exclusion != EntrustExclusion.None)
                            {
                                if(exclusion == EntrustExclusion.NonDeliverableGear && itemCounts.ContainsKey(item->ItemId)) FlowSkippedGear.Add(item->ItemId);
                                continue;
                            }
                            if(item->ItemId != 0 && !itemList.ContainsKey(item->ItemId))
                            {
                                var data = ExcelItemHelper.Get(item->ItemId);
                                if(data == null || data.Value.IsUnique) continue;
                                var canFit = data.Value.StackSize - item->Quantity;
                                if(canFit > 0)
                                {
                                    foreach(var playerType in allowedPlayerInventories)
                                    {
                                        var playerInv = InventoryManager.Instance()->GetInventoryContainer(playerType);
                                        if(playerInv == null) continue;
                                        for(var q = 0; q < playerInv->Size; q++)
                                        {
                                            var playerItem = playerInv->GetInventorySlot(q);
                                            if(playerItem == null) continue;
                                            if(playerItem->ItemId == item->ItemId && playerItem->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality) == item->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality))
                                            {
                                                if(IsSlotStuck(playerType, q, playerItem->ItemId, playerItem->Quantity)) continue;
                                                var toEntrustFromStack = Math.Min(canFit, playerItem->Quantity);
                                                MoveSlotToRetainerInventoryUnsafe(playerItem, (int)toEntrustFromStack, q, playerType, allowedPlayerInventories);
                                                return false;
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
                // Nothing left to move - the flow is over, so report where its time actually went.
                ReportDeferredEmptyVisit(plan, s, itemList, itemCounts);
                ReportFlow(itemCounts);
                return true;
            }
        }
        catch(Exception e)
        {
            e.Log();
        }
        return false;
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="item"></param>
    /// <param name="toEntrustFromStack"></param>
    /// <param name="i">slot id</param>
    /// <param name="type"></param>
    private static void MoveSlotToRetainerInventoryUnsafe(InventoryItem* item, int toEntrustFromStack, int i, InventoryType type, InventoryType[] allowedPlayerInventories)
    {
        if(!InventorySpaceManager.IsRetainerInventoryLoaded())
        {
            if(EzThrottler.Throttle("REI SelectEntrust", 2000))
            {
                WasOpen = true;
                RetainerHandlers.SelectEntrustItems();
            }
        }
        else
        {
            // 讀不到就這一輪什麼都不做。呼叫端在本方法回來後一律 return false（＝繼續輪詢），
            // 下一輪會重新掃描並補上，跟上面 !IsRetainerInventoryLoaded() 那條分支的行為一致。
            // 🔴 絕對不能在讀不到的情況下往下走：底下會送出 RetainerItemCommand 實際搬運道具，
            // 拿不到 slot 就等於不知道自己在搬什麼。
            var container = InventoryManager.Instance()->GetInventoryContainer(type);
            if(container == null) return;
            if(i < 0 || i >= container->Size) return;
            var slot = container->GetInventorySlot(i);
            if(slot == null) return;
            void printToChat()
            {
                if(C.EnableEntrustChat && ExcelItemHelper.Get(slot->ItemId) != null) Svc.Chat.Print(new SeStringBuilder().Append("Entrusting: ").Append([new ItemPayload(slot->ItemId, slot->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality))]).Append(ExcelItemHelper.GetName(slot->ItemId)).Append([RawPayload.LinkTerminator]).Build());
            }
            // Snapshot before firing: nothing below may read the slot pointer again.
            var sentItemId = slot->ItemId;
            var sentQuantity = slot->Quantity;
            if(type == InventoryType.Crystals)
            {
                RequestEntrustQuantity = (int)toEntrustFromStack;
                CapturedInventoryState = Utils.GetCapturedInventoryState(allowedPlayerInventories);
                EzThrottler.Throttle("InventoryTimeout", 5000, true);
                InternalLog.Debug($"Entrusting crystals from slot: {i}/{type} - {ExcelItemHelper.GetName(slot->ItemId, true)} quantuity = {toEntrustFromStack}");
                printToChat();
                // Crystals always make the game ask for a quantity, so this is always the InputNumeric path
                // regardless of the command used - that is why it is counted as partial.
                RecordSend(type, i, sentItemId, sentQuantity, true);
                P.Memory.RetainerItemCommandDetour(InventorySpaceManager.AgentRetainerItemCommandModule, (uint)i, type, 0, RetainerItemCommand.EntrustToRetainer);
            }
            else
            {
                if(item->Quantity <= 1 || item->Quantity == toEntrustFromStack)
                {
                    CapturedInventoryState = Utils.GetCapturedInventoryState(allowedPlayerInventories);
                    EzThrottler.Throttle("InventoryTimeout", 5000, true);
                    InternalLog.Debug($"Entrusting from slot: {i}/{type} - {ExcelItemHelper.GetName(slot->ItemId, true)} quantuity = all");
                    printToChat();
                    RecordSend(type, i, sentItemId, sentQuantity, false);
                    P.Memory.RetainerItemCommandDetour(InventorySpaceManager.AgentRetainerItemCommandModule, (uint)i, type, 0, RetainerItemCommand.EntrustToRetainer);
                }
                else
                {
                    //partial entrust
                    RequestEntrustQuantity = (int)toEntrustFromStack;
                    CapturedInventoryState = Utils.GetCapturedInventoryState(allowedPlayerInventories);
                    EzThrottler.Throttle("InventoryTimeout", 5000, true);
                    InternalLog.Debug($"Entrusting from slot: {i}/{type} - {ExcelItemHelper.GetName(slot->ItemId, true)} quantuity = {toEntrustFromStack}");
                    printToChat();
                    RecordSend(type, i, sentItemId, sentQuantity, true);
                    P.Memory.RetainerItemCommandDetour(InventorySpaceManager.AgentRetainerItemCommandModule, (uint)i, type, 0, RetainerItemCommand.EntrustQuantity);
                }
            }
        }
    }
}
