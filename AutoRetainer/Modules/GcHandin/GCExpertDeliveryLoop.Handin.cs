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
    private static bool HasDeliverableGearInBags()
    {
        foreach(var type in Utils.PlayerInvetories)
        {
            var inv = RetainerRetrieve.TryGetReadableContainer(type);
            if(inv == null) continue;
            for(var i = 0; i < inv->Size; i++)
            {
                var item = inv->GetInventorySlot(i);
                if(item == null) continue;
                if(IsDeliverableGear(item->ItemId)) return true;
            }
        }
        return false;
    }

    private static void TickHandin()
    {
        if(Utils.IsBusy) return;

        if(!HasDeliverableGearInBags())
        {
            if(RoundEndReason == RoundEnd.ReserveReached)
            {
                Stop(Loc.T("Stopped: the inventory is down to the reserve but holds nothing that can be delivered."));
                return;
            }
            FinishSuccessfully(Loc.T("Finished: the retainers have no more gear to deliver."));
            return;
        }

        PluginLog.Information($"[ExpertDeliveryLoop] Starting handin round {HandinRounds + 1} with {RetrievedThisRound} item(s) retrieved this round.");
        if(HasGCTarget)
        {
            // 使用者指定了繳交點:自己導航過去,然後只接繳交那一段。
            // 走內建流程的話它會再送一次自己的移動指令,等於導航兩次。
            var gc = GetGCDestination(Player.CID);
            if(!GoToFavorite(gc.Id, gc.Sub, "Grand Company"))
            {
                Stop(Loc.T("Stopped: could not travel to the chosen Grand Company destination - check that it is still starred in Lifestream."));
                return;
            }
            P.TaskManager.Enqueue(() => !Utils.LifestreamIsBusy(), "WaitLifestreamBeforeHandin", new(timeLimitMS: 5 * 60 * 1000));
            P.TaskManager.Enqueue(() =>
            {
                LogGcArrivalOnce();
                GCContinuation.EnqueueInitiation(true);
            }, "EnqueueInitiation");
        }
        else
        {
            TaskDeliverItems.Enqueue();
        }
        // 這一段一定含移動,整段都算導航窗。
        NavigationDeadline = Environment.TickCount64 + NavigationGraceMs;
        HandinRounds++;
        SetPhase(Phase.HandinWait);
    }

    /// <summary>正常完成的收尾。
    /// 🔴 只有**正常完成**才做回鈴停靠:錯誤停止與使用者手動停止一律原地不動 ——
    /// 出錯時多一段導航只會把現場弄得更難查。
    /// 停在鈴邊是為了跟外掛平常的停靠慣例一致,多角色連跑與日常僱員流程可以直接接手 ——
    /// 📌 多角色連跑更用得到這一點:登出前停在鈴邊,下次換回這個角色時就直接登入在鈴旁邊。</summary>
    private static void FinishSuccessfully(string reason)
    {
        if(GetPreferredBell() != null)
        {
            FinishAtBell(reason);
            return;
        }

        // 沒有可用的回程手段就照樣收工,不要卡在這裡。沒設目的地就是沒有回程手段。
        if(FinishReturnAttempted || !HasBellTarget)
        {
            FinishCharacter(reason);
            return;
        }

        PendingFinishReason = reason;
        SetPhase(Phase.FinishReturnToBell);
    }

    private static void TickFinishReturnToBell()
    {
        if(Utils.IsBusy) return;

        FinishReturnAttempted = true;

        // 📌 只有 HasBellTarget 為真才進得了這個階段(FinishSuccessfully 的閘門),所以這裡不必再判一次。
        var bell = GetBellDestination(Player.CID);
        if(!GoToFavorite(bell.Id, bell.Sub, "bell"))
        {
            FinishWithoutReturning();
            return;
        }
        SetPhase(Phase.FinishReturnToBellWait);
    }

    private static void TickFinishReturnToBellWait()
    {
        if(!ChainFinished) return;

        if(GetPreferredBell() != null)
        {
            PluginLog.Information($"[ExpertDeliveryLoop] Back at the summoning bell, this character is complete.");
            FinishAtBell(PendingFinishReason + " " + Loc.T("Back at the summoning bell."));
            return;
        }
        FinishWithoutReturning();
    }

    /// <summary>回不去鈴邊時照樣算完成 —— 東西已經全部繳完了,停不到定位不該被講成失敗。</summary>
    private static void FinishWithoutReturning()
    {
        PluginLog.Information($"[ExpertDeliveryLoop] This character is complete, but could not park at a summoning bell.");
        FinishCharacter(PendingFinishReason + " " + Loc.T("Could not return to a summoning bell."));
    }

    private static void TickHandinWait()
    {
        if(ChainFinished)
        {
            PluginLog.Information($"[ExpertDeliveryLoop] Handin round {HandinRounds} finished. Round end reason: {RoundEndReason}.");
            if(RoundEndReason == RoundEnd.NoGearLeft)
            {
                FinishSuccessfully(Loc.T("Finished: everything has been delivered."));
                return;
            }
            SetPhase(Phase.EnsureBell);
            return;
        }

        if(TimeInPhase > C.ExpertDeliveryLoopHandinTimeoutMinutes * 60L * 1000L)
        {
            Stop(Loc.T("Stopped: the handin round did not finish in time."));
        }
    }
}
