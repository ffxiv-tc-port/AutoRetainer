using AutoRetainer.Internal;
using AutoRetainer.Modules.Voyage;
using AutoRetainer.Modules.Voyage.Tasks;
using AutoRetainer.Scheduler.Tasks;
using AutoRetainer.Services;
using AutoRetainer.UI.MainWindow.MultiModeTab;
using AutoRetainerAPI.Configuration;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Config;
using Dalamud.Interface.ImGuiNotification;
using ECommons.CircularBuffers;
using ECommons.Events;
using ECommons.ExcelServices.TerritoryEnumeration;
using ECommons.EzSharedDataManager;
using ECommons.GameHelpers;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using static AutoRetainer.Modules.OfflineDataManager;

namespace AutoRetainer.Modules.Multi;

internal static unsafe partial class MultiMode
{
    private static void WatchRelogGate()
    {
        var now = Environment.TickCount64;
        if(!RelogGate.Advance(now, Svc.Condition[ConditionFlag.OccupiedSummoningBell])) return;

        var block = RelogGateBlock.None;
        string taskName = null;
        var logoutRunning = false;
        if(P.TaskManager.IsBusy)
        {
            block = RelogGateBlock.Task;
            var task = P.TaskManager.CurrentTask;
            taskName = task?.Name;
            logoutRunning = task != null && task.Name == nameof(TaskChangeCharacter.Logout) && task.Location == nameof(TaskChangeCharacter);
        }
        else if(!ProperOnLogin.PlayerPresent) block = RelogGateBlock.NoPlayer;
        else if(IsOccupied() || !IsScreenReady()) block = RelogGateBlock.Occupied;
        else if(!IsInteractionAllowed()) block = RelogGateBlock.Cooldown;
        else if(Synchronize) block = RelogGateBlock.Sync;
        else if(!IsCurrentCharacterDone()) block = RelogGateBlock.NotDone;

        var outcome = RelogGate.Account(block, taskName, logoutRunning, now);
        if(RelogGate.SnapshotDue(now)) RelogGate.Snapshot = DescribeRelogGates(now);
        if(outcome != null) EndRelogGateWatch(outcome);
    }

    private static string DescribeRelogGates(long now)
    {
        try
        {
            var flags = string.Join(",", Svc.Condition.AsReadOnlySet());
            return $"taskBusy={P.TaskManager.IsBusy} 目前任務={P.TaskManager.CurrentTask?.Name ?? "-"} 佇列={P.TaskManager.NumQueuedTasks} playerPresent={ProperOnLogin.PlayerPresent} screenReady={IsScreenReady()} occupied={IsOccupied()} 狀態旗標=[{flags}] 互動冷卻剩 {Math.Max(0, NextInteractionAt - now)}ms synchronize={Synchronize} 僱員做完={IsCurrentCharacterRetainersDone()} 潛水艇做完={IsCurrentCharacterCaptainDone()} utilsBusy={Utils.IsBusy} 排程器開著={SchedulerMain.PluginEnabled} 後處理鎖={SchedulerMain.CharacterPostProcessLocked}";
        }
        catch(Exception e)
        {
            return $"讀取失敗：{e.Message}";
        }
    }

    /// <param name="outcome">固定字面值，不可傳組出來的字串（每幀路徑上呼叫）。</param>
    private static void EndRelogGateWatch(string outcome)
    {
        var waited = RelogGate.End(Environment.TickCount64);
        if(waited <= RelogGateTracker.LateMs) return;
        var g = RelogGate;
        PluginLog.Information($"[MultiMode] [RelogGate] 傳喚鈴關窗後 {waited}ms {outcome}；各閘門擋住的時間：任務佇列 {g.TaskMs}ms（第一個 {g.FirstTask ?? "-"}、最後 {g.LastTask ?? "-"}）、角色不在 {g.NoPlayerMs}ms、角色忙碌或畫面未就緒 {g.OccupiedMs}ms、互動冷卻 {g.CooldownMs}ms、同步等待 {g.SyncMs}ms、角色還沒做完 {g.NotDoneMs}ms；第 2 秒當下：{g.Snapshot ?? "?"}");
    }

    internal static bool Relog(OfflineCharacterData data, out string ErrorMessage, RelogReason reason, bool allowFromTaskManager = false, MultiModeChangeCause cause = MultiModeChangeCause.User)
    {
        if(reason.EqualsAny(RelogReason.Overlay, RelogReason.Command, RelogReason.ConfigGUI))
        {
            if(MultiModeRoundTrip.Active) MultiModeRoundTrip.Cancel($"手動換角（{reason}）");
            if(C.MultiDisableOnRelog)
            {
                SetEnabled(false, $"手動換角（{reason}）且設定為手動換角時關閉多角色模式", cause);
            }
            if(MultiMode.Active && !C.MultiNoPreferredReset)
            {
                foreach(var z in C.OfflineData)
                {
                    z.Preferred = false;
                }
                Notify.Warning("Preferred character has been reset");
            }
        }
        ErrorMessage = string.Empty;
        if(P.TaskManager.IsBusy && !allowFromTaskManager)
        {
            ErrorMessage = "AutoRetainer is processing tasks";
        }
        else if(SchedulerMain.CharacterPostProcessLocked)
        {
            ErrorMessage = "Currently in post-processing of character";
        }
        /*else if (data != null && !data.Index.InRange(1, 9))
        {
            ErrorMessage = "Invalid character index";
        }*/
        else
        {
            if(Player.Available)
            {
                if(IsOccupied())
                {
                    ErrorMessage = "Player is occupied";
                }
                else if(data != null && data.CID == Svc.PlayerState.ContentId)
                {
                    ErrorMessage = "Targeted player is logged in";
                }
                else
                {
                    // 只在多開排程自己的換角上叫停：手動換角不該動別人的自動化。單向、不等它。
                    if(reason is RelogReason.MultiMode or RelogReason.ExpertDeliveryLoop or RelogReason.RoundTripReturn) QuestionableRelogIPC.StopBeforeRelog();
                    if(reason == RelogReason.MultiMode || C.AllowManualPostprocess)
                    {
                        TaskPostprocessCharacterIPC.Enqueue();
                    }
                    if(MultiMode.Enabled)
                    {
                        CharaCnt.IncrementOrSet(Svc.PlayerState.ContentId);
                    }
                    else
                    {
                        CharaCnt.Clear();
                    }
                    if(data != null)
                    {
                        TaskChangeCharacter.Enqueue(data.CurrentWorld, data.Name, data.World, data.ServiceAccount);
                    }
                    else
                    {
                        TaskChangeCharacter.EnqueueLogout();
                    }
                    RecordManualRelog(reason, cause, data);
                    return true;
                }
            }
            else
            {
                if(Utils.CanAutoLogin() || (allowFromTaskManager && Utils.CanAutoLoginFromTaskManager()))
                {
                    TaskChangeCharacter.EnqueueLogin(data.CurrentWorld, data.Name, data.World, data.ServiceAccount);
                    RecordManualRelog(reason, cause, data);
                    return true;
                }
                else
                {
                    ErrorMessage = "Can not log in now";
                }
            }
        }
        return false;
    }
}
