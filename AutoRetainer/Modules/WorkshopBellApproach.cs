using AutoRetainer.Modules.Voyage;
using ECommons.Automation;
using ECommons.GameHelpers;

namespace AutoRetainer.Modules;

/// <summary>
/// Walks up to a Company Workshop summoning bell on behalf of the plugin that currently holds a character
/// postprocess (<see cref="SchedulerMain.CharacterPostProcessLocked"/>).
/// </summary>
/// <remarks>
/// 契約:只在框架執行緒進出。收尾期間 <c>P.TaskManager</c> 被無時限的等待任務占住,所以這裡完全不碰它,
/// 自己每幀推進;鈴每幀重找、不跨幀留 <c>IGameObject</c>。每條結束路徑都經 <see cref="Finish"/>,
/// 而它一定把我們開的 /automove 關掉。走不到就放棄(有時限、有進度檢查),絕不重試。
/// </remarks>
internal static class WorkshopBellApproach
{
    /// <summary>Values handed out over IPC; never renumber.</summary>
    internal enum State
    {
        Idle = 0,
        Walking = 1,
        Arrived = 2,
        GaveUp = 3,
    }

    private const float StartLimit = 20f;
    private const long TimeoutMs = 30_000;
    private const long TargetingTimeoutMs = 5_000;
    private const long StallMs = 5_000;
    private const float StallProgress = 0.5f;
    private const long SettleMs = 3_000;

    private static volatile int CurrentState = (int)State.Idle;

    private static long StartedAt;
    private static long ProgressAt;
    private static long LastTargetAttemptAt;
    private static long SettleUntil;
    private static long LastSettleOffAt;
    private static float BestDistance;
    private static float StartDistance;
    private static float StopRange;
    private static uint StartTerritory;
    private static bool LockonSent;
    private static bool AutorunRequested;

    /// <summary>Lock-free read for the IPC state endpoint.</summary>
    internal static int StateValue => CurrentState;

    internal static bool IsWalking => CurrentState == (int)State.Walking;

    internal static bool TryStart(out string refused)
    {
        refused = null;
        var distance = -1f;
        if(!Player.Available) refused = "player is not available";
        else if(!VoyageUtils.Workshops.Contains(Svc.ClientState.TerritoryType)) refused = $"not in a Company Workshop (territory {Svc.ClientState.TerritoryType})";
        else if(!SchedulerMain.CharacterPostProcessLocked) refused = "no character postprocess is holding AutoRetainer (outside of one, use EnqueueApproachWorkshopBell)";
        else if(IsWalking) refused = "already walking to a summoning bell";
        else if(AutoGCHandin.Operation || Utils.LifestreamIsBusy()) refused = "a Grand Company handin or Lifestream is running";
        else if(IsOccupied()) refused = "the character is occupied";
        else if(Utils.GetReachableRetainerBell(false) != null) refused = "a summoning bell is already within reach";
        else if(Utils.GetNearestRetainerBell(out distance) == null || distance >= StartLimit) refused = distance == float.MaxValue ? "no targetable summoning bell in the object table" : $"the nearest summoning bell is {distance:F1} m away (limit {StartLimit:F0} m)";
        if(refused != null) return false;

        var now = Environment.TickCount64;
        StartedAt = now;
        ProgressAt = now;
        LastTargetAttemptAt = 0;
        SettleUntil = 0;
        BestDistance = distance;
        StartDistance = distance;
        // 與 HouseEnterTask.AutorunOffBell 同一個停步距離,比 Marketbuddy 的互動門檻 4.6 米近。
        StopRange = 4f + Random.Shared.NextSingle() * 0.25f;
        StartTerritory = Svc.ClientState.TerritoryType;
        LockonSent = false;
        AutorunRequested = false;
        CurrentState = (int)State.Walking;
        PluginLog.Information($"[WorkshopBellApproach] Started: walking to the workshop summoning bell {distance:F1} m away during a character postprocess (gives up after {TimeoutMs / 1000} s, or {StallMs / 1000} s without getting {StallProgress} m closer).");
        return true;
    }

    /// <summary>Driven from the framework update, outside any task queue.</summary>
    internal static void Tick()
    {
        try
        {
            if(IsWalking) Step();
            else if(SettleUntil != 0) Settle();
        }
        catch(Exception e)
        {
            // 例外也要走 Finish:狀態停在 Walking 會讓 Marketbuddy 等滿它自己的上限。
            if(IsWalking) Finish(State.GaveUp, $"unexpected error: {e.Message}");
            else SettleUntil = 0;
        }
    }

    private static void Step()
    {
        var now = Environment.TickCount64;
        if(!SchedulerMain.CharacterPostProcessLocked) { Finish(State.GaveUp, "the character postprocess hold was released while walking"); return; }
        if(!Player.Available) { Finish(State.GaveUp, "player is no longer available"); return; }
        if(Svc.ClientState.TerritoryType != StartTerritory) { Finish(State.GaveUp, $"territory changed to {Svc.ClientState.TerritoryType}"); return; }
        if(now - StartedAt > TimeoutMs) { Finish(State.GaveUp, $"not there after {TimeoutMs / 1000} s (closest {BestDistance:F1} m)"); return; }

        var bell = Utils.GetNearestRetainerBell(out var distance);
        if(bell == null || distance >= StartLimit) { Finish(State.GaveUp, bell == null ? "the summoning bell is no longer in the object table" : $"the nearest summoning bell is now {distance:F1} m away"); return; }

        if(distance < StopRange && Utils.GetReachableRetainerBell(false) != null)
        {
            Finish(State.Arrived, $"{distance:F1} m from the bell after {(now - StartedAt) / 1000.0:F1} s");
            return;
        }

        if(distance < BestDistance - StallProgress)
        {
            BestDistance = distance;
            ProgressAt = now;
        }

        if(AutorunRequested)
        {
            if(now - ProgressAt > StallMs) Finish(State.GaveUp, $"no progress for {StallMs / 1000} s (stuck at {distance:F1} m, started at {StartDistance:F1} m)");
            return;
        }

        // 與 HouseEnterTask 的 LockonBell → Approach 同一套:先選中鈴、/lockon,下一幀才開 /automove。
        if(LockonSent)
        {
            AutomoveManager.On();
            AutorunRequested = true;
            ProgressAt = now;
            return;
        }
        if(now - StartedAt > TargetingTimeoutMs) { Finish(State.GaveUp, $"could not target the summoning bell within {TargetingTimeoutMs / 1000} s"); return; }
        if(now - LastTargetAttemptAt < 200) return;
        LastTargetAttemptAt = now;
        if(Svc.Targets.Target?.Address == bell.Address)
        {
            Chat.ExecuteCommand("/lockon");
            LockonSent = true;
        }
        else
        {
            Svc.Targets.Target = bell;
        }
    }

    private static void Finish(State result, string why)
    {
        if(AutorunRequested)
        {
            AutomoveManager.Off();
            SettleUntil = Environment.TickCount64 + SettleMs;
            LastSettleOffAt = Environment.TickCount64;
        }
        AutorunRequested = false;
        LockonSent = false;
        CurrentState = (int)result;
        if(result == State.Arrived) PluginLog.Information($"[WorkshopBellApproach] Arrived: {why}.");
        else PluginLog.Information($"[WorkshopBellApproach] Gave up: {why}.");
    }

    /// <summary>主佇列被占住時 <see cref="AutomoveManager.Tick"/> 的補救不會觸發,所以這裡自己確認 /automove off 有落地。</summary>
    private static void Settle()
    {
        var now = Environment.TickCount64;
        if(now > SettleUntil || !Player.Available || !AutomoveManager.IsAutoRunning())
        {
            SettleUntil = 0;
            return;
        }
        if(now - LastSettleOffAt < 500) return;
        LastSettleOffAt = now;
        PluginLog.Information("[WorkshopBellApproach] Autorun is still engaged after stopping - sending /automove off again.");
        AutomoveManager.Off();
    }
}
