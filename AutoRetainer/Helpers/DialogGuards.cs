using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
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
    /// 🔴 <b>不該在沒有實機證據的情況下改這個值。</b>
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
    /// 🔴 這個值只在 <see cref="PersistentAddons"/> 裡的窗名上生效；其餘窗名<b>只走</b>「位址從清單消失」那條解除。
    /// 要求連續觀察到隱藏 20 幀（約兩倍於危險窗口）才解除，等於「等到穩定隱藏＝拆除已經結束」才放行。
    /// </remarks>
    internal const int HiddenReleaseFrames = 20;

    /// <summary>
    /// 已經確認是「常駐」的窗名 —— 它們從遊戲啟動起就一直在 <c>AllLoadedUnitsList</c> 裡，關閉只是被設成不可見，
    /// 位址永遠不會從 <c>GetAddonByName</c> 的清單消失。只有這些窗名才套用「連續隱藏 <see cref="HiddenReleaseFrames"/>
    /// 幀就解除」這條規則。
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>沒有確認過是常駐的窗名一律不要加。</b>
    /// </remarks>
    private static readonly HashSet<string> PersistentAddons = new(StringComparer.Ordinal)
    {
        "ContextMenu",
        "ContextIconMenu",
        // Talk 關閉只是被設成不可見，位址整場不離開清單 ⇒ 路徑①要等到登出那一刻才觸發得到。
        "Talk",
    };

    /// <summary>一個位址的按下紀錄。</summary>
    /// <remarks>
    /// 🔴 <b>刻意是 class 不是 struct</b>：struct 的話每次改動都要寫回字典，
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

    /// <summary>看過 PreFinalize 的（窗名，位址）：位址從清單消失或同位址 PostSetup 之前任何按法都不准，沒有逃生口。</summary>
    /// <remarks>受守護的窗名一進 PreFinalize 就上鎖，不管我們按過沒有。</remarks>
    private static readonly HashSet<(string Name, nint Address)> Finalizing = [];

    /// <summary>窗名 → 已掛上的 PreFinalize／PostSetup 監聽器（掛上就不拆，只在 <see cref="ForceTeardown"/> 拆）。</summary>
    private static readonly Dictionary<string, IAddonLifecycle.AddonEventDelegate> Watchers = new(StringComparer.Ordinal);
    private static readonly List<string> LifecycleEmptyKeysBuf = [];
    private static readonly List<(string Name, nint Address)> UnlockBuf = [];

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
    /// 凡是靠文字做判定的按下點<b>這一幀不要碰</b>。
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
    /// <see langword="false"/>（預設）＝「回答一次即終結」的窗，按下去就該關。間隔是 <see cref="RePressEscapeFrames"/>（60）幀，走到那裡代表「按了卻沒關掉」，是該被回報的異常 ⇒ 每次都寫 <c>Information</c>。
    /// 🔴 兩類的<b>安全性完全相同</b>（都是「窗還在就不准再送」＋位址等值比較、永不解參），差別只在間隔長度與要不要寫 log。
    /// </param>
    /// <returns>
    /// <see langword="true"/> ＝ 可以按（而且已經記下）；<see langword="false"/> ＝ 這一輪不要按。
    /// </returns>
    /// <remarks>
    /// 回 <see langword="false"/> 對呼叫端的意義一律是「這一輪沒按到，下一輪再來」。
    /// 🔴 絕不回 <see langword="null"/>：NeoTaskManager 的 <c>bool?</c> 三態裡 <see langword="null"/> 是 Abort，會清掉整條佇列。
    /// </remarks>
    internal static bool TryPressOnce(string addonName, nint addon, string label = null, string paramKey = null, bool escapeIsRoutine = false)
    {
        if(addon == 0 || string.IsNullOrEmpty(addonName)) return false;
        EnsureWatching(addonName);
        // 常駐窗的解除條件是「連續隱藏」，這裡是它的反面：隱藏中（或這一幀查不到）的常駐窗一律不送。
        if(PersistentAddons.Contains(addonName) && !IsListedAndVisible(addonName, addon)) return false;
        if(Finalizing.Contains((addonName, addon))) return false;
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
            // 這一扇已經按過。窗還在 ＝ 可能正在關閉中，此時再按就是攔不住的 AccessViolation。
            if(escapeIsRoutine)
            {
                // 純節流：這一類窗按了不會消失，間隔到期再按一次就是它前進的正常方式，不是異常。
                // ⇒ 刻意不寫 log（每按一次一行會洗版）：只累加次數，窗真的收掉時由 Tick 寫一行總結。
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
            // PressRecord 是 class，上面兩條分支對 rec 的改動已經就地生效；下面 slot.Pressed[addon] = rec 的寫回是
            // 🔑 「刻意保留」的冗餘：未來有人把型別改回 struct、或改成先取區域複本，也不會靜默生出崩潰面。
            rec.Frame = frame;
            // 🔴 這一行是承重的：隱藏解除是「從最後一次按下起算」連續隱藏幾幀。漏掉的話，
            //    「窗已經隱藏了 15 幀 → 逃生口在第 60 幀補按一次」之後只要再 5 幀就會解除記號，
            //    等於把補按之後的保護期縮到 5 幀。
            rec.HiddenFrames = 0;
            slot.Pressed[addon] = rec;
            LogPress(addonName, addon, paramKey);
            return true;
        }
        slot.Pressed[addon] = new PressRecord { Frame = frame, FirstFrame = frame };
        LogPress(addonName, addon, paramKey);
        return true;
    }

    /// <summary>跨外掛重按診斷：全艦隊各份守衛逐字統一這一行；只在真的要送出時寫、不節流、位址只印數值。</summary>
    private static void LogPress(string addonName, nint addon, string paramKey)
        => PluginLog.Verbose($"[按窗診斷] plugin=AutoRetainer addon={addonName} addr=0x{addon:X} key={paramKey}");

    /// <summary>
    /// 對 <paramref name="addonName"/> 這扇窗（只看第 1 格）送一次「取消／關閉」
    /// （<c>Callback.Fire(addon, true, -1)</c>），同一扇窗只送一次。與 <see cref="TryPressOnce"/>
    /// 共用不帶參數的那把 key，所以別的模組對同一扇窗按過「是」之後這裡也不會再送取消。
    /// </summary>
    /// <returns>
    /// <see langword="true"/> ＝ 剛送出取消，或先前送過而窗還沒收掉；兩者對呼叫端的意義相同：這一輪不要再往下走（畫面上還有擋路的窗）。窗不在或還沒 ready 回 false。
    /// </returns>
    internal static bool TryCancelDialogOnce(string addonName)
    {
        if(!TryGetAddonByName<AtkUnitBase>(addonName, out var addon) || addon == null) return false;
        var current = (nint)addon;
        EnsureWatching(addonName);
        // 正在銷毀＝畫面上仍有擋路的窗、但一發都不能再送：與「送過還沒收掉」同義。
        if(Finalizing.Contains((addonName, current))) return true;
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
        LogPress(addonName, current, null);
        Callback.Fire(addon, true, -1);
        return true;
    }

    /// <summary>
    /// 每幀無條件呼叫（<c>AutoRetainer.DialogGuardsTick</c>，在 <c>Load()</c> 最前面獨立訂閱 <c>Framework.Update</c>）。
    /// 做兩件事：①推進 <see cref="CurrentFrame"/> 這個守衛專用的時鐘　②被記下的位址已經從該窗名的清單裡消失時解除封鎖 —— 後者是唯一能確定「上一次按下的那扇已經收乾淨」的證據。
    /// </summary>
    /// <remarks>
    /// 🔴 全程只做位址等值比較，<b>永遠不解參</b>。
    /// 另有生命週期兩條（<see cref="OnLifecycle"/>）：同位址 PostSetup 提早解除、PreFinalize 上鎖到位址消失。
    /// </remarks>
    internal static void Tick()
    {
        // 🔴 遞增必須排在下面那行「沒有記號就回來」的前面：這個計數器是逃生口唯一的時間來源，
        //    沒有窗被記著的時候就停住的話，下一次按下之後的等待會從一個早就過期的值開始算，等於沒有時鐘。
        frameCounter++;
        if(Slots.Count == 0 && Finalizing.Count == 0) return;
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
        foreach(var (lockedName, _) in Finalizing)
        {
            if(!NamesBuf.Contains(lockedName)) NamesBuf.Add(lockedName);
        }
        foreach(var name in NamesBuf)
        {
            // 常駐窗名才需要知道「可不可見」；其餘窗名這一幀連讀都不讀。
            var persistent = PersistentAddons.Contains(name);
            PresentBuf.Clear();
            HiddenBuf.Clear();
            var scanComplete = false;
            for(var i = 1; i <= MaxAddonIndex; i++)
            {
                var unit = Svc.GameGui.GetAddonByName(name, i);
                var present = unit.Address;
                if(present == 0)
                {
                    scanComplete = true;
                    break;
                }
                PresentBuf.Add(present);
                // 🔑 Tick 裡唯一一次解參，而且解的是「本幀剛從 GetAddonByName 拿回來」的指標，
                //    不是跨幀保存的 slot.Pressed 的 key（那些永遠只做等值比較）。
                //    AtkUnitBasePtr.IsVisible 先判 null、再讀固定位移的旗標欄位，不再往下追任何指標。
                if(persistent && !unit.IsVisible) HiddenBuf.Add(present);
            }
            // 掃滿上限仍沒看到盡頭＝清單不完整，這一幀這個窗名什麼都不解除。
            if(!scanComplete) continue;
            UnlockBuf.Clear();
            foreach(var locked in Finalizing)
            {
                if(locked.Name == name && !PresentBuf.Contains(locked.Address)) UnlockBuf.Add(locked);
            }
            foreach(var locked in UnlockBuf) Finalizing.Remove(locked);
            foreach(var (key, slot) in Slots)
            {
                if(slot.AddonName != name || slot.Pressed.Count == 0) continue;
                RemoveBuf.Clear();
                foreach(var (addr, rec) in slot.Pressed)
                {
                    // ①位址從清單裡消失 ＝ 那扇窗已經收乾淨。
                    if(!PresentBuf.Contains(addr))
                    {
                        RemoveBuf.Add(addr);
                        continue;
                    }
                    // ②只有常駐窗名走得到：它們永遠不會從清單消失，①對它們是死路。
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
                    // 純節流那一類窗的總結：一輪一行。
                    // 這裡是記號解除的時點（位址消失，或常駐窗連續隱藏夠久），總次數只有在這裡才完整。
                    if(slot.Pressed.TryGetValue(addr, out var done) && done.Represses > 0)
                        PluginLog.Verbose($"{slot.Label ?? key}: 這一輪按了 {done.Represses + 1} 次（每 {RoutineRepressIntervalFrames} 幀最多一次），第一次到最後一次相隔 {done.Frame - done.FirstFrame} 幀");
                    slot.Pressed.Remove(addr);
                }
                if(slot.Pressed.Count == 0) EmptyKeysBuf.Add(key);
            }
        }
        // 空掉的 key 順手收掉，帶動態參數組的 key（Assign{VentureID} 這類）才不會無限累積。
        foreach(var key in EmptyKeysBuf) Slots.Remove(key);
    }

    /// <summary>外掛卸載時拆掉所有生命週期監聽器（不留指向本組件的委派）並清掉所有記號。</summary>
    internal static void ForceTeardown()
    {
        foreach(var (addonName, handler) in Watchers)
        {
            Svc.AddonLifecycle.UnregisterListener(AddonEvent.PreFinalize, addonName, handler);
            Svc.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, addonName, handler);
        }
        Watchers.Clear();
        Slots.Clear();
        Finalizing.Clear();
    }

    /// <summary>在同名清單裡找到 <paramref name="addon"/>，而且它這一幀看得見；查不到一律回 <see langword="false"/>。</summary>
    /// <remarks>讀的是 <c>GetAddonByName</c> 這一幀交回來的指標；<paramref name="addon"/> 本身只做等值比較。</remarks>
    private static bool IsListedAndVisible(string addonName, nint addon)
    {
        for(var i = 1; i <= MaxAddonIndex; i++)
        {
            var unit = Svc.GameGui.GetAddonByName(addonName, i);
            if(unit.Address == 0) return false;
            if(unit.Address == addon) return unit.IsVisible;
        }
        return false;
    }

    /// <summary>全外掛按法用到的固定窗名（含委託重複道具那兩種道具欄）；新增按下點時一併加進來。</summary>
    /// <remarks>漏列的窗名不會失去保護，只是退回「第一次按下才掛監聽器」。</remarks>
    private static readonly string[] KnownGuardedAddons =
    [
        "AirShipExploration", "AirShipExplorationDetail", "AirShipExplorationResult", "AirShipPartsMenu",
        "Bank", "CompanyCraftSupply", "ContextIconMenu", "ContextMenu",
        "FreeCompany", "FreeCompanyChest", "FreeCompanyCreditShop",
        "GrandCompanyExchange", "GrandCompanySupplyList", "GrandCompanySupplyReward",
        "InputNumeric", "InventoryRetainer", "InventoryRetainerLarge",
        "RetainerItemTransferList", "RetainerItemTransferProgress", "RetainerList",
        "RetainerTaskAsk", "RetainerTaskList", "RetainerTaskResult", "RetainerTaskSupply",
        "SelectIconString", "SelectString", "SelectYesno", "Shop", "ShopExchangeCurrencyDialog",
        "SubmarineExplorationMapSelect", "Talk", "TitleDCWorldMap", "Trade",
        "_CharaSelectListMenu", "_CharaSelectWorldServer", "_TitleMenu",
    ];

    /// <summary>外掛載入時（框架執行緒）先替 <see cref="KnownGuardedAddons"/> 掛好監聽器。</summary>
    /// <remarks>Dalamud 的 RegisterListener 要到下一幀才生效；等到第一次按下才掛，那一發就沒有銷毀鎖。重複呼叫無害。</remarks>
    internal static void WatchKnownAddons()
    {
        foreach(var name in KnownGuardedAddons) EnsureWatching(name);
    }

    /// <summary>第一次守護某個窗名時掛上 PreFinalize／PostSetup 監聽器；掛上之後就不再拆（只在 <see cref="ForceTeardown"/> 拆）。</summary>
    private static void EnsureWatching(string addonName)
    {
        if(Watchers.ContainsKey(addonName)) return;
        IAddonLifecycle.AddonEventDelegate handler = (type, args) => OnLifecycle(addonName, type, args);
        Watchers[addonName] = handler;
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, addonName, handler);
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, addonName, handler);
    }

    /// <summary>該位址進入銷毀（PreFinalize：上鎖）或重新開始（PostSetup：解鎖，並清掉這個窗名底下所有 key 對它的舊記號）。</summary>
    /// <remarks>
    /// 🔴 只讀 <c>args.Addon.Address</c> 做等值比較，不解參。
    /// PostSetup 時<b>這一幀</b>才登記的記號留著：監聽器之間的呼叫順序沒有保證，清掉它等於自己拆掉剛按下的那一發的保護。
    /// </remarks>
    private static void OnLifecycle(string addonName, AddonEvent type, AddonArgs args)
    {
        var address = args.Addon.Address;
        if(address == 0) return;
        if(type == AddonEvent.PreFinalize)
        {
            LockFinalizing(addonName, address);
            return;
        }
        Finalizing.Remove((addonName, address));
        if(Slots.Count == 0) return;
        var frame = CurrentFrame;
        LifecycleEmptyKeysBuf.Clear();
        foreach(var (key, slot) in Slots)
        {
            if(slot.AddonName != addonName) continue;
            if(!slot.Pressed.TryGetValue(address, out var rec)) continue;
            if(rec.Frame >= frame) continue;
            slot.Pressed.Remove(address);
            if(slot.Pressed.Count == 0) LifecycleEmptyKeysBuf.Add(key);
        }
        foreach(var key in LifecycleEmptyKeysBuf) Slots.Remove(key);
    }

    /// <summary>鎖住正在銷毀的位址；只加不刪，解鎖交給 <see cref="Tick"/> 的消失輪詢與同位址 PostSetup。</summary>
    private static void LockFinalizing(string addonName, nint address) => Finalizing.Add((addonName, address));
}
