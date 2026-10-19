using ECommons.Automation.NeoTaskManager;

namespace AutoRetainer.Internal;

/// <summary>
/// 讓 NeoTaskManager 的「任務逾時」在 dalamud.log 上說得出是<b>哪一步</b>逾時。
/// </summary>
/// <remarks>
/// 僱員存取金幣、軍票交付、潛水艇排程這些鏈上任何一步卡住而 <c>AbortOnTimeout</c> 清掉整條佇列時，日誌上查不出是哪一步。
/// 🔴 等級刻意維持 <c>Warning</c>（被它取代的 ECommons 那行本來就是 Warning）。 🔴 刻意<b>不</b>用 <c>DuoLog</c>（它每一則都會印到聊天視窗）。
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
    public static TaskManager Attach(TaskManager taskManager, string tag, Func<TaskManagerTask, string> describeState = null)
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
                (abort ? Discarded(taskManager) : "，只丟棄這一步，其餘任務繼續。")
                + State(describeState, task));
        };
        return taskManager;
    }

    /// <summary>中止時會連帶丟掉的後續步驟。</summary>
    /// <remarks>
    /// 逾時處理器跑在 <c>Abort()</c> 之前，所以這時 <c>Tasks</c>／<c>Stack</c> 都還是完整的，只讀不改。
    /// 要分辨的是「只丟掉逾時這一步」與「連潛水艇、多角模式的工作一起丟掉」：
    /// 同一個 TaskManager 是僱員、潛水艇、多角模式與關機流程共用的。
    /// </remarks>
    private static string Discarded(TaskManager taskManager)
    {
        var pending = new List<TaskManagerTask>(taskManager.Tasks);
        if(taskManager.IsStackActive) pending.AddRange(taskManager.Stack);
        if(pending.Count == 0) return "，整條任務佇列會被中止（後面沒有別的待辦，等於只丟掉這一步）。";
        const int MaxNames = 10;
        var shown = string.Join("、", pending.Take(MaxNames).Select(ShortName));
        var tail = pending.Count > MaxNames ? "…" : "";
        return $"，整條任務佇列會被中止，後面 {pending.Count} 步一併丟掉：{shown}{tail}。";
    }

    /// <summary>清單用的短名：名字仍是編譯器產生的角括號形狀時只留外層方法；ECommons 改寫過的「類別.外層方法#b__N」原樣印（序號是同一方法內各步驟唯一分得開的東西）。</summary>
    private static string ShortName(TaskManagerTask task)
    {
        var name = task.Name ?? "";
        if(name.StartsWith('<') && TryGetEnclosingMethod(name, out var enclosing)) return $"{enclosing}() 內的匿名步驟";
        return name.Length == 0 ? "(未具名)" : name;
    }

    /// <summary>取現場快照，接在逾時訊息尾巴。快照自己壞掉不可以把逾時訊息一起弄丟。</summary>
    private static string State(Func<TaskManagerTask, string> describeState, TaskManagerTask task)
    {
        if(describeState == null) return "";
        try
        {
            var state = describeState(task);
            return string.IsNullOrEmpty(state) ? "" : " " + state;
        }
        catch(Exception e)
        {
            return $" 現場快照讀取失敗：{e.Message}";
        }
    }

    /// <summary>
    /// 盡量把一個任務描述成人看得懂的樣子。
    /// </summary>
    /// <remarks>
    /// ⚠️ 任務只帶名字與宣告型別，所以查得到的是哪個方法／哪個型別，不是第幾行。
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
        return location.StartsWith("<>", StringComparison.Ordinal) ? $"[{name}]" : $"[{name}@{location}]";
    }

    /// <summary>
    /// 從 <c>類別.外層方法#b__N</c>（子模組那份 ECommons 給 lambda 的名字）取出「類別.外層方法」；
    /// 開頭是角括號的編譯器原名（<c>&lt;外層方法&gt;b__N</c>，ECommons 改寫不了時原樣交回）也認。
    /// </summary>
    /// <remarks>要連 <c>b__</c> 一起認：具名任務的名字也可能含 '#'。</remarks>
    private static bool TryGetEnclosingMethod(string name, out string enclosing)
    {
        enclosing = "";
        if(name.Length >= 3 && name[0] == '<')
        {
            var end = name.IndexOf('>');
            if(end <= 1) return false;
            enclosing = name.Substring(1, end - 1);
            return true;
        }
        var hash = name.IndexOf("#b__", StringComparison.Ordinal);
        if(hash <= 0) return false;
        enclosing = name[..hash];
        return true;
    }
}
