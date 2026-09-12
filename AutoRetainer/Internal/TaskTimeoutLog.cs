using ECommons.Automation.NeoTaskManager;

namespace AutoRetainer.Internal;

/// <summary>
/// 讓 NeoTaskManager 的「任務逾時」在 dalamud.log 上說得出是<b>哪一步</b>逾時。
/// </summary>
/// <remarks>
/// 僱員存取金幣、軍票交付、潛水艇排程這些鏈上任何一步卡住而 <c>AbortOnTimeout</c> 清掉整條佇列時，日誌上查不出是哪一步。
/// 🔴 等級刻意維持 <c>Warning</c>。 🔴 刻意<b>不</b>用 <c>DuoLog</c>。
/// ⚠️ <c>remainingTimeMS</c> 是 <c>ref</c>：寫它等於偷偷延長逾時，這裡<b>只讀不寫</b>。
/// </remarks>
public static class TaskTimeoutLog
{
    /// <summary>
    /// 把逾時說明掛到 <paramref name="taskManager"/> 上，並回傳同一個實例。
    /// </summary>
    /// <remarks>
    /// ⚠️ 這裡改的是 TaskManager <b>自己那份</b> <c>DefaultConfiguration</c>，不是傳進建構子的那個物件。
    /// <c>TimeoutSilently = true</c> 是為了蓋掉 ECommons 那行沒有任何資訊的匿名 Warning。
    /// ⚠️ 這個蓋法的前提是<b>沒有任務把 <c>ExecuteDefaultConfigurationEvents</c> 設成 false</b>。
    /// </remarks>
    public static TaskManager Attach(TaskManager taskManager, string tag)
    {
        taskManager.DefaultConfiguration.TimeoutSilently = true;
        taskManager.DefaultConfiguration.OnTaskTimeout += (TaskManagerTask task, ref long remainingTimeMS) =>
        {
            // 該任務自己帶了處理器就交給它印，不要印兩遍。
            if(task.Configuration?.OnTaskTimeout != null) return;

            var limit = task.Configuration?.TimeLimitMS ?? taskManager.DefaultConfiguration.TimeLimitMS;
            var abort = task.Configuration?.AbortOnTimeout ?? taskManager.DefaultConfiguration.AbortOnTimeout ?? true;
            PluginLog.Warning(
                $"[{tag}] 任務逾時：{Describe(task)}，上限 {(limit.HasValue ? limit.Value.ToString() : "?")} ms" +
                (abort ? "，整條任務佇列會被中止。" : "，只丟棄這一步，其餘任務繼續。"));
        };
        return taskManager;
    }

    /// <summary>
    /// 盡量把一個任務描述成人看得懂的樣子。
    /// </summary>
    /// <remarks>
    /// ⚠️ 所以這仍然只是「從完全查不出來」變成「查得到是哪個方法／哪個檔」，不是「查得到是第幾行」。
    /// </remarks>
    public static string Describe(TaskManagerTask task)
    {
        var name = task.Name ?? "";
        var location = task.Location ?? "";
        if(TryGetEnclosingMethod(name, out var enclosing))
        {
            // lambda 的 Location 是編譯器產生的 <>c / <>c__DisplayClassN_M，印出來只是噪音。
            return location.StartsWith("<>", StringComparison.Ordinal)
                ? $"{enclosing}() 內的匿名步驟 [{name}]"
                : $"{enclosing}() 內的匿名步驟 [{name}@{location}]";
        }
        return $"[{name}@{location}]";
    }

    /// <summary>從 <c>&lt;外層方法&gt;b__N</c> / <c>&lt;外層方法&gt;g__名字|N_M</c> 取出外層方法名。</summary>
    private static bool TryGetEnclosingMethod(string name, out string enclosing)
    {
        enclosing = "";
        if(name.Length < 3 || name[0] != '<') return false;
        var end = name.IndexOf('>');
        if(end <= 1) return false;
        enclosing = name.Substring(1, end - 1);
        return true;
    }
}
