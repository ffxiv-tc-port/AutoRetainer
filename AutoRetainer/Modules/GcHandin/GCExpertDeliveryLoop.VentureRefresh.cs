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
    // 契約：這幾個階段是循環自己依序跑的一段，一般排程器的互斥（RetainerAutomationDeferred）全程維持；
    // 每個僱員只排「選僱員 → 探險處理 → 告辭 → 等回到清單」，清單只由這一段自己在最後關一次。
    // 刻意只做探險：一般處理同一趟的存入／出售／提領 Gil／IPC 後處理都不做，留給循環結束後的一般處理。

    private static bool IsRetainerListReady() => TryGetAddonByName<AtkUnitBase>("RetainerList", out var a) && IsAddonReady(a);

    /// <summary>站在鈴邊完成一個角色。選項關著時＝直接 <see cref="FinishCharacter"/>。</summary>
    private static void FinishAtBell(string reason)
    {
        if(!C.ExpertDeliveryLoopRefreshVentures)
        {
            FinishCharacter(reason);
            return;
        }
        PendingVentureFinishReason = reason;
        VentureQueue = null;
        VentureIndex = 0;
        VenturesResent = 0;
        VenturesCollectedOnly = 0;
        VentureSkips.Clear();
        SetPhase(Phase.VentureStart);
    }

    /// <summary>整個角色不做探險收尾的理由；null＝可以做。條件與一般排程器開始一輪之前的閘門對齊。</summary>
    private static string GetVentureRefreshBlocker()
    {
        if(IPC.Suppressed) return "another plugin has suppressed AutoRetainer";
        if(!SchedulerMain.RetainerPassAvailable) return "the venture list update signal is not available on this game version";
        if(!C.OfflineData.TryGetFirst(x => x.CID == Player.CID, out var data)) return "there is no saved data for this character";
        if(!data.Enabled) return "this character is not enabled for retainer Multi Mode";
        return null;
    }

    private static void TickVentureStart()
    {
        if(Utils.IsBusy) return;

        var blocker = GetVentureRefreshBlocker();
        if(blocker != null)
        {
            FinishVentureRefresh(blocker);
            return;
        }
        // 僱員資料讀得到而且沒有到期的就不開鈴；讀不到時開鈴之後再數。
        if(GameRetainerManager.Ready && !SchedulerMain.EnumeratePendingRetainers().Any())
        {
            FinishVentureRefresh("no venture is due");
            return;
        }

        PluginLog.Information($"[ExpertDeliveryLoop] Opening the summoning bell again to collect and resend the ventures that have come due before leaving this character.");
        AfterBellOpen = Phase.VentureSelect;
        SetPhase(Phase.OpenBell);
    }

    private static void TickVentureSelect()
    {
        if(Utils.IsBusy) return;

        if(!RetainerListLoaded || !IsRetainerListReady())
        {
            if(TimeInPhase > RetainerListLoadTimeoutMs)
            {
                PluginLog.Information($"[ExpertDeliveryLoop] Venture refresh: the retainer list was not ready for {TimeInPhase}ms (managerReady={GameRetainerManager.Ready} count={GameRetainerManager.Count}).");
                Stop(Loc.T("Stopped: the retainer list did not load after using the summoning bell."));
            }
            return;
        }

        if(VentureQueue == null)
        {
            VentureQueue = [.. SchedulerMain.EnumeratePendingRetainers()];
            VentureIndex = 0;
            PluginLog.Information($"[ExpertDeliveryLoop] Venture refresh: {VentureQueue.Count} retainer(s) due on this character: {VentureQueue.Print()}. Free slots {Utils.GetInventoryFreeSlotCount()} (minimum {C.MultiMinInventorySlots}), venture tokens {Utils.GetVenturesAmount()}.");
        }

        if(VentureIndex >= VentureQueue.Count)
        {
            SetPhase(Phase.VentureCloseBell);
            return;
        }

        var name = VentureQueue[VentureIndex];
        if(!Utils.TryGetRetainerByName(name, out var ret))
        {
            VentureSkips.Add($"{name}: not in the game's retainer list");
            VentureIndex++;
            return;
        }

        // 與一般排程器同一道空間閘門：收成果要背包有空位，下一個僱員一定撞同一面牆，所以剩下的一起略過。
        if(!Utils.IsInventoryStateReadable())
        {
            if(TimeInPhase <= RetainerListLoadTimeoutMs) return;
            SkipRemainingVentures("the inventory could not be read");
            return;
        }
        if(!Utils.IsInventoryFree())
        {
            SkipRemainingVentures($"fewer than {C.MultiMinInventorySlots} free inventory slots");
            return;
        }

        var adata = Utils.GetAdditionalData(Svc.PlayerState.ContentId, ret.Name.ToString());
        P.TaskManager.Enqueue(() => RetainerListHandlers.SelectRetainerByName(name), $"SelectRetainerByName({name})");
        VentureAction = SchedulerMain.EnqueueVentureBusiness(ret, name, adata);
        if(C.RetainerMenuDelay > 0)
        {
            TaskWaitSelectString.Enqueue(C.RetainerMenuDelay);
        }
        P.TaskManager.Enqueue(RetainerHandlers.SelectQuit, "SelectQuit");
        P.TaskManager.Enqueue(RetainerHandlers.ConfirmCantBuyback, "ConfirmCantBuyback");
        P.TaskManager.Enqueue(IsRetainerListReady, "WaitRetainerList");
        PluginLog.Debug($"[ExpertDeliveryLoop] Venture refresh: visiting {name} ({VentureIndex + 1}/{VentureQueue.Count}), action={VentureAction}.");
        VentureChainDoneAt = 0;
        SetPhase(Phase.VentureSelectWait);
    }

    private static void TickVentureSelectWait()
    {
        if(!ChainFinished) return;

        var name = VentureQueue[VentureIndex];
        if(!IsRetainerListReady())
        {
            PluginLog.Information($"[ExpertDeliveryLoop] Venture refresh: did not get back to the retainer list after visiting {name} (action={VentureAction}, {TimeInPhase}ms).");
            Stop(Loc.T("Stopped: could not get back to the retainer list."));
            return;
        }

        var now = Environment.TickCount64;
        if(VentureChainDoneAt == 0) VentureChainDoneAt = now;

        // 結果只拿來分類診斷：讀僱員資料的新探險結束時間。
        var found = Utils.TryGetRetainerByName(name, out var ret);
        if(found && ret.VentureID != 0 && ret.GetVentureSecondsRemaining() > C.UnsyncCompensation)
        {
            VenturesResent++;
        }
        else if(found && ret.VentureID == 0)
        {
            VenturesCollectedOnly++;
        }
        else
        {
            if(now - VentureChainDoneAt < VentureOutcomeSettleMs) return;
            VentureSkips.Add($"{name}: still due after the visit (action={VentureAction}, found={found})");
        }

        VentureIndex++;
        SetPhase(Phase.VentureSelect);
    }

    private static void SkipRemainingVentures(string why)
    {
        for(var i = VentureIndex; i < VentureQueue.Count; i++)
        {
            VentureSkips.Add($"{VentureQueue[i]}: {why}");
        }
        VentureIndex = VentureQueue.Count;
        SetPhase(Phase.VentureCloseBell);
    }

    private static void TickVentureCloseBell()
    {
        P.TaskManager.Enqueue(RetainerListHandlers.CloseRetainerList, "CloseRetainerList");
        P.TaskManager.Enqueue(() => !IsOccupied(), "WaitUntilNotOccupiedAfterBell");
        SetPhase(Phase.VentureCloseBellWait);
    }

    private static void TickVentureCloseBellWait()
    {
        if(!ChainFinished) return;
        if(IsOccupied())
        {
            Stop(Loc.T("Stopped: could not leave the summoning bell."));
            return;
        }
        FinishVentureRefresh(null);
    }

    /// <summary>寫這個角色的探險收尾診斷（每角色一行），累計到整趟，然後照常收工換角。</summary>
    private static void FinishVentureRefresh(string blocker)
    {
        VentureCharacters++;
        VenturesResentTotal += VenturesResent;
        VenturesCollectedOnlyTotal += VenturesCollectedOnly;
        VentureSkippedTotal += VentureSkips.Count;
        var who = MultiCharacterRun ? $"character {BatchIndex + 1}/{BatchCIDs.Count} (CID {Player.CID})" : $"CID {Player.CID}";
        if(blocker != null)
        {
            PluginLog.Information($"[ExpertDeliveryLoop] Venture refresh skipped for {who}: {blocker}.");
        }
        else
        {
            PluginLog.Information($"[ExpertDeliveryLoop] Venture refresh done for {who}: due={VentureQueue?.Count ?? 0} resent={VenturesResent} collectedOnly={VenturesCollectedOnly} skipped={VentureSkips.Count}{(VentureSkips.Count > 0 ? " [" + string.Join("; ", VentureSkips) + "]" : "")}.");
        }
        FinishCharacter(PendingVentureFinishReason);
    }
}
