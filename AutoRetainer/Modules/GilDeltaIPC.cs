using Dalamud.Plugin.Ipc.Exceptions;

namespace AutoRetainer.Modules;

/// <summary>
/// 單向橋接到「金幣流水帳」(GilDelta)：本外掛自動動到錢包之前，先告訴它下一筆金幣變動是什麼。
/// 🔴 零組件相依——只用 Dalamud 原生 CallGate 的字串契約，對方沒安裝時每條路徑都是安靜的 no-op；
/// 端點、簽章與分類名逐字取自 GilDelta 的 <c>Ipc/GilDeltaIpc.cs</c> 與 <c>Events/GilEventCategory.cs</c>。
/// 🔴 只能從 framework 執行緒呼叫——IPC 實作跑在呼叫端的執行緒上。⚠️ 單向通知，回傳值只寫記錄。
/// </summary>
internal static class GilDeltaIPC
{
    /// <summary><c>Func&lt;string, string, int, bool&gt;</c>：<c>Hint(category, note, ttlMs)</c> → 有沒有被收下。</summary>
    internal const string TagHint = "GilDelta.Hint";

    /// <summary>來源前綴。Dalamud 不告訴 GilDelta 是誰呼叫的，這字串是流水帳上唯一能追出處的東西。</summary>
    internal const string NotePrefix = "AutoRetainer:";

    internal const string CategoryRetainerWithdraw = "RetainerWithdraw";
    internal const string CategoryRetainerDeposit = "RetainerDeposit";

    /// <summary>GilDelta 那側<b>只</b>由提示指派這個分類，它自己推不出來。</summary>
    internal const string CategorySubmarineReturn = "SubmarineReturn";

    /// <summary>
    /// 僱員存提的提示有效期。🔴 刻意比其他外掛的 15 秒短：僱員類的 scope 是全分類最寬的
    /// （自己或僱員錢包、<b>兩個方向都吃</b>，見 GilDelta <c>Events/HintScope.cs</c>），沒被用掉
    /// 就會把同一輪稍後的賣店收入、或下一位僱員的變動認領走。
    /// </summary>
    internal const int RetainerTtlMs = 10_000;

    /// <summary>
    /// 潛水艇回收的提示有效期。scope 只吃「自由公司寶箱的<b>增加</b>」，唯一可能認錯的是同一段時間裡
    /// 別的團員存錢進寶箱，所以可以比僱員那條寬。
    /// </summary>
    internal const int VoyageTtlMs = 30_000;

    /// <summary>各只寫一行的旗標。只在 framework 執行緒上讀寫（見類別註解）。</summary>
    private static bool MissingReported;
    private static bool FailureReported;

    /// <summary>僱員提領：在 <c>TaskWithdrawGil</c> 送出提領 callback 之前。</summary>
    internal static void HintRetainerWithdraw()
        => TrySend(CategoryRetainerWithdraw, $"僱員提領（{CurrentRetainerLabel()}）", RetainerTtlMs);

    /// <summary>僱員存入：在 <c>TaskDepositGil</c> 送出存入 callback 之前。</summary>
    internal static void HintRetainerDeposit()
        => TrySend(CategoryRetainerDeposit, $"僱員存入（{CurrentRetainerLabel()}）", RetainerTtlMs);

    /// <summary>潛水艇回收：在按下「收回」或「再派出」之前。兩者都會把這趟的收穫結算掉。</summary>
    internal static void HintSubmarineReturn(string vessel)
        => TrySend(CategorySubmarineReturn,
            string.IsNullOrEmpty(vessel) ? "潛水艇回收" : $"潛水艇回收（{vessel}）", VoyageTtlMs);

    /// <summary>目前這位僱員的名字；讀不到就回「未知僱員」——寧可寫得不精確也不要留空字串。</summary>
    private static string CurrentRetainerLabel()
        => Utils.TryGetCurrentRetainer(out var name) && !string.IsNullOrEmpty(name) ? name : "未知僱員";

    /// <summary>
    /// 告訴 GilDelta「接下來那筆金幣變動是我造成的」。對方沒裝、拒絕、擲例外都是安靜的 no-op；
    /// <paramref name="detail"/> 會自動補上 <see cref="NotePrefix"/> 前綴。
    /// </summary>
    private static void TrySend(string category, string detail, int ttlMs)
    {
        try
        {
            var note = NotePrefix + detail;
            var accepted = Svc.PluginInterface
                .GetIpcSubscriber<string, string, int, bool>(TagHint)
                .InvokeFunc(category, note, ttlMs);
            // Information 級：使用者說「流水帳沒分對類」時，這是唯一問得出 AR 有沒有送出去的一行。
            PluginLog.Information($"[GilDelta] 已送出歸因提示 {category}（{note}，ttl {ttlMs}ms），對方回傳 {accepted}。");
        }
        catch(IpcNotReadyError)
        {
            // 沒安裝／還沒載入是正常狀態，只說一次；提示照樣繼續送，GilDelta 晚點才載入也接得上。
            if(MissingReported) return;
            MissingReported = true;
            PluginLog.Information($"[GilDelta] 沒有偵測到 GilDelta（端點 {TagHint} 沒有人註冊）。本次遊戲不再重複這行。");
        }
        catch(Exception e)
        {
            if(FailureReported) return;
            FailureReported = true;
            PluginLog.Information($"[GilDelta] 送出歸因提示失敗（{category}）：{e.Message}。本次遊戲不再重複這行。");
        }
    }
}
