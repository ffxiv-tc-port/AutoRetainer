using AutoRetainer.Modules.EzIPCManagers;
using AutoRetainerAPI;
using AutoRetainerAPI.Configuration;
using ECommons.EzIpcManager;
using ECommons.Reflection;
using System.Collections.Immutable;
using System.Reflection;

namespace AutoRetainer.Modules;

internal static class IPC
{
    private static void Log(string s)
    {
        DebugLog($"[IPC] {s}");
    }

    /// <summary>舊端點 <c>AutoRetainer.SetSuppressed</c> 寫的那個無主布林。</summary>
    /// <remarks>
    /// 🔴 語意與行為都<b>沒有</b>改變，只是從 <c>Suppressed</c> 這個名字底下搬出來，
    /// 好讓它與 <see cref="SuppressionLeases"/>（具名、可計數、會逾時的租約）並存。
    /// 舊端點是無主的：誰都可以寫、誰寫的最後一次算數 —— 所以新的消費端請改用租約端點。
    /// </remarks>
    internal static bool ManualSuppressed = false;

    /// <summary>AutoRetainer 的自動化現在是不是被壓制著。</summary>
    /// <remarks>
    /// 讀＝「舊的無主布林」<b>或</b>「還有任何一筆有效租約」。寫＝只寫舊的無主布林
    /// （所以既有呼叫點 <c>IPC.Suppressed = false</c> 的語意逐字不變：它清的是自己那一份，
    /// 清不掉別人的租約 —— 這正是無主布林原本互相踩踏的地方）。
    /// </remarks>
    internal static bool Suppressed
    {
        get => ManualSuppressed || SuppressionLeases.AnyActive;
        set => ManualSuppressed = value;
    }

    internal static void Init()
    {
        Log("IPC init");
        Svc.PluginInterface.GetIpcProvider<object>("AutoRetainer.Init").RegisterAction(() => { });
        Svc.PluginInterface.GetIpcProvider<bool>("AutoRetainer.GetSuppressed").RegisterFunc(GetSuppressed);
        Svc.PluginInterface.GetIpcProvider<bool, object>("AutoRetainer.SetSuppressed").RegisterAction(SetSuppressed);
        Svc.PluginInterface.GetIpcProvider<string, int, Guid>("AutoRetainer.AcquireSuppressionFor").RegisterFunc(AcquireSuppressionFor);
        Svc.PluginInterface.GetIpcProvider<Guid, bool>("AutoRetainer.ReleaseSuppression").RegisterFunc(ReleaseSuppression);
        Svc.PluginInterface.GetIpcProvider<Guid, bool>("AutoRetainer.RenewSuppression").RegisterFunc(RenewSuppression);
        Svc.PluginInterface.GetIpcProvider<Guid, int, bool>("AutoRetainer.RenewSuppressionFor").RegisterFunc(RenewSuppressionFor);
        Svc.PluginInterface.GetIpcProvider<bool>("AutoRetainer.GetMultiModeEnabled").RegisterFunc(GetMultiModeEnabled);
        Svc.PluginInterface.GetIpcProvider<bool, object>("AutoRetainer.SetMultiModeEnabled").RegisterAction(SetMultiModeEnabled);
        Svc.PluginInterface.GetIpcProvider<bool>("AutoRetainer.IsBusy").RegisterFunc(GetIsBusy);
        Svc.PluginInterface.GetIpcProvider<uint, object>("AutoRetainer.SetVenture").RegisterAction(SetVenture);
        Svc.PluginInterface.GetIpcProvider<ulong, OfflineCharacterData>("AutoRetainer.GetOfflineCharacterData").RegisterFunc(GetOCD);
        Svc.PluginInterface.GetIpcProvider<OfflineCharacterData, object>("AutoRetainer.WriteOfflineCharacterData").RegisterAction(SetOCD);
        Svc.PluginInterface.GetIpcProvider<ulong, string, AdditionalRetainerData>("AutoRetainer.GetAdditionalRetainerData").RegisterFunc(GetARD);
        Svc.PluginInterface.GetIpcProvider<ulong, string, AdditionalRetainerData, object>("AutoRetainer.WriteAdditionalRetainerData").RegisterAction(SetARD);
        Svc.PluginInterface.GetIpcProvider<List<ulong>>("AutoRetainer.GetRegisteredCIDs").RegisterFunc(GetRegisteredCIDs);
        Svc.PluginInterface.GetIpcProvider<string, object>(ApiConsts.RequestRetainerPostProcess).RegisterAction(RequestRetainerPostprocess);
        Svc.PluginInterface.GetIpcProvider<object>(ApiConsts.FinishRetainerPostprocessRequest).RegisterAction(FinishRetainerPostprocessRequest);
        Svc.PluginInterface.GetIpcProvider<string, object>(ApiConsts.RequestCharacterPostProcess).RegisterAction(RequestCharacterPostprocess);
        Svc.PluginInterface.GetIpcProvider<object>(ApiConsts.FinishCharacterPostprocessRequest).RegisterAction(FinishCharacterPostprocessRequest);
        Svc.PluginInterface.GetIpcProvider<string, object>(ApiConsts.OnRetainerListCustomTask).RegisterAction(OnRetainerListCustomTask);
        EzIPC.Init(typeof(IPC));
    }

    private static void OnRetainerListCustomTask(string s)
    {
        P.RetainerListOverlay.PluginToProcess = s;
    }

    internal static void Shutdown()
    {
        Log("IPC Shutdown");
        Svc.PluginInterface.GetIpcProvider<object>("AutoRetainer.Init").UnregisterAction();
        Svc.PluginInterface.GetIpcProvider<bool>("AutoRetainer.GetSuppressed").UnregisterFunc();
        Svc.PluginInterface.GetIpcProvider<bool, object>("AutoRetainer.SetSuppressed").UnregisterAction();
        Svc.PluginInterface.GetIpcProvider<string, int, Guid>("AutoRetainer.AcquireSuppressionFor").UnregisterFunc();
        Svc.PluginInterface.GetIpcProvider<Guid, bool>("AutoRetainer.ReleaseSuppression").UnregisterFunc();
        Svc.PluginInterface.GetIpcProvider<Guid, bool>("AutoRetainer.RenewSuppression").UnregisterFunc();
        Svc.PluginInterface.GetIpcProvider<Guid, int, bool>("AutoRetainer.RenewSuppressionFor").UnregisterFunc();
        SuppressionLeases.ReleaseAll("AutoRetainer 正在卸載");
        Svc.PluginInterface.GetIpcProvider<bool>("AutoRetainer.GetMultiModeEnabled").UnregisterFunc();
        Svc.PluginInterface.GetIpcProvider<bool, object>("AutoRetainer.SetMultiModeEnabled").UnregisterAction();
        Svc.PluginInterface.GetIpcProvider<bool>("AutoRetainer.IsBusy").UnregisterFunc();
        Svc.PluginInterface.GetIpcProvider<uint, object>("AutoRetainer.SetVenture").UnregisterAction();
        Svc.PluginInterface.GetIpcProvider<ulong, OfflineCharacterData>("AutoRetainer.GetOfflineCharacterData").UnregisterFunc();
        Svc.PluginInterface.GetIpcProvider<OfflineCharacterData, object>("AutoRetainer.WriteOfflineCharacterData").UnregisterAction();
        Svc.PluginInterface.GetIpcProvider<ulong, string, AdditionalRetainerData>("AutoRetainer.GetAdditionalRetainerData").UnregisterFunc();
        Svc.PluginInterface.GetIpcProvider<ulong, string, AdditionalRetainerData, object>("AutoRetainer.WriteAdditionalRetainerData").UnregisterAction();
        Svc.PluginInterface.GetIpcProvider<List<ulong>>("AutoRetainer.GetRegisteredCIDs").UnregisterFunc();
        Svc.PluginInterface.GetIpcProvider<string, object>(ApiConsts.RequestRetainerPostProcess).UnregisterAction();
        Svc.PluginInterface.GetIpcProvider<object>(ApiConsts.FinishRetainerPostprocessRequest).UnregisterAction();
        Svc.PluginInterface.GetIpcProvider<string, object>(ApiConsts.RequestCharacterPostProcess).UnregisterAction();
        Svc.PluginInterface.GetIpcProvider<object>(ApiConsts.FinishCharacterPostprocessRequest).UnregisterAction();
        Svc.PluginInterface.GetIpcProvider<string, object>(ApiConsts.OnRetainerListCustomTask).UnregisterAction();
    }

    private static void FinishRetainerPostprocessRequest()
    {
        Log("Received retainer postprocess request finish");
        SchedulerMain.RetainerPostProcessLocked = false;
    }

    private static void FinishCharacterPostprocessRequest()
    {
        Log("Received character postprocess request finish");
        SchedulerMain.CharacterPostProcessLocked = false;
    }

    private static void RequestRetainerPostprocess(string pluginName)
    {
        // 🔴 原本是「先 Contains 判斷、再把 Add 的結果指派回去」兩步，而 IPC 端點跑在呼叫端的
        //    執行緒上 ⇒ 兩個外掛同時登記時，後寫的那份是以「它自己讀到的那份清單」為基底算出來的，
        //    會把先寫的那筆整個蓋掉。失敗形式是「不會擲例外、不會壞資料」，而是有人的登記靜默消失，
        //    之後永遠等不到自己的後處理輪次 —— 正是最難歸因的那一種。
        //    改用 CAS 迴圈，把「判斷」與「加入」變成一個原子動作；不需要鎖。
        //    📌 觀察行為逐字不變：重複登記照樣擲同一個例外，而且擲之前一樣沒有寫入
        //       （轉換函式對重複的情況回同一個實例 ⇒ Update 回 false ⇒ 完全沒有寫入發生）。
        //    ⚠️ 轉換函式在 CAS 重試時會被呼叫多次，所以它必須是純函式 —— 這裡是。
        if(!ImmutableInterlocked.Update(ref SchedulerMain.RetainerPostprocess,
            (list, plugin) => list.Contains(plugin) ? list : list.Add(plugin), pluginName))
        {
            throw new Exception($"Retainer Postprocess request from {pluginName} already exist");
        }
        Log($"Retainer Postprocess requested from {pluginName}");
    }

    /// <remarks>理由與原子性說明同 <see cref="RequestRetainerPostprocess"/>。</remarks>
    private static void RequestCharacterPostprocess(string pluginName)
    {
        if(!ImmutableInterlocked.Update(ref SchedulerMain.CharacterPostprocess,
            (list, plugin) => list.Contains(plugin) ? list : list.Add(plugin), pluginName))
        {
            throw new Exception($"Character Postprocess request from {pluginName} already exist");
        }
        Log($"Character Postprocess requested from {pluginName}");
    }

    /// <remarks>🔴 回的雖然是複本（<c>ToList</c>），但<b>做出這份複本的過程</b>要走訪活的
    /// <c>C.OfflineData</c> 與巢狀的 <c>C.Blacklist</c>，而那兩個集合是 framework 執行緒在增刪的。
    /// 「回複本＝安全」對這支不成立 —— 真正要問的是「複製這個動作在哪個執行緒上做」。</remarks>
    private static List<ulong> GetRegisteredCIDs()
    {
        return IpcFrameworkGate.Run("GetRegisteredCIDs",
            () => C.OfflineData.Where(x => !C.Blacklist.Any(z => z.CID == x.CID) && !x.Name.EqualsAny("Unknown", "")).Select(x => x.CID).ToList(),
            new List<ulong>(),
            "the caller is told no character is registered (an empty list)");
    }

    /// <remarks>
    /// 🔴 回的是<b>本尊</b>，這是刻意的：<c>AutoRetainerAPI</c> 的既有契約就是
    /// 「Get 出來改欄位、再 <c>WriteOfflineCharacterData</c> 寫回去」，換成複本會讓那條路靜默失效。
    /// 閘門在這裡負責的是另一件事：<c>FirstOrDefault</c> 要走訪活的 <c>C.OfflineData</c>。
    /// 📌 逾時回 <c>null</c> <b>不是新語意</b> —— 查無此 CID 時本來就回 null（<c>FirstOrDefault</c>），
    /// 消費端本來就得處理。
    /// </remarks>
    private static OfflineCharacterData GetOCD(ulong CID)
    {
        return IpcFrameworkGate.Run("GetOfflineCharacterData",
            () => C.OfflineData.FirstOrDefault(x => x.CID == CID), null,
            "the caller is told there is no data for that character (null), the same answer as an unknown CID");
    }

    private static void SetOCD(OfflineCharacterData OCD)
    {
        IpcFrameworkGate.Run("WriteOfflineCharacterData", () =>
        {
            var index = C.OfflineData.IndexOf(x => x.CID == OCD.CID);
            if(index != -1)
            {
                //C.OfflineData[index] = OCD;
                var data = C.OfflineData[index];
                foreach(var field in OCD.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
                {
                    if(data.GetFoP(field.Name) != null)
                    {
                        data.SetFoP(field.Name, field.GetValue(OCD));
                        PluginLog.Verbose($"Setting {field.Name} to {field.GetValue(data)}");
                    }
                }
            }
            else
            {
                C.OfflineData.Add(OCD);
            }
        });
    }

    /// <remarks>
    /// 🔴 回的是<b>本尊</b>（同 <see cref="GetOCD"/> 的理由：Get 出來改欄位再
    /// <c>WriteAdditionalRetainerData</c> 寫回去是既有契約）。
    /// 🔴 而且這支名字叫 Get 卻<b>會寫入</b>：<c>Utils.GetAdditionalData</c> 走
    /// <c>GetAdditionalDataKey(create: true)</c>，鍵不存在時會往 <c>C.AdditionalData</c>
    /// 這個裸 <c>Dictionary</c> 插一筆 —— 而 IPC 端點跑在呼叫端的執行緒上。閘門修的就是這個。
    /// 🔴🔴 <b>逾時回 <c>null</c>，不是回一份全新的預設值物件。</b>
    /// 舊版回預設值物件是為了「這支永遠不回 null」，但那個選擇會造成<b>使用者的設定被靜默重置</b>：
    /// 既有契約是「Get 出來改欄位、再 <c>WriteAdditionalRetainerData</c> 寫回去」，
    /// 逾時時呼叫端拿到的是<b>全預設值</b>，照契約寫回去就會把這名僱員真正的設定逐欄位蓋掉。
    /// ⚠️ 而 <c>AutoRetainerApi.WriteAdditionalRetainerData</c> 的 <c>CreationFrame</c> 守衛
    /// <b>攔不住</b>它：那個替代物件是在這一刻、於呼叫端的執行緒上建構的，
    /// <c>CreationFrame</c> 就是當下的 FrameCount，同幀寫回照樣通過檢查。
    /// ⇒ 失敗形式是「僱員設定莫名其妙變回預設」，而 log 上只有一則逾時的 Information。
    /// 🔑 回 <c>null</c> 讓呼叫端<b>分得出</b>「沒答案」與「答案是預設值」，這是回預設值物件做不到的。
    /// 📌 <c>AdditionalRetainerData</c> 是 <c>class</c>（<c>AutoRetainerAPI/Configuration/</c>），
    /// CallGate 回傳 null 對參考型別是安全的 —— 那條會擲 <c>NullReferenceException</c> 的路
    /// （<c>CallGateChannel.InvokeFunc</c> 對 null 直接回 null、再 <c>(TRet)result</c>）
    /// 只發生在<b>不可為 null 的值型別</b>上。
    /// ⚠️ 本外掛內唯一的消費端 <c>AutoRetainer.cs</c> 的 <c>AddVenture</c> 已一併補判空。
    /// </remarks>
    private static AdditionalRetainerData GetARD(ulong cid, string name)
    {
        return IpcFrameworkGate.Run("GetAdditionalRetainerData",
            () => Utils.GetAdditionalData(cid, name), null,
            "the caller is given null - do NOT write anything back for this retainer, that would overwrite its real settings with defaults");
    }

    private static void SetARD(ulong cid, string name, AdditionalRetainerData data)
    {
        IpcFrameworkGate.Run("WriteAdditionalRetainerData", () =>
        {
            var x = C.AdditionalData[Utils.GetAdditionalDataKey(cid, name)];
            foreach(var field in data.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if(x.GetFoP(field.Name) != null)
                {
                    x.SetFoP(field.Name, field.GetValue(data));
                    PluginLog.Verbose($"Setting {field.Name} to {field.GetValue(data)}");
                }
            }
        });
    }

    private static void SetVenture(uint VentureID)
    {
        // 🔴 這裡真正要搬的不是那個 volatile 指派，是 DebugLog 那行的「引數」：
        //    內插字串一定會先求值（DebugLog 收的是 string，不是內插處理常式），
        //    所以 VentureUtils.GetVentureName 的 Lumina 查表「每次都會」在呼叫端的執行緒上跑。
        //    ⚠️ 「改成只在 Debug 開著時才組字串」在這裡沒有用：使用者的 LogLevel 是 1（Debug 收得到），
        //    那條路照樣會走 —— 所以整支進閘門才是真的修掉，而且 diff 更小。
        IpcFrameworkGate.Run("SetVenture", () =>
        {
            SchedulerMain.VentureOverride = VentureID;
            DebugLog($"Received venture override to {VentureID} / {VentureUtils.GetVentureName(VentureID)} via IPC");
        });
    }

    private static bool GetSuppressed()
    {
        return Suppressed;
    }

    private static void SetSuppressed(bool s)
    {
        Suppressed = s;
    }

    #region 具名壓制租約（憑證形狀，與 YesAlready 統一）

    /*
     * 🔑🔑 形狀為什麼是「Guid 憑證」——2026-09-03 與 YesAlready 統一
     * ───────────────────────────────────────────────────────────────────────────
     * 這四支的名稱與簽章與 YesAlready 的同名端點<b>逐字相同</b>：
     *
     *     AcquireSuppressionFor(string owner, int ms) -> Guid
     *     ReleaseSuppression   (Guid lease)           -> bool
     *     RenewSuppression     (Guid lease)           -> bool
     *     RenewSuppressionFor  (Guid lease, int ms)   -> bool
     *
     * 兩邊形狀一致的理由不是美觀，是「形狀不一致會靜默出錯」：
     * Dalamud 的 CallGateChannel 在型別對不上時<b>不會直接報錯</b>，而是把值 JSON 序列化再
     * 反序列化成呼叫端宣告的型別（Dalamud/Plugin/Ipc/Internal/CallGateChannel.cs 的
     * ConvertObject）。舊形狀 ReleaseSuppression(string) 遇上照 YesAlready 寫法傳進來的
     * Guid 時，Guid -> string 這個方向<b>轉得過去</b>，於是變成「歸還一個名字是 GUID 的租約」
     * ——舊實作對任何非空字串都回 true，呼叫端拿到的是一個看似成功的 true，
     * 而真正的租約繼續壓著 AutoRetainer 直到逾時。全程零訊息。
     *
     * 🔴 舊的 AcquireSuppression(string) -> bool 與 ReleaseSuppression(string) -> bool
     *    已經移除，理由見下方「過渡」。
     *
     * 🔴 過渡：AcquireSuppression 這個名字<b>刻意不重新註冊成 Guid 版</b>。
     *    舊版 ICE／GatherBuddyReborn 宣告的是 Func<string, bool>；如果同名端點改成回 Guid，
     *    CallGate 會嘗試 Guid -> bool 的轉換並丟 IpcTypeMismatchError，而 ECommons 的
     *    SafeWrapper.IPCException <b>只攔 IpcNotReadyError</b>（ECommons/EzIpcManager/
     *    SafeWrapperIPC.cs），例外會逸出到 GatherBuddyReborn 的 DoAutoGather，
     *    使用者只更新 AutoRetainer 而沒更新 GBR 時自動採集會整個停擺。
     *    端點<b>不存在</b>反而是乾淨的：IpcNotReadyError 兩邊的 SafeWrapper 都攔得住，
     *    落回它們既有的 fail-safe 路徑（「沒拿到租約，照現況跑」，且已經寫 Information）。
     *    ⇒ 等消費端全部更新過一輪之後，可以再補一支 AcquireSuppression(string) -> Guid
     *      的便利版本（YesAlready 有）。現在補會踩上面那條。
     *
     * 🔴 ReleaseSuppression 這個名字沿用（改成收 Guid）是安全的，因為舊消費端只有在
     *    Acquire 成功之後才會走到 Release（ICE 與 GBR 都是 `if(!holding) return;`），
     *    而 Acquire 現在一定失敗 ⇒ 舊版永遠走不到型別不合的那條路。
     */

    /// <summary>取得一把具名的壓制租約。<b>這是消費端該用的端點。</b></summary>
    /// <remarks>
    /// 🔴 與 <c>SetSuppressed</c> 的差別就是「誰先結束誰就把別人的壓制解除」這個 bug 的解法：
    /// 每一把租約一個憑證，<b>全部還完</b>壓制才真的解除。<br/>
    /// 🔴 租約會逾時，租用者必須拿著憑證週期性 <see cref="RenewSuppression"/>
    /// （建議間隔 <see cref="SuppressionLeases.RenewIntervalHintMs"/>）。<br/>
    /// ⚠️ 要求的租期會被夾到 <see cref="SuppressionLeases.MaxLeaseMilliseconds"/>（5 分鐘）——
    /// 這是 AutoRetainer 自己的時間政策，比 YesAlready 的 60 分鐘短，<b>不要假設你拿到了要求的時長</b>。<br/>
    /// 📌 回傳 <see cref="Guid.Empty"/>＝沒拿到。呼叫端拿不到 AutoRetainer（IPC 不存在）時應該<b>照現況跑</b>，
    /// 不要卡住自己的流程。
    /// </remarks>
    /// <param name="owner">租用者識別字串，慣例是對方外掛的 InternalName。</param>
    /// <param name="milliseconds">要求的租期。</param>
    private static Guid AcquireSuppressionFor(string owner, int milliseconds) => SuppressionLeases.Acquire(owner, milliseconds);

    /// <summary>交回一把壓制租約。</summary>
    /// <returns><c>false</c>＝這把不存在（已經還過、或已經逾時被掃掉）。冪等。</returns>
    private static bool ReleaseSuppression(Guid lease) => SuppressionLeases.Release(lease);

    /// <summary>續約（心跳），沿用取得時的租期。</summary>
    /// <returns>
    /// <c>false</c>＝<b>你那把已經沒了</b>，必須重新 <see cref="AcquireSuppressionFor"/>，
    /// 不要當成成功。
    /// </returns>
    private static bool RenewSuppression(Guid lease) => SuppressionLeases.Renew(lease);

    /// <summary>同 <see cref="RenewSuppression"/>，但指定新的租期。</summary>
    private static bool RenewSuppressionFor(Guid lease, int milliseconds) => SuppressionLeases.Renew(lease, milliseconds);

    #endregion

    private static bool GetMultiModeEnabled()
    {
        return MultiMode.Enabled;
    }

    /// <remarks>
    /// 🔴 這支經 <see cref="MultiMode.OnMultiModeEnabled"/> 同步碰到原生層與任務佇列：
    /// <c>MultiMode.CanHET</c> 走 <c>TaskNeoHET.GetFcOrPrivateEntranceFromMarkers()</c>
    /// （裡面 <c>AgentHUD.Instance()</c> ＋走 <c>Svc.Objects</c>），成立時再 <c>TaskNeoHET.Enqueue</c>
    /// 往 <c>P.TaskManager.Tasks</c>（裸 <c>List&lt;T&gt;</c>）塞任務。而 IPC 端點跑在<b>呼叫端的執行緒</b>上。
    /// ⇒ 經 <see cref="IpcFrameworkGate"/> 搬到 framework 執行緒。
    /// 📌 這不是新增自動化：觸發者仍然只有「呼叫端明確打了這支端點」，而且已經在 framework 執行緒上
    /// 呼叫時是就地執行，行為逐字不變。
    /// </remarks>
    private static void SetMultiModeEnabled(bool s)
    {
        IpcFrameworkGate.Run(nameof(SetMultiModeEnabled), () =>
        {
            MultiMode.Enabled = s;
            MultiMode.OnMultiModeEnabled();
        });
    }

    /// <summary>
    /// 「AutoRetainer 正在驅動雇員自動化」的唯讀狀態，給市場板類外掛
    /// （如 Marketbuddy）做傳喚鈴互斥用：
    /// PluginEnabled＝鈴自動化已武裝（開著就會在鈴開啟時接手，含
    /// IPC.Suppressed 尊重）、MultiMode.Active＝多角色模式執行期狀態、
    /// TaskManager.IsBusy＝任務引擎正在執行。純暴露狀態，零行為變更。
    /// </summary>
    /// <remarks>
    /// 🔴 這支跑在<b>呼叫端的執行緒</b>上,而 <c>P.TaskManager.IsBusy</c> 問的是 ECommons
    /// <c>TaskManager</c> 的兩個裸 <c>List&lt;T&gt;</c>(ECommons 自己的註解就寫著
    /// only ever do that from Framework.Update event) —— framework 執行緒每幀在增刪它們。
    /// 從別的執行緒讀不只是「拿到舊值」,並行改動時看到的可能是集合內部不變式被打破的中間態。
    /// <br/><br/>
    /// 🔑 這裡刻意<b>不</b>走 <see cref="IpcFrameworkGate"/>:那條路最多會等 framework 執行緒
    /// <see cref="IpcFrameworkGate.WaitMilliseconds"/> 毫秒,而 <c>AutoRetainer.IsBusy</c> 是被
    /// Marketbuddy 這類外掛<b>高頻輪詢</b>的布林端點 —— 把呼叫端的執行緒卡住,比讓它讀到
    /// 差一幀的值糟得多。改成讀 framework 執行緒每幀寫入的快照
    /// (<see cref="UpdateIsBusySnapshot"/>),最舊差一幀。
    /// <br/><br/>
    /// 📌 已經在 framework 執行緒上時就地算,回傳值與時序逐字不變。
    /// 📌 卸載期不受影響:這條路徑一次都沒有呼叫 <c>RunOnFrameworkThread</c>,
    /// 所以沒有「<c>IsFrameworkUnloading</c> 為真時就地在呼叫端執行緒執行」那個旁路可踩。
    /// </remarks>
    private static bool GetIsBusy()
        => Svc.Framework.IsInFrameworkUpdateThread ? GetIsBusyCore() : IsBusySnapshot;

    /// <summary>
    /// <see cref="GetIsBusy"/> 的實際判斷,三個分量與改動前逐字相同。
    /// <b>只能在 framework 執行緒上呼叫。</b>
    /// </summary>
    private static bool GetIsBusyCore()
    {
        return SchedulerMain.PluginEnabled || MultiMode.Active || P.TaskManager.IsBusy;
    }

    /// <summary>
    /// <see cref="GetIsBusy"/> 給別的執行緒讀的每幀快照。
    /// <c>volatile</c> 保證讀到的是最近一次寫入的值(<c>bool</c> 的讀寫本身就是原子的),不需要鎖。
    /// </summary>
    private static volatile bool IsBusySnapshot;

    /// <summary>
    /// 由 <c>AutoRetainer.Tick</c>(framework 執行緒)每幀呼叫一次。
    /// 📌 放在 Tick 的<b>最後</b>:這一幀排程器/MultiMode/任務佇列的變動都已經發生完,
    /// 快照拿到的是這一格結束時的狀態,而不是開頭的。
    /// </summary>
    internal static void UpdateIsBusySnapshot() => IsBusySnapshot = GetIsBusyCore();

    internal static void FireSendRetainerToVentureEvent(string retainer)
    {
        Log($"Firing FireSendRetainerToVentureEvent for {retainer}");
        Svc.PluginInterface.GetIpcProvider<string, object>(ApiConsts.OnSendRetainerToVenture).SendMessage(retainer);
    }

    internal static void FireRetainerPostprocessTaskRequestEvent(string retainer)
    {
        Log($"Firing FireRetainerPostprocessTaskRequestEvent for {retainer}");
        Svc.PluginInterface.GetIpcProvider<string, object>(ApiConsts.OnRetainerAdditionalTask).SendMessage(retainer);
    }

    internal static void FireRetainerPostprocessEvent(string pluginName, string retainer)
    {
        Log($"Firing FireRetainerPostprocessEvent for {retainer} for plugin {pluginName}");
        Svc.PluginInterface.GetIpcProvider<string, string, object>(ApiConsts.OnRetainerReadyForPostprocess).SendMessage(pluginName, retainer);
    }

    internal static void FireCharacterPostprocessTaskRequestEvent()
    {
        Log($"Firing FireCharacterPostprocessTaskRequestEvent");
        Svc.PluginInterface.GetIpcProvider<object>(ApiConsts.OnCharacterAdditionalTask).SendMessage();
    }

    internal static void FireCharacterPostprocessEvent(string pluginName)
    {
        Log($"Firing FireCharacterPostprocessEvent for plugin {pluginName}");
        Svc.PluginInterface.GetIpcProvider<string, object>(ApiConsts.OnCharacterReadyForPostprocess).SendMessage(pluginName);
    }
}
