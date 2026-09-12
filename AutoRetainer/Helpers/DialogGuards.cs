using FFXIVClientStructs.FFXIV.Component.GUI;

namespace AutoRetainer.Helpers;

/// <summary>
/// 「這扇窗已經按過了」的共用守衛：同一扇窗（位址）在它走完生命週期之前只按一次。
/// 全外掛所有對 addon 的按法都要先問過 <see cref="TryPressOnce"/>；解除點集中在 <see cref="Tick"/>。
/// 🔴 這是在防一種 <c>try</c>/<c>catch</c> 攔不住的崩潰：唯一的防護是「不要送第二次」，不是「送了再接住」。
/// </summary>
internal static unsafe class DialogGuards
{
    /// <summary>
    /// 已經按過、那扇窗卻還沒消失時，最多再等這麼多幀才允許補按一次。
    /// </summary>
    /// <remarks>
    /// 🔑 這不是節流 —— 真正的防護是「同一扇窗只按一次」，這個值只是防死鎖的逃生口。
    /// </remarks>
    internal const int RePressEscapeFrames = 60;

    /// <summary>
    /// 「按一次翻一頁、窗不會因為被按而消失」的多次互動窗（Talk 是代表）專用的逃生口：<see cref="TryPressOnce"/> 的 <c>escapeIsRoutine</c> 為 <see langword="true"/> 時用它取代 <see cref="RePressEscapeFrames"/>。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>值沒有改，也不該在沒有實機證據的情況下改。</b>
    /// ⚠️ 判準刻意<b>不</b>用「文字變了」當翻頁證據：關閉中文字會讀壞，時間是唯一不靠未證實假設的判準。
    /// </remarks>
    internal const int RoutineRepressIntervalFrames = 15;

    /// <summary>
    /// <see cref="Tick"/> 掃同名 addon 清單時最多掃到第幾個實例；掃到第一個空的就提早停。
    /// </summary>
    /// <remarks>
    /// 📌 256 是<b>遊戲自己夾的上限</b>，不是估出來的數字。
    /// 取太小的後果是 <b>fail-open</b>：天花板是唯一的防線。
    /// </remarks>
    private const int MaxAddonIndex = 256;

    /// <summary>
    /// 「常駐 addon」（顯示／隱藏、而不是建立／銷毀的窗）被按下之後，最少要<b>連續</b>觀察到它「還在清單裡、
    /// 但已經被隱藏」這麼多幀，才把按下記號解除。
    /// </summary>
    /// <remarks>
    /// 🔴 這個值只在 <see cref="PersistentAddons"/> 裡的窗名上生效；其餘窗名的解除條件<b>一個字都沒有改</b>。
    /// 要求連續觀察到隱藏 20 幀（約兩倍於危險窗口）才解除，等於「等到穩定隱藏＝拆除已經結束」才放行。
    /// </remarks>
    internal const int HiddenReleaseFrames = 20;

    /// <summary>
    /// 已經確認是「常駐」的窗名 —— 它們從遊戲啟動起就一直在 <c>AllLoadedUnitsList</c> 裡，關閉只是被設成不可見，
    /// 位址永遠不會從 <c>GetAddonByName</c> 的清單消失。只有這些窗名才套用「連續隱藏 <see cref="HiddenReleaseFrames"/>
    /// 幀就解除」這條新規則。
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>沒有這種證據的窗名一律不要加。</b>
    /// </remarks>
    private static readonly HashSet<string> PersistentAddons = new(StringComparer.Ordinal)
    {
        "ContextMenu",
    };

    /// <summary>一個位址的按下紀錄。</summary>
    /// <remarks>
    /// 🔴 <b>刻意是 class 不是 struct。</b>
    /// 而漏掉寫回<b>不會編譯失敗</b>，只會讓間隔判斷永遠拿到舊的 <see cref="Frame"/>：到期之後<b>每一幀都放行</b>，正好是這個守衛在防的那種 AccessViolation。
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
    /// 守衛專用的幀計數器。<b>刻意不用 <c>Svc.PluginInterface.UiBuilder.FrameCount</c></b> —— 那個計數器在外掛 UI 被隱藏的期間<b>完全停止前進</b>，逃生口會永遠不到期。
    /// </summary>
    /// <remarks>
    /// 🔴 Dalamud 的 <c>UiBuilder.OnDraw()</c> 在三種情形成立時<b>直接 <c>return</c></b>，
    /// <c>this.FrameCount++</c> 寫在那個 <c>return</c> 的<b>後面</b>（中間還隔著整段 <c>Draw</c> 派送）。
    /// 只有這裡讀寫它，而且全部發生在 framework 執行緒上；只做差值比較，不依賴絕對值（從 0 開始也對）。
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
    /// <see langword="true"/> ＝ 這個按下點是「多次互動窗」，<b>純節流</b>：每 <see cref="RoutineRepressIntervalFrames"/>（15）幀最多按一次，<b>不寫任何 log</b>（只累加次數，窗收掉時由 <see cref="Tick"/> 寫一行總結）。
    /// <see langword="false"/>（預設）＝「回答一次即終結」的窗，按下去就該關。間隔改成 <see cref="RePressEscapeFrames"/>（60）幀，而且走到那裡代表「按了卻沒關掉」，是該被回報的異常 ⇒ 每次都寫 <c>Information</c>。
    /// 🔴 兩類的<b>安全性完全相同</b>（都是「窗還在就不准再送」＋位址等值比較、永不解參），差別只在間隔長度與要不要寫 log。
    /// </param>
    /// <returns>
    /// <see langword="true"/> ＝ 可以按（而且已經記下）；<see langword="false"/> ＝ 這一輪不要按。
    /// </returns>
    /// <remarks>
    /// 回 <see langword="false"/> 對呼叫端的意義一律是「這一輪沒按到，下一輪再來」
    /// 🔴 絕不回 <see langword="null"/>：NeoTaskManager 的 <c>bool?</c> 三態裡 <see langword="null"/> 是 Abort，會清掉整條佇列。
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
    /// <see langword="true"/> 代表「這一輪呼叫端不要再往下走」兩者對呼叫端的意義相同（畫面上還有擋路的窗）。
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
    /// 做兩件事：①推進 <see cref="CurrentFrame"/> 這個守衛專用的時鐘　②被記下的位址已經從該窗名的清單裡消失時解除封鎖 —— 後者是唯一能確定「上一次按下的那扇已經收乾淨」的證據。
    /// </summary>
    /// <remarks>
    /// 🔴 全程只做位址等值比較，<b>永遠不解參</b>。
    /// 沒有 AddonLifecycle PostDraw／PostUpdate 驅動的按下點，所以輪詢解除就夠用，不需要 PreFinalize／PostSetup 雙軌。
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
