namespace AutoRetainer.Modules.Multi;

/// <summary>多角色 Tick 依序判斷的閘門；<see cref="None"/>＝這一幀沒有被擋。</summary>
internal enum RelogGateBlock : byte
{
    None,
    Task,
    NoPlayer,
    Occupied,
    Cooldown,
    Sync,
    NotDone,
}

/// <summary>
/// 診斷「傳喚鈴關窗→多角色 Tick 放行下一步」的計時狀態機。不碰遊戲狀態，只在框架執行緒上用。
/// 每幀先 <see cref="Advance"/>，回 true 才判閘門並呼叫 <see cref="Account"/>；結束一律走 <see cref="End"/>。
/// </summary>
internal sealed class RelogGateTracker
{
    internal const long LateMs = 2000;
    internal const long GiveUpMs = 30000;
    internal const string GiveUp = "仍未放行（30 秒後停止追蹤）";
    // 換角或登出已經在佇列裡（例如別的插件在角色收尾時開關鈴）：Tick 沒有要放行的東西，不是卡住。
    internal const string LogoutQueued = "換角已在佇列（登出）";
    internal const string PlayerGone = "角色已不在（登出或換區）";

    internal long Since { get; private set; }
    private long lastTick;
    private bool wasAtBell;
    private long dt;

    internal long TaskMs { get; private set; }
    internal long NoPlayerMs { get; private set; }
    internal long OccupiedMs { get; private set; }
    internal long CooldownMs { get; private set; }
    internal long SyncMs { get; private set; }
    internal long NotDoneMs { get; private set; }
    internal string FirstTask { get; private set; }
    internal string LastTask { get; private set; }
    internal string Snapshot;

    /// <returns>這一幀是否在追蹤中（剛看到關鈴的那一幀也算）。</returns>
    internal bool Advance(long now, bool atBell)
    {
        if(now - lastTick > 1000)
        {
            // 中間有幀沒追到（多角色被關或被抑制）：舊的邊緣與計時都不可信。
            Since = 0;
            wasAtBell = atBell;
        }
        dt = now - lastTick;
        lastTick = now;
        if(wasAtBell && !atBell)
        {
            Since = now;
            TaskMs = NoPlayerMs = OccupiedMs = CooldownMs = SyncMs = NotDoneMs = 0;
            FirstTask = LastTask = Snapshot = null;
            dt = 0;
        }
        wasAtBell = atBell;
        return Since != 0;
    }

    /// <param name="logoutRunning">目前任務是換角／登出的登出那一步。</param>
    /// <returns>非 null＝呼叫端該用這個固定字面值呼叫 <see cref="End"/>。</returns>
    internal string Account(RelogGateBlock block, string taskName, bool logoutRunning, long now)
    {
        switch(block)
        {
            case RelogGateBlock.Task:
                TaskMs += dt;
                if(taskName != null)
                {
                    FirstTask ??= taskName;
                    LastTask = taskName;
                }
                break;
            case RelogGateBlock.NoPlayer: NoPlayerMs += dt; break;
            case RelogGateBlock.Occupied: OccupiedMs += dt; break;
            case RelogGateBlock.Cooldown: CooldownMs += dt; break;
            case RelogGateBlock.Sync: SyncMs += dt; break;
            case RelogGateBlock.NotDone: NotDoneMs += dt; break;
        }
        if(block == RelogGateBlock.Task && logoutRunning) return LogoutQueued;
        if(block == RelogGateBlock.NoPlayer) return PlayerGone;
        return now - Since > GiveUpMs ? GiveUp : null;
    }

    internal bool SnapshotDue(long now) => Since != 0 && Snapshot == null && now - Since > LateMs;

    /// <returns>關鈴到現在的毫秒數；沒在追蹤回 -1。</returns>
    internal long End(long now)
    {
        if(Since == 0) return -1;
        var waited = now - Since;
        Since = 0;
        return waited;
    }
}
