using FFXIVClientStructs.FFXIV.Component.GUI;

namespace AutoRetainer.Helpers;

/// <summary>
/// 「這扇窗已經按過了」的記號。每個按下點各記各的（不分按的是「是」、取消還是別的按鈕）。
/// </summary>
/// <remarks>
/// 🔴 <see cref="Addon"/> 存的是 <see cref="AtkUnitBase"/> 的位址，但<b>只拿來做等值比較，永遠不解參</b>。
/// 跨幀保存原生指標再解參是崩潰級的錯誤；這裡要的只是「下次看到的是不是同一扇窗」這個身分判斷。
/// </remarks>
internal struct DialogGuard
{
    /// <summary>上一次按下的那扇窗的位址；<c>0</c> ＝ 目前沒有任何窗被記著。</summary>
    public nint Addon;

    /// <summary>按下當時的幀序號，只用來走 <see cref="DialogGuards.RePressEscapeFrames"/> 這個逃生口。</summary>
    public long Frame;
}

/// <summary>
/// 「同一扇窗只按一次」的共用守衛。原本只長在 <see cref="Modules.GcHandin.GCContinuation"/> 裡，
/// 因為同一種崩潰在這個外掛裡有好幾個入口，所以抽到這裡讓每個按下點都接得上。
/// </summary>
/// <remarks>
/// <para>
/// 🔴 這是在防一種 <c>try</c>/<c>catch</c> 攔不住的崩潰：addon 被按下之後有「正在關閉中」的幾幀，
/// <c>GetAddonByName</c> 仍然拿得到實例，<c>IsVisible</c> 與 <c>UldManager.LoadedState == Loaded</c>
/// 也都還成立（＝ <c>IsAddonReady</c> 三關全過），此時再送一次 callback／輸入事件就會踩到原生
/// AccessViolation（C0000005）。AVE 在 .NET Core 是 corrupted-state exception，
/// <c>try</c>/<c>catch</c> 與 <c>HookSafety.ExecuteSafe</c> 都完全無效 ——
/// 唯一的防護是「不要送第二次」，不是「送了再接住」。
/// </para>
/// <para>
/// 🔴 節流<b>不是</b>這個防護。節流記的是「上一次動作在哪一幀／哪個時刻」，不是「這扇窗已經按過」：
/// <list type="bullet">
/// <item><see cref="Utils.GenericThrottle"/> 全外掛共用一把 key，而且幀數是
/// <see cref="Utils.FrameDelay"/> ＝ <c>10 + C.ExtraFrameDelay</c>；<c>ExtraFrameDelay</c> 的合法範圍是
/// <c>ValidateRange(-10, 100)</c>（UI 滑桿只給 <c>0..50</c>，但設定檔可以是負的），
/// 設成 <c>-10</c> 時延遲是 <b>0 幀</b>，等於<b>每一幀都放行</b>。</item>
/// <item><c>EzThrottler</c> 的 key 是全域而且跨場景持久的，<b>首次一定放行</b>。</item>
/// <item><c>FrameThrottler</c> 的幀數（這個外掛裡是 4~10 幀）遠短於一扇窗關閉所需的時間。</item>
/// </list>
/// </para>
/// <para>
/// 🔴 「遊戲會把按過的按鈕停用，所以不會重按」在這個外掛裡<b>不成立</b>：
/// <c>AddonMaster.SelectYesno.Yes()</c> 遇到停用的「是」鈕時會主動翻 <c>NodeFlags</c> 的 bit 5
/// 強制啟用再點下去（ECommons <c>SelectYesno.cs</c>），遊戲那層天然的防護被這條碼路徑破壞掉了。
/// 所以凡是走 <c>AddonMaster.SelectYesno</c> 的按下點，一律要自己記「按過了沒」。
/// </para>
/// </remarks>
internal static unsafe class DialogGuards
{
    /// <summary>
    /// 已經按過、那扇窗卻還沒消失時，最多再等這麼多幀才允許補按一次。
    /// </summary>
    /// <remarks>
    /// 🔑 這不是節流 —— 真正的防護是「同一扇窗只按一次」，這個值只是防死鎖的逃生口：
    /// 永久封鎖會讓呼叫端的任務一路卡到逾時，而 NeoTaskManager 預設的 <c>abortOnTimeout</c>
    /// 會清掉<b>整條</b>佇列（不只是卡住的那一步）。取 60 幀（約 0.5~1 秒）是為了遠遠大於
    /// 「關閉中的那幾幀」，補按永遠不會落在危險窗口內。
    /// </remarks>
    internal const int RePressEscapeFrames = 60;

    private static long CurrentFrame => (long)Svc.PluginInterface.UiBuilder.FrameCount;

    /// <summary>
    /// 問「這扇窗現在可以按嗎」，可以的話<b>順便記下</b>已經按過。呼叫端拿到
    /// <see langword="true"/> 才去按，按法（<c>AddonMaster</c>、<c>Callback.Fire</c>、
    /// 送輸入事件……）留給呼叫端自己決定。
    /// </summary>
    /// <param name="addon">要按的 addon 位址。<b>只做等值比較，這裡永遠不解參。</b></param>
    /// <param name="guard">這個按下點專屬的記號。</param>
    /// <param name="label">逃生口觸發時寫進 log 的名字。</param>
    /// <param name="escapeIsRoutine">
    /// <see langword="true"/> ＝ 這個按下點「同一扇窗本來就會被按很多次」（例如 Talk 是按一次翻一頁，
    /// 窗不會因為被按而消失），走逃生口是常態，寫 <c>Debug</c> 不洗版；
    /// <see langword="false"/>（預設）＝ 走逃生口代表「按了是卻沒關掉」這種該被回報的異常，寫
    /// <c>Information</c>（使用者跑 LogLevel 2，Debug 收不到）。
    /// </param>
    /// <returns>
    /// <see langword="true"/> ＝ 可以按（而且已經記下）；<see langword="false"/> ＝ 這一輪不要按。
    /// </returns>
    /// <remarks>
    /// 回 <see langword="false"/> 對呼叫端的意義一律是「這一輪沒按到，下一輪再來」——
    /// 與「addon 還沒出現」「節流還沒放行」走的是同一條既有路徑，所以接上這個守衛<b>不會</b>
    /// 改變任何一個呼叫端的控制流。
    /// </remarks>
    internal static bool TryPressOnce(nint addon, ref DialogGuard guard, string label, bool escapeIsRoutine = false)
    {
        if(addon == 0) return false;
        var frame = CurrentFrame;
        if(guard.Addon == addon)
        {
            // 這一扇已經按過。窗還在 ＝ 可能正在關閉中，此時再按就是上面說的 AVE。
            if(frame - guard.Frame < RePressEscapeFrames) return false;
            // 逃生口：等了遠超過關閉所需的時間，窗仍在。視為那次沒生效（或這是另一扇重用了同一塊
            // 記憶體的新窗），放行補按一次。
            var msg = $"{label}: 按下後 {frame - guard.Frame} 幀仍是同一扇窗，補按一次";
            if(escapeIsRoutine) PluginLog.Debug(msg); else PluginLog.Information(msg);
        }
        guard = new() { Addon = addon, Frame = frame };
        return true;
    }

    /// <summary>
    /// 對 <paramref name="addonName"/> 這扇窗送一次「取消／關閉」（<c>Callback.Fire(addon, true, -1)</c>），
    /// 同一扇窗只送一次。
    /// </summary>
    /// <returns>
    /// <see langword="true"/> 代表「這一輪呼叫端不要再往下走」—— 涵蓋「剛按了取消」與
    /// 「按過了、窗還在關閉中」兩種情形，兩者對呼叫端的意義相同（畫面上還有擋路的窗）。
    /// </returns>
    internal static bool TryCancelDialogOnce(string addonName, ref DialogGuard guard)
    {
        if(!TryGetAddonByName<AtkUnitBase>(addonName, out var addon) || addon == null)
        {
            // 窗真的從 addon 清單消失了 —— 這是唯一能確定「上一次按下的那扇已經收乾淨」的證據。
            // 只有在這裡解除封鎖，下一扇同名窗才會被當成新的窗來處理。
            guard = default;
            return false;
        }
        var current = (nint)addon;
        var frame = CurrentFrame;
        if(guard.Addon == current)
        {
            // 這一扇已經按過取消。窗還在 ＝ 可能正在關閉中，此時再 FireCallback 就是 AVE。
            if(frame - guard.Frame < RePressEscapeFrames) return true;
            // 逃生口，理由同 TryPressOnce。
            PluginLog.Information($"{addonName} 按下取消後 {frame - guard.Frame} 幀仍未關閉，補按一次");
            guard = default;
        }
        if(!addon->IsReady()) return false;
        guard = new() { Addon = current, Frame = frame };
        Callback.Fire(addon, true, -1);
        return true;
    }

    /// <summary>
    /// 那扇被記下的窗已經從 <paramref name="addonName"/> 的清單裡消失時解除封鎖 —— 這是唯一能確定
    /// 「上一次按下的那扇已經收乾淨」的證據，與 <see cref="TryCancelDialogOnce"/> 用的是同一種判準。
    /// </summary>
    /// <remarks>
    /// 🔴 全程只做位址等值比較，<b>永遠不解參</b>。
    /// <para>
    /// 掃整串索引而不是只看第 1 個，是因為同時可能開著多扇同名窗（SelectYesno 就會），被記下的那扇
    /// 不一定在第 1 格；掃到第一個空的就停，與 <see cref="Utils.GetSpecificYesno(Predicate{string})"/>
    /// 的走法一致。
    /// </para>
    /// <para>
    /// ⚠️ 判準刻意<b>不</b>用「文字還對不對」或「還可不可見」：窗在拆除途中可能有幾幀讀不到提示文字、
    /// 或已經被設成不可見，拿那些當「窗不見了」會<b>正好在最危險的那幾幀</b>把封鎖解除掉。
    /// </para>
    /// <para>
    /// 📌 <c>guard.Addon == 0</c> 時整支函式就是一個整數比較，沒有窗被記著的時候等於免費，
    /// 所以可以放心無條件每幀呼叫。
    /// </para>
    /// </remarks>
    internal static void ReleaseGuardIfGone(string addonName, ref DialogGuard guard)
    {
        if(guard.Addon == 0) return;
        for(var i = 1; i < 100; i++)
        {
            var addon = (nint)Svc.GameGui.GetAddonByName(addonName, i).Address;
            if(addon == 0) break;
            if(addon == guard.Addon) return;
        }
        guard = default;
    }
}
