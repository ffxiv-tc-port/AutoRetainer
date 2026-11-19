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
    internal static void Start()
    {
        if(Running) return;

        // 🔴 多角色模式跟這條循環是互斥的,而且**單角跑一樣會被踩**:多角色模式看到「這個角色沒事做」
        //    就自己把角色登出換掉,時機與這條流程完全無關 —— 循環會被丟在另一個角色上繼續送僱員互動,
        //    而那個角色的僱員名單根本不是它要找的。多角色連跑更兇:兩套東西同時在換角色。
        //    ⚠️ 看的是 Enabled 不是 Active:被別的外掛抑制中的多角色模式隨時會恢復,不算安全狀態。
        if(MultiMode.Enabled)
        {
            PluginLog.Information($"[ExpertDeliveryLoop] Refusing to start because Multi Mode is enabled: multiCharacterRun={C.ExpertDeliveryLoopMultiCharacter} multiModeEnabled={MultiMode.Enabled} multiModeActive={MultiMode.Active} suppressed={IPC.Suppressed} multiModeType={C.MultiModeType} nightMode={C.NightMode}. Multi Mode logs itself out and switches characters on its own schedule, which would strand this loop on a different character.");
            Fail(Loc.T("Multi Mode is on. Turn it off before starting this loop - Multi Mode logs out and switches characters by itself, so it would either fight this run's own character switching or leave the loop stranded on another character."));
            return;
        }

        if(!Player.Available)
        {
            Fail(Loc.T("Player is not available."));
            return;
        }
        if(Utils.IsBusy)
        {
            Fail(Loc.T("AutoRetainer or Lifestream is busy."));
            return;
        }
        // 取回與繳交是這條循環的兩隻腳；任一項改版後對不上就不開始，免得跑到一半才空轉到逾時。
        if(!NativeLayout.SupplyListAvailable)
        {
            Fail(UnavailableNotice.SupplyListMessage);
            return;
        }
        if(!P.Memory.CanSendRetainerItemCommand)
        {
            Fail(UnavailableNotice.RetainerItemCommandMessage);
            return;
        }

        List<ulong> batch = null;
        if(C.ExpertDeliveryLoopMultiCharacter)
        {
            if(!TryBuildBatch(out batch, out var batchError))
            {
                Fail(batchError);
                return;
            }
        }

        // 整批的第一個角色就是現在登入的這個時,不必先換角 —— 直接驗這個角色的前置條件。
        // 不是的話這一趟的第一個動作就是換角色,現在這個角色的設定與它無關,不該擋著開始。
        var startHere = batch == null || batch[0] == Player.CID;
        if(startHere && !TryBeginCharacter(out var error))
        {
            Fail(error);
            return;
        }

        CancelResumeMultiMode("稀有品繳交循環又開始了");
        Running = true;
        MultiCharacterRun = batch != null;
        BatchCIDs = batch ?? [];
        BatchIndex = 0;
        CharactersDone = 0;
        RelogTargetCID = 0;
        RelogSuppressedSince = 0;
        RetrievedTotal = 0;
        HandinRounds = 0;
        VentureCharacters = 0;
        VenturesResentTotal = 0;
        VenturesCollectedOnlyTotal = 0;
        VentureSkippedTotal = 0;
        // 軍票上限中斷次數跟著這一趟走：這裡是它唯一的歸零點（見 AutoGCHandin.SealCapPauses）。
        AutoGCHandin.SealCapPauses = 0;
        GcArrivalLoggedCID = 0;
        BellDestinationsProvenThisRun.Clear();
        ListCountDistrusted = false;
        VisitsSkippedEmptied = 0;
        VisitsSkippedListEmpty = 0;
        StatusText = "";

        if(startHere)
        {
            SealUseRetries = 0;
            EnterSealBonus();
            PluginLog.Information($"[ExpertDeliveryLoop] Started{(MultiCharacterRun ? $" (multi-character: {BatchCIDs.Count} character(s))" : "")}. Retainers to visit: {Retainers.Print()}. Reserved slots: {EffectiveReservedSlots} (config {C.ExpertDeliveryLoopReservedSlots}, MultiMinInventorySlots {C.MultiMinInventorySlots}).");
        }
        else
        {
            SetPhase(Phase.Relog);
            PluginLog.Information($"[ExpertDeliveryLoop] Started (multi-character: {BatchCIDs.Count} character(s)). The logged-in character (CID {Player.CID}) is not on the list, so the first thing this run does is switch to CID {BatchCIDs[0]}.");
        }
    }

    /// <summary>驗完整批角色的前置條件,並排出拜訪順序。
    /// <para>🔴 這些條件全部讀得到離線資料,**不必登入那個角色就驗得到**。少了這一步,設定漏掉的角色
    /// 要等到換過去、跑到一半才會發現 —— 而那時候已經過了十幾分鐘,還占用了一次換角色。</para>
    /// <para>回 false 時 <paramref name="error"/> 直接是給使用者看的完整說法(含是哪幾個角色、缺什麼)。</para></summary>
    private static bool TryBuildBatch(out List<ulong> batch, out string error)
    {
        batch = null;
        error = "";

        // 🔴 兩套換角色的東西同時在跑一定會打架:多開排程看到「這個角色沒事做」就會把人換走,
        //    而它換走的時機與這條流程完全無關。這不是保守,是實際會互相踩。
        // 📌 Start() 在更前面就擋掉了(單角跑也會被踩)。這一道刻意留著:
        //    這個函式驗的是「整批角色跑不跑得起來」,不該假設呼叫端一定先擋過。
        if(MultiMode.Enabled)
        {
            error = Loc.T(SharedText.MultiModeBlocksMultiCharacterRun);
            return false;
        }
        // 這個除錯選項會讓登出那一步直接把整條任務佇列砍掉,而且不留下任何訊息。
        if(C.DontLogout)
        {
            error = Loc.T(SharedText.DontLogoutBlocksCharacterSwitch);
            return false;
        }

        List<OfflineCharacterData> selected = [];
        List<string> problems = [];
        foreach(var cid in C.ExpertDeliveryLoopCharacters)
        {
            if(!C.OfflineData.TryGetFirst(x => x.CID == cid, out var data))
            {
                problems.Add(string.Format(Loc.T("CID {0}: there is no saved data for this character any more."), cid));
                continue;
            }
            selected.Add(data);
        }
        // 順序照 UI 上看到的(離線資料本身的順序),不要照勾選的先後 —— 使用者無從得知後者。
        selected = [.. C.OfflineData.Where(selected.Contains)];

        if(selected.Count == 0 && problems.Count == 0)
        {
            error = Loc.T("No characters are selected for the multi-character run.");
            return false;
        }

        foreach(var data in selected)
        {
            var who = Censor.Character(data.Name, data.World);
            if(data.IsLockedOut())
            {
                problems.Add(string.Format(Loc.T("{0}: this character's region is locked out right now."), who));
            }
            else if(data.GCDeliveryType == GCDeliveryType.Disabled)
            {
                problems.Add(string.Format(Loc.T("{0}: expert delivery is disabled - set a delivery mode for this character first."), who));
            }
            else if(ResolveRetainers(data).Count == 0)
            {
                problems.Add(string.Format(Loc.T("{0}: no retainer matches the current selection."), who));
            }
        }

        if(problems.Count > 0)
        {
            // 🔴 一次列出全部,不要只講第一個 —— 一次修一個問題等於要使用者重按五次按鈕。
            error = Loc.T("Not starting: some of the selected characters are not ready.") + "\n" + string.Join("\n", problems);
            return false;
        }

        batch = [.. selected.Select(x => x.CID)];
        // 從現在登入的這個角色開始,省掉一次沒必要的換角色。
        var idx = batch.IndexOf(Player.CID);
        if(idx > 0) batch = [.. batch.Skip(idx), .. batch.Take(idx)];
        return true;
    }

    /// <summary>把狀態機重設成「這個角色從頭開始」,並驗這個角色跑不跑得動。
    /// <para>🔴 每一個屬於「上一個角色」的東西都要在這裡清掉:僱員名單、僱員索引、這一輪取了幾件、
    /// 有沒有已經傳送過、導航窗、回鈴收尾旗標、跳過與在途的道具。換角色之後位置、區域、僱員清單
    /// 全部是新的,沿用任何一項都會讓流程對著上一個角色的世界做決定。</para></summary>
    private static bool TryBeginCharacter(out string error)
    {
        error = "";
        if(GCContinuation.GetGCInfo() == null)
        {
            error = Loc.T("This character is not employed by a Grand Company.");
            return false;
        }
        if(!AutoGCHandin.IsEnabled())
        {
            error = Loc.T("Expert delivery is disabled for this character - set a delivery mode first.");
            return false;
        }

        var retainers = ResolveRetainers();
        if(retainers.Count == 0)
        {
            error = C.ExpertDeliveryLoopManualRetainers
                ? Loc.T("No retainers are selected.")
                : Loc.T("No retainer of this character carries the selected entrust plan.");
            return false;
        }

        Retainers = retainers;
        RetainerIndex = 0;
        RetrievedThisRound = 0;
        BellTravelsThisRound = 0;
        LastBellTravelAt = 0;
        BellTravelOccupiedSince = 0;
        UnavailableSince = 0;
        NavigationDeadline = 0;
        PendingFinishReason = "";
        FinishReturnAttempted = false;
        RoundEndReason = RoundEnd.NoGearLeft;
        BellInteractAttempts = 0;
        LastBellInteractAt = 0;
        PassFired = 0;
        SkippedItems.Clear();
        DeferredItems.Clear();
        EmptiedRetainers.Clear();
        BellOpenedByLoop = false;
        ListCalibratedThisBell = false;
        SelectedListItemCount = -1;
        return true;
    }

    internal static void Stop(string reason, bool success = false)
    {
        if(!Running && CurrentPhase == Phase.Idle) return;
        Running = false;
        CurrentPhase = Phase.Idle;
        RelogSuppressedSince = 0;
        StatusText = reason;
        // 🔴 停止只停外層狀態機,已經排進 AutoRetainer/Lifestream 的任務鏈會自己跑完 ——
        //    中途硬中斷互動鏈的風險比讓它跑完高。但那會造成「說停了卻還在繳交」的困惑,
        //    所以當下如果還有東西在跑,訊息要明講。
        var stillBusy = Utils.IsBusy;
        var summary = $"{reason} ({string.Format(Loc.T("retrieved {0}, handin rounds {1}"), RetrievedTotal, HandinRounds)})";
        if(MultiCharacterRun) summary += " " + string.Format(Loc.T("Characters finished: {0}/{1}."), CharactersDone, BatchCIDs.Count);
        // 軍票上限中斷不逐次印進聊天視窗，整趟結束時報一次總數：
        // 使用者拿得到同一個事實，但只付一行的代價。
        if(AutoGCHandin.SealCapPauses > 0) summary += " " + string.Format(Loc.T("Seals hit the cap {0} time(s) along the way."), AutoGCHandin.SealCapPauses);
        if(VentureCharacters > 0)
        {
            summary += " " + string.Format(Loc.T("Ventures resent: {0}."), VenturesResentTotal);
            PluginLog.Information($"[ExpertDeliveryLoop] Venture refresh over this run: characters={VentureCharacters} resent={VenturesResentTotal} collectedOnly={VenturesCollectedOnlyTotal} skipped={VentureSkippedTotal}.");
        }
        PluginLog.Information($"[ExpertDeliveryLoop] Retainer visits skipped over this run: alreadyEmptied={VisitsSkippedEmptied} listShowsEmpty={VisitsSkippedListEmpty} listCountDistrusted={ListCountDistrusted}.");
        if(stillBusy) summary += " " + Loc.T("The loop has stopped, but work already queued in AutoRetainer will finish on its own.");
        if(success)
        {
            if(C.ExpertDeliveryLoopResumeMultiMode)
            {
                ResumeMultiModeSince = Environment.TickCount64;
                summary += " " + Loc.T("Multi Mode will be turned back on once AutoRetainer is idle.");
            }
            DuoLog.Information(summary);
            if(C.GCHandinNotify) Utils.TryNotify(summary);
            // 整趟跑完了請塔塔露念一句。掛在這裡是因為這是三條成功路徑唯一的匯流點,
            // 而且本函式開頭的「已經停了就直接回」守衛保證同一趟只會走到這裡一次。
            // 🔴 失敗路徑(含使用者手動按停止)不出聲:響音代表「做完了,可以回來看了」。
            // 🔴 執行緒:TryPraise 只能在主執行緒呼叫。success 路徑全都在 GCExpertDeliveryLoop.Tick() 鏈上(由掛在
            //    Svc.Framework.Update 的 AutoRetainer.Tick 呼叫);UI 的「使用者停止」按鈕走的是 success=false,到不了這裡。
            TataruPraiseIPC.TryPraise(TataruPraiseIPC.CategoryExpertDelivery,
                MultiCharacterRun
                    ? $"稀有品繳交循環完成:多角色 {CharactersDone}/{BatchCIDs.Count}"
                    : "稀有品繳交循環完成:單角色");
        }
        else
        {
            DuoLog.Warning(summary);
            // 🔴 多角色連跑失敗時也出聲。這種跑法本來就是丟著讓它跑的 —— 沒響的話,一批五個角色
            //    死在第二個,使用者要在半小時後才發現,而且發現時已經分不出是哪一段出的事。
            //    ⚠️ 這裡刻意**不**沿用「完成時通知」那個開關:它預設是關的,而「整批做完了」與
            //    「批次死在半路」的重要程度完全不同。失敗有自己的開關,預設開。
            if(MultiCharacterRun && C.ExpertDeliveryLoopNotifyOnFailure) Utils.TryNotify(summary);
        }
        PluginLog.Information($"[ExpertDeliveryLoop] Stopped: {reason} | retrieved={RetrievedTotal} handinRounds={HandinRounds} sealCapPauses={AutoGCHandin.SealCapPauses} success={success} stillBusy={stillBusy} multiCharacter={MultiCharacterRun} charactersDone={CharactersDone}/{BatchCIDs.Count}");
    }

    /// <summary>艦隊急停：停掉循環並取消待執行的「跑完後開回多角色模式」。只停不開；只在框架執行緒呼叫。</summary>
    internal static void EmergencyStop(string reason)
    {
        CancelResumeMultiMode(reason);
        Stop(reason);
    }

    private static void Fail(string reason)
    {
        StatusText = reason;
        DuoLog.Warning(reason);
        PluginLog.Information($"[ExpertDeliveryLoop] Refused to start: {reason}");
    }

    /// <summary>成功收工之後開回多角色模式。開法與主視窗的勾選框相同，夜間模式等設定一律不動。</summary>
    private static void TickResumeMultiMode()
    {
        if(Running || !C.ExpertDeliveryLoopResumeMultiMode)
        {
            CancelResumeMultiMode(Running ? "稀有品繳交循環又開始了" : "「跑完後開回多角色模式」被關掉了");
            return;
        }
        var waited = Environment.TickCount64 - ResumeMultiModeSince;
        if(MultiMode.Enabled)
        {
            ResumeMultiModeSince = 0;
            PluginLog.Information($"[ExpertDeliveryLoop] 多角色模式在等待期間已經被打開（等了 {waited}ms），不再重開");
            return;
        }
        if(Utils.IsBusy)
        {
            if(waited <= ResumeMultiModeTimeoutMs) return;
            ResumeMultiModeSince = 0;
            StatusText += " " + string.Format(Loc.T("Multi Mode was not turned back on: AutoRetainer or Lifestream was still busy after {0} minutes."), ResumeMultiModeTimeoutMs / 60000);
            PluginLog.Information($"[ExpertDeliveryLoop] 放棄開回多角色模式：等了 {waited}ms 仍在忙 taskManagerBusy={P.TaskManager.IsBusy} lifestreamBusy={Utils.LifestreamIsBusy()} gcHandinOperation={AutoGCHandin.Operation}");
            return;
        }
        ResumeMultiModeSince = 0;
        PluginLog.Information($"[ExpertDeliveryLoop] 稀有品繳交循環跑完，照設定開回多角色模式（等了 {waited}ms；nightMode={C.NightMode} multiModeType={C.MultiModeType} suppressed={IPC.Suppressed}）");
        MultiMode.SetEnabled(true, "稀有品繳交循環跑完後開回", MultiModeChangeCause.AutoRetainer);
        MultiMode.OnMultiModeEnabled();
    }

    private static void CancelResumeMultiMode(string why)
    {
        if(ResumeMultiModeSince == 0) return;
        ResumeMultiModeSince = 0;
        PluginLog.Information($"[ExpertDeliveryLoop] 取消開回多角色模式：{why}");
    }

    /// <summary>僱員清單是不是真的可以用了。
    /// 🔴 <c>GameRetainerManager.Ready</c> 只讀 <c>RetainerManager.IsReady</c> 這個旗標,**不保證
    /// 僱員陣列已經填好** —— Ready 為 true 而 Count 為 0 是真實會出現的狀態(剛換區、剛開鈴的
    /// 那一小段)。而 <c>TryGetRetainerByName</c> 在那個狀態下對每個名字都回 false,與「這個僱員
    /// 真的不存在」完全不可分。所以「存在性」這種結論只准在這個閘門為 true 時做。</summary>
    internal static bool RetainerListLoaded => GameRetainerManager.Ready && GameRetainerManager.Count > 0;

    internal static int EffectiveReservedSlots => Math.Max(C.ExpertDeliveryLoopReservedSlots, C.MultiMinInventorySlots);

    private static void SetPhase(Phase phase)
    {
        CurrentPhase = phase;
        PhaseEnteredAt = Environment.TickCount64;
    }

    private static long TimeInPhase => Environment.TickCount64 - PhaseEnteredAt;

    private static bool ChainFinished => !Utils.IsBusy && TimeInPhase > EnqueueGraceMs;
}
