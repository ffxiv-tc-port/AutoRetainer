using AutoRetainer.Internal;
using AutoRetainer.Internal.InventoryManagement;
using AutoRetainer.Scheduler.Handlers;
using AutoRetainer.Scheduler.Tasks;
using AutoRetainerAPI.Configuration;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons.ExcelServices;
using ECommons.GameHelpers;
using ECommons.IPC;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace AutoRetainer.Modules.GcHandin;

internal static unsafe partial class GCExpertDeliveryLoop
{
    private static long RelogTimeoutMs => C.ExpertDeliveryLoopRelogTimeoutMinutes * 60L * 1000L;

    /// <summary>別的外掛把 AutoRetainer 抑制住(<c>AutoRetainer.SetSuppressed</c>)時,換角色就原地持住。
    /// 回 true ＝ 這一幀什麼都不要做。
    /// <para>🔴 抑制的意思是「現在不要接手這個角色」,而換角色是這條流程裡侵入性最強的一步:
    /// 它會把角色登出。在抑制期間登出,等於把別的外掛(或使用者自己)正在做的事直接砍掉,
    /// 而且**不可逆** —— 所以這裡選擇持住不動,不是照跑也不是放棄整趟。</para>
    /// <para>🔴 暫停期間不計入換角逾時。逾時是拿階段起始時刻比對現在時間算的,不把暫停時間補回去的話
    /// 「被抑制十分鐘」會表現成一次假的逾時失敗,而逾時訊息還會指向一個完全無關的成因。</para>
    /// <para>⚠️ 只管換角色。繳交途中不受抑制節制是既有行為,這裡不動它。</para>
    /// <para>診斷只在**狀態翻轉**時各印一行(進入暫停、恢復),不是每幀 —— 這段可以持續好幾分鐘。</para></summary>
    private static bool RelogSuppressionHolds()
    {
        var now = Environment.TickCount64;
        if(IPC.Suppressed)
        {
            if(RelogSuppressedSince == 0)
            {
                RelogSuppressedSince = now;
                PluginLog.Information($"[ExpertDeliveryLoop] Character switch paused: another plugin has suppressed AutoRetainer (AutoRetainer.SetSuppressed). Holding in phase {CurrentPhase} with targetCID {RelogTargetCID}, {TimeInPhase}ms spent in this phase so far; the {RelogTimeoutMs}ms switch timeout does not advance while paused. Nothing is logged out until this is released.");
            }
            return true;
        }

        if(RelogSuppressedSince != 0)
        {
            var pausedFor = now - RelogSuppressedSince;
            RelogSuppressedSince = 0;
            PhaseEnteredAt += pausedFor;
            RelogHeartbeatAt += pausedFor;
            PluginLog.Information($"[ExpertDeliveryLoop] Character switch resumed: the suppression was released after {pausedFor}ms. Phase {CurrentPhase}, targetCID {RelogTargetCID}; the phase and heartbeat clocks were pushed forward by that amount, so {TimeInPhase}ms of the {RelogTimeoutMs}ms switch timeout has actually been used.");
        }
        return false;
    }

    private static void TickRelog()
    {
        // 上一個角色排進去的東西還在跑、或角色還被什麼佔用著,就先等 —— 登出會把排隊中的動作全部丟掉,
        // 而換角色的原語本身在被佔用時是直接拒絕的(那會被講成「換不過去」,實際上只是還沒放手)。
        // ⚠️ 這是等待點,所以要有逾時:沒有的話「有東西永遠不放手」會表現成流程無聲地停在這裡。
        if(Utils.IsBusy || IsOccupied())
        {
            if(TimeInPhase <= RelogTimeoutMs) return;
            PluginLog.Information($"[ExpertDeliveryLoop] Gave up waiting to start the character switch after {TimeInPhase}ms (limit {RelogTimeoutMs}ms): taskManagerBusy={P.TaskManager.IsBusy} lifestreamBusy={Utils.LifestreamIsBusy()} gcHandinOperation={AutoGCHandin.Operation} occupied={IsOccupied()}.");
            Stop(string.Format(Loc.T("Stopped: nothing let go of this character within {0} minutes, so it could not be switched."), C.ExpertDeliveryLoopRelogTimeoutMinutes));
            return;
        }

        if(BatchIndex >= BatchCIDs.Count)
        {
            // 走不到這裡:換角色只在「還有下一個」時才被排進來。真的走到了就當整批做完,
            // 不要靜默停在一個沒有處理函式的狀態。
            PluginLog.Information($"[ExpertDeliveryLoop] Relog phase entered with no character left (index {BatchIndex} of {BatchCIDs.Count}); treating the run as finished.");
            Stop(Loc.T("Finished: every selected character has been done."), success: true);
            return;
        }

        var cid = BatchCIDs[BatchIndex];
        if(!C.OfflineData.TryGetFirst(x => x.CID == cid, out var data))
        {
            Stop(string.Format(Loc.T(SharedText.StoppedNextCharacterDataGone), cid));
            return;
        }

        if(Player.Available && Player.CID == cid)
        {
            // 已經站在目標角色上了(整批的第一個就是現在登入的這個)。不必換,直接開跑。
            BeginCharacterOrStop(data);
            return;
        }

        if(!MultiMode.Relog(data, out var error, RelogReason.ExpertDeliveryLoop))
        {
            Stop(string.Format(Loc.T("Stopped: could not switch to {0} - {1}"), Censor.Character(data.Name, data.World), error));
            return;
        }

        RelogTargetCID = cid;
        RelogHeartbeatAt = Environment.TickCount64;
        PluginLog.Information($"[ExpertDeliveryLoop] Switching to character {BatchIndex + 1}/{BatchCIDs.Count}: {data.Name}@{data.World} (CID {cid}), from CID {Player.CID}. Waited {TimeInPhase}ms for the character to be free; switch timeout {RelogTimeoutMs}ms.");
        SetPhase(Phase.RelogWait);
    }

    /// <summary>等新角色登入並且真的可以做事。
    /// <para>🔴 「登入了」不等於「可以做事了」:剛登入時背包容器可能還讀不到(空格數會回 0,與背包
    /// 真的滿了完全同形),而登入後還有場景安定延遲。所以完成條件是四件事同時成立,不是只看有沒有登入。</para>
    /// <para>逾時的原因彼此互斥而且要修的東西各不相同,所以逾時訊息要指出是哪一種,並且把實際數值印出來。</para></summary>
    private static void TickRelogWait()
    {
        var now = Environment.TickCount64;
        var elapsed = TimeInPhase;

        var loggedIn = Svc.ClientState.IsLoggedIn;
        var currentCID = Player.CID;
        var onTarget = Player.Available && currentCID == RelogTargetCID;
        var busy = Utils.IsBusy;
        var readable = Utils.IsInventoryStateReadable();
        var occupied = IsOccupied();

        if(onTarget && !busy && readable && !occupied)
        {
            PluginLog.Information($"[ExpertDeliveryLoop] Character switch finished after {elapsed}ms: now on CID {currentCID} (target {RelogTargetCID}), taskManagerBusy={P.TaskManager.IsBusy} lifestreamBusy={Utils.LifestreamIsBusy()} inventoryReadable={readable} occupied={occupied}.");
            RelogTargetCID = 0;
            if(!C.OfflineData.TryGetFirst(x => x.CID == currentCID, out var data))
            {
                Stop(string.Format(Loc.T(SharedText.StoppedNextCharacterDataGone), currentCID));
                return;
            }
            BeginCharacterOrStop(data);
            return;
        }

        if(now - RelogHeartbeatAt > RelogHeartbeatMs)
        {
            RelogHeartbeatAt = now;
            PluginLog.Information($"[ExpertDeliveryLoop] Still switching characters after {elapsed}ms of {RelogTimeoutMs}ms: isLoggedIn={loggedIn} currentCID={currentCID} targetCID={RelogTargetCID} playerAvailable={Player.Available} taskManagerBusy={P.TaskManager.IsBusy} lifestreamBusy={Utils.LifestreamIsBusy()} gcHandinOperation={AutoGCHandin.Operation} inventoryReadable={readable} occupied={occupied}.");
        }

        if(elapsed <= RelogTimeoutMs) return;

        // 互斥的成因梯,由外而內:還沒登入 → 登入到別人 → 登入了但還在忙 → 不忙但讀不到背包 → 被佔用。
        string cause;
        if(!loggedIn) cause = Loc.T("it never got past the login screen");
        else if(currentCID != RelogTargetCID) cause = Loc.T("a different character is logged in");
        else if(busy) cause = Loc.T("AutoRetainer or Lifestream never stopped being busy");
        else if(!readable) cause = Loc.T("the inventory never became readable");
        else cause = Loc.T("the character stayed occupied");

        PluginLog.Information($"[ExpertDeliveryLoop] Character switch timed out after {elapsed}ms (limit {RelogTimeoutMs}ms): isLoggedIn={loggedIn} currentCID={currentCID} targetCID={RelogTargetCID} playerAvailable={Player.Available} taskManagerBusy={P.TaskManager.IsBusy} lifestreamBusy={Utils.LifestreamIsBusy()} gcHandinOperation={AutoGCHandin.Operation} inventoryReadable={readable} occupied={occupied}.");
        Stop(string.Format(Loc.T("Stopped: switching characters did not finish within {0} minutes - {1}."), C.ExpertDeliveryLoopRelogTimeoutMinutes, cause));
    }

    /// <summary>新角色就位了 —— 驗它的設定,不行就停。
    /// <para>🔴 刻意**不跳過**跑不了的角色:使用者勾了它就是要它跑,靜默跳過的結果是整批看起來成功了
    /// 但少做了一個角色,而且沒有任何地方講過。跑不了就當場說是哪一個角色、缺什麼。</para></summary>
    private static void BeginCharacterOrStop(OfflineCharacterData data)
    {
        if(!TryBeginCharacter(out var error))
        {
            Stop(string.Format(Loc.T("Stopped on {0}: {1}"), Censor.Character(data.Name, data.World), error));
            return;
        }
        var bell = GetBellDestination(data.CID);
        var gc = GetGCDestination(data.CID);
        PluginLog.Information($"[ExpertDeliveryLoop] Character {BatchIndex + 1}/{BatchCIDs.Count} {data.Name}@{data.World} (CID {data.CID}) starting. Retainers: {Retainers.Print()}. Bell favourite id={bell.Id} sub={bell.Sub} \"{bell.Name}\", GC favourite id={gc.Id} sub={gc.Sub} \"{gc.Name}\". Reserved slots: {EffectiveReservedSlots}.");
        SealUseRetries = 0;
        EnterSealBonus();
    }

    /// <summary>一個角色跑完了。多角色連跑就換下一個,否則整趟收工。
    /// <para>🔴 每個角色收工只在聊天視窗留一行,**不響音** —— 響音代表「整件事做完了,可以回來看了」,
    /// 每個角色都響會讓它失去這個意義。</para></summary>
    private static void FinishCharacter(string reason)
    {
        if(MultiCharacterRun)
        {
            CharactersDone++;
            BatchIndex++;
            if(BatchIndex < BatchCIDs.Count)
            {
                var line = string.Format(Loc.T("{0} finished ({1}/{2} characters), switching to the next one."),
                    Censor.Character(Player.Name, Player.HomeWorld), CharactersDone, BatchCIDs.Count);
                DuoLog.Information($"{reason} {line}");
                PluginLog.Information($"[ExpertDeliveryLoop] Character {CharactersDone}/{BatchCIDs.Count} done: {reason} | retrievedTotal={RetrievedTotal} handinRounds={HandinRounds}. Next CID {BatchCIDs[BatchIndex]}.");
                SetPhase(Phase.Relog);
                return;
            }
            Stop(string.Format(Loc.T("Finished: all {0} selected characters are done."), BatchCIDs.Count), success: true);
            return;
        }
        Stop(reason, success: true);
    }
}
