using AutoRetainer.Internal;
using AutoRetainerAPI.Configuration;
using ECommons.Events;

namespace AutoRetainer.Modules.Multi;

/// <summary>
/// 「跑一輪就回來」：記住按下時的角色、開多角色模式，等多角色模式自己判斷沒有角色要處理之後，
/// 關閉多角色模式並換回那個角色。
/// </summary>
/// <remarks>
/// <para>只在框架執行緒上讀寫（主視窗按鈕、<see cref="MultiMode.Tick"/>、<see cref="Tick"/>、手動換角、IPC 經閘門）；狀態只在記憶體，插件重載即忘。</para>
/// <para>「做完」＝<see cref="MultiMode.Tick"/> 走到「目前角色做完、也沒有下一個角色可換」的分支，連續 <see cref="IdleConfirmMs"/> 每幀都如此。</para>
/// <para>取消：等待中多角色模式被任何來源關掉，或手動換角（<see cref="MultiMode.Relog"/> 手動那一組）。</para>
/// <para>例外：<see cref="ActiveId"/>、<see cref="ActivePhaseCode"/>、<see cref="TryGetResult"/> 可在任何執行緒讀。</para>
/// </remarks>
internal static class MultiModeRoundTrip
{
    private enum Phase
    {
        Idle,
        Running,
        Returning,
    }

    /// <summary>每一趟結束時記下的結果代碼；IPC 原樣交出去，改名＝破壞巨集的契約。</summary>
    internal static class ResultCode
    {
        /// <summary>做完了，人在原角色上（含本來就在），多角色模式已關。</summary>
        internal const string Returned = "returned";
        /// <summary>做完了、多角色模式已關，但期限內沒能換回原角色。</summary>
        internal const string ReturnTimeout = "return_timeout";
        /// <summary>做完了、多角色模式已關，但原角色已不在角色清單裡。</summary>
        internal const string ReturnMissing = "return_missing";
        internal const string CancelledMultiOff = "cancelled_multi_off";
        internal const string CancelledMultiOn = "cancelled_multi_on";
        internal const string CancelledRelog = "cancelled_relog";
        internal const string CancelledUser = "cancelled_user";
        internal const string CancelledStop = "cancelled_stop";
        internal const string CancelledIpc = "cancelled_ipc";
    }

    /// <summary><see cref="Start"/> 不能開始時的回值；正數是這一趟的編號。</summary>
    internal const int RefusedAlreadyRunning = -1;
    internal const int RefusedMultiModeOn = -2;
    internal const int RefusedNoCharacter = -3;

    private const long IdleConfirmMs = 3000;
    private const long ReturnTimeoutMs = 5 * 60 * 1000;
    private const long RelogRetryMs = 5000;
    private const int KeepResults = 8;

    private static Phase phase = Phase.Idle;
    private static ulong returnCid;
    private static string returnLabel = "";
    private static string activeButtonLabel = "";
    private static bool nothingToDoThisFrame;
    private static long idleSince;
    private static bool relogQueued;
    private static long nextRelogAttempt;
    private static long returnDeadline;

    // 起點隨機：插件重載後編號不會撞到重載前的舊編號（呼叫端拿舊編號查只會得到「不知道」）。
    private static int lastId = Random.Shared.Next(1, 1 << 30);
    private static volatile int activeId;
    private static volatile string activePhaseCode = "";
    private static readonly (int Id, string Code)[] results = new (int, string)[KeepResults];
    private static int nextResultSlot;
    private static readonly object ResultLock = new();

    internal static bool Active => phase != Phase.Idle;

    /// <summary>進行中那一趟的編號；沒有進行中＝0。任何執行緒可讀。</summary>
    internal static int ActiveId => activeId;

    /// <summary>沒有進行中＝空字串；"running"＝多角色模式在處理；"returning"＝已關多角色、正在換回原角色。任何執行緒可讀。</summary>
    internal static string ActivePhaseCode => activePhaseCode;

    /// <summary>進行中時主視窗按鈕的文字（含要回去的角色，已套用隱藏名字設定）。</summary>
    internal static string ActiveButtonLabel => activeButtonLabel;

    internal static bool CanStart(out string reason) => CanStart(out reason, out _);

    private static bool CanStart(out string reason, out int refusal)
    {
        if(Active)
        {
            reason = Loc.T("Already running.");
            refusal = RefusedAlreadyRunning;
            return false;
        }
        if(MultiMode.Enabled)
        {
            reason = Loc.T("Multi Mode is already on. Turn it off first, then press this on the character you want to come back to.");
            refusal = RefusedMultiModeOn;
            return false;
        }
        if(!ProperOnLogin.PlayerPresent || TryGetCharacter(Svc.PlayerState.ContentId) == null)
        {
            reason = Loc.T("Log in to the character you want to come back to first (it must be on AutoRetainer's character list).");
            refusal = RefusedNoCharacter;
            return false;
        }
        reason = null;
        refusal = 0;
        return true;
    }

    internal static void Start() => Start("主視窗的「跑一輪就回來」", MultiModeChangeCause.User);

    /// <returns>開始了＝這一趟的編號（正數）；不能開始＝<see cref="RefusedAlreadyRunning"/> 等負數。</returns>
    internal static int Start(string source, MultiModeChangeCause cause)
    {
        if(!CanStart(out var why, out var refusal))
        {
            PluginLog.Information($"[RoundTrip] 沒有開始（{source}）：{why}");
            return refusal;
        }
        var data = TryGetCharacter(Svc.PlayerState.ContentId);
        returnCid = data.CID;
        returnLabel = Censor.Character(data.Name, data.World);
        activeButtonLabel = string.Format(Loc.T("Back to {0} when done (click to cancel)"), returnLabel) + "###RoundTrip";
        lastId = lastId >= int.MaxValue - 1 ? 1 : lastId + 1;
        activeId = lastId;
        SetPhase(Phase.Running);
        idleSince = 0;
        nothingToDoThisFrame = false;
        PluginLog.Information($"[RoundTrip] 開始：多角色模式處理完到期的僱員與航行器後，關閉多角色模式並換回 {returnLabel}（{source}，編號 {lastId}）");
        MultiMode.SetEnabled(true, source, cause);
        MultiMode.OnMultiModeEnabled();
        return lastId;
    }

    internal static void Cancel(string why, string resultCode)
    {
        if(phase == Phase.Idle) return;
        var id = End(resultCode);
        PluginLog.Information($"[RoundTrip] 取消（{why}，編號 {id}）：不會換回 {returnLabel}；多角色模式目前{(MultiMode.Enabled ? "開啟" : "關閉")}");
    }

    /// <summary>只取消編號相符的那一趟；不動多角色模式，跟主視窗的取消一樣。</summary>
    internal static bool CancelIfActive(int id, string why)
    {
        if(id <= 0 || phase == Phase.Idle || activeId != id) return false;
        Cancel(why, ResultCode.CancelledIpc);
        return true;
    }

    /// <summary>已結束的那一趟的結果；進行中、編號不認得（重載前的、或已被較新的擠掉）回 false。任何執行緒可呼叫。</summary>
    internal static bool TryGetResult(int id, out string code)
    {
        lock(ResultLock)
        {
            foreach(var x in results)
            {
                if(x.Id == id && id > 0)
                {
                    code = x.Code;
                    return true;
                }
            }
        }
        code = null;
        return false;
    }

    /// <summary><see cref="MultiMode.Tick"/> 判定「這一幀沒有任何角色要處理」時呼叫。</summary>
    internal static void NotifyNothingToDo()
    {
        if(phase == Phase.Running) nothingToDoThisFrame = true;
    }

    /// <summary>每幀在 <see cref="MultiMode.Tick"/> 之後呼叫一次。</summary>
    internal static void Tick()
    {
        var nothingToDo = nothingToDoThisFrame;
        nothingToDoThisFrame = false;
        if(phase == Phase.Running) TickRunning(nothingToDo);
        else if(phase == Phase.Returning) TickReturning();
    }

    private static void TickRunning(bool nothingToDo)
    {
        if(!MultiMode.Enabled)
        {
            Cancel("多角色模式被關閉", ResultCode.CancelledMultiOff);
            return;
        }
        if(!nothingToDo)
        {
            idleSince = 0;
            return;
        }
        var now = Environment.TickCount64;
        if(idleSince == 0) idleSince = now;
        if(now - idleSince < IdleConfirmMs) return;

        if(IsOnReturnCharacter())
        {
            MultiMode.SetEnabled(false, "跑一輪就回來：做完了，人已在原角色", MultiModeChangeCause.AutoRetainer);
            Finish($"沒有角色要處理了，人本來就在 {returnLabel}，多角色模式已關閉", Loc.T("Run once and return: all done and already on {0}. Multi Mode is off."));
            return;
        }
        MultiMode.SetEnabled(false, "跑一輪就回來：做完了，換回原角色", MultiModeChangeCause.AutoRetainer);
        SetPhase(Phase.Returning);
        relogQueued = false;
        nextRelogAttempt = 0;
        returnDeadline = now + ReturnTimeoutMs;
        PluginLog.Information($"[RoundTrip] 沒有角色要處理了，已關閉多角色模式，換回 {returnLabel}");
    }

    private static void TickReturning()
    {
        if(MultiMode.Enabled)
        {
            Cancel("多角色模式又被開啟", ResultCode.CancelledMultiOn);
            return;
        }
        if(IsOnReturnCharacter())
        {
            Finish($"已換回 {returnLabel}，多角色模式已關閉", Loc.T("Run once and return: done, back on {0}. Multi Mode is off."));
            return;
        }
        var now = Environment.TickCount64;
        // 別的插件壓著 AutoRetainer 時不換角，也不讓等待時間耗掉期限。
        if(IPC.Suppressed)
        {
            returnDeadline = now + ReturnTimeoutMs;
            return;
        }
        if(now > returnDeadline)
        {
            End(ResultCode.ReturnTimeout);
            PluginLog.Warning($"[RoundTrip] {ReturnTimeoutMs / 60000} 分鐘內沒能換回 {returnLabel}，放棄；多角色模式維持關閉");
            Svc.Chat.Print(string.Format(Loc.T("Run once and return: could not get back to {0} in time. Multi Mode stays off."), returnLabel));
            return;
        }
        if(relogQueued)
        {
            if(P.TaskManager.IsBusy) return;
            // 換角任務已經結束卻不在原角色上（被中止或登入失敗）：隔一段時間再送一次。
            relogQueued = false;
            nextRelogAttempt = now + RelogRetryMs;
            return;
        }
        if(now < nextRelogAttempt) return;
        var data = TryGetCharacter(returnCid);
        if(data == null)
        {
            End(ResultCode.ReturnMissing);
            PluginLog.Warning($"[RoundTrip] {returnLabel} 已不在角色清單裡，無法換回；多角色模式維持關閉");
            Svc.Chat.Print(string.Format(Loc.T("Run once and return: could not get back to {0} in time. Multi Mode stays off."), returnLabel));
            return;
        }
        if(MultiMode.Relog(data, out var error, RelogReason.RoundTripReturn))
        {
            relogQueued = true;
            PluginLog.Information($"[RoundTrip] 已排入換回 {returnLabel}");
        }
        else
        {
            nextRelogAttempt = now + RelogRetryMs;
            PluginLog.Debug($"[RoundTrip] 換角暫時送不出去，{RelogRetryMs / 1000} 秒後再試：{error}");
        }
    }

    private static void Finish(string logText, string chatFormat)
    {
        End(ResultCode.Returned);
        PluginLog.Information($"[RoundTrip] {logText}");
        Svc.Chat.Print(string.Format(chatFormat, returnLabel));
    }

    /// <summary>每條結束路徑都經過這裡：先記結果再清編號，讀的一方看到編號歸零時結果一定已經查得到。</summary>
    private static int End(string resultCode)
    {
        var id = activeId;
        lock(ResultLock)
        {
            results[nextResultSlot] = (id, resultCode);
            nextResultSlot = (nextResultSlot + 1) % KeepResults;
        }
        SetPhase(Phase.Idle);
        activeId = 0;
        return id;
    }

    private static void SetPhase(Phase p)
    {
        phase = p;
        activePhaseCode = p switch
        {
            Phase.Running => "running",
            Phase.Returning => "returning",
            _ => "",
        };
    }

    private static bool IsOnReturnCharacter() => ProperOnLogin.PlayerPresent && Svc.PlayerState.ContentId == returnCid;

    private static OfflineCharacterData TryGetCharacter(ulong cid)
    {
        foreach(var x in C.OfflineData)
        {
            if(x.CID == cid) return x;
        }
        return null;
    }
}
