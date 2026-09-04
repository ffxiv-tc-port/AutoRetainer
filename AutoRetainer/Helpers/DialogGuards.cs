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
/// 🔑 解除點＝<see cref="Tick"/> 每幀掃 addon 清單（全索引 1..<see cref="MaxAddonIndex"/>，
/// 掃到第一個空的停），被記下的位址不在清單裡才解除。走法與
/// <see cref="Utils.GetSpecificYesno(Predicate{string})"/> 相同，但<b>天花板不同</b>：那邊是「找窗」，
/// 掃不到就是不按（fail-closed，只會少做事）；這裡是「解除封鎖」，掃不到會誤判成「窗已經收掉」而放行
/// （fail-open，會崩），所以這裡的天花板必須取遊戲自己的真值。
/// 判準刻意<b>不</b>用「文字還對不對」或「還可不可見」：窗在拆除途中可能有幾幀讀不到提示文字、或已經
/// 被設成不可見，拿那些當「窗不見了」會<b>正好在最危險的那幾幀</b>把封鎖解除掉。
/// </para>
/// <para>
/// 🔑 2026-09-04 補上<b>第二條</b>解除路徑，只給 <see cref="PersistentAddons"/> 裡的常駐窗名用：那些窗是
/// 「顯示／隱藏」而不是「建立／銷毀」，位址永遠不會從清單消失 ⇒ 上面那條唯一的解除路徑對它們是死路，
/// 記號一旦記下就<b>永不解除</b>（實機證據見 <see cref="HiddenReleaseFrames"/>）。
/// 新路徑是「連續觀察到隱藏 <see cref="HiddenReleaseFrames"/> 幀」，看到可見就歸零 —— 它<b>不</b>推翻上面那句話：
/// 「這一幀不可見」照樣不算數，要的是<b>穩定</b>隱藏，而穩定隱藏的長度（20 幀）刻意取到危險窗口（實測 &lt;10 幀）的兩倍以上。
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
    /// 寫 <c>Information</c>（使用者跑 LogLevel 1，Debug 收得到但單檔數十萬行會淹沒）。
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
    /// <para>
    /// 🔑 2026-09-04 更名：這個值的角色是<b>節流間隔</b>，不是「出了事才走的逃生口」。這一類窗本來就會被
    /// 按很多次，間隔到期再按一次是它<b>唯一</b>的前進方式 ⇒ 每次都寫一行 log 等於把正常流程當異常記錄，
    /// 實機兩天光 <c>Talk</c> 就 10,423 行（而且是 <c>Debug</c>，使用者的 <c>LogLevel</c> 是 1，收得到）。
    /// 現在改成：走這條完全不寫 log，只累加次數，窗收掉時寫一行總結。
    /// </para>
    /// <para>
    /// 🔴 <b>值沒有改，也不該在沒有實機證據的情況下改。</b>要讓這類窗前進得更快，唯一的辦法是拿「內容變了沒」
    /// 當「這扇窗還活著」的證據，而讀內容必須解參那扇可能正在拆除的窗的節點 —— 那正是這個守衛在防的 AVE，
    /// 而且失敗形式是崩潰不是回錯值。時間是唯一不必解參就能用的判準。
    /// </para>
    /// </remarks>
    internal const int RoutineRepressIntervalFrames = 15;

    /// <summary>
    /// <see cref="Tick"/> 掃同名 addon 清單時最多掃到第幾個實例；掃到第一個空的就提早停。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 📌 256 是<b>遊戲自己夾的上限</b>，不是估出來的數字：<c>AtkUnitManager::GetAddonByName</c>（台服
    /// <c>0x14064B960</c>）走的是 <c>AtkUnitManager.AllLoadedUnitsList</c>（<c>FieldOffset(0x6900)</c>），
    /// 而 <c>AtkUnitList</c> 的項目陣列是 <c>FixedSizeArray256</c>（<c>AtkUnitList.cs:8</c>：項目在 <c>+0x8</c>、
    /// <c>Count</c> 在 <c>+0x808</c> ⇒ 相差 <c>0x800</c> ＝ 256×8）；反組譯裡把 <c>Count</c> 讀進來之後緊接著
    /// <c>mov ebp, 0x100</c> 就把它硬夾成 256。同名實例不可能多過清單本身的長度，所以 256 就是真值。
    /// </para>
    /// <para>
    /// 🔑 <c>index</c> 的語意是「掃完整份清單、數第 <c>index</c> 個<b>同名</b>命中」，<b>不是</b>原始槽位編號
    /// （反組譯：逐項比對名字，命中就把傳入的 index 減 1，減到 0 才回傳）⇒ 同名實例的索引是<b>連續</b>的，
    /// 「掃到第一個空的就停」在數學上不可能漏掉還活著的實例。
    /// </para>
    /// <para>
    /// ⚠️ 這個值以前是 99，沒有任何出處。取太小的後果是 <b>fail-open</b>：被記下的那扇窗排在天花板之外時，
    /// <see cref="Tick"/> 會誤判成「它已經收掉了」而解除封鎖，下一幀就對一扇正在關閉的窗再送一次
    /// ＝ 本檔開頭講的那種 AVE。這個守衛<b>沒有</b> AddonLifecycle 那一軌兜底（理由見 <see cref="Tick"/>
    /// 的說明），天花板是唯一的防線。
    /// </para>
    /// <para>
    /// 📌 改大不花成本：這段只在「還有按下記號沒被解除」時才跑（同時存在的記號實務上 0~3 個），
    /// 而且掃到第一個空的就 break —— 正常情況下每次只跑 1~3 圈，天花板只有在真的同時開著 256 扇
    /// 同名窗時才碰得到。
    /// </para>
    /// </remarks>
    private const int MaxAddonIndex = 256;

    /// <summary>
    /// 「常駐 addon」（顯示／隱藏、而不是建立／銷毀的窗）被按下之後，最少要<b>連續</b>觀察到它「還在清單裡、
    /// 但已經被隱藏」這麼多幀，才把按下記號解除。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 🔴 <b>為什麼需要這條路徑</b>：<see cref="Tick"/> 原本唯一的解除條件是「位址不在 <c>GetAddonByName</c> 的清單裡」。
    /// 對常駐窗而言那個條件<b>永遠不成立</b>（它從頭到尾都在清單裡，只是被隱藏），於是記號一旦記下就<b>永不解除</b>，
    /// 整扇窗退化成「每 <see cref="RePressEscapeFrames"/> 幀才准動作一次」。實機證據（2026-09-04 單一場次）：
    /// <c>ContextMenu</c> 送出 295 次，其中 <b>290 次</b>是走逃生口走出來的（＝記號從沒被解除過），
    /// 另有 <b>144 次</b>使用者的右鍵被整個吞掉（<c>QuickSellItems</c> 直接 <c>return</c>，遊戲自己的選單留在畫面上，
    /// 使用者看到的現象是「按了沒反應／變成別的選項」）。
    /// </para>
    /// <para>
    /// 🔴 <b>為什麼是「連續 N 幀」而不是「這一幀不可見就解除」</b>：本檔開頭寫過，窗在拆除途中會有幾幀「已經被設成
    /// 不可見、但還沒拆完」，那正是這個守衛在防的危險窗口（實測 &lt;10 幀）。要求連續觀察到隱藏 20 幀（約兩倍於
    /// 危險窗口）才解除，等於「等到穩定隱藏＝拆除已經結束」才放行。看到可見就把計數歸零，所以短暫閃一下不會累積。
    /// </para>
    /// <para>
    /// 🔑 <b>20 這個數字是量出來的，不是猜的</b>：同一份 log 量到 144 次被吞掉的右鍵距上一次送出的幀距，
    /// 最快 14 幀、p5 ＝ 16 幀、p25 ＝ 33 幀、中位數 41 幀（該場次每幀 10.07 ms）。取 20 幀可以救回其中 85%，
    /// 而剩下的都落在「距上次送出 &lt; 0.17 秒」的那一叢（16 筆密集擠在 145~170 ms，形狀比較像滑鼠彈跳／連點
    /// 而不是人的第二次點擊）——那一叢本來就該被擋。往下調到 12 幀可以救回 100%，但只剩 2 幀的安全邊際，不值得。
    /// </para>
    /// <para>
    /// 🔴 這個值只在 <see cref="PersistentAddons"/> 裡的窗名上生效；其餘窗名的解除條件<b>一個字都沒有改</b>。
    /// </para>
    /// <para>
    /// 🔑 <b>這條路徑在每一發的安全性上不是退步</b>：既有的 <see cref="RePressEscapeFrames"/> 逃生口是
    /// 「距上次按下滿 60 幀就放行」，<b>完全不看那扇窗當下的狀態</b>，而且那條路徑實機已經跑了 290 次；
    /// 新路徑要的是「連續觀察到隱藏 20 幀」——那是<b>拆除已完成的直接證據</b>，比「按下後過了 60 幀」這個
    /// 間接證據強。
    /// </para>
    /// <para>
    /// 🔴 但上面那段是<b>論證不是證明</b>，所以真正把崩潰面拆掉的是另一件事：唯一的消費端
    /// （<c>QuickSellItems</c> 的 detour）在送 callback 之前補了一道就地的 <c>IsAddonReady</c>。
    /// 它的放行條件是「可見」，正好是這裡解除條件（「連續 20 幀不可見」）的反面 ⇒ 記號被解除之後還要能送出，
    /// 中間必須有一次遊戲自己把窗重新 Show 起來。所以<b>就算這個幀數設得太短</b>，最壞的結果也只是
    /// 「那一發沒送出」，不會變成對正在拆除的窗再送一次（＝攔不到的 AccessViolation）。
    /// ⚠️ 要把新的窗名加進 <see cref="PersistentAddons"/> 時，<b>連它的每一個按下點有沒有這道就地檢查一起看</b>。
    /// </para>
    /// </remarks>
    internal const int HiddenReleaseFrames = 20;

    /// <summary>
    /// 已經確認是「常駐」的窗名 —— 它們從遊戲啟動起就一直在 <c>AllLoadedUnitsList</c> 裡，關閉只是被設成不可見，
    /// 位址永遠不會從 <c>GetAddonByName</c> 的清單消失。只有這些窗名才套用「連續隱藏 <see cref="HiddenReleaseFrames"/>
    /// 幀就解除」這條新規則。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 🔑 <b>怎麼判定一個窗名是常駐的（加名字進來的門檻）</b>：拿實機 log 數「真的送出的次數」與「走逃生口的次數」。
    /// 兩者幾乎相等 ＝ 記號從來沒有被『消失就移除』那條路徑解除過 ＝ 這扇窗不會被銷毀。
    /// <c>ContextMenu</c> 是 295 : 290，因此收錄。
    /// </para>
    /// <para>
    /// ⚠️ <b>沒有這種證據的窗名一律不要加。</b>猜錯的方向是危險的：把一扇「會被銷毀」的窗誤標成常駐，等於在它
    /// 拆除途中提早 <see cref="HiddenReleaseFrames"/> 幀解除封鎖。已知的候選但<b>證據不足、刻意沒收</b>的有
    /// <c>ContextIconMenu</c>（與 <c>ContextMenu</c> 同一個 <c>AgentContext</c> 家族，直覺上同樣常駐，
    /// 但實機 log 裡沒有它的逃生口紀錄，無從證實）。
    /// </para>
    /// <para>
    /// 📌 目前收錄的 <c>ContextMenu</c> 全外掛<b>只有一個</b>按下點（<c>QuickSellItems</c> 的 hook detour），
    /// 所以這條新規則的實際影響面就是那一個呼叫點。
    /// </para>
    /// </remarks>
    private static readonly HashSet<string> PersistentAddons = new(StringComparer.Ordinal)
    {
        "ContextMenu",
    };

    /// <summary>一個位址的按下紀錄。</summary>
    /// <remarks>
    /// 🔑 <see cref="Represses"/> 只為了診斷存在：純節流那一類窗（<c>escapeIsRoutine</c>）不再每按一次
    /// 寫一行 log，改成累加，等這扇窗真的從 addon 清單消失時由 <see cref="Tick"/> 寫一行總結。
    /// <para>
    /// 🔴 <b>刻意是 class 不是 struct。</b>當初寫成 struct 時，<c>TryGetValue</c> 拿到的是複本，
    /// 「改完要寫回字典」就成了承重的一行 —— 而漏掉寫回<b>不會編譯失敗</b>，只會讓間隔判斷永遠
    /// 拿到舊的 <see cref="Frame"/>：到期之後<b>每一幀都放行</b>，正好是這個守衛在防的那種
    /// AccessViolation（corrupted-state exception，<c>try</c>/<c>catch</c> 攔不到）。
    /// 改成 class 之後取出的是參考、就地改就生效，這個地雷從根本上不存在。
    /// ⚠️ 同時存在的紀錄實務上只有 0~3 個，多出來的配置可以忽略。
    /// </para>
    /// </remarks>
    private sealed class PressRecord
    {
        /// <summary>最近一次按下的幀。所有間隔判斷都拿它跟現在的幀比。</summary>
        public long Frame;
        /// <summary>第一次按下的幀，只用來寫總結那行的「前後共幾幀」。</summary>
        public long FirstFrame;
        /// <summary>間隔到期後又按了幾次。0 ＝ 只按過一次。</summary>
        public int Represses;
        /// <summary>
        /// 連續觀察到「位址還在清單裡、但那扇窗已經被隱藏」的幀數。只有 <see cref="PersistentAddons"/> 裡的
        /// 窗名會累加；看到可見就歸零，重新按下也歸零。到達 <see cref="HiddenReleaseFrames"/> 就解除記號。
        /// </summary>
        public int HiddenFrames;
    }

    /// <summary>
    /// 一把 key（窗名＋參數組）底下「已經按過的位址 → 按下當時的幀」。同一扇同名窗可能同時開好幾扇
    /// （SelectYesno 就會），所以是集合不是單一格。
    /// </summary>
    private sealed class Slot
    {
        public string AddonName;
        /// <summary>總結那行要印的名字（第一個給了 label 的呼叫端說了算）；沒有就退回 key。</summary>
        public string Label;
        public readonly Dictionary<nint, PressRecord> Pressed = new();
    }

    private static readonly Dictionary<string, Slot> Slots = new(StringComparer.Ordinal);

    // Tick 用的可重用緩衝，沒有窗被記著時 Tick 是一個整數比較就回來，不配置任何東西。
    private static readonly List<string> NamesBuf = [];
    private static readonly HashSet<nint> PresentBuf = [];
    private static readonly List<nint> RemoveBuf = [];
    private static readonly List<string> EmptyKeysBuf = [];
    // 本幀掃到「還在清單裡、但被隱藏」的位址；只在常駐窗名上填。
    private static readonly HashSet<nint> HiddenBuf = [];
    // 「這個窗名第一次走隱藏解除」只寫一行 Information，之後不再寫（每場遊戲每個窗名最多一行，不會洗版）。
    private static readonly HashSet<string> HiddenReleaseReported = new(StringComparer.Ordinal);

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
    /// <see cref="RoutineRepressIntervalFrames"/> 一律不到期：守衛從「按過就等一下」變成「按過就永久封鎖」。
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
    /// 這扇窗（位址）上一次被按下距今幾幀；沒有記號時回 <c>-1</c>。純診斷用，呼叫端不要拿它做安全判斷。
    /// </summary>
    /// <remarks>🔴 只做位址等值比較，<b>永遠不解參</b>。</remarks>
    internal static long FramesSincePress(string addonName, nint addon)
        => addon != 0 && !string.IsNullOrEmpty(addonName)
        && Slots.TryGetValue(addonName, out var slot) && slot.Pressed.TryGetValue(addon, out var rec)
        ? CurrentFrame - rec.Frame : -1;

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
    /// <see langword="true"/> ＝ 這個按下點是「多次互動窗」：同一扇窗本來就會被按很多次而且按了不會消失
    /// （<c>Talk</c> 按一次翻一頁、分頁鈕、清單選列、隱藏而不拆除的標題畫面窗…）。對這一類窗這個守衛的角色
    /// 是<b>純節流</b>：每 <see cref="RoutineRepressIntervalFrames"/>（15）幀最多按一次，間隔到期再按就是它前進
    /// 的正常方式 ⇒ <b>不寫任何 log</b>（只累加次數，窗收掉時由 <see cref="Tick"/> 寫一行總結）。
    /// <see langword="false"/>（預設）＝「回答一次即終結」的窗，按下去就該關。間隔改成
    /// <see cref="RePressEscapeFrames"/>（60）幀，而且走到那裡代表「按了卻沒關掉」，是該被回報的異常 ⇒
    /// 每次都寫 <c>Information</c>。
    /// 🔴 兩類的<b>安全性完全相同</b>（都是「窗還在就不准再送」＋位址等值比較、永不解參），差別只在間隔長度
    /// 與要不要寫 log。這次（2026-09-04）只動 log，兩邊的幀數都沒改。
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
        if(paramKey != null && Slots.TryGetValue(addonName, out var answered) && answered.Pressed.TryGetValue(addon, out var answeredRec))
        {
            // 這扇窗已經被「回答」過（我們自己按了關閉／取消／是）。窗還在 ＝ 正在關閉中，任何參數組都不准再送。
            // 超過逃生口仍在的話交給不帶參數那把 key 自己去判，這裡放行。
            if(frame - answeredRec.Frame < RePressEscapeFrames) return false;
        }
        var key = paramKey == null ? addonName : addonName + "|" + paramKey;
        if(!Slots.TryGetValue(key, out var slot))
        {
            slot = new() { AddonName = addonName };
            Slots[key] = slot;
        }
        slot.Label ??= label;
        if(slot.Pressed.TryGetValue(addon, out var rec))
        {
            // 這一扇已經按過。窗還在 ＝ 可能正在關閉中，此時再按就是上面說的 AVE。
            if(escapeIsRoutine)
            {
                // 純節流：這一類窗按了不會消失，間隔到期再按一次就是它前進的正常方式，不是異常。
                // ⇒ 這裡刻意不寫 log：實機兩天光 Talk 就 10,423 行 Debug（LogLevel 1 收得到）。
                //   只累加次數，窗真的收掉時由 Tick 寫一行總結，行數從「按了幾次」降到「開過幾扇窗」。
                if(frame - rec.Frame < RoutineRepressIntervalFrames) return false;
                rec.Represses++;
            }
            else
            {
                // 逃生口：等了遠超過關閉所需的時間，窗仍在。視為那次沒生效（或這是另一扇重用了同一塊
                // 記憶體的新窗），放行補按一次。
                if(frame - rec.Frame < RePressEscapeFrames) return false;
                var msg = $"{label ?? key}: 按下後 {frame - rec.Frame} 幀仍是同一扇窗，補按一次";
                PluginLog.Information(msg);
            }
            // PressRecord 是 class，上面兩條分支對 rec 的改動已經就地生效了。
            // 🔑 下面這行寫回是「刻意保留」的冗餘：class 語意下它只是把同一個參考放回去，
            //    struct 語意下它才是承重的那一行 —— 留著它，兩種語意下這段都正確，
            //    未來有人把型別改回 struct、或改成先取區域複本，也不會靜默生出崩潰面。
            rec.Frame = frame;
            // 🔴 這一行是承重的：隱藏解除是「從最後一次按下起算」連續隱藏幾幀。漏掉的話，
            //    「窗已經隱藏了 15 幀 → 逃生口在第 60 幀補按一次」之後只要再 5 幀就會解除記號，
            //    等於把補按之後的保護期縮到 5 幀。
            rec.HiddenFrames = 0;
            slot.Pressed[addon] = rec;
            return true;
        }
        slot.Pressed[addon] = new PressRecord { Frame = frame, FirstFrame = frame };
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
        if(slot != null && slot.Pressed.TryGetValue(current, out var cancelRec))
        {
            // 這一扇已經按過。窗還在 ＝ 可能正在關閉中，此時再 FireCallback 就是 AVE。
            if(frame - cancelRec.Frame < RePressEscapeFrames) return true;
            // 逃生口，理由同 TryPressOnce。先把記號拿掉：下面若還沒 ready 就回 false，下一幀 ready 時再記再送，
            // 不要每幀都印一次逃生口。
            PluginLog.Information($"{addonName} 按下取消後 {frame - cancelRec.Frame} 幀仍未關閉，補按一次");
            slot.Pressed.Remove(current);
        }
        if(!addon->IsReady()) return false;
        if(slot == null)
        {
            slot = new() { AddonName = addonName };
            Slots[addonName] = slot;
        }
        slot.Pressed[current] = new PressRecord { Frame = frame, FirstFrame = frame };
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
    /// 不一定在第 1 格；掃到第一個空的就停，上限 <see cref="MaxAddonIndex"/>。同一個窗名底下不管有
    /// 幾把 key（不帶參數的、各參數組的）都只掃一次清單。
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
            // 常駐窗名才需要知道「可不可見」；其餘窗名這一幀連讀都不讀，行為與改動前逐字相同。
            var persistent = PersistentAddons.Contains(name);
            PresentBuf.Clear();
            HiddenBuf.Clear();
            for(var i = 1; i <= MaxAddonIndex; i++)
            {
                var unit = Svc.GameGui.GetAddonByName(name, i);
                var present = unit.Address;
                if(present == 0) break;
                PresentBuf.Add(present);
                // 🔑 這是本檔唯一一次解參，而且解的是「本幀剛從 GetAddonByName 拿回來」的指標，
                //    不是跨幀保存的 slot.Pressed 的 key（那些永遠只做等值比較）。
                //    AtkUnitBasePtr.IsVisible 先判 null、再讀 AtkUnitBase.Flags198 的 bit 0x200000
                //    （固定位移的 uint 欄位，不再往下追任何指標）。
                if(persistent && !unit.IsVisible) HiddenBuf.Add(present);
            }
            foreach(var (key, slot) in Slots)
            {
                if(slot.AddonName != name || slot.Pressed.Count == 0) continue;
                RemoveBuf.Clear();
                foreach(var (addr, rec) in slot.Pressed)
                {
                    // ①既有路徑：位址從清單裡消失 ＝ 那扇窗已經收乾淨。行為完全沒有改。
                    if(!PresentBuf.Contains(addr))
                    {
                        RemoveBuf.Add(addr);
                        continue;
                    }
                    // ②新路徑：只有常駐窗名走得到。它們永遠不會從清單消失，①對它們是死路。
                    if(!persistent) continue;
                    if(!HiddenBuf.Contains(addr))
                    {
                        // 還看得見 ＝ 要嘛還沒關、要嘛正在關閉中的危險窗口內。歸零重數。
                        rec.HiddenFrames = 0;
                        continue;
                    }
                    if(++rec.HiddenFrames < HiddenReleaseFrames) continue;
                    RemoveBuf.Add(addr);
                    if(HiddenReleaseReported.Add(name))
                        PluginLog.Information($"{name}：偵測到這是常駐窗（隱藏而不銷毀），按下記號改由「連續隱藏 {HiddenReleaseFrames} 幀」解除。這一行每個窗名每次遊戲只寫一次。");
                }
                foreach(var addr in RemoveBuf)
                {
                    // 純節流那一類窗的總結：一扇窗一行，取代原本「每按一次一行」。
                    // 這裡是唯一能確定「這扇窗已經收乾淨」的時點，所以總次數也只有在這裡才是完整的。
                    if(slot.Pressed.TryGetValue(addr, out var done) && done.Represses > 0)
                        PluginLog.Debug($"{slot.Label ?? key}: 這扇窗按了 {done.Represses + 1} 次（每 {RoutineRepressIntervalFrames} 幀最多一次）才收掉，前後共 {done.Frame - done.FirstFrame} 幀");
                    slot.Pressed.Remove(addr);
                }
                if(slot.Pressed.Count == 0) EmptyKeysBuf.Add(key);
            }
        }
        // 空掉的 key 順手收掉，帶動態參數組的 key（Assign{VentureID} 這類）才不會無限累積。
        foreach(var key in EmptyKeysBuf) Slots.Remove(key);
    }
}
