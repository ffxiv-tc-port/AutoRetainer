using AutoRetainer.Services;
using AutoRetainerAPI.Configuration;
using Dalamud.Plugin.Ipc.Exceptions;
using ECommons.Throttlers;

namespace AutoRetainer.Scheduler;

internal static class Artisan
{
    internal static bool IsListRunning => Svc.PluginInterface.GetIpcSubscriber<bool>("Artisan.IsListRunning").InvokeFunc();
    internal static bool IsListPaused => Svc.PluginInterface.GetIpcSubscriber<bool>("Artisan.IsListPaused").InvokeFunc();
    internal static bool GetStopRequest => Svc.PluginInterface.GetIpcSubscriber<bool>("Artisan.GetStopRequest").InvokeFunc();
    internal static bool GetEnduranceStatus => Svc.PluginInterface.GetIpcSubscriber<bool>("Artisan.GetEnduranceStatus").InvokeFunc();
    internal static void SetEnduranceStatus(bool b)
    {
        Svc.PluginInterface.GetIpcSubscriber<bool, object>("Artisan.SetEnduranceStatus").InvokeAction(b);
    }

    internal static void SetListPause(bool b)
    {
        Svc.PluginInterface.GetIpcSubscriber<bool, object>("Artisan.SetListPause").InvokeAction(b);
    }

    internal static void SetStopRequest(bool b)
    {
        Svc.PluginInterface.GetIpcSubscriber<bool, object>("Artisan.SetStopRequest").InvokeAction(b);
    }

    internal static bool WasPaused = false;

    // 急停後不接手、也不恢復 Artisan，直到 Artisan 離開「清單暫停中」（使用者自己恢復或結束清單）。
    // 只擋接手不夠：急停停的是清單（暫停但仍在處理），接手條件照樣成立，會重開僱員排程、做完再恢復 Artisan。
    private static volatile bool HoldAfterStop = false;
    private static long HoldSetAt = 0;
    private const long HoldMinimumMs = 1000;

    /// <summary>急停時呼叫。可能在 IPC 呼叫端的執行緒上：只寫欄位、寫 log，不碰原生層、任務佇列與 EzThrottler。</summary>
    internal static void CancelPendingResumeForStop()
    {
        HoldSetAt = Environment.TickCount64;
        HoldAfterStop = true;
        if(WasPaused)
        {
            WasPaused = false;
            PluginLog.Information("[Artisan] 急停：作廢 Artisan 待恢復，AutoRetainer 不會再自己恢復 Artisan（要繼續請在 Artisan 自己按恢復）");
        }
    }

    // 讀不到 Artisan 一律放行：那時 IsCurrentlyOperating 也回 false，不會接手。
    private static bool CanReleaseHold(long now)
    {
        if(now - HoldSetAt < HoldMinimumMs) return false;
        try
        {
            return !IsListPaused;
        }
        catch
        {
            return true;
        }
    }

    // 接手檢查（IPC 兩次＋整張物件表找傳喚鈴兩次）限流到每秒 4 次：接手最多晚 0.25 秒。
    // 交還 Artisan 那段照舊每幀跑，「忙碌結束後 2.5 秒才交還」的計時不受影響。
    private const long TakeoverCheckIntervalMs = 250;
    private static long NextTakeoverCheck = 0;

    internal static void ArtisanTick()
    {
        if(C.ArtisanIntegration)
        {
            var now = Environment.TickCount64;
            var checkTakeover = now >= NextTakeoverCheck;
            if(checkTakeover) NextTakeoverCheck = now + TakeoverCheckIntervalMs;
            if(HoldAfterStop)
            {
                if(!checkTakeover || !CanReleaseHold(now)) return;
                HoldAfterStop = false;
                PluginLog.Information("[Artisan] 急停後的 Artisan 整合暫停解除：Artisan 已不在清單暫停中");
            }
            // 僱員排程不可用時不接手：接手會把 Artisan 暫停在一件永遠做不完的事上。
            if(checkTakeover && SchedulerMain.RetainerPassAvailable && IsCurrentlyOperating() && MultiMode.EnsureCharacterValidity(true))
            {
                try
                {
                    var bell = Utils.GetReachableRetainerBell(true);
                    if(AnyRetainersAvailable() && bell != null)
                    {
                        if(!WasPaused)
                        {
                            WasPaused = true;
                            SetStopRequest(true);
                        }

                        if(!SchedulerMain.PluginEnabled || SchedulerMain.Reason != PluginEnableReason.Artisan)
                        {
                            SchedulerMain.EnablePlugin(PluginEnableReason.Artisan);
                            DebugLog($"Enabling AutoRetainer because of Artisan integration");
                        }
                    }
                }
                catch(IpcNotReadyError) { }
                catch(Exception ex)
                {
                    {
                        ex.Log();
                    }
                }
            }
            if(WasPaused && !AnyRetainersAvailable())
            {
                if(IsOccupied())
                {
                    EzThrottler.Throttle("ArtisanCanReenableOccupied", 2500, true);
                }
                if(EzThrottler.Check("ArtisanCanReenableOccupied"))
                {
                    WasPaused = false;
                    SetStopRequest(false);
                }
            }
        }
    }

    internal static bool AnyRetainersAvailable()
    {
        if(C.OfflineData.TryGetFirst(x => x.CID == Svc.PlayerState.ContentId, out var data))
        {
            return data.GetEnabledRetainers().Any(z => z.GetVentureSecondsRemaining() <= C.UnsyncCompensation);
        }
        return false;
    }

    internal static bool IsCurrentlyOperating()
    {
        try
        {
            return IsListRunning || GetEnduranceStatus;
        }
        catch(IpcNotReadyError) { }
        catch(Exception ex)
        {
            ex.LogWarning();
        }
        return false;
    }
}
