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
    /// caps a slot at <see cref="MaxEntrustAttempts"/> tries), so even at 0 this throttle could not produce
    /// a per-frame resend. What it does bound is the full inventory scan below: while the retainer window
    /// is still opening, every pass that gets past this throttle rebuilds the item/count dictionaries and
    /// then discovers the retainer inventory is not loaded yet. At 50ms that is a handful of scans per
    /// flow; with no floor at all it would be one per frame, which is the same per-frame-rescan stutter
    /// already fixed once in the GC handin flow.</summary>
    private const int MinEntrustIntervalMS = 50;
    private const int MaxEntrustIntervalMS = 1000;

    /// <summary>The configured spacing, clamped. Read per pass so changing the setting takes effect
    /// without a reload.
    ///
    /// Why the default is 150 and not the 333 it used to be: the [TED-timing] numbers from a live
    /// multi-character run showed the per item period sitting at ~330ms with the server round trip only
    /// ~120ms of it (measured plain_avg across six flows: 95/104/166/101/123/149ms), the remaining ~200ms
    /// being this throttle. The real "wait for the previous item to land" gate is the InventoryTimeout /
    /// captured-inventory-state comparison further down, which walks every slot and refuses to move on
    /// until the previous item has actually left - this throttle is dead time layered on top of it.
    /// 150ms sits just above the observed round trip, so it stops being the binding constraint in the
    /// normal case while still capping how often the scan below can run.</summary>
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
                // 只算真的還留在身上的。multistack duplicates 那條路是拿雇員身上的東西加進清單的,
                // 玩家手上不一定有同一件,那種情況不該算進「留在身上」。
                var n = itemCounts.GetValueOrDefault(id);
                if(n <= 0) continue;
                kinds++;
                pieces += n;
            }
            // 寫 Information:使用者跑的 log 等級收不到 Debug,而這一行正是「東西怎麼沒被存進去」的答案。
            if(pieces > 0) PluginLog.Information($"[TED] 「裝備類別只存放可繳交的稀有品」讓 {pieces} 件裝備({kinds} 種)留在身上沒有存進雇員 —— 它們不是稀有品繳交循環拿得走的東西(時裝、白色稀有度、不可分解的裝備都在此列)。");
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

    public static void EnqueueNew(EntrustPlan plan)
    {
        P.TaskManager.Enqueue((Action)(() => { WasOpen = false; ResetFlowTracking(); }));
        P.TaskManager.Enqueue(() => TryGetAddonByName<AtkUnitBase>("SelectString", out var addon) && IsAddonReady(addon));
        P.TaskManager.Enqueue(() => RecursivelyEntrustItems(plan), new(timeLimitMS: 60 * 60 * 1000));
        P.TaskManager.Enqueue(() => !WasOpen || TaskVendorItems.CloseInventory() == true);
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
            // identical, and that is exactly how this used to look like "it is just being slow".
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
        // How long we sat in our own EntrustItem throttle after the previous item landed. The original
        // comment here claimed this was "~0, because the server round trip is longer than the throttle";
        // the instrumentation proved the opposite - at the old 333ms it was ~200ms per item against a
        // ~120ms round trip, i.e. the throttle was the pacing source and the server was not.
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

    // 「這扇數量輸入框已經送出過了」的記號。🔴 這支是任務回呼、每幀跑，而窗被送出之後有「正在關閉中」
    // 的幾幀：GetAddonByName 仍拿得到實例、IsAddonReady 三關也全過，此時再 FireCallback 一次就是攔不到的
    // 原生 AccessViolation。原本唯一的閘門是那個 200ms 的 EzThrottler —— 節流不是防護（EzThrottler 的 key
    // 是全域且跨場景持久的，而且首次一定放行），機制與理由寫在 Helpers/DialogGuards.cs。
    private static DialogGuard InputNumericGuard;

    private static bool? RecursivelyEntrustItems(EntrustPlan plan)
    {
        try
        {
            var s = Data.GetIMSettings();
            var allowedPlayerInventories = plan.GetAllowedInventories();
            // 窗不在了才解除封鎖 —— 每件要輸入數量的道具都會開一扇新的，中間那幾幀 addon 清單裡沒有
            // InputNumeric，記號就在這裡歸零，所以正常流程一件也不會被擋。
            DialogGuards.ReleaseGuardIfGone("InputNumeric", ref InputNumericGuard);
            if(TryGetAddonByName<AtkUnitBase>("InputNumeric", out var numeric))
            {
                if(IsAddonReady(numeric))
                {
                    var maxAmount = numeric->AtkValues[3].UInt;
                    var result = Math.Clamp(RequestEntrustQuantity, 1, maxAmount);
                    if(EzThrottler.Throttle("EntrustItemInputNumeric", 200)
                        && DialogGuards.TryPressOnce((nint)numeric, ref InputNumericGuard, "EntrustItemInputNumeric"))
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
            // Sole user of the "EntrustItem" throttle key - EzThrottler keys are global and process
            // persistent, so this was checked before changing the value. ("EntrustItemInputNumeric" above
            // and "InventoryTimeout" are separate keys, and NpcSaleManager has its own
            // "NpcInventoryTimeout".)
            if(EzThrottler.Throttle("EntrustItem", EntrustInterval))
            {
                // Only build the [TED] diagnostics when someone has actually asked to see them. They are
                // not free: each line does an Excel row lookup plus a SeString to string conversion, and
                // InternalLog has no level filter of its own - it pushes every message into a 1000 entry
                // ring buffer and only filters at display time, so the strings are retained, not merely
                // allocated and dropped.
                var log = C.ExtraDebug;

                // ItemID -> amount to keep. Was a List scanned with a lambda predicate on every lookup,
                // which is O(n) per slot across three separate scan loops. First entry wins, same as the
                // old List plus "skip if already present" checks.
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
                // 🔴 燃料的硬排除。itemList 是上面三條建立路徑（EntrustItems、EntrustCategories、
                // multistack duplicates）的共同出口，也是底下「無條件存入」迴圈唯一的依據，所以在這裡
                // 拔掉一次就同時蓋住三條路，日後新增的來源也會自動被蓋到。
                // ⚠️ 但這**不包括**下面 !DuplicatesMultiStack 的那條 duplicates 迴圈：它的條件是
                // `!itemList.ContainsKey(...)`，從 itemList 拿掉反而會讓它開始處理燃料，所以那裡另外
                // 有一個明確的跳過。理由見 AutoBuyFuelManager.IsFuelReservedForAutoBuy。
                // ContainsKey 先問，是為了讓那條 Information 只在真的排除掉東西時才出現：
                // 計畫根本沒涵蓋燃料的人不需要看到它。
                if(itemList.ContainsKey(AutoBuyFuelManager.FuelItemId)
                    && AutoBuyFuelManager.IsFuelReservedForAutoBuy(AutoBuyFuelManager.FuelItemId))
                {
                    itemList.Remove(AutoBuyFuelManager.FuelItemId);
                }

                // 「裝備類別只存放可繳交的稀有品」。放在這裡的理由與上面燃料那段完全相同:itemList 是
                // 三條建立路徑(EntrustItems、EntrustCategories、multistack duplicates)的共同出口,
                // 在這裡拔一次就同時蓋住三條。
                // ⚠️ 同樣**不包括**下面 !DuplicatesMultiStack 的那條 duplicates 迴圈:它的條件是
                // 「不在 itemList 裡」,從 itemList 拿掉反而會讓它開始處理 —— 那裡另外有一個明確的跳過。
                // 📌 逐件點名在 EntrustItems 裡的道具刻意不受影響:那份清單的語意就是
                // 「不管規則怎麼寫,這幾件我要存」,使用者已經明確指名過了。
                if(plan.EntrustOnlyDeliverableGear && itemList.Count > 0)
                {
                    // 不能邊列舉邊移除,先收集再拔。
                    List<uint> dropGear = null;
                    foreach(var id in itemList.Keys)
                    {
                        if(plan.EntrustItems.Contains(id)) continue;
                        if(!IsNonDeliverableGear(id, s)) continue;
                        (dropGear ??= []).Add(id);
                    }
                    if(dropGear != null)
                    {
                        foreach(var id in dropGear)
                        {
                            itemList.Remove(id);
                            FlowSkippedGear.Add(id);
                        }
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
                            // Below the keep threshold: canFit can only ever lower toEntrust, so the
                            // outcome is already decided. Skipping here avoids a full ~175 slot scan of the
                            // retainer's pages for every slot that is merely being kept - and those slots
                            // never empty out, so they were being rescanned on every single pass for the
                            // whole run. This is the main reason stacked items felt so much slower: they
                            // leave a residual partial stack sitting at the keep threshold.
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
                            if(plan.ExcludeProtected && s.IMProtectList.Contains(item->ItemId)) continue;
                            // 見上面 itemList.Remove 那段：這條路徑的條件是「不在 itemList 裡」，
                            // 所以從 itemList 拔掉燃料對它是反效果，必須在這裡明確跳過。
                            if(AutoBuyFuelManager.IsFuelReservedForAutoBuy(item->ItemId)) continue;
                            // 同上,「裝備類別只存放可繳交的稀有品」的濾網也必須在這裡再擋一次。
                            // ⚠️ 目前裝備的 StackSize 都是 1,底下的 canFit 會算成 0 而自然跳過 ——
                            // 但那是**別的條件**碰巧擋住,不是這個選項在生效。不要拿它當保證。
                            if(plan.EntrustOnlyDeliverableGear
                                && !plan.EntrustItems.Contains(item->ItemId)
                                && IsNonDeliverableGear(item->ItemId, s))
                            {
                                if(itemCounts.ContainsKey(item->ItemId)) FlowSkippedGear.Add(item->ItemId);
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
