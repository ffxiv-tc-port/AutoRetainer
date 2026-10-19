using AutoRetainer.Internal;
using AutoRetainerAPI.Configuration;
using ECommons.Events;

namespace AutoRetainer.Modules.Multi;

/// <summary>
/// 「跑一輪就回來」：記住按下時的角色、開多角色模式，等多角色模式自己判斷沒有角色要處理之後，
/// 關閉多角色模式並換回那個角色。
/// </summary>
/// <remarks>
/// <para>只在框架執行緒上讀寫（主視窗按鈕、<see cref="MultiMode.Tick"/>、<see cref="Tick"/>、手動換角）；狀態只在記憶體，插件重載即忘。</para>
/// <para>「做完」＝<see cref="MultiMode.Tick"/> 走到「目前角色做完、也沒有下一個角色可換」的分支，連續 <see cref="IdleConfirmMs"/> 每幀都如此。</para>
/// <para>取消：等待中多角色模式被任何來源關掉，或手動換角（<see cref="MultiMode.Relog"/> 手動那一組）。</para>
/// </remarks>
internal static class MultiModeRoundTrip
{
    private enum Phase
    {
        Idle,
        Running,
        Returning,
    }

    private const long IdleConfirmMs = 3000;
    private const long ReturnTimeoutMs = 5 * 60 * 1000;
    private const long RelogRetryMs = 5000;

    private static Phase phase = Phase.Idle;
    private static ulong returnCid;
    private static string returnLabel = "";
    private static string activeButtonLabel = "";
    private static bool nothingToDoThisFrame;
    private static long idleSince;
    private static bool relogQueued;
    private static long nextRelogAttempt;
    private static long returnDeadline;

    internal static bool Active => phase != Phase.Idle;

    /// <summary>進行中時主視窗按鈕的文字（含要回去的角色，已套用隱藏名字設定）。</summary>
    internal static string ActiveButtonLabel => activeButtonLabel;

    internal static bool CanStart(out string reason)
    {
        if(Active)
        {
            reason = Loc.T("Already running.");
            return false;
        }
        if(MultiMode.Enabled)
        {
            reason = Loc.T("Multi Mode is already on. Turn it off first, then press this on the character you want to come back to.");
            return false;
        }
        if(!ProperOnLogin.PlayerPresent || TryGetCharacter(Svc.PlayerState.ContentId) == null)
        {
            reason = Loc.T("Log in to the character you want to come back to first (it must be on AutoRetainer's character list).");
            return false;
        }
        reason = null;
        return true;
    }

    internal static void Start()
    {
        if(!CanStart(out var why))
        {
            PluginLog.Information($"[RoundTrip] 沒有開始：{why}");
            return;
        }
        var data = TryGetCharacter(Svc.PlayerState.ContentId);
        returnCid = data.CID;
        returnLabel = Censor.Character(data.Name, data.World);
        activeButtonLabel = string.Format(Loc.T("Back to {0} when done (click to cancel)"), returnLabel) + "###RoundTrip";
        phase = Phase.Running;
        idleSince = 0;
        nothingToDoThisFrame = false;
        PluginLog.Information($"[RoundTrip] 開始：多角色模式處理完到期的僱員與航行器後，關閉多角色模式並換回 {returnLabel}");
        MultiMode.SetEnabled(true, "主視窗的「跑一輪就回來」", MultiModeChangeCause.User);
        MultiMode.OnMultiModeEnabled();
    }

    internal static void Cancel(string why)
    {
        if(phase == Phase.Idle) return;
        phase = Phase.Idle;
        PluginLog.Information($"[RoundTrip] 取消（{why}）：不會換回 {returnLabel}；多角色模式目前{(MultiMode.Enabled ? "開啟" : "關閉")}");
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
            Cancel("多角色模式被關閉");
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
        phase = Phase.Returning;
        relogQueued = false;
        nextRelogAttempt = 0;
        returnDeadline = now + ReturnTimeoutMs;
        PluginLog.Information($"[RoundTrip] 沒有角色要處理了，已關閉多角色模式，換回 {returnLabel}");
    }

    private static void TickReturning()
    {
        if(MultiMode.Enabled)
        {
            Cancel("多角色模式又被開啟");
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
            phase = Phase.Idle;
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
            phase = Phase.Idle;
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
        phase = Phase.Idle;
        PluginLog.Information($"[RoundTrip] {logText}");
        Svc.Chat.Print(string.Format(chatFormat, returnLabel));
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
