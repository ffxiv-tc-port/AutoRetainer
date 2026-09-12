using ECommons.EzIpcManager;
using System.Runtime.ExceptionServices;

namespace AutoRetainer.Modules.EzIPCManagers;

/// <summary>
/// 把 IPC 端點的本體搬到 framework 執行緒上跑的閘門。
/// 🔴 <see cref="EzIPC"/> 註冊出去的端點是<b>在呼叫端的執行緒上執行的</b>，不是在 framework 執行緒上。這兩個門面底下的實作幾乎每一支都會同步碰到下面三類東西，而三類全部只有在 framework 執行緒上才成立。
/// 🔴 「逾時」只代表「我們不等了」，不代表 <c>body</c> 沒有執行。 <b>逾時回值必須是「沒答案」而不是「一個看起來像答案的值」</b>。
/// </summary>
internal static class IpcFrameworkGate
{
    /// <summary>等 framework 執行緒的上限。寫死是刻意的：這不是使用者該調的旋鈕，
    /// 而且只有在「framework 執行緒卡住超過五秒」時才會走到 —— 那種狀態下遊戲本身已經停了。</summary>
    internal const int WaitMilliseconds = 5000;

    /// <summary>同一支端點兩次逾時訊息之間的最小間隔。消費端會輪詢這些端點，不節流會洗版。</summary>
    private const long ReportIntervalMs = 60000;

    /// <summary>🔴 自帶節流表而不是用 <c>EzThrottler</c>：後者是整個外掛共用的靜態 <c>Dictionary</c>
    /// 且<b>零同步</b>，而這裡正是「從別人的執行緒進來」的那條路 —— 在這裡呼叫它，
    /// 壞掉的會是整個外掛所有模組的節流表，而不只是這一行訊息。</summary>
    private static readonly Dictionary<string, long> LastTimeoutReport = [];
    private static readonly object ReportLock = new();

    /// <summary>在 framework 執行緒上執行 <paramref name="body"/> 並等它的結果。</summary>
    /// <param name="endpoint">端點名。只用在逾時訊息與節流鍵上。</param>
    /// <param name="body">端點原本的本體，一字不改地搬進來。</param>
    /// <param name="onTimeout">等不到時回什麼。必須是該端點<b>原本就定義過</b>的「不可用」值。</param>
    /// <param name="onTimeoutMeaning">寫進逾時訊息裡，說明呼叫端拿到的那個值代表什麼。</param>
    internal static T Run<T>(string endpoint, Func<T> body, T onTimeout, string onTimeoutMeaning)
    {
        if(IsUnloading(endpoint)) return onTimeout;

        var task = Svc.Framework.RunOnFrameworkThread(body);
        if(Task.WaitAny([task], WaitMilliseconds) != 0 || task.IsCanceled)
        {
            ReportTimeout(endpoint, onTimeoutMeaning);
            return onTimeout;
        }
        Rethrow(task);
        return task.Result;
    }

    /// <summary>無回值版本。逾時就是「這次什麼都沒做」。</summary>
    internal static void Run(string endpoint, Action body)
    {
        if(IsUnloading(endpoint)) return;

        var task = Svc.Framework.RunOnFrameworkThread(body);
        if(Task.WaitAny([task], WaitMilliseconds) != 0 || task.IsCanceled)
        {
            ReportTimeout(endpoint, "nothing was done at all");
            return;
        }
        Rethrow(task);
    }

    /// <summary>
    /// 🔴 Dalamud 卸載期的閘門旁路：<c>Framework.RunOnFrameworkThread</c> 在 <c>IsFrameworkUnloading</c> 為真時會<b>就地在呼叫端執行緒</b>執行 body，等於這一層完全失效、原生記憶體存取退回未保護狀態。
    /// 🔑 所以卸載期一律直接回該端點原本的「不可用」值。
    /// 📌 已經在 framework 執行緒上時不受影響（那本來就是安全的執行緒）。
    /// </summary>
    private static bool IsUnloading(string endpoint)
    {
        if(!Svc.Framework.IsFrameworkUnloading || Svc.Framework.IsInFrameworkUpdateThread) return false;
        var now = Environment.TickCount64;
        bool report;
        var key = endpoint + "/unloading";
        lock(ReportLock)
        {
            report = !LastTimeoutReport.TryGetValue(key, out var last) || now - last >= ReportIntervalMs;
            if(report) LastTimeoutReport[key] = now;
        }
        if(report) PluginLog.Information($"[IpcFrameworkGate] {endpoint} was called off the framework thread while Dalamud was unloading, so nothing was done at all. During unload RunOnFrameworkThread runs the body inline on the caller's thread, which would leave native memory access unguarded - a crash there is worse than the feature not answering.");
        return true;
    }

    /// <summary>把 framework 執行緒上擲出的例外<b>原封不動</b>再擲一次。</summary>
    /// <remarks>🔴 直接讓 <c>Task.Result</c> 去擲會包成 <see cref="AggregateException"/>，
    /// 消費端既有的 <c>catch</c> 就對不上型別了 —— 那是行為變更。
    /// 📌 在 framework 執行緒上呼叫時根本走不到這裡：那條路是同步執行，例外直接往外擲。</remarks>
    private static void Rethrow(Task task)
    {
        if(!task.IsFaulted) return;
        var ex = (Exception)task.Exception?.InnerException ?? task.Exception;
        if(ex != null) ExceptionDispatchInfo.Capture(ex).Throw();
    }

    private static void ReportTimeout(string endpoint, string consequence)
    {
        var now = Environment.TickCount64;
        bool report;
        // 🔴 鎖內只做「決定要不要印」。寫 log 是 I/O，放進鎖裡就變成「持著鎖等 Serilog」，
        //    而這個鎖會被任意數量的外掛執行緒撞上。
        lock(ReportLock)
        {
            report = !LastTimeoutReport.TryGetValue(endpoint, out var last) || now - last >= ReportIntervalMs;
            if(report) LastTimeoutReport[endpoint] = now;
        }
        if(!report) return;
        PluginLog.Information($"[IpcFrameworkGate] {endpoint} was called off the framework thread and the framework thread did not answer within {WaitMilliseconds}ms, so {consequence}. This is not a refusal by AutoRetainer - the game's main loop was stalled. Poll again rather than treating this answer as final.");
    }
}
