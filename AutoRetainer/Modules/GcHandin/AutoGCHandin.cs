using AutoRetainer.Internal;
using AutoRetainer.Services;
using AutoRetainer.UI.Overlays;
using AutoRetainerAPI.Configuration;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Utility;
using ECommons.ExcelServices;
using ECommons.ExcelServices.TerritoryEnumeration;
using ECommons.Throttlers;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace AutoRetainer.Modules.GcHandin;

internal static unsafe partial class AutoGCHandin
{
    internal static AutoGCHandinOverlay Overlay;
    internal static bool Operation = false;

    internal static bool IsEnabled()
    {
        if(C.OfflineData.TryGetFirst(x => x.CID == Svc.PlayerState.ContentId, out var d))
        {
            return d.GCDeliveryType != GCDeliveryType.Disabled;
        }
        return false;
    }
    internal static bool IsArmoryChestEnabled()
    {
        if(C.OfflineData.TryGetFirst(x => x.CID == Svc.PlayerState.ContentId, out var d))
        {
            return d.GCDeliveryType.EqualsAny(GCDeliveryType.Hide_Gear_Set_Items, GCDeliveryType.Show_All_Items);
        }
        return false;
    }

    internal static bool IsAllItemsEnabled()
    {
        Safety.Check();
        if(C.OfflineData.TryGetFirst(x => x.CID == Svc.PlayerState.ContentId, out var d))
        {
            return d.GCDeliveryType == GCDeliveryType.Show_All_Items;
        }
        return false;
    }

    internal static void Init()
    {
        Overlay = new();
        P.WindowSystem.AddWindow(Overlay);
    }

    internal static void Tick()
    {
        // 不論是誰打開的（浮層勾選、繳交流程、IPC），清單版面沒驗過就不讓它跑：讀不到清單只會原地空轉。
        if(Operation && !NativeLayout.SupplyListAvailable)
        {
            Operation = false;
            UnavailableNotice.ReportSupplyList(nameof(AutoGCHandin));
        }
        if(Svc.Condition[ConditionFlag.OccupiedInQuestEvent] && TryGetAddonByName<AtkUnitBase>("GrandCompanySupplyList", out var addon))
        {
            if(addon->X != 0 || addon->Y != 0)
            {
                Overlay.Position = new(addon->X, addon->Y - Overlay.height);
            }
        }
        if(Svc.Condition[ConditionFlag.OccupiedInQuestEvent] && IsEnabled())
        {
            Safety.Check();
            // 使用者從浮層把自動繳交關掉時，狀態機也要跟著歸零。
            if(!Operation && Phase != HandinPhase.Idle) ResetPhase();
            if(Operation && HandleConfirmation())
            {
                //
            }
            else if(Operation && HandleYesno())
            {
                //
            }
            else if(Operation && HandleWaiting())
            {
                //
            }
            else
            {
                HandleGCList();
            }
        }
        else
        {
            if(Overlay.Allowed) Overlay.Allowed = false;
            if(Operation)
            {
                Operation = false;
                LogSessionStats();
            }
            if(Phase != HandinPhase.Idle) ResetPhase();
        }
    }

    // ── 繳交流程狀態機 ────────────────────────────────────────────────────
    // 每一步的閘門都是「條件成不成立」，沒有任何固定延遲；不成立就下一幀再試。
    // 按下交付、獎勵視窗一關掉，就主動對 AgentGrandCompanySupply 送出重選分頁事件逼它當場重建清單（GCSupplyRefresh）。
    // 逾時只是保險絲：任何一步的假設不成立時，退回「等遊戲自己重建」再重新掃描，不會卡死也不會崩。
    private enum HandinPhase
    {
        /// <summary>可以掃描清單並送出下一件。</summary>
        Idle,
        /// <summary>已送出繳交，等繳交確認（獎勵）視窗出現。</summary>
        AwaitingReward,
        /// <summary>已按下交付，等獎勵視窗關掉後主動刷新清單。</summary>
        AwaitingRefresh,
        /// <summary>已送出刷新事件，等清單筆數真的變了才准掃描。</summary>
        AwaitingList,
    }

    private static HandinPhase Phase = HandinPhase.Idle;
    private static long PhaseEnteredAt;
    private static uint ListCountAtHandin;

    // 逾時只是保險絲，不是節奏來源。
    // AwaitingList 的閘門是「清單筆數變了」，主動刷新萬一沒作用，這一段自然退回等遊戲自己重建。
    // ⚠️ 逾時的下限不能拉太低：FindNextHandinItem 有可能挑到剛剛交掉的那一件，「道具不在背包」而中斷整段繳交。
    private const int RewardTimeoutMs = 3000;
    // 走到這裡代表獎勵視窗已經關掉（伺服器確認繳交），代理人卻遲遲不能收事件。
    // 這種情況下退回「等遊戲自己重建」本來就會成功，不需要空等五秒。
    // ⚠️ 這道保險絲同時是「AddonId != 0 這個閘門萬一在台服不成立」的成本上限。
    private const int RefreshTimeoutMs = 1000;

    private const int MinListTimeoutMs = 300;
    private const int MaxListTimeoutMs = 5000;
    private const int MinRefreshRetryMs = 50;
    private const int MaxRefreshRetryMs = 1000;

    /// <summary>清單筆數必須在這段時間內改變，否則放棄主動刷新、退回等遊戲自己重建。</summary>
    private static int ListSettleTimeoutMs => Math.Clamp(C.GCHandinListTimeoutMs, MinListTimeoutMs, MaxListTimeoutMs);
    /// <summary>送出刷新事件之後，等這麼久清單還是沒動就補送一次。</summary>
    private static int RefreshRetryMs => Math.Clamp(C.GCHandinRefreshRetryMs, MinRefreshRetryMs, MaxRefreshRetryMs);
    /// <summary>一件最多送幾次刷新事件（含第一次）。上限的用途是避免補送退化成每幀狂送。</summary>
    private const int MaxRefreshSends = 3;

    private const string ThrottlerDeliver = "Handin.Deliver";
    private const string ThrottlerYesno = "Handin.Yesno";

    // 節奏量測：要使用者回報的診斷。
    // Grep 標記：GCHandin
    private static long CycleStartedAt;
    private static long CycleDeliveredAt;
    private static long CycleRewardGoneAt;
    private static long CycleRefreshedAt;
    /// <summary>這一件的刷新事件送出過幾次。</summary>
    private static int RefreshSendCount;
    /// <summary>最後一次送出刷新事件的時刻。</summary>
    private static long RefreshSentAt;
    /// <summary>第一次送出時 addon 還沒 ready ——「提早送出」成不成立就看這個。</summary>
    private static bool CycleSentEarly;
    /// <summary>刷新之後 addon 第一次變成完全就緒的時刻（IsReadyToOperate 的主要成分）。</summary>
    private static long CycleAddonReadyAt;
    /// <summary>獎勵視窗剛消失那一刻，各個閘門的快照。</summary>
    private static string CycleGateAtRewardGone = "";

    // ── 「等刷新閘門」那半秒是誰欠的 ──────────────────────────────────────
    // 獎勵視窗一消失，清單視窗就**不存在**，代理人的清單陣列與 AddonId 也都是 0：刷新事件在那一刻沒有對象可送。
    // 底下三個時刻分開記，才分得出「視窗回來」「代理人清單回來」「代理人重新綁定」是同一件事還是三件事。
    /// <summary>清單視窗第一次重新存在的時刻。</summary>
    private static long CycleAddonBackAt;
    /// <summary>代理人的清單陣列第一次重新可用的時刻。</summary>
    private static long CycleAgentListBackAt;
    /// <summary>代理人第一次重新綁上視窗（AddonId != 0）的時刻。</summary>
    private static long CycleAddonBoundAt;

    // 一整輪（一次自動繳交）的統計，用來算提早送出的成功率與淨效益。
    private static int StatCycles;
    private static int StatEarlyOk;
    private static int StatReadyOk;
    private static int StatResendOk;
    private static int StatResends;
    private static int StatFallback;

    /// <summary>
    /// 這一趟因為軍票已達上限而中斷過幾次。
    /// 🔑 軍票續購（<c>C.AutoGCContinuation</c>）開著時這是**正常節奏**的一部分。
    /// 歸零點只有一個：<see cref="GCExpertDeliveryLoop.Start"/>。刻意不在每一輪繳交結束時歸零。
    /// </summary>
    internal static int SealCapPauses;

    private static long NowMs => Environment.TickCount64;

    private static void SetPhase(HandinPhase phase)
    {
        Phase = phase;
        PhaseEnteredAt = NowMs;
    }

    private static void ResetPhase()
    {
        SetPhase(HandinPhase.Idle);
        CycleStartedAt = CycleDeliveredAt = CycleRewardGoneAt = CycleRefreshedAt = 0;
        RefreshSendCount = 0;
        RefreshSentAt = 0;
        CycleSentEarly = false;
        CycleAddonReadyAt = 0;
        CycleGateAtRewardGone = "";
        CycleAddonBackAt = CycleAgentListBackAt = CycleAddonBoundAt = 0;
    }

    /// <summary>
    /// 取樣「獎勵視窗關掉之後，清單重建到哪一步了」，每個子條件各記第一次成立的時刻。
    /// <para/>
    /// 回傳值與 <see cref="IsSupplyListAddonReady"/> 相同（送出刷新事件前的就緒狀態），
    /// 順便讓這一幀只查一次視窗而不是兩次。
    /// <para/>
    /// 🔴 取得的原生指標只在這一幀內使用，不保存。
    /// </summary>
    private static bool SampleRebuildGates()
    {
        var now = NowMs;
        var hasAddon = TryGetAddonByName<AtkUnitBase>("GrandCompanySupplyList", out var list);
        if(hasAddon && CycleAddonBackAt == 0) CycleAddonBackAt = now;
        GCSupplyRefresh.SampleAgentGates(out var listArrayBack, out var addonBound);
        if(listArrayBack && CycleAgentListBackAt == 0) CycleAgentListBackAt = now;
        if(addonBound && CycleAddonBoundAt == 0) CycleAddonBoundAt = now;
        return hasAddon && IsAddonReady(list);
    }

    /// <summary>把重建過程的某個時刻換算成「獎勵視窗關掉之後幾毫秒」，量不到時回 -1。</summary>
    private static long SinceRewardGone(long at)
        => at == 0 || CycleRewardGoneAt == 0 ? -1 : at - CycleRewardGoneAt;

    /// <summary>
    /// 軍需品清單 addon 是否處於 IsAddonReady 的「完全就緒」狀態。
    /// 主動刷新已經不需要它（見 GCSupplyRefresh 的類別註解），但補送與診斷還是要用。
    /// </summary>
    private static bool IsSupplyListAddonReady()
        => TryGetAddonByName<AtkUnitBase>("GrandCompanySupplyList", out var list) && IsAddonReady(list);

    /// <summary>
    /// 診斷字串：把 ECommons 的 IsAddonReady 拆成三個子條件分開報。
    /// 「等刷新閘門」那段時間到底是哪一項擋的，只有這樣才看得出來。
    /// </summary>
    private static string DescribeSupplyListGate()
    {
        if(!TryGetAddonByName<AtkUnitBase>("GrandCompanySupplyList", out var list)) return "清單視窗=無";
        var uldLoaded = list->UldManager.LoadedState == AtkLoadState.Loaded;
        // 順序刻意跟 IsAddonReady 的短路順序一致：ULD 沒載入完就不要去呼叫 IsFullyLoaded。
        var fullyLoaded = uldLoaded && list->IsFullyLoaded();
        return $"清單視窗=有 可見={(list->IsVisible ? 1 : 0)} ULD={(uldLoaded ? 1 : 0)} 已載入={(fullyLoaded ? 1 : 0)}";
    }

    /// <summary>逐件明細那幾行 Verbose 的組字串成本（查印章、查道具名）只在真的收得到 Verbose 時才付。</summary>
    /// <remarks>🔴 不可以改判 <c>Svc.Log.MinimumLogLevel</c>：ECommons 的 <c>Init</c> 無條件把它設成 Verbose，
    /// 那個判斷式恆真、等於沒有閘門。這裡與 <c>InternalLog.CaptureLevel</c> 取同一個來源。</remarks>
    private static bool VerboseWanted
        => global::Dalamud.EntryPoint.LogLevelSwitch.MinimumLevel <= Serilog.Events.LogEventLevel.Verbose;

    /// <param name="fellBack">true 代表這一件沒等到清單變化，是靠逾時退回舊路徑收尾的。</param>
    private static void LogCycle(bool fellBack = false)
    {
        if(CycleStartedAt == 0) return;
        var now = NowMs;
        string outcome;
        StatCycles++;
        if(fellBack)
        {
            outcome = $"退回等遊戲重建(已送出{RefreshSendCount}次)";
            StatFallback++;
        }
        else if(RefreshSendCount > 1)
        {
            outcome = $"補送{RefreshSendCount - 1}次後成功";
            StatResendOk++;
        }
        else if(CycleSentEarly)
        {
            outcome = "提早送出即成功";
            StatEarlyOk++;
        }
        else
        {
            outcome = "等就緒後送出";
            StatReadyOk++;
        }
        // 逐件明細走 Verbose、本輪統計留 Information：這一行每件一筆，量大到會淹掉同級的其他訊息。
        if(VerboseWanted) PluginLog.Verbose(
            $"[GCHandin] 每件 {now - CycleStartedAt}ms | " +
            $"送出→按交付 {(CycleDeliveredAt == 0 ? -1 : CycleDeliveredAt - CycleStartedAt)}ms | " +
            $"交付→主動刷新 {(CycleDeliveredAt == 0 || CycleRefreshedAt == 0 ? -1 : CycleRefreshedAt - CycleDeliveredAt)}ms " +
            // 拆開「交付→主動刷新」的兩段：前段是遊戲自己把獎勵視窗收掉（＝伺服器確認繳交），
            // 後段才是我們送刷新事件的閘門。前段幾乎等於全部的話那就是下限，不要再往這裡調。
            $"(等獎勵視窗關閉 {(CycleDeliveredAt == 0 || CycleRewardGoneAt == 0 ? -1 : CycleRewardGoneAt - CycleDeliveredAt)}ms + " +
            $"等刷新閘門 {(CycleRewardGoneAt == 0 || CycleRefreshedAt == 0 ? -1 : CycleRefreshedAt - CycleRewardGoneAt)}ms) | " +
            $"刷新→清單更新 {(CycleRefreshedAt == 0 ? -1 : now - CycleRefreshedAt)}ms" +
            // 以下是為了判斷「提早送出」值不值得而加的：結果分類、末次送出到清單更新的距離
            // （0~16ms 代表是那次送出造成的；幾百毫秒代表其實是遊戲自己重建完的），
            // 以及獎勵視窗剛消失那一刻各個閘門的狀態。
            $" | 結果={outcome} | 刷新送出 {RefreshSendCount} 次 | " +
            $"末次刷新→清單更新 {(RefreshSentAt == 0 ? -1 : now - RefreshSentAt)}ms | " +
            // 下一件的掃描被 IsReadyToOperate 擋著，所以這一項才是「提早送出有沒有真的省到」的答案：
            // 它接近 0 代表刷新事件本身讓視窗提早就緒（真的有省）；它仍是一兩百毫秒代表視窗就緒
            // 是獨立於刷新的過程，提早送出只是把等待換了個位置。
            $"刷新→視窗就緒 {(CycleRefreshedAt == 0 || CycleAddonReadyAt == 0 ? -1 : CycleAddonReadyAt - CycleRefreshedAt)}ms | " +
            // 「等刷新閘門」那段時間的拆解：三者都相對於獎勵視窗消失那一刻。
            // 若三個數字幾乎相同，代表遊戲是一口氣把視窗與代理人一起帶回來的，
            // 那段等待就是遊戲自己的下限；若「視窗回來」明顯早於「重新綁定」，
            // 差額才是還有機會省下來的部分。
            $"重建(相對視窗關閉) 視窗回來 {SinceRewardGone(CycleAddonBackAt)}ms / " +
            $"代理人清單 {SinceRewardGone(CycleAgentListBackAt)}ms / " +
            $"重新綁定 {SinceRewardGone(CycleAddonBoundAt)}ms | " +
            $"閘門@視窗關閉 {CycleGateAtRewardGone}");
        CycleStartedAt = CycleDeliveredAt = CycleRewardGoneAt = CycleRefreshedAt = 0;
    }

    /// <summary>一輪繳交結束時把統計倒出來，用來算提早送出的成功率。</summary>
    /// <remarks>全部一次成功的輪次只寫 Verbose；有補送或退回等遊戲重建的輪次仍寫 Information。</remarks>
    private static void LogSessionStats()
    {
        if(StatCycles == 0) return;
        var anomalous = StatResendOk > 0 || StatFallback > 0 || StatResends > 0;
        if(anomalous || VerboseWanted)
        {
            var line = $"[GCHandin] 本輪統計 共 {StatCycles} 件 | 提早送出即成功 {StatEarlyOk} | 等就緒後送出 {StatReadyOk} | " +
                $"補送後成功 {StatResendOk} | 退回等遊戲重建 {StatFallback} | 補送總次數 {StatResends}";
            if(anomalous) PluginLog.Information(line);
            else PluginLog.Verbose(line);
        }
        StatCycles = StatEarlyOk = StatReadyOk = StatResendOk = StatResends = StatFallback = 0;
    }
}
