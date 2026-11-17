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
    internal static bool HasSealBonus()
    {
        if(!Player.Available) return false;
        foreach(var status in Player.Object.StatusList)
        {
            if(status == null) continue;
            if(SealBonusStatusIds.Contains(status.StatusId)) return true;
        }
        return false;
    }

    internal static int GetSealAllowanceCount() => InventoryManager.Instance()->GetInventoryItemCount(SealAllowanceItemId);

    private static string CurrentCharacterLabel => Censor.Character(Player.Name, Player.HomeWorld);

    private static void EnterSealBonus()
    {
        SealCharacterSettled = false;
        SealFreeSince = 0;
        SealSawOccupied = false;
        SetPhase(Phase.SealBonus);
    }

    /// <summary>角色連續 <see cref="SealFreeQuietMs"/> 不忙才回 true;忙到 <see cref="SealOccupiedWaitMs"/> 也回 true(照樣送)。
    /// 「忙」沿用送傳送前的同一個 <c>IsOccupied()</c>。</summary>
    private static bool SealCharacterFree()
    {
        var now = Environment.TickCount64;
        if(IsOccupied())
        {
            SealFreeSince = 0;
            if(!SealSawOccupied)
            {
                SealSawOccupied = true;
                PluginLog.Information($"[ExpertDeliveryLoop] 角色仍在忙碌／事件／過場中,先等它結束再用軍票預支單;最多等 {SealOccupiedWaitMs / 1000} 秒。");
            }
            if(TimeInPhase < SealOccupiedWaitMs) return false;
            PluginLog.Information($"[ExpertDeliveryLoop] 等了 {TimeInPhase / 1000} 秒角色仍在忙碌中,照樣送出使用軍票預支單。");
            return true;
        }
        if(SealFreeSince == 0) SealFreeSince = now;
        if(now - SealFreeSince < SealFreeQuietMs) return false;
        if(SealSawOccupied) PluginLog.Information($"[ExpertDeliveryLoop] 角色已連續 {SealFreeQuietMs}ms 不在忙碌狀態(這一步共等了 {TimeInPhase}ms),送出使用軍票預支單。");
        return true;
    }

    private static void TickSealBonus()
    {
        if(!C.ExpertDeliveryLoopUseSealAllowance || HasSealBonus())
        {
            SetPhase(Phase.EnsureBell);
            return;
        }

        // 重送前數量已經變少＝上一張其實吃掉了、只是背包數字晚到;不送第二張,回去等加成。
        if(SealUseRetries > 0 && GetSealAllowanceCount() < SealAllowanceCountAtUse)
        {
            PluginLog.Information($"[ExpertDeliveryLoop] Before resending, the Priority Seal Allowance count dropped to {GetSealAllowanceCount()} (was {SealAllowanceCountAtUse}) - the previous use went through late. Waiting for the bonus instead of using another one.");
            SetPhase(Phase.SealBonusWait);
            return;
        }

        if(GetSealAllowanceCount() <= 0)
        {
            if(C.ExpertDeliveryLoopStopWithoutSealBonus)
            {
                Stop(Loc.T("Stopped: no Priority Seal Allowance left and the loop is set to stop without the bonus."));
                return;
            }
            PluginLog.Information($"[ExpertDeliveryLoop] No Priority Seal Allowance in inventory, continuing without the seal bonus.");
            SetPhase(Phase.EnsureBell);
            return;
        }

        if(!SealCharacterSettled)
        {
            if(!SealCharacterFree()) return;
            SealCharacterSettled = true;
            // 下面「現在不能用」的等待從角色不忙之後才開始算。
            SetPhase(Phase.SealBonus);
        }

        // 動畫鎖住的時候送出去也是白送。
        if(Player.IsAnimationLocked) return;

        // 🔴 走「使用能力」而不是「背包右鍵使用」。後者(AgentInventoryContext.UseItem)需要道具欄開著才有效,
        //    關著時**靜默失敗**。這裡照抄 Artisan 吃食物/藥水/工程手冊的路徑,不需要任何視窗。
        //    ⚠️ extraParam 65535 對道具是必要的,不是可以省略的預設值。
        //    ⚠️ HQ 道具的能力 id 要 +1000000(數量查詢則是用原 id 加 isHq 旗標);軍票預支單沒有 HQ 版本,所以直接用原 id。
        var status = ActionManager.Instance()->GetActionStatus(ActionType.Item, SealAllowanceItemId);
        if(status != 0)
        {
            // 0 以外都是遊戲說「現在不能用」(戰鬥中、詠唱中、地圖限制…)。等一下再試,
            // 但不要無限等 —— 一直不能用就照設定決定要停還是不帶加成繼續。
            if(TimeInPhase <= SealUsableWaitMs) return;

            PluginLog.Information($"[ExpertDeliveryLoop] GetActionStatus(Item, {SealAllowanceItemId}) = {status} for {TimeInPhase}ms, giving up on the seal bonus.");
            if(C.ExpertDeliveryLoopStopWithoutSealBonus)
            {
                Stop(string.Format(Loc.T("Stopped: the game will not allow the Priority Seal Allowance to be used right now (status {0})."), status));
                return;
            }
            DuoLog.Warning(string.Format(Loc.T("{0}: the game will not allow the Priority Seal Allowance to be used right now (status {1}), so this character gets no seal bonus - continuing the hand-ins."), CurrentCharacterLabel, status));
            SetPhase(Phase.EnsureBell);
            return;
        }

        if(!EzThrottler.Throttle("ExpertDeliveryLoopUseAllowance", 2000)) return;

        SealAllowanceCountAtUse = GetSealAllowanceCount();
        ActionManager.Instance()->UseAction(ActionType.Item, SealAllowanceItemId, extraParam: 65535);
        PluginLog.Information($"[ExpertDeliveryLoop] Sent use-item for Priority Seal Allowance ({SealAllowanceCountAtUse} held before use).");
        SetPhase(Phase.SealBonusWait);
    }

    private static void TickSealBonusWait()
    {
        if(HasSealBonus())
        {
            PluginLog.Information($"[ExpertDeliveryLoop] Seal bonus is now active.");
            SetPhase(Phase.EnsureBell);
            return;
        }

        // 先驗「道具有沒有真的被消耗」,再談加成。兩者分開才講得出真正的原因。
        if(TimeInPhase > SealItemConsumeWaitMs && GetSealAllowanceCount() == SealAllowanceCountAtUse
            && !Player.IsAnimationLocked && !Svc.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.Casting])
        {
            PluginLog.Information($"[ExpertDeliveryLoop] Use-item did not consume anything (still {GetSealAllowanceCount()} held after {TimeInPhase}ms; occupied={IsOccupied()} retries={SealUseRetries}/{MaxSealUseRetries}).");
            if(SealUseRetries < MaxSealUseRetries)
            {
                SealUseRetries++;
                PluginLog.Information($"[ExpertDeliveryLoop] Retrying the Priority Seal Allowance ({SealUseRetries}/{MaxSealUseRetries}) after waiting for the character to be free again.");
                EnterSealBonus();
                return;
            }
            if(C.ExpertDeliveryLoopStopWithoutSealBonus)
            {
                Stop(Loc.T("Stopped: the Priority Seal Allowance was not consumed - the game refused the use-item."));
                return;
            }
            DuoLog.Warning(string.Format(Loc.T("{0}: the Priority Seal Allowance was not consumed even after retrying, so this character gets no seal bonus - continuing the hand-ins."), CurrentCharacterLabel));
            SetPhase(Phase.EnsureBell);
            return;
        }

        if(TimeInPhase <= SealBuffWaitMs) return;

        if(C.ExpertDeliveryLoopStopWithoutSealBonus)
        {
            Stop(Loc.T("Stopped: the seal bonus did not appear after using a Priority Seal Allowance."));
            return;
        }
        DuoLog.Warning(Loc.T("The seal bonus did not appear after using a Priority Seal Allowance - continuing without it."));
        SetPhase(Phase.EnsureBell);
    }
}
