using FFXIVClientStructs.FFXIV.Component.GUI;

namespace AutoRetainer.Helpers;

/// <summary>
/// 「這扇窗已經按過了」的共用守衛：同一扇窗（位址）在它走完生命週期之前只按一次。
/// 全外掛所有對 addon 的按法（<c>AddonMaster</c> 的 <c>Yes()</c>／<c>Select()</c>／<c>Click()</c>／<c>Deliver()</c>…、
/// <c>Callback.Fire</c>、<c>FireCallback</c>、<c>ClickAddonButton</c>／<c>ClickRadioButton</c>、直送 <c>ReceiveEvent</c>、
/// <c>Close(true)</c>）都要先問過 <see cref="TryPressOnce"/>；解除點集中在 <see cref="Tick"/>。
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
/// <item><c>FrameThrottler</c> 的幀數（這個外掛裡是 2~20 幀）遠短於一扇窗關閉所需的時間。</item>
/// </list>
/// </para>
/// <para>
/// 🔴 「遊戲會把按過的按鈕停用，所以不會重按」在這個外掛裡<b>不成立</b>：
/// <c>AddonMaster.SelectYesno.Yes()</c> 遇到停用的「是」鈕時會主動翻 <c>NodeFlags</c> 的 bit 5
/// 強制啟用再點下去（ECommons <c>SelectYesno.cs</c>），遊戲那層天然的防護被這條碼路徑破壞掉了。
/// </para>
/// <para>
/// 🔑 2026-09-02 起改成<b>集中式</b>：記號以「窗名（＋參數組）」為 key、以位址集合為值，統一放在這裡，
/// 不再由各按下點自己持有一個 struct。理由是同一扇窗會被<b>不同模組</b>按到——
/// <c>MiniTA.SkipItemConfirmations</c> 與 <c>AutoGCHandin.HandleYesno</c> 都會比中 Addon 102434
/// （高品質道具交易確認）的同一扇 SelectYesno——各持一份記號等於互相看不見：A 按過、窗關閉中，
/// B 的記號是空的就會再按一次，正是要防的那種崩潰。同一扇窗的所有按下點共用一把 key，
/// 誰先按到誰記，其他人在窗消失前一律擋下。
/// </para>
/// <para>
/// 🔑 粒度＝（窗，位址，參數組）：
/// <list type="bullet">
/// <item>「回答一次即終結」的窗（SelectYesno 族、確認鈕按下即關、關閉／取消）<b>不帶</b> <c>paramKey</c>，
/// 整扇窗一把 key——不管按的是「是」「否」還是取消，按過任何一個之後窗就在關閉中，別的都不准再送。</item>
/// <item>按下不會關的窗（分頁、清單選列、設定數量、搜尋…）帶 <c>paramKey</c>，同一扇窗對不同參數組可以
/// 各按一次（保住「同幀對同窗連送不同參數」的正常流程）；但只要這扇窗<b>不帶參數的</b> key 已經記下
/// 這個位址（＝我們自己把它關了），任何參數組都不准再送。</item>
/// </list>
/// </para>
/// <para>
/// 🔑 解除點＝<see cref="Tick"/> 每幀掃 addon 清單（全索引 1..99，掃到第一個空的停，與
/// <see cref="Utils.GetSpecificYesno(Predicate{string})"/> 的走法一致），被記下的位址不在清單裡才解除。
/// 判準刻意<b>不</b>用「文字還對不對」或「還可不可見」：窗在拆除途中可能有幾幀讀不到提示文字、或已經
/// 被設成不可見，拿那些當「窗不見了」會<b>正好在最危險的那幾幀</b>把封鎖解除掉。
/// ⚠️ 位址可能被下一扇新窗重用：舊窗消失與新窗建立若落在同一幀之間，<see cref="Tick"/> 看不出差別，
/// 新窗會被擋到逃生口（最多 <see cref="RePressEscapeFrames"/> 幀）才放行——代價是延遲，不是崩潰。
/// </para>
/// <para>
/// 🔴 全程只做位址等值比較，<b>永遠不解參</b>。跨幀保存原生指標再解參是崩潰級的錯誤；
/// 這裡要的只是「下次看到的是不是同一扇窗」這個身分判斷。
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
    /// 「關閉中的那幾幀」，補按永遠不會落在危險窗口內。走到這個逃生口代表「按了卻沒關掉」，
    /// 寫 <c>Information</c>（使用者跑 LogLevel 2，Debug 收不到）。
    /// </remarks>
    internal const int RePressEscapeFrames = 60;

    /// <summary>
    /// 「按一次翻一頁、窗不會因為被按而消失」的多次互動窗（Talk 是代表；同形狀還有翻頁式對話框、
    /// 分頁鈕、清單選列、設定數量這類按下不關的操作）專用的逃生口：<see cref="TryPressOnce"/> 的
    /// <c>escapeIsRoutine</c> 為 <see langword="true"/> 時用它取代 <see cref="RePressEscapeFrames"/>。
    /// </summary>
    /// <remarks>
    /// 🔑 這類窗走逃生口是常態（那才是翻到下一頁／再送下一次的方式），所以逃生口的長度直接決定節奏。
    /// 關閉中的危險窗口實測 &lt;10 幀，15 幀不落在裡面；每頁 +0.25s 幾乎無感。
    /// 60 幀會把每一頁壓成 0.5~1 秒，使用者裁決（2026-09-02）改成 15；走逃生口寫 <c>Debug</c> 不洗版。
    /// ⚠️ 判準刻意<b>不</b>用「文字變了」當翻頁證據：關閉中文字會讀壞，時間是唯一不靠未證實假設的判準。
    /// </remarks>
    internal const int RoutineRePressEscapeFrames = 15;

    /// <summary>
    /// 一把 key（窗名＋參數組）底下「已經按過的位址 → 按下當時的幀」。同一扇同名窗可能同時開好幾扇
    /// （SelectYesno 就會），所以是集合不是單一格。
    /// </summary>
    private sealed class Slot
    {
        public string AddonName;
        public readonly Dictionary<nint, long> Pressed = new();
    }

    private static readonly Dictionary<string, Slot> Slots = new(StringComparer.Ordinal);

    // Tick 用的可重用緩衝，沒有窗被記著時 Tick 是一個整數比較就回來，不配置任何東西。
    private static readonly List<string> NamesBuf = [];
    private static readonly HashSet<nint> PresentBuf = [];
    private static readonly List<nint> RemoveBuf = [];
    private static readonly List<string> EmptyKeysBuf = [];

    /// <summary>
    /// 守衛專用的幀計數器。<b>刻意不用 <c>Svc.PluginInterface.UiBuilder.FrameCount</c></b> ——
    /// 那個計數器在外掛 UI 被隱藏的期間<b>完全停止前進</b>，逃生口會永遠不到期。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 🔴 Dalamud 的 <c>UiBuilder.OnDraw()</c>（本 pin <c>Dalamud/Interface/UiBuilder.cs</c>）在三種情形成立時
    /// <b>直接 <c>return</c></b>：①使用者按熱鍵隱藏 UI ＋ <c>ToggleUiHide</c>　②<b>過場動畫</b> ＋
    /// <c>ToggleUiHideDuringCutscenes</c>（預設<b>開</b>）　③GPose ＋ <c>ToggleUiHideDuringGpose</c>；
    /// 而 <c>this.FrameCount++</c> 寫在那個 <c>return</c> 的<b>後面</b>（中間還隔著整段 <c>Draw</c> 派送）。
    /// </para>
    /// <para>
    /// ⇒ 過場動畫播放中、或使用者按下隱藏 UI 熱鍵的期間，拿它當時鐘的話「已經按過」的記號會
    /// <b>永遠</b>停在 <c>frame - pressedAt == 0</c>，<see cref="RePressEscapeFrames"/> 與
    /// <see cref="RoutineRePressEscapeFrames"/> 一律不到期：守衛從「按過就等一下」變成「按過就永久封鎖」。
    /// 這個方向不會崩（fail-closed），但「按一次翻一頁、窗不會消失」的窗（<c>Talk</c> 最典型）會停在
    /// 第一頁不動，整條任務卡到 NeoTaskManager 逾時、再被 <c>abortOnTimeout</c> 清掉<b>整條</b>佇列。
    /// </para>
    /// <para>
    /// 🔑 改成自己在 <c>Framework.Update</c> 上數（遞增點在 <see cref="Tick"/> 的最前面）。Dalamud 的
    /// <c>Framework.Update</c> 是掛在遊戲自己的 <c>Framework::Tick</c> 虛擬函式 hook 裡（<c>Dalamud/Game/Framework.cs</c>
    /// 的 <c>HandleFrameworkUpdate</c>），與 <c>UiBuilder.OnDraw</c> 完全無關，唯一的關閉點是遊戲結束
    /// （<c>HandleFrameworkDestroy</c> 把 <c>DispatchUpdateEvents</c> 設成 <see langword="false"/>），
    /// <b>不受 UI 隱藏／過場／GPose 影響</b>。同樣做法的先例：TCToolbox <c>Core/AddonPressGuard.cs</c>。
    /// </para>
    /// <para>
    /// 📌 兩種時鐘的速率：遊戲主迴圈是「跑一次 <c>Framework::Tick</c> 再畫一張」，正常情況下 1:1，
    /// 所以 15／60 這兩個幀數的意義沒有改變（那是艦隊政策值，這次<b>只換時鐘來源、不動數值</b>）。
    /// 真要說差別的話新時鐘只會更<b>保守</b>：UI 隱藏時它照走、繪製幀不走。
    /// 只有這裡讀寫它，而且全部發生在 framework 執行緒上；只做差值比較，不依賴絕對值（從 0 開始也對）。
    /// </para>
    /// </remarks>
    private static long frameCounter;

    private static long CurrentFrame => frameCounter;

    /// <summary>
    /// 從窗上讀出來的文字含 U+FFFD（替換字元）＝ 這幾幀窗的記憶體正在變動（多半是關閉中），
    /// 凡是靠文字做判定的按下點<b>這一幀不要碰</b>。這是崩潰前 log 裡實測看到的旁證。
    /// </summary>
    internal static bool TextIsUnstable(string text) => text != null && text.IndexOf('\uFFFD') >= 0;

    /// <summary>
    /// 這扇窗（位址）現在是不是還被記著「已經按過」—— 也就是「我們按過它，而它還沒從 addon 清單消失」。
    /// 只認<b>不帶參數組</b>的那把 key（＝「回答一次即終結」的那種按下）。
    /// </summary>
    /// <remarks>
    /// 🔴 只做位址等值比較，<b>永遠不解參</b>。
    /// <para>
    /// 🔑 用途是讓呼叫端把「對同一扇窗的第二個動作」延到下一輪去做（例如送完 callback 之後隔一輪才關窗），
    /// 而且分得出「同一扇窗還開著」與「新的一扇窗剛好落在同一塊記憶體」—— 後者在 <see cref="Tick"/>
    /// 把舊紀錄清掉之後就會回 <see langword="false"/>，光靠呼叫端自己存一個位址是分不出來的。
    /// </para>
    /// </remarks>
    internal static bool WasPressed(string addonName, nint addon)
        => addon != 0 && !string.IsNullOrEmpty(addonName)
        && Slots.TryGetValue(addonName, out var slot) && slot.Pressed.ContainsKey(addon);

    /// <summary>
    /// 問「這扇窗現在可以按嗎」，可以的話<b>順便記下</b>已經按過。呼叫端拿到
    /// <see langword="true"/> 才去按，按法（<c>AddonMaster</c>、<c>Callback.Fire</c>、送輸入事件……）
    /// 留給呼叫端自己決定。
    /// </summary>
    /// <param name="addonName">窗名。是 <see cref="Tick"/> 掃清單解除封鎖時用的名字，也是 key 的前半。</param>
    /// <param name="addon">要按的 addon 位址。<b>只做等值比較，這裡永遠不解參。</b></param>
    /// <param name="label">逃生口觸發時寫進 log 的名字；省略就用 key。</param>
    /// <param name="paramKey">
    /// <see langword="null"/>（預設）＝「回答一次即終結」的窗，整扇窗一把 key；
    /// 非空＝按下不會關的窗，同一扇窗對不同參數組各准按一次。
    /// </param>
    /// <param name="escapeIsRoutine">
    /// <see langword="true"/> ＝ 這個按下點「同一扇窗本來就會被按很多次」（Talk 按一次翻一頁、分頁鈕、
    /// 清單選列、隱藏而不拆除的標題畫面窗…），逃生口縮成 <see cref="RoutineRePressEscapeFrames"/>（15 幀）
    /// 而不是 <see cref="RePressEscapeFrames"/>（60 幀），走逃生口是常態，寫 <c>Debug</c> 不洗版；
    /// <see langword="false"/>（預設）＝ 走逃生口代表「按了是卻沒關掉」這種該被回報的異常，寫 <c>Information</c>。
    /// </param>
    /// <returns>
    /// <see langword="true"/> ＝ 可以按（而且已經記下）；<see langword="false"/> ＝ 這一輪不要按。
    /// </returns>
    /// <remarks>
    /// 回 <see langword="false"/> 對呼叫端的意義一律是「這一輪沒按到，下一輪再來」——
    /// 與「addon 還沒出現」「節流還沒放行」走的是同一條既有路徑，所以接上這個守衛<b>不會</b>
    /// 改變任何一個呼叫端的控制流。🔴 絕不回 <see langword="null"/>：NeoTaskManager 的 <c>bool?</c>
    /// 三態裡 <see langword="null"/> 是 Abort，會清掉整條佇列。
    /// </remarks>
    internal static bool TryPressOnce(string addonName, nint addon, string label = null, string paramKey = null, bool escapeIsRoutine = false)
    {
        if(addon == 0 || string.IsNullOrEmpty(addonName)) return false;
        var frame = CurrentFrame;
        if(paramKey != null && Slots.TryGetValue(addonName, out var answered) && answered.Pressed.TryGetValue(addon, out var answeredAt))
        {
            // 這扇窗已經被「回答」過（我們自己按了關閉／取消／是）。窗還在 ＝ 正在關閉中，任何參數組都不准再送。
            // 超過逃生口仍在的話交給不帶參數那把 key 自己去判，這裡放行。
            if(frame - answeredAt < RePressEscapeFrames) return false;
        }
        var key = paramKey == null ? addonName : addonName + "|" + paramKey;
        if(!Slots.TryGetValue(key, out var slot))
        {
            slot = new() { AddonName = addonName };
            Slots[key] = slot;
        }
        if(slot.Pressed.TryGetValue(addon, out var pressedAt))
        {
            // 這一扇已經按過。窗還在 ＝ 可能正在關閉中，此時再按就是上面說的 AVE。
            var escapeFrames = escapeIsRoutine ? RoutineRePressEscapeFrames : RePressEscapeFrames;
            if(frame - pressedAt < escapeFrames) return false;
            // 逃生口：等了遠超過關閉所需的時間，窗仍在。視為那次沒生效（或這是另一扇重用了同一塊
            // 記憶體的新窗），放行補按一次。
            var msg = $"{label ?? key}: 按下後 {frame - pressedAt} 幀仍是同一扇窗，補按一次";
            if(escapeIsRoutine) PluginLog.Debug(msg); else PluginLog.Information(msg);
        }
        slot.Pressed[addon] = frame;
        return true;
    }

    /// <summary>
    /// 對 <paramref name="addonName"/> 這扇窗（只看第 1 格）送一次「取消／關閉」
    /// （<c>Callback.Fire(addon, true, -1)</c>），同一扇窗只送一次。與 <see cref="TryPressOnce"/>
    /// 共用不帶參數的那把 key，所以別的模組對同一扇窗按過「是」之後這裡也不會再送取消。
    /// </summary>
    /// <returns>
    /// <see langword="true"/> 代表「這一輪呼叫端不要再往下走」—— 涵蓋「剛按了取消」與
    /// 「按過了、窗還在關閉中」兩種情形，兩者對呼叫端的意義相同（畫面上還有擋路的窗）。
    /// </returns>
    internal static bool TryCancelDialogOnce(string addonName)
    {
        if(!TryGetAddonByName<AtkUnitBase>(addonName, out var addon) || addon == null) return false;
        var current = (nint)addon;
        var frame = CurrentFrame;
        Slots.TryGetValue(addonName, out var slot);
        if(slot != null && slot.Pressed.TryGetValue(current, out var pressedAt))
        {
            // 這一扇已經按過。窗還在 ＝ 可能正在關閉中，此時再 FireCallback 就是 AVE。
            if(frame - pressedAt < RePressEscapeFrames) return true;
            // 逃生口，理由同 TryPressOnce。先把記號拿掉：下面若還沒 ready 就回 false，下一幀 ready 時再記再送，
            // 不要每幀都印一次逃生口。
            PluginLog.Information($"{addonName} 按下取消後 {frame - pressedAt} 幀仍未關閉，補按一次");
            slot.Pressed.Remove(current);
        }
        if(!addon->IsReady()) return false;
        if(slot == null)
        {
            slot = new() { AddonName = addonName };
            Slots[addonName] = slot;
        }
        slot.Pressed[current] = frame;
        Callback.Fire(addon, true, -1);
        return true;
    }

    /// <summary>
    /// 每幀無條件呼叫（<c>AutoRetainer.DialogGuardsTick</c>，在 <c>Load()</c> 最前面獨立訂閱 <c>Framework.Update</c>）。
    /// 做兩件事：①推進 <see cref="CurrentFrame"/> 這個守衛專用的時鐘　②被記下的位址已經從該窗名的清單裡
    /// 消失時解除封鎖 —— 後者是唯一能確定「上一次按下的那扇已經收乾淨」的證據。
    /// </summary>
    /// <remarks>
    /// 🔴 全程只做位址等值比較，<b>永遠不解參</b>。
    /// <para>
    /// 掃整串索引而不是只看第 1 個，是因為同時可能開著多扇同名窗（SelectYesno 就會），被記下的那扇
    /// 不一定在第 1 格；掃到第一個空的就停。同一個窗名底下不管有幾把 key（不帶參數的、各參數組的）
    /// 都只掃一次清單。
    /// </para>
    /// <para>
    /// 📌 這個外掛所有按下點都是 Framework.Update 或 NeoTaskManager（同樣跑在 Framework.Update 上）驅動的，
    /// 沒有 AddonLifecycle PostDraw／PostUpdate 驅動的按下點，所以輪詢解除就夠用，不需要
    /// PreFinalize／PostSetup 雙軌。這裡放在 Tick 最前面且不受任何開關限制：解除點若只長在各自的
    /// 分支裡，開關剛好在按下之後轉為關閉時記號會一直留著，下一扇重用同一塊位址的窗會被白白擋到逃生口。
    /// </para>
    /// </remarks>
    internal static void Tick()
    {
        // 🔴 遞增必須排在下面那行「沒有記號就回來」的前面：這個計數器是逃生口唯一的時間來源，
        //    沒有窗被記著的時候就停住的話，下一次按下之後的等待會從一個早就過期的值開始算，等於沒有時鐘。
        frameCounter++;
        if(Slots.Count == 0) return;
        NamesBuf.Clear();
        EmptyKeysBuf.Clear();
        foreach(var (key, slot) in Slots)
        {
            if(slot.Pressed.Count == 0)
            {
                EmptyKeysBuf.Add(key);
                continue;
            }
            if(!NamesBuf.Contains(slot.AddonName)) NamesBuf.Add(slot.AddonName);
        }
        foreach(var name in NamesBuf)
        {
            PresentBuf.Clear();
            for(var i = 1; i < 100; i++)
            {
                var present = (nint)Svc.GameGui.GetAddonByName(name, i).Address;
                if(present == 0) break;
                PresentBuf.Add(present);
            }
            foreach(var (key, slot) in Slots)
            {
                if(slot.AddonName != name || slot.Pressed.Count == 0) continue;
                RemoveBuf.Clear();
                foreach(var addr in slot.Pressed.Keys)
                {
                    if(!PresentBuf.Contains(addr)) RemoveBuf.Add(addr);
                }
                foreach(var addr in RemoveBuf) slot.Pressed.Remove(addr);
                if(slot.Pressed.Count == 0) EmptyKeysBuf.Add(key);
            }
        }
        // 空掉的 key 順手收掉，帶動態參數組的 key（Assign{VentureID} 這類）才不會無限累積。
        foreach(var key in EmptyKeysBuf) Slots.Remove(key);
    }
}
