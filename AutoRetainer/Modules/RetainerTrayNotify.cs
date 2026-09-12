using Dalamud.Utility;

namespace AutoRetainer.Modules;

/// <summary>
/// 僱員探險完成時的 Windows 側通知（系統匣氣球 ＋ 工作列閃爍）。
/// </summary>
/// <remarks>
/// 📌 全部是顯示用的，而且維持 opt-in：兩個開關的預設值都是 <c>false</c>，這裡沒有改。
/// 🔴 <b>刻意不走 NotificationMaster 的通知樞紐（<c>NotificationMaster.Notify</c>）。</b>兩邊都送就會念兩次。
/// </remarks>
internal static class RetainerTrayNotify
{
    /// <summary>
    /// 「NotificationMaster 沒有回應」這件事一個 session 只值一行記錄。
    /// </summary>
    /// <remarks>
    /// 🔴 呼叫點在 framework tick 上的狀態邊緣：沒有這個旗標的話，
    /// 每一波僱員回來都會再寫一次同一句話。
    /// </remarks>
    private static bool LoggedUnavailable = false;

    /// <summary>
    /// 有僱員的探險完成了（狀態<b>剛剛</b>從「沒有」翻成「有」）。
    /// </summary>
    /// <remarks>
    /// 🔴 只能從 <see cref="NotificationHandler.Tick"/> 的<b>狀態邊緣</b>呼叫，
    /// 不可以放進輪詢路徑——那會變成每幀一顆氣球。
    /// </remarks>
    internal static void OnRetainersBecameAvailable()
    {
        if(!C.NotifyDeskopToast && !C.NotifyFlashTaskbar) return;

        // 既有的核取方塊：「Do not notify if AutoRetainer is enabled or MultiMode is running」。
        // 排程器正在跑的時候，人多半就在旁邊看著它跑，這時候彈氣球是雜訊。
        if(C.NotifyNoToastWhenRunning && (SchedulerMain.PluginEnabled || MultiMode.Enabled)) return;

        // Util.ApplicationIsActivated() 問的是 Windows「現在前景視窗是誰」，不讀遊戲記憶體。
        // 遊戲已經在前景的話，聊天訊息與浮動提示使用者都看得到了，再彈一顆氣球只是雜訊。
        // ⚠️ NotificationMaster 也有一個等價的 IsGameWindowActivated，但那個成員在
        //    NotificationMasterAPI 1.0.0.1（本 repo 用的就是這顆 nuget）裡是 private，叫不到；
        //    Dalamud 這支是 public，而且沒裝 NotificationMaster 時照樣能用。
        if(Util.ApplicationIsActivated()) return;

        try
        {
            var delivered = false;
            if(C.NotifyDeskopToast)
            {
                delivered = P.NotificationMasterApi.DisplayTrayNotification(
                    "AutoRetainer",
                    Loc.T("Some of the retainers have completed their ventures!"));

                if(!delivered && !LoggedUnavailable)
                {
                    LoggedUnavailable = true;
                    // Information 級：這是使用者說「我勾了但沒反應」時唯一問得出真相的一行。
                    PluginLog.Information(
                        "[僱員通知] 要求顯示系統匣通知，但 NotificationMaster 沒有接受"
                        + "（多半是沒安裝或沒啟用）。改為只閃工作列。");
                }
            }

            // 🔴 不論氣球有沒有送出去都要閃：FlashWindow 是 Dalamud 內建的，
            //    沒裝 NotificationMaster 時這半邊照樣有效。
            //    它預設的 flashIfOpen=false 會自己再確認一次前景視窗，
            //    所以中途重新取得焦點的遊戲不會被打擾。
            if(C.NotifyFlashTaskbar) Util.FlashWindow();
        }
        catch(Exception e)
        {
            // DisplayTrayNotification 是跨外掛 IPC。NotificationMasterApi 自己只吞
            // IpcNotReadyError，其他都會冒出來；而這裡跑在 framework tick 上，
            // 讓例外逃出去會把整個 Tick 那一幀打斷。
            PluginLog.Information($"[僱員通知] 送 Windows 通知失敗，略過不影響其他流程：{e.Message}");
        }
    }
}
