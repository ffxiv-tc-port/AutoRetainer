using AutoRetainer.Internal;
using AutoRetainer.Internal.InventoryManagement;
using AutoRetainerAPI.Configuration;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace AutoRetainer.Modules.GcHandin;

/// <summary>
/// 兵裝庫緩衝：背包到保留格時，把這一趟從僱員取回的可繳交裝備搬進兵裝庫，再繼續取。
/// 🔴 契約：只搬 <see cref="RetrievedItemIds"/> 裡、而且角色開始時不在那一格的道具；使用者原本的東西不碰。
/// 每次進搬移都要搬成功至少一件才回去取，兵裝庫空間有限，所以「取→搬」的來回有上界。
/// 搬不動、被拒、搬了之後取不到東西，一律關掉這個角色的緩衝、照原本流程去繳交。
/// </summary>
internal static unsafe partial class GCExpertDeliveryLoop
{
    private enum StowContext
    {
        /// <summary>僱員道具視窗開著。</summary>
        RetainerWindow,
        /// <summary>僱員清單開著。</summary>
        RetainerList,
        /// <summary>已經關鈴。前兩種被拒時的退路。</summary>
        BellClosed,
    }

    /// <summary>這個角色開始時設定與繳交模式都符合；中途被關掉也保持 true（決定要不要判無進展停止）。</summary>
    private static bool ArmoryBufferEnabledForCharacter;
    private static bool ArmoryBufferActive;
    private static readonly HashSet<uint> RetrievedItemIds = [];
    private static readonly HashSet<(InventoryType Type, int Slot, uint ItemId)> BagSlotsAtStart = [];
    /// <summary>這個角色由循環放進兵裝庫、確認還在的道具。繳交閘門與無進展判斷都看它。</summary>
    private static readonly List<ArmoryStow.Moved> LoopStowed = [];
    private static ArmoryStow.Batch StowBatch;
    private static StowContext StowBatchContext;
    private static bool StowAfterSettle;
    /// <summary>鈴前搬移被拒過；這個角色之後一律關鈴再搬。</summary>
    private static bool StowWithBellClosed;
    private static bool ClosedBellStowPending;
    private static bool KeepRetainerIndex;
    private static bool CheckProgressOnNextOpen;
    /// <summary>搬完回去取時僱員身上的格數；-1＝不需要檢查。下一次落地檢查時格數沒減少＝搬移其實沒被收。</summary>
    private static int OccupiedAtStowResume = -1;
    private static int RoundStowed;
    /// <summary>這一輪第一次搬之前兵裝庫各分類扣保留格後的空間總和；-1＝這一輪還沒量。
    /// 繳交之間兵裝庫只會變滿，所以一輪搬的件數超過它＝搬過去的東西被彈回來又被重搬。</summary>
    private static int RoundRoomAtFirstStow = -1;
    private static int RoundBlockedUnique;
    private static int RoundBagBefore;
    private static int RoundArmoryBefore;

    private static void BeginArmoryBuffer()
    {
        RetrievedItemIds.Clear();
        BagSlotsAtStart.Clear();
        LoopStowed.Clear();
        StowBatch = null;
        StowAfterSettle = false;
        StowWithBellClosed = false;
        ClosedBellStowPending = false;
        KeepRetainerIndex = false;
        CheckProgressOnNextOpen = false;
        OccupiedAtStowResume = -1;
        RoundStowed = 0;
        RoundBlockedUnique = 0;
        RoundRoomAtFirstStow = -1;
        ArmoryBufferEnabledForCharacter = false;
        ArmoryBufferActive = false;
        if(!C.ExpertDeliveryLoopUseArmoryBuffer) return;

        var mode = Utils.GetCurrentCharacterData()?.GCDeliveryType;
        string why = null;
        if(!ArmoryStow.DeliveryModeUsesArmory(mode)) why = ArmoryStow.DeliveryModeNotUsingArmory;
        else if(!ArmoryStow.MoveAvailable) why = "MoveItemSlot is not resolved on this client";
        else if(!SnapshotBags()) why = "the bags could not be read";
        ArmoryBufferEnabledForCharacter = why == null;
        ArmoryBufferActive = why == null;
        PluginLog.Information($"[ExpertDeliveryLoop] [ArmoryBuffer] active={ArmoryBufferActive} deliveryMode={mode?.ToString() ?? "?"}{(why == null ? "" : $" ({why})")} bagFree={Utils.GetInventoryFreeSlotCount()} bagSlotsAtStart={BagSlotsAtStart.Count} armoury: {ArmoryStow.DescribeArmory(C.ArmoryStowReserveSlots)}");
    }

    private static bool SnapshotBags()
    {
        foreach(var type in Utils.PlayerInvetories)
        {
            var inv = RetainerRetrieve.TryGetReadableContainer(type);
            if(inv == null) return false;
            for(var i = 0; i < inv->Size; i++)
            {
                var item = inv->GetInventorySlot(i);
                if(item == null) return false;
                if(item->ItemId != 0) BagSlotsAtStart.Add((type, i, item->ItemId));
            }
        }
        return true;
    }

    private static void DisableArmoryBuffer(string why)
    {
        if(!ArmoryBufferActive) return;
        ArmoryBufferActive = false;
        PluginLog.Information($"[ExpertDeliveryLoop] [ArmoryBuffer] Turned off for the rest of this character: {why}. The loop carries on the usual way (hand in once the bags reach the reserve). Gear already moved and still in the armoury: {LoopStowedRemaining()}.");
    }

    /// <summary>僱員道具視窗開著時到了保留格：先等在途的取回落地再搬。回 false＝不接手。</summary>
    private static bool TryStowFromRetainerWindow()
    {
        if(!ArmoryBufferActive) return false;
        if(PassFired > 0)
        {
            StowAfterSettle = true;
            SettleLastCount = CountOccupiedRetainerSlots();
            SettleLastChangeAt = Environment.TickCount64;
            SetPhase(Phase.RetrieveSettle);
            return true;
        }
        return TryStartStow(StowContext.RetainerWindow);
    }

    /// <summary>接手「背包到保留格」之後的轉移；回 false＝不接手，呼叫端照原本流程走。</summary>
    private static bool TryStartStow(StowContext ctx)
    {
        if(!ArmoryBufferActive) return false;
        if(StowWithBellClosed && ctx != StowContext.BellClosed)
        {
            RoundEndReason = RoundEnd.ReserveReached;
            ClosedBellStowPending = true;
            if(ctx == StowContext.RetainerWindow)
            {
                KeepRetainerIndex = true;
                SetPhase(Phase.LeaveRetainer);
            }
            else
            {
                SetPhase(Phase.CloseBell);
            }
            return true;
        }
        if(RoundRoomAtFirstStow < 0) RoundRoomAtFirstStow = ArmoryStow.ReadRooms(C.ArmoryStowReserveSlots).Values.Sum(x => x ?? 0);
        var im = Data.GetIMSettings();
        StowBatch = new ArmoryStow.Batch("ExpertDeliveryLoop", C.ArmoryStowReserveSlots,
            (type, slot, itemId) => RetrievedItemIds.Contains(itemId)
                && !BagSlotsAtStart.Contains((type, slot, itemId))
                && IsDeliverableGear(itemId, im));
        StowBatchContext = ctx;
        SetPhase(Phase.ArmoryStow);
        return true;
    }

    private static void TickArmoryStow()
    {
        if(StowBatch == null)
        {
            Stop(Loc.T("Stopped: moving gear into the armoury chest lost track of its work."));
            return;
        }
        if(StowBatchContext == StowContext.RetainerWindow && !InventorySpaceManager.IsRetainerInventoryLoaded())
        {
            FinishStowBatch();
            Stop(Loc.T(SharedText.StoppedRetainerStorageClosed));
            return;
        }
        if(!ArmoryStow.Step(StowBatch)) return;

        var b = FinishStowBatch();
        var good = b.MovedItems.Count - b.Bounced;
        switch(StowBatchContext)
        {
            case StowContext.RetainerWindow:
                if(b.Rejected)
                {
                    SwitchToClosedBellStow(b);
                    KeepRetainerIndex = true;
                    SetPhase(Phase.LeaveRetainer);
                }
                else if(good > 0)
                {
                    RetainerRetrieve.ResetTracking();
                    DeferredItems.Clear();
                    PassFired = 0;
                    OccupiedAtStowResume = CountOccupiedRetainerSlots();
                    SetPhase(Phase.Retrieve);
                }
                else
                {
                    RoundEndReason = RoundEnd.ReserveReached;
                    SetPhase(Phase.LeaveRetainer);
                }
                return;

            case StowContext.RetainerList:
                if(b.Rejected)
                {
                    SwitchToClosedBellStow(b);
                    SetPhase(Phase.CloseBell);
                }
                else if(good > 0)
                {
                    SetPhase(Phase.SelectRetainer);
                }
                else
                {
                    RoundEndReason = RoundEnd.ReserveReached;
                    SetPhase(Phase.CloseBell);
                }
                return;

            default:
                if(b.Rejected)
                {
                    DisableArmoryBuffer($"moving gear was not accepted even with the summoning bell closed (moved {b.MovedItems.Count}, failed {b.Failed}, bounced {b.Bounced}, {b.EndReason})");
                    SetPhase(Phase.Handin);
                }
                else if(good > 0)
                {
                    // 關鈴前 RetainerIndex 沒有前進，重開鈴後回同一位僱員。
                    CheckProgressOnNextOpen = true;
                    AfterBellOpen = Phase.SelectRetainer;
                    SetPhase(Phase.OpenBell);
                }
                else
                {
                    SetPhase(Phase.Handin);
                }
                return;
        }
    }

    private static ArmoryStow.Batch FinishStowBatch()
    {
        var b = StowBatch;
        StowBatch = null;
        if(b == null) return null;
        ArmoryStow.Report(b, StowBatchContext.ToString());
        foreach(var m in b.MovedItems)
        {
            if(!ArmoryStow.IsAt(m.Dst, m.DstSlot, m.ItemId)) continue;
            LoopStowed.Add(m);
            RoundStowed++;
        }
        if(RoundRoomAtFirstStow >= 0 && RoundStowed > RoundRoomAtFirstStow)
        {
            DisableArmoryBuffer($"{RoundStowed} items were moved this round but the armoury only had room for {RoundRoomAtFirstStow} when the round began, so moved gear is coming back to the bags");
        }
        return b;
    }

    private static void SwitchToClosedBellStow(ArmoryStow.Batch b)
    {
        StowWithBellClosed = true;
        ClosedBellStowPending = true;
        RoundEndReason = RoundEnd.ReserveReached;
        PluginLog.Information($"[ExpertDeliveryLoop] [ArmoryBuffer] Moving gear with the {(StowBatchContext == StowContext.RetainerWindow ? "retainer's item window" : "retainer list")} open was not accepted (failed {b.Failed}, bounced {b.Bounced}, {b.EndReason}). For the rest of this character the loop closes the summoning bell, moves the gear, then opens the bell again.");
    }

    /// <summary>搬完回去取之後第一次落地：僱員格數沒減少＝伺服器沒收搬移（背包其實沒空出來）。回 true＝已接手轉移。</summary>
    private static bool CheckRetrieveProgressAfterStow(int occupiedNow)
    {
        if(OccupiedAtStowResume < 0) return false;
        var before = OccupiedAtStowResume;
        OccupiedAtStowResume = -1;
        if(occupiedNow < before) return false;
        DisableArmoryBuffer($"after moving gear into the armoury, retrieving again landed nothing (retainer slots {before} -> {occupiedNow}); the server may not have accepted the moves");
        RoundEndReason = RoundEnd.ReserveReached;
        SetPhase(Phase.LeaveRetainer);
        return true;
    }

    /// <summary>循環放進兵裝庫、現在還在的件數；同一容器同一道具按「記錄數」與「實際格數」取小。讀不到的容器照記錄數算。</summary>
    private static int LoopStowedRemaining()
    {
        if(LoopStowed.Count == 0) return 0;
        var total = 0;
        foreach(var g in LoopStowed.GroupBy(x => (x.Dst, x.ItemId)))
        {
            var present = ArmoryStow.CountIn(g.Key.Dst, g.Key.ItemId);
            var recorded = g.Count();
            total += present < 0 ? recorded : Math.Min(recorded, present);
        }
        return total;
    }

    private static int CountDeliverableInBags()
    {
        var im = Data.GetIMSettings();
        var n = 0;
        foreach(var type in Utils.PlayerInvetories)
        {
            var inv = RetainerRetrieve.TryGetReadableContainer(type);
            if(inv == null) continue;
            for(var i = 0; i < inv->Size; i++)
            {
                var item = inv->GetInventorySlot(i);
                if(item != null && IsDeliverableGear(item->ItemId, im)) n++;
            }
        }
        return n;
    }

    /// <summary>繳交閘門：背包裡有可交的，或循環放進兵裝庫的還在。所有「還有沒有東西可交」都走這裡。</summary>
    private static bool HasDeliverableGear() => HasDeliverableGearInBags() || LoopStowedRemaining() > 0;

    private static void BeginHandinRoundStats()
    {
        if(!ArmoryBufferEnabledForCharacter) return;
        RoundBagBefore = CountDeliverableInBags();
        RoundArmoryBefore = LoopStowedRemaining();
    }

    /// <summary>D5 與無進展停止。回 true＝已停下。</summary>
    private static bool EndHandinRoundStats()
    {
        if(!ArmoryBufferEnabledForCharacter) return false;
        var bagAfter = CountDeliverableInBags();
        var armouryAfter = LoopStowedRemaining();
        var armouryStuck = RoundArmoryBefore > 0 && armouryAfter >= RoundArmoryBefore;
        PluginLog.Information($"[ExpertDeliveryLoop] [ArmoryBuffer] Round {HandinRounds}: retrievedToBags={RetrievedThisRound} movedToArmoury={RoundStowed} blockedUnique={RoundBlockedUnique} handedInFromBags={RoundBagBefore - bagAfter} handedInFromArmoury={RoundArmoryBefore - armouryAfter} armouryLeft={armouryAfter} bagDeliverableLeft={bagAfter} bufferActive={ArmoryBufferActive} roundEnd={RoundEndReason}{(armouryStuck ? " - none of the gear in the armoury was handed in; the supply list may not include armoury items for this delivery mode" : "")}");
        var noProgress = bagAfter + armouryAfter >= RoundBagBefore + RoundArmoryBefore;
        RoundStowed = 0;
        RoundBlockedUnique = 0;
        RoundRoomAtFirstStow = -1;
        if(noProgress && RoundEndReason == RoundEnd.ReserveReached)
        {
            Stop(Loc.T("Stopped: this handin round delivered nothing - neither from the bags nor from the gear this loop put in the armoury chest - so another round would do the same."));
            return true;
        }
        return false;
    }

    private static void LogArmoryLeftover()
    {
        if(LoopStowed.Count == 0) return;
        PluginLog.Information($"[ExpertDeliveryLoop] [ArmoryBuffer] Gear this loop put in the armoury and not handed in: {LoopStowedRemaining()} (of {LoopStowed.Count} moved). It is left there; nothing moves it back.");
        LoopStowed.Clear();
    }
}
