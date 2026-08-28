using Dalamud.Plugin.Ipc.Exceptions;

namespace AutoRetainer.Modules;

/// <summary>
/// 單向橋接到「塔塔露誇獎」(TataruPraise)：整輪自動化成功收尾時請它念一句。
/// </summary>
/// <remarks>
/// 🔴 <b>零組件相依。</b>只用 Dalamud 原生 CallGate 的字串契約，對方沒安裝時本檔的每一條路徑都是 no-op。
/// <para>
/// 🔴 契約名逐字取自 TataruPraise 的 <c>IpcContract.cs</c>。CallGate 是純字串比對，
/// 名字打錯不會有任何錯誤訊息，只會永遠得到「這個頻道沒有人註冊」——<b>靜默斷線</b>。
/// 所以三個字串都寫成常數，不散在呼叫點上。
/// </para>
/// <para>
/// 🔴 <b>只能從主執行緒(framework tick / Draw)呼叫。</b>IPC 的實作是在呼叫端的執行緒上跑的，
/// 從背景 Task 叫過去等於把對方的程式碼拉到背景執行緒。目前兩個呼叫點都在
/// <c>Svc.Framework.Update</c> 的鏈上(<see cref="Scheduler.SchedulerMain.Tick"/> 與
/// <see cref="Voyage.VoyageMain.Tick"/>)。
/// </para>
/// <para>
/// ⚠️ 這是<b>單向通知</b>：回傳值只拿來寫記錄，不影響 AutoRetainer 的任何流程，
/// 也不因為對方回 false 而重試。
/// </para>
/// </remarks>
internal static class TataruPraiseIPC
{
    /// <summary><c>Func&lt;bool&gt;</c>：總開關開著而且真的有可播的內容。</summary>
    internal const string TagIsAvailable = "TataruPraise.IsAvailable";

    /// <summary><c>Func&lt;string, bool&gt;</c>：從指定情境的誇獎池挑一句念。</summary>
    internal const string TagPraise = "TataruPraise.Praise";

    /// <summary>
    /// 送過去的情境字串。
    /// ⚠️ TataruPraise 拿這個字串當 <c>pool.json</c> 的鍵，<b>對不上就靜默不出聲</b>
    /// (它會寫一行 Information 說「情境沒有已合成語音的句子」)。
    /// </summary>
    internal const string CategoryVoyage = "潛艇";

    /// <summary>
    /// 請塔塔露誇一句。對方沒裝、關著、或池裡沒東西，這裡都是安靜的 no-op。
    /// </summary>
    /// <param name="reason">寫進記錄用的來源描述，讓 log 分得出是潛艇那條還是僱員那條。</param>
    internal static void TryPraise(string reason)
    {
        if(!C.TataruPraiseOnCompletion) return;

        try
        {
            // 每次呼叫前先問一次：對方的總開關關著、或池裡一句已合成的都沒有，就不要浪費它的冷卻。
            if(!Svc.PluginInterface.GetIpcSubscriber<bool>(TagIsAvailable).InvokeFunc()) return;

            var accepted = Svc.PluginInterface.GetIpcSubscriber<string, bool>(TagPraise).InvokeFunc(CategoryVoyage);
            // Information 級：這是「使用者說沒出聲」時唯一問得出真相的一行(使用者跑 LogLevel 2)。
            PluginLog.Information($"[TataruPraise] {reason}：Praise(「{CategoryVoyage}」) 回傳 {accepted}。");
        }
        catch(IpcNotReadyError)
        {
            // 對方沒安裝／沒載入。這是完全正常的狀態，刻意不寫 log——沒裝的人每輪都會走到這裡。
        }
        catch(Exception e)
        {
            PluginLog.Information($"[TataruPraise] 呼叫失敗({reason})：{e.Message}");
        }
    }
}
