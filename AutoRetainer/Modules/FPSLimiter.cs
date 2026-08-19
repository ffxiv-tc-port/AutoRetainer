using System.Diagnostics;
using System.Threading;

namespace AutoRetainer.Modules;

internal static unsafe class FPSLimiter
{
    private static readonly Stopwatch Stopwatch = new();
    internal static void FPSLimit()
    {
        if(MultiMode.Active)
        {
            // CSFramework.Instance() 是 isPointer:true 的靜態位址，會合法回 null。
            // 這段每幀跑（掛在 UiBuilder.Draw），所以取一次、判一次，絕不寫 log。
            // 讀不到就把 WindowInactive 當 false ＝ 保守地「不做 FPS 限制」，
            // 而不是拿 null 去解參考。
            var framework = CSFramework.Instance();
            var windowInactive = framework != null && framework->WindowInactive;
            if(
                (!C.NoFPSLockWhenActive || windowInactive)
                && (!C.FpsLockOnlyShutdownTimer || Shutdown.Active || (C.NightMode && C.NightModeFPSLimit))
                )
            {
                if(Utils.IsBusy || !IsScreenReady())
                {
                    if(C.TargetMSPTRunning > 0)
                    {
                        var ms = (int)(C.TargetMSPTRunning - Stopwatch.ElapsedMilliseconds);
                        if(ms > 0 && ms <= C.TargetMSPTRunning)
                        {
                            Thread.Sleep(ms);
                        }
                    }
                }
                else
                {
                    if(C.TargetMSPTIdle > 0)
                    {
                        var targetMSPT = C.TargetMSPTIdle;
                        if(C.NightMode && Utils.CanAutoLogin() && MultiMode.Active)
                        {
                            targetMSPT = windowInactive ? 5000 : 100;
                        }
                        var ms = (int)(targetMSPT - Stopwatch.ElapsedMilliseconds);
                        if(ms > 0 && ms <= targetMSPT)
                        {
                            Thread.Sleep(ms);
                        }
                    }
                }
            }
            Stopwatch.Restart();
        }
    }
}
