using AutoRetainer.Internal;
using AutoRetainer.Internal.InventoryManagement;
using AutoRetainer.Scheduler.Handlers;
using AutoRetainer.Scheduler.Tasks;
using AutoRetainer.Services;
using AutoRetainerAPI.Configuration;
using ECommons.ExcelServices;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System.Collections.Immutable;

namespace AutoRetainer.Scheduler;

internal static unsafe partial class SchedulerMain
{
    /// <summary>Takes the next retainer off the deferred entrust-duplicates/auto-vendor queue and
    /// enqueues its visit, one retainer per call. Returns whether anything was queued.
    ///
    /// Both steps only ever move items OUT of the player's inventory (entrust hands them to the
    /// retainer, auto-vendor sells them), so this is safe - and useful - to run while the inventory
    /// is too full to collect ventures.</summary>
    /// <remarks>
    /// 🔴 入列時算出來的「有工作」會過期（auto-vendor 與無條件存入讀的是所有僱員共用的玩家背包），所以開僱員之前重驗共用的那一半。
    /// 失敗方向鎖在安全側：讀不到、拿不到設定、有任何一絲不確定 ⇒ 照樣開啟。
    /// 少開一次會漏掉真正該做的搬運，多開一次只是浪費幾秒。
    /// </remarks>
    private static bool TryDrainEntrustVendorPass()
    {
        while(PendingEntrustVendorPostprocess.Count > 0)
        {
            var (next, duplicatesCandidates, room) = PendingEntrustVendorPostprocess[0];
            PendingEntrustVendorPostprocess.RemoveAt(0);
            if(!Utils.TryGetRetainerByName(next, out _)) continue;

            var adata = Utils.GetAdditionalData(Svc.PlayerState.ContentId, next);
            var selectedPlan = C.EntrustPlans.FirstOrDefault(x => x.Guid == adata.EntrustPlan && !x.ManualPlan);

            // Data null、背包讀不到、或開著時沒量到重複道具候選 ⇒ 不重驗，照樣把僱員開起來。
            var canRevalidate = Data != null && Utils.IsInventoryStateReadable() && duplicatesCandidates != null;
            TaskEntrustDuplicates.EntrustPrecheckHit precheck = default;
            if(canRevalidate
                && !TryFindPlayerHeldCandidate(selectedPlan, duplicatesCandidates, room, next, out precheck)
                && !TryFindSharedEntrustVendorWork(selectedPlan, room, next, out precheck))
            {
                if(room != null && (TryFindPlayerHeldCandidate(selectedPlan, duplicatesCandidates, null, next, out var dropped) || TryFindSharedEntrustVendorWork(selectedPlan, null, next, out dropped)))
                {
                    PluginLog.Information($"[EntrustVendorPass] Skipping {next}: everything the player still carries for it is something it had no room for when it was open (first: {dropped.Path} item={dropped.ItemId} hq={dropped.Hq} reason={RoomReason(room, dropped.ItemId, dropped.Hq)}; {room.NoRoom?.Count ?? 0} item/quality pair(s) judged no-room, freeSlots={room.FreeSlots}, fillableStacks={room.FillableStacks.Count}).");
                    continue;
                }
                PluginLog.Information($"[EntrustVendorPass] Skipping {next}: no shared inventory work is left (an earlier retainer in this batch already did it, or there was none), and the player no longer carries any of the {duplicatesCandidates.Count} item(s) this retainer could have taken as duplicates.");
                continue;
            }

            P.TaskManager.Enqueue(() => RetainerListHandlers.SelectRetainerByName(next));

            if(C.EnableEntrustManager && selectedPlan != null)
            {
                TaskEntrustDuplicates.EnqueueNew(selectedPlan, next, precheck, next);
            }

            if(Data.GetIMSettings().IMEnableAutoVendor)
            {
                TaskVendorItems.Enqueue();
            }

            if(C.RetainerMenuDelay > 0)
            {
                TaskWaitSelectString.Enqueue(C.RetainerMenuDelay);
            }
            P.TaskManager.Enqueue(RetainerHandlers.SelectQuit);
            P.TaskManager.Enqueue(RetainerHandlers.ConfirmCantBuyback);
            return true;
        }
        TaskEntrustDuplicates.ReportHubBatchEnd();
        return false;
    }

    /// <summary>The half of the deferred entrust/vendor work that is read entirely off the <b>player's</b> inventory: auto-vendor, and entrust's "unconditional" items/categories checked against the player's carried counts.
    /// That inventory is shared by every retainer, so this answer is not specific to any one of them.
    /// The plan is still per-retainer, so this is evaluated with that retainer's plan against the live shared inventory.
    /// </summary>
    /// <remarks>
    /// 讀不到的容器／格位一律 <c>continue</c>，也就是**只可能少報工作、不可能多報**。
    /// ⚠️ 解參考 null 在 .NET Core 是 corrupted-state exception，try/catch 攔不到，只能靠事前檢查。
    /// </remarks>
    private static bool HasSharedEntrustVendorWork(EntrustPlan? plan, string retainer) => TryFindSharedEntrustVendorWork(plan, null, retainer, out _);

    /// <summary><see cref="HasSharedEntrustVendorWork"/> 本體；<paramref name="hit"/> 是讓它回 true 的第一件（給空開診斷用）。</summary>
    /// <param name="room">null＝不篩；否則跳過快照判定放不進的（道具, HQ）——實際存放對這些道具只走「無條件存入」那圈，那圈放不下就不搬。</param>
    /// <param name="retainer">要開的僱員；集中僱員規則要和實際存放取同一位。</param>
    private static unsafe bool TryFindSharedEntrustVendorWork(EntrustPlan? plan, EntrustRoomSnapshot room, string retainer, out TaskEntrustDuplicates.EntrustPrecheckHit hit)
    {
        hit = default;
        var vs = Data.GetIMSettings();

        if(vs.IMEnableAutoVendor)
        {
            foreach(var invType in InventorySpaceManager.GetAllowedToSellInventoryTypes())
            {
                var inv = InventoryManager.Instance()->GetInventoryContainer(invType);
                if(inv == null || inv->Items == null) continue;
                for(var i = 0; i < inv->Size; i++)
                {
                    var item = inv->Items[i];
                    if(item.ItemId == 0) continue;
                    if((item.Quantity < vs.IMAutoVendorHardStackLimit || vs.IMAutoVendorHardIgnoreStack.Contains(item.ItemId))
                        && vs.IMAutoVendorHard.Contains(item.ItemId)
                        && !TaskDesynthItems.DesynthEligible(item.ItemId))
                    {
                        hit = new(TaskEntrustDuplicates.EntrustPrecheckPath.Vendor, item.ItemId, item.Flags.HasFlag(InventoryItem.ItemFlags.HighQuality));
                        return true;
                    }
                }
            }
        }

        if(!C.EnableEntrustManager || plan == null)
        {
            return false;
        }

        var allowedPlayerInventories = plan.GetAllowedInventories();
        var hub = TradeableGearHub.Resolve(retainer);

        //unconditional entrusts: player is carrying more than the configured keep-amount
        foreach(var type in allowedPlayerInventories)
        {
            var inv = InventoryManager.Instance()->GetInventoryContainer(type);
            if(inv == null) continue;
            for(var i = 0; i < inv->Size; i++)
            {
                var item = InventoryManager.Instance()->GetInventorySlot(type, i);
                if(item == null) continue;
                if(item->ItemId == 0 || item->Quantity == 0) continue;
                // 存入那一側整件排除的道具（保護清單、燃料保留、裝備濾網）不能回報「有工作」——
                // 否則僱員會被開起來、發現沒東西可搬、再關掉（空開）。判斷與存入那一側是同一支。
                if(TaskEntrustDuplicates.GetEntrustExclusion(item->ItemId, plan, vs, hub) != TaskEntrustDuplicates.EntrustExclusion.None) continue;

                int? toKeep = null;
                if(plan.EntrustItems.Contains(item->ItemId))
                {
                    toKeep = plan.EntrustItemsAmountToKeep.SafeSelect(item->ItemId);
                }
                else
                {
                    var data = ExcelItemHelper.Get(item->ItemId);
                    if(data != null && plan.EntrustCategories.TryGetFirst(c => c.ID == data.Value.ItemUICategory.RowId, out var catInfo))
                    {
                        toKeep = catInfo.AmountToKeep;
                    }
                }

                if(toKeep != null && Utils.GetItemCount(allowedPlayerInventories, item->ItemId) > toKeep)
                {
                    if(room != null && IsKnownNoRoom(room, item->ItemId, item->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality))) continue;
                    hit = new(TaskEntrustDuplicates.EntrustPrecheckPath.SharedEntrust, item->ItemId, item->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality));
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>The half of the deferred entrust/vendor work that belongs to <b>this specific retainer</b>: entrust-duplicates.
    /// 🔴 Only collectable while this retainer is open - the retainer containers are not mapped otherwise.
    /// This is the half of the duplicates rule that depends on the retainer, and the only half that cannot change while it is left alone, so it is what the queue carries.
    /// The set therefore only ever needs the retainer-side conditions applied.
    /// </summary>
    /// <remarks>
    /// 讀不到的容器／格位一律 <c>continue</c>，也就是**只可能少收候選、不可能多收**。少收＝可能少開一次僱員，
    /// 所以這裡刻意收得寬：品質(HQ)不納入比對、multi-stack 不預判數量，一律把判斷留給 drain 時的實際掃描。
    /// ⚠️ 解參考 null 在 .NET Core 是 corrupted-state exception，try/catch 攔不到，只能靠事前檢查。
    /// </remarks>
    private static unsafe HashSet<uint> CollectDuplicatesCandidates(EntrustPlan? plan)
    {
        HashSet<uint> candidates = [];
        if(!C.EnableEntrustManager || plan == null || !plan.Duplicates)
        {
            return candidates;
        }

        var vs = Data.GetIMSettings();

        foreach(var type in Utils.RetainerInventoriesWithCrystals)
        {
            if(type.EqualsAny(InventoryType.Crystals, InventoryType.RetainerCrystals)) continue;
            var inv = InventoryManager.Instance()->GetInventoryContainer(type);
            if(inv == null) continue;
            for(var i = 0; i < inv->Size; i++)
            {
                var item = inv->GetInventorySlot(i);
                if(item == null) continue;
                if(item->ItemId == 0) continue;
                if(plan.ExcludeProtected && vs.IMProtectList.Contains(item->ItemId)) continue;
                // 燃料不會被存入（AutoBuyFuelManager.IsFuelReservedForAutoBuy），所以僱員身上的燃料不構成「有工作」。
                if(AutoBuyFuelManager.IsFuelReservedForAutoBuy(item->ItemId)) continue;

                if(plan.DuplicatesMultiStack)
                {
                    candidates.Add(item->ItemId);
                    continue;
                }

                // 只有這個變體多一個僱員側的前提：這裡的堆疊要還有空間可以補。
                var data = ExcelItemHelper.Get(item->ItemId);
                if(data == null || data.Value.IsUnique) continue;
                if(data.Value.StackSize - item->Quantity <= 0) continue;
                candidates.Add(item->ItemId);
            }
        }

        return candidates;
    }

    /// <summary>Whether the player is currently carrying any of <paramref name="itemIds"/> in the
    /// containers this plan is allowed to take from. This is the live, player-side half of the duplicates
    /// rule - see <see cref="CollectDuplicatesCandidates"/> for the frozen retainer-side half.</summary>
    /// <remarks>
    /// 刻意比 duplicates 的真正條件寬：不比對品質(HQ)、不看數量夠不夠、不管堆疊放不放得下。
    /// 這是「有沒有可能有工作」的必要條件而非充分條件，回 <c>true</c> 只代表「還得開起來看」。
    /// 寬 ⇒ 失敗方向是多開一次僱員，不是漏搬。
    /// </remarks>
    private static bool PlayerHoldsAnyOf(EntrustPlan? plan, HashSet<uint> itemIds, string retainer) => TryFindPlayerHeldCandidate(plan, itemIds, null, retainer, out _);

    /// <summary><see cref="PlayerHoldsAnyOf"/> 本體；<paramref name="hit"/> 是讓它回 true 的第一件（給空開診斷用）。</summary>
    /// <param name="room">null＝不篩。只在多堆疊模式篩：那時重複道具只經「無條件存入」那圈搬（放不下就不搬）；
    /// 補堆疊模式的實際條件是「同品質的堆疊還有空間」，不是同一支判斷，所以不篩。</param>
    private static unsafe bool TryFindPlayerHeldCandidate(EntrustPlan? plan, HashSet<uint> itemIds, EntrustRoomSnapshot room, string retainer, out TaskEntrustDuplicates.EntrustPrecheckHit hit)
    {
        hit = default;
        if(plan == null || itemIds.Count == 0) return false;
        var hub = TradeableGearHub.Resolve(retainer);

        InventoryManagementSettings vs = null;
        foreach(var type in plan.GetAllowedInventories())
        {
            var inv = InventoryManager.Instance()->GetInventoryContainer(type);
            if(inv == null) continue;
            for(var i = 0; i < inv->Size; i++)
            {
                var item = inv->GetInventorySlot(i);
                if(item == null) continue;
                if(item->ItemId == 0 || item->Quantity <= 0) continue;
                if(!itemIds.Contains(item->ItemId)) continue;
                // 排除在這裡現場判、不在收集候選時凍結：設定在入列與開僱員之間改了，也照當下的規則走。
                vs ??= Data.GetIMSettings();
                if(TaskEntrustDuplicates.GetEntrustExclusion(item->ItemId, plan, vs, hub) != TaskEntrustDuplicates.EntrustExclusion.None) continue;
                if(room != null && plan.DuplicatesMultiStack && IsKnownNoRoom(room, item->ItemId, item->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality))) continue;
                hit = new(TaskEntrustDuplicates.EntrustPrecheckPath.Duplicates, item->ItemId, item->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality));
                return true;
            }
        }

        return false;
    }

    /// <summary>僱員開著、選單就緒時，把 <see cref="CollectDuplicatesCandidates"/> 與「放不放得進」快照（<see cref="CollectEntrustNoRoom"/>＋<see cref="MeasureRoom"/>）記到這個僱員的延後批次項目上。只在框架執行緒的任務回呼裡跑。</summary>
    /// <remarks>量不到（選單被探險視窗蓋住、最近的僱員不是它、僱員背包讀不到、項目已被清掉）就不記 ⇒ 重驗照舊開，失敗方向是多開一次不是漏存。</remarks>
    /// <param name="queuedOnlyForDuplicates">這次入列只因為「可能有重複道具」：量到之後確定沒有任何工作就撤掉，等同入列當下就知道答案。</param>
    private static bool? CaptureEntrustNoRoom(string retainer, EntrustPlan plan, bool queuedOnlyForDuplicates)
    {
        if(TryGetAddonByName<AtkUnitBase>("RetainerTaskSupply", out _)) return true;
        if(!TryGetAddonByName<AtkUnitBase>("SelectString", out var addon) || !IsAddonReady(addon)) return false;
        var index = PendingEntrustVendorPostprocess.FindIndex(x => x.Retainer == retainer);
        if(index < 0) return true;
        if(!Utils.TryGetCurrentRetainer(out var current) || current != retainer)
        {
            PluginLog.Debug($"[EntrustVendorPass] {retainer}: not measuring room, the nearest retainer is {current ?? "none"}");
            return true;
        }
        var noRoom = CollectEntrustNoRoom(plan);
        if(noRoom == null)
        {
            // 背包讀不到時候選集合也收不準（讀不到的格位只會少收），所以兩者都不記。
            PluginLog.Debug($"[EntrustVendorPass] {retainer}: not measuring, retainer containers unreadable");
            return true;
        }
        var candidates = CollectDuplicatesCandidates(plan);
        if(queuedOnlyForDuplicates && !PlayerHoldsAnyOf(plan, candidates, retainer) && !HasSharedEntrustVendorWork(plan, retainer))
        {
            PendingEntrustVendorPostprocess.RemoveAt(index);
            PluginLog.Debug($"[EntrustVendorPass] {retainer}: dropped from the batch, the player carries none of its {candidates.Count} duplicate candidate(s) and there is no shared work");
            return true;
        }
        var room = MeasureRoom(noRoom);
        PendingEntrustVendorPostprocess[index] = PendingEntrustVendorPostprocess[index] with { DuplicatesCandidates = candidates, Room = room };
        PluginLog.Debug($"[EntrustVendorPass] {retainer}: {candidates.Count} duplicate candidate(s); no room for {noRoom.Count} item/quality pair(s) the player carries; freeSlots={room.FreeSlots}, fillableStacks={room.FillableStacks.Count}, heldReadable={room.HeldItemIds != null}");
        return true;
    }

    /// <summary>
    /// 量這個開著的僱員的空格數、可補的堆疊、身上有哪些道具（給獨占道具），讓量完之後玩家才拿到的道具也能判斷。
    /// 走的容器、格位與旗標判斷和 <see cref="Utils.GetAmountThatCanFit"/> 相同；讀不到的部分記成「不知道」，重驗時照常打開。
    /// </summary>
    private static unsafe EntrustRoomSnapshot MeasureRoom(HashSet<(uint ItemId, bool Hq)> noRoom)
    {
        var room = new EntrustRoomSnapshot { NoRoom = noRoom, BellSession = EntrustBellSession };
        var im = InventoryManager.Instance();
        foreach(var type in Utils.RetainerInventoriesWithCrystals)
        {
            if(type.EqualsAny(InventoryType.Crystals, InventoryType.RetainerCrystals)) continue;
            var inv = im->GetInventoryContainer(type);
            if(inv == null || inv->Items == null || inv->Size <= 0)
            {
                room.PageUnreadable();
                continue;
            }
            for(var i = 0; i < inv->Size; i++)
            {
                var item = im->GetInventorySlot(type, i);
                if(item == null)
                {
                    room.PageUnreadable();
                    break;
                }
                int? stackSize = item->ItemId == 0 ? null : (int?)ExcelItemHelper.Get(item->ItemId)?.StackSize;
                room.AddPageSlot(item->ItemId, item->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality), item->Flags.HasFlag(InventoryItem.ItemFlags.Collectable), item->Quantity, stackSize);
            }
        }
        foreach(var type in Utils.RetainerEntireInventory)
        {
            var inv = im->GetInventoryContainer(type);
            if(inv == null || inv->Items == null)
            {
                room.HeldUnreadable();
                break;
            }
            for(var i = 0; i < inv->Size; i++) room.AddHeldItem(inv->Items[i].ItemId);
        }
        return room;
    }

    private static bool IsKnownNoRoom(EntrustRoomSnapshot room, uint itemId, bool hq) => RoomReason(room, itemId, hq) != EntrustRoomSnapshot.NoRoomReason.None;

    private static EntrustRoomSnapshot.NoRoomReason RoomReason(EntrustRoomSnapshot room, uint itemId, bool hq)
    {
        var data = ExcelItemHelper.Get(itemId);
        return room.Reason(itemId, hq, data != null, data?.IsUnique == true, data?.ItemUICategory.RowId == 59, EntrustBellSession);
    }

    /// <summary>玩家身上（計畫允許的容器）哪些（道具, HQ）這個僱員現在一件都放不進；null＝僱員背包讀不到，不能下結論。</summary>
    /// <remarks>
    /// 🔴 判斷就是實際存放「無條件存入」那圈用的同一支 <see cref="Utils.GetAmountThatCanFit"/>（同樣的容器、同樣的 HQ），回 0 才記。
    /// 讀不到的容器在那支裡也是回 0，所以先擋：分頁讀不到整個不記；市場／裝備欄讀不到時獨佔道具不記（只有它會看那兩格）。
    /// 量完之後玩家才拿到的道具不在集合裡，交給 <see cref="MeasureRoom"/> 的空格數判斷。
    /// </remarks>
    internal static unsafe HashSet<(uint ItemId, bool Hq)> CollectEntrustNoRoom(EntrustPlan plan)
    {
        foreach(var type in Utils.RetainerInventoriesWithCrystals)
        {
            var inv = InventoryManager.Instance()->GetInventoryContainer(type);
            if(inv == null || inv->Items == null || inv->Size <= 0) return null;
        }
        var uniqueReadable = true;
        foreach(var type in Utils.RetainerEntireInventory)
        {
            if(!type.IsInventoryReadable()) uniqueReadable = false;
        }
        HashSet<(uint ItemId, bool Hq)> seen = [];
        HashSet<(uint ItemId, bool Hq)> noRoom = [];
        foreach(var type in plan.GetAllowedInventories())
        {
            var inv = InventoryManager.Instance()->GetInventoryContainer(type);
            if(inv == null) continue;
            for(var i = 0; i < inv->Size; i++)
            {
                var item = inv->GetInventorySlot(i);
                if(item == null || item->ItemId == 0 || item->Quantity <= 0) continue;
                var key = (item->ItemId, item->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality));
                if(!seen.Add(key)) continue;
                if(!uniqueReadable && ExcelItemHelper.Get(key.Item1)?.IsUnique != false) continue;
                if(Utils.GetAmountThatCanFit(Utils.RetainerInventoriesWithCrystals, key.Item1, key.Item2) == 0) noRoom.Add(key);
            }
        }
        return noRoom;
    }
}
