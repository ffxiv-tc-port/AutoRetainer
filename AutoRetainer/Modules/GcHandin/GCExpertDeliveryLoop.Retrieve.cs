using AutoRetainer.Internal;
using AutoRetainer.Internal.InventoryManagement;
using AutoRetainer.Scheduler.Handlers;
using AutoRetainer.Scheduler.Tasks;
using AutoRetainerAPI.Configuration;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons.ExcelServices;
using ECommons.GameHelpers;
using ECommons.IPC;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace AutoRetainer.Modules.GcHandin;

internal static unsafe partial class GCExpertDeliveryLoop
{
    /// <summary>可以拿去繳交稀有品的裝備。判定式與 <see cref="GCContinuation.DoesInventoryHaveDeliverableItem"/>
    /// 完全一致 —— 兩邊只要有一邊寬,循環就會取回繳交不掉的東西,把背包塞滿之後每一輪都做白工。</summary>
    internal static bool IsDeliverableGear(uint itemId) => IsDeliverableGear(itemId, Data.GetIMSettings());

    /// <summary>同一個判定式,但由呼叫端提供已經取得的道具管理設定。
    ///
    /// <para>存在的理由是效能:<c>GetIMSettings()</c> 在任何一個「附加模式」開著的時候
    /// **每次呼叫都會做一次 <c>DSFClone()</c> 深層複製**,而存放流程(TaskEntrustDuplicates)
    /// 一輪要對背包裡的每一件裝備問一次。取裝備那邊一格只問一次,用無參數版本即可。</para>
    ///
    /// <para>🔴 判定式只有這一份。存放端的「裝備類別只存放可繳交的稀有品」濾網也是呼叫這裡,
    /// 不另外抄一份 —— 兩邊一旦分岔,使用者看到的就是「存進去的東西取不回來」。</para></summary>
    internal static bool IsDeliverableGear(uint itemId, InventoryManagementSettings imSettings)
    {
        if(itemId == 0) return false;
        if(imSettings.IMProtectList.Contains(itemId)) return false;
        var data = ExcelItemHelper.Get(itemId);
        if(data == null) return false;
        if(!data.Value.ItemUICategory.RowId.EqualsAny(Utils.GearUICategories)) return false;
        if(!data.Value.GetRarity().EqualsAny(ItemRarity.Green, ItemRarity.Pink, ItemRarity.Blue)) return false;
        if(data.Value.Desynth == 0) return false;
        return true;
    }

    /// <summary>目前開著的僱員身上,這一趟還沒送過指令、也還沒被判定拿不到的第一件可繳交裝備。</summary>
    private static uint FindGearOnOpenRetainer()
    {
        foreach(var type in Utils.RetainerInventories)
        {
            var inv = RetainerRetrieve.TryGetReadableContainer(type);
            if(inv == null) continue;
            for(var i = 0; i < inv->Size; i++)
            {
                var item = inv->GetInventorySlot(i);
                if(item == null) continue;
                if(item->ItemId == 0 || item->Quantity <= 0) continue;
                if(SkippedItems.Contains(item->ItemId)) continue;
                if(DeferredItems.Contains(item->ItemId)) continue;
                if(!IsDeliverableGear(item->ItemId)) continue;
                return item->ItemId;
            }
        }
        return 0;
    }

    /// <summary>僱員身上還剩幾格有東西,用來判斷「這一批指令落地了沒」。</summary>
    private static int CountOccupiedRetainerSlots()
    {
        var used = 0;
        foreach(var type in Utils.RetainerInventories)
        {
            var inv = RetainerRetrieve.TryGetReadableContainer(type);
            if(inv == null) continue;
            for(var i = 0; i < inv->Size; i++)
            {
                var item = inv->GetInventorySlot(i);
                if(item == null) continue;
                if(item->ItemId != 0 && item->Quantity > 0) used++;
            }
        }
        return used;
    }

    private static bool AllRetainerPagesReadable()
    {
        foreach(var type in Utils.RetainerInventories)
        {
            if(RetainerRetrieve.TryGetReadableContainer(type) == null) return false;
        }
        return true;
    }

    /// <summary>打開僱員那一刻比對清單件數與實際格數；不符就整趟不再用清單件數跳過。</summary>
    private static void CalibrateListItemCount(string name)
    {
        var listCount = SelectedListItemCount;
        SelectedListItemCount = -1;
        var readable = AllRetainerPagesReadable();
        var occupied = CountOccupiedRetainerSlots();
        switch(ExpertDeliveryLoopSkipRules.Calibrate(listCount, occupied, readable))
        {
            case ExpertDeliveryLoopSkipRules.Calibration.Match:
                if(!ListCalibratedThisBell && BellOpenedByLoop)
                {
                    PluginLog.Information($"[ExpertDeliveryLoop] {name}: retainer list count {listCount} matches the storage, so retainers the list shows as empty are skipped for the rest of this bell visit.");
                }
                ListCalibratedThisBell = true;
                break;
            case ExpertDeliveryLoopSkipRules.Calibration.Mismatch:
                if(!ListCountDistrusted)
                {
                    PluginLog.Information($"[ExpertDeliveryLoop] {name}: retainer list says {listCount} item(s) but the storage holds {occupied}. Retainers will not be skipped by list count for the rest of this run.");
                }
                ListCountDistrusted = true;
                break;
            default:
                PluginLog.Information($"[ExpertDeliveryLoop] {name}: could not compare the retainer list count ({listCount}) with the storage (readable={readable}); this retainer does not count toward trusting the list.");
                break;
        }
    }

    private static bool ReserveReached => Utils.GetInventoryFreeSlotCount() <= EffectiveReservedSlots;

    private static void TickSelectRetainer()
    {
        if(Utils.IsBusy) return;

        if(ReserveReached)
        {
            PluginLog.Information($"[ExpertDeliveryLoop] Inventory down to {Utils.GetInventoryFreeSlotCount()} free slots (reserve {EffectiveReservedSlots}), {(ArmoryBufferActive ? "moving gear into the armoury first" : "going to hand in")}.");
            if(TryStartStow(StowContext.RetainerList)) return;
            RoundEndReason = RoundEnd.ReserveReached;
            SetPhase(Phase.CloseBell);
            return;
        }

        if(RetainerIndex >= Retainers.Count)
        {
            // 名單走完了。RoundEndReason 維持 NoGearLeft,繳完這批就收工。
            SetPhase(Phase.CloseBell);
            return;
        }

        // 🔴 存在性判斷的前置條件要在**每一次**判斷之前重驗,不能只靠「進這個階段之前驗過一次」。
        //    清單會在換區、關鈴、繳交來回之後失效,而失效狀態下 TryGetRetainerByName 對每個名字
        //    都回 false,會把僱員全判成「已經不存在」。
        if(!RetainerListLoaded)
        {
            if(TimeInPhase > RetainerListLoadTimeoutMs)
            {
                Stop(Loc.T("Stopped: the retainer list is not loaded, so it cannot be told whether the retainers are still there."));
            }
            return;
        }

        var name = Retainers[RetainerIndex];
        // 只有在上面那道閘門為 true 時,這個 false 才真的代表「這個僱員不存在」。
        if(!Utils.TryGetRetainerByName(name, out var listed))
        {
            DuoLog.Warning(string.Format(Loc.T("Retainer \"{0}\" no longer exists, skipping."), name));
            PluginLog.Information($"[ExpertDeliveryLoop] Retainer {name} is not in the game's retainer list, skipping.");
            RetainerIndex++;
            return;
        }

        var listItemCount = listed.ItemCount;
        var listTrusted = ExpertDeliveryLoopSkipRules.IsListTrusted(BellOpenedByLoop, IsRetainerListReady(), ListCalibratedThisBell, ListCountDistrusted);
        var skip = ExpertDeliveryLoopSkipRules.Decide(EmptiedRetainers.Contains(name), listItemCount, listTrusted);
        if(skip != ExpertDeliveryLoopSkipRules.RetainerVisitSkip.None)
        {
            var emptied = skip == ExpertDeliveryLoopSkipRules.RetainerVisitSkip.AlreadyEmptied;
            if(emptied) VisitsSkippedEmptied++;
            else VisitsSkippedListEmpty++;
            PluginLog.Information($"[ExpertDeliveryLoop] Not opening {name} ({RetainerIndex + 1}/{Retainers.Count}): {(emptied ? "already emptied of deliverable gear earlier for this character, and the loop never puts gear back" : "the retainer list from this loop's own bell visit shows 0 items, and an earlier retainer's list count matched its storage on this visit")}. listItems={listItemCount}");
            RetainerIndex++;
            return;
        }

        SelectedListItemCount = listItemCount;
        PluginLog.Information($"[ExpertDeliveryLoop] Opening item storage of {name} ({RetainerIndex + 1}/{Retainers.Count}). listItems={listItemCount} listTrusted={listTrusted}");
        P.TaskManager.Enqueue(() => RetainerListHandlers.SelectRetainerByName(name), $"SelectRetainerByName({name})");
        P.TaskManager.Enqueue(() => Utils.TryGetCurrentRetainer(out _), $"WaitCurrentRetainer({name})");
        P.TaskManager.Enqueue(RetainerHandlers.SelectEntrustItems, $"SelectEntrustItems({name})");
        P.TaskManager.Enqueue(InventorySpaceManager.IsRetainerInventoryLoaded, $"WaitRetainerInventoryLoaded({name})");
        SetPhase(Phase.SelectRetainerWait);
    }

    private static void TickSelectRetainerWait()
    {
        if(!ChainFinished) return;

        if(!InventorySpaceManager.IsRetainerInventoryLoaded())
        {
            Stop(string.Format(Loc.T("Stopped: could not open the item storage of \"{0}\"."), Retainers[RetainerIndex]));
            return;
        }

        CalibrateListItemCount(Retainers[RetainerIndex]);
        if(CheckProgressOnNextOpen)
        {
            CheckProgressOnNextOpen = false;
            OccupiedAtStowResume = CountOccupiedRetainerSlots();
        }
        RetainerRetrieve.ResetTracking();
        SkippedItems.Clear();
        DeferredItems.Clear();
        PassFired = 0;
        SetPhase(Phase.Retrieve);
    }

    private static void TickRetrieve()
    {
        if(ReserveReached)
        {
            PluginLog.Information($"[ExpertDeliveryLoop] Reserve reached while retrieving ({Utils.GetInventoryFreeSlotCount()} free, reserve {EffectiveReservedSlots}).");
            if(TryStowFromRetainerWindow()) return;
            RoundEndReason = RoundEnd.ReserveReached;
            SetPhase(Phase.LeaveRetainer);
            return;
        }

        if(!InventorySpaceManager.IsRetainerInventoryLoaded())
        {
            Stop(Loc.T(SharedText.StoppedRetainerStorageClosed));
            return;
        }

        if(!EzThrottler.Throttle("ExpertDeliveryLoopRetrieve", RetrieveIntervalMs)) return;

        var itemId = FindGearOnOpenRetainer();
        if(itemId == 0)
        {
            // 這一趟沒有還能送指令的東西了。有送出過就等它們落地再看一次;
            // 一次都沒送過就代表這個僱員真的沒有可取的裝備。
            // 🔴 僱員背包有東西但全都不是稀有品時也是走這裡,不可以去等一個永遠不會來的取回。
            if(PassFired > 0)
            {
                SettleLastCount = CountOccupiedRetainerSlots();
                SettleLastChangeAt = Environment.TickCount64;
                SetPhase(Phase.RetrieveSettle);
                return;
            }
            OccupiedAtStowResume = -1;
            var emptied = ExpertDeliveryLoopSkipRules.IsEmptiedVerdict(true, SkippedItems.Count, DeferredItems.Count, AllRetainerPagesReadable());
            if(emptied) EmptiedRetainers.Add(Retainers[RetainerIndex]);
            PluginLog.Information($"[ExpertDeliveryLoop] {Retainers[RetainerIndex]} has no more deliverable gear. emptied={emptied} skipped={SkippedItems.Count} deferred={DeferredItems.Count}");
            SetPhase(Phase.LeaveRetainer);
            return;
        }

        var result = RetainerRetrieve.RetrieveSlotById(itemId, false, false);
        if(result >= 1)
        {
            RetrievedTotal++;
            RetrievedThisRound++;
            PassFired++;
            RetrievedItemIds.Add(itemId);
            // 送出去了就先不要再看這一件,改去對別的格子送 —— 不必站在這裡等它落地。
            DeferredItems.Add(itemId);
            return;
        }

        switch(result)
        {
            case RetainerRetrieve.ResultCommandInFlight:
                // 這一件還在飛。先跳過去做別的,等這一趟送完再統一等落地。
                DeferredItems.Add(itemId);
                return;

            case RetainerRetrieve.ResultInventoryFull:
                if(TryStowFromRetainerWindow()) return;
                RoundEndReason = RoundEnd.ReserveReached;
                SetPhase(Phase.LeaveRetainer);
                return;

            case RetainerRetrieve.ResultBlockedUnique:
                RoundBlockedUnique++;
                goto case RetainerRetrieve.ResultNotPresent;
            case RetainerRetrieve.ResultInCrystals:
            case RetainerRetrieve.ResultNotPresent:
                SkippedItems.Add(itemId);
                PluginLog.Information($"[ExpertDeliveryLoop] Skipping item {itemId} ({ExcelItemHelper.GetName(itemId)}): retrieve returned {result}.");
                return;

            case RetainerRetrieve.ResultRetainerUnavailable:
                // 🔴 「讀不到」不等於「沒有」。
                Stop(string.Format(Loc.T("Stopped: could not read the storage of \"{0}\"."), Retainers[RetainerIndex]));
                return;

            default:
                Stop(string.Format(Loc.T("Stopped: unexpected retrieve result {0}."), result));
                return;
        }
    }

    /// <summary>等這一趟送出去的指令落地。判準是「僱員格數不再變動」而不是固定秒數 ——
    /// 送指令比伺服器消化快,固定秒數會嚴重低估進度。</summary>
    private static void TickRetrieveSettle()
    {
        if(!InventorySpaceManager.IsRetainerInventoryLoaded())
        {
            Stop(Loc.T(SharedText.StoppedRetainerStorageClosed));
            return;
        }
        if(!EzThrottler.Throttle("ExpertDeliveryLoopSettle", SettlePollMs)) return;

        var now = Environment.TickCount64;
        var count = CountOccupiedRetainerSlots();
        if(count != SettleLastCount)
        {
            SettleLastCount = count;
            SettleLastChangeAt = now;
            return;
        }

        if(now - SettleLastChangeAt < SettleQuietMs && now - PhaseEnteredAt < SettleCapMs) return;

        if(CheckRetrieveProgressAfterStow(count)) return;
        if(StowAfterSettle)
        {
            StowAfterSettle = false;
            if(TryStartStow(StowContext.RetainerWindow)) return;
            RoundEndReason = RoundEnd.ReserveReached;
            SetPhase(Phase.LeaveRetainer);
            return;
        }

        // 落地了(或等太久)。重新開一趟:上一趟被伺服器拒收的格子這時候會重新被納入。
        RetainerRetrieve.ResetTracking();
        DeferredItems.Clear();
        PassFired = 0;
        SetPhase(Phase.Retrieve);
    }

    /// <summary>關掉道具管理視窗、離開這個僱員,回到僱員清單。
    /// 🔴 不能只送「關閉僱員代理」就去等「不再被佔用」:站在鈴前本來就一直是被佔用狀態,永遠等不到。
    /// 正式流程的收尾是「關道具管理 → 在僱員選單選告辭 → 回到僱員清單」,這裡照抄。</summary>
    private static void TickLeaveRetainer()
    {
        PluginLog.Information($"[ExpertDeliveryLoop] Leaving retainer {Retainers[RetainerIndex]}.");
        P.TaskManager.Enqueue(RetainerHandlers.CloseAgentRetainer, "CloseAgentRetainer");
        P.TaskManager.Enqueue(RetainerHandlers.SelectQuit, "SelectQuit");
        P.TaskManager.Enqueue(() => TryGetAddonByName<AtkUnitBase>("RetainerList", out var a) && IsAddonReady(a), "WaitRetainerList");
        SetPhase(Phase.LeaveRetainerWait);
    }

    private static void TickLeaveRetainerWait()
    {
        if(!ChainFinished) return;

        if(!TryGetAddonByName<AtkUnitBase>("RetainerList", out var list) || !IsAddonReady(list))
        {
            Stop(Loc.T("Stopped: could not get back to the retainer list."));
            return;
        }

        if(KeepRetainerIndex) KeepRetainerIndex = false;
        else RetainerIndex++;
        SetPhase(RoundEndReason == RoundEnd.ReserveReached ? Phase.CloseBell : Phase.SelectRetainer);
    }

    /// <summary>關掉僱員清單,離開傳喚鈴。</summary>
    private static void TickCloseBell()
    {
        BellOpenedByLoop = false;
        ListCalibratedThisBell = false;
        P.TaskManager.Enqueue(RetainerListHandlers.CloseRetainerList, "CloseRetainerList");
        P.TaskManager.Enqueue(() => !IsOccupied(), "WaitUntilNotOccupiedAfterBell");
        SetPhase(Phase.CloseBellWait);
    }

    private static void TickCloseBellWait()
    {
        if(!ChainFinished) return;
        if(IsOccupied())
        {
            Stop(Loc.T("Stopped: could not leave the summoning bell."));
            return;
        }
        if(ClosedBellStowPending)
        {
            ClosedBellStowPending = false;
            if(TryStartStow(StowContext.BellClosed)) return;
        }
        SetPhase(Phase.Handin);
    }
}
