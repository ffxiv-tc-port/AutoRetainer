using ECommons.IPC;

namespace AutoRetainer.Modules;

/// <summary>單向橋接到 Questionable：多角輪巡換角前先請它停止自動任務。</summary>
/// <remarks>
/// 🔴 單向、不等待、不做流程分支——停不下來也照樣換角；反過來等它就等於讓別人的忙碌旗標當 AR 的閘門。
/// 🔴 只能從主執行緒呼叫。📌 wrapper 由 <c>AutoRetainer.cs</c> 設成 <c>AnyException</c>，對方沒裝＝no-op。
/// </remarks>
internal static class QuestionableRelogIPC
{
    /// <summary>原因字串，Questionable 原樣寫進自己的 log（<c>IPC: 原因</c>）。</summary>
    private const string StopReason = "AutoRetainer 換角";

    /// <summary>只寫一行的旗標。只在主執行緒上讀寫。</summary>
    private static bool FailureReported;

    /// <summary>開關關閉、對方沒裝、對方沒在跑，三種情況都是 no-op。</summary>
    internal static void StopBeforeRelog()
    {
        if(!C.MultiStopQuestionableBeforeRelog) return;
        try
        {
            if(!ECommonsIPC.Questionable.IsRunning()) return;
            ECommonsIPC.Questionable.Stop(StopReason);
            // 使用者回報「任務又被打斷」時，這是唯一問得出 AR 有沒有先叫停的一行。
            PluginLog.Information("[Questionable] 換角前已停止 Questionable 自動任務。");
        }
        catch(Exception e)
        {
            // AnyException wrapper 只包呼叫本身；第一次存取時的 EzIPC.Init 不在它裡面。
            if(FailureReported) return;
            FailureReported = true;
            PluginLog.Information($"[Questionable] 換角前停止自動任務失敗：{e.Message}。本次遊戲不再重複這行。");
        }
    }
}
