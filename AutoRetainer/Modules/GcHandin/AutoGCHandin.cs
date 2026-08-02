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

internal static unsafe class AutoGCHandin
{
    internal static AutoGCHandinOverlay Overlay;
    internal static bool Operation = false;

    internal static bool IsEnabled()
    {
        if(C.OfflineData.TryGetFirst(x => x.CID == Svc.ClientState.LocalContentId, out var d))
        {
            return d.GCDeliveryType != GCDeliveryType.Disabled;
        }
        return false;
    }
    internal static bool IsArmoryChestEnabled()
    {
        if(C.OfflineData.TryGetFirst(x => x.CID == Svc.ClientState.LocalContentId, out var d))
        {
            return d.GCDeliveryType.EqualsAny(GCDeliveryType.Hide_Gear_Set_Items, GCDeliveryType.Show_All_Items);
        }
        return false;
    }

    internal static bool IsAllItemsEnabled()
    {
        Safety.Check();
        if(C.OfflineData.TryGetFirst(x => x.CID == Svc.ClientState.LocalContentId, out var d))
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
    // 舊作法：送出繳交 → 按交付 → 用幀節流擋住掃描 → 等「遊戲自己把清單重建好」
    //         才進行下一件。實測每件 0.672 秒，其中約 0.562 秒純粹是在等重建，
    //         所以調整我們自己的節流（10 幀改 3 幀）量到的變化是 0.000 秒。
    //
    // 現在改抄 DailyRoutines：
    //   1. 每一步的閘門都是「條件成不成立」，沒有任何固定延遲；不成立就下一幀再試。
    //   2. 按下交付、獎勵視窗一關掉，就主動對 AgentGrandCompanySupply 送出重選分頁
    //      事件逼它當場重建清單（GCSupplyRefresh），不再空等遊戲自己來。
    //   3. 掃描清單的閘門從「幀節流」換成「狀態機處於 Idle」——同樣不會每幀重跑
    //      95×物品欄的掃描，但不再因此付出固定的 10 幀延遲。
    //
    // 逾時只是保險絲：任何一步的假設不成立時，退回「等遊戲自己重建」的舊行為
    // 重新掃描，不會卡死也不會崩。
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

    // 逾時只是保險絲，不是節奏來源：
    //  - AwaitingList 的閘門是「清單筆數變了」，而不管是主動刷新還是遊戲自己重建都會讓它變，
    //    所以主動刷新萬一沒作用，這一段自然退回舊行為（實測 0.562 秒）而不是空等到逾時。
    //  - 真的走到逾時代表狀態已經不對，這時寧可重新掃描也不要卡在那裡。
    //
    // ⚠️ 逾時的下限不能拉太低：走到逾時之後我們會退回去掃描清單，而清單如果還是舊的，
    // FindNextHandinItem 有可能挑到剛剛交掉的那一件，接著被 HasInInventory 判成
    // 「道具不在背包」而中斷整段繳交。純退回路徑（等遊戲自己重建）實測約 0.56 秒，
    // 所以預設值留了約 3 倍餘裕。
    private const int RewardTimeoutMs = 3000;
    // 走到這裡代表獎勵視窗已經關掉（伺服器確認繳交），代理人卻遲遲不能收事件。
    // 舊值 5000 太長：這種情況下退回「等遊戲自己重建」本來就會成功，不需要空等五秒。
    private const int RefreshTimeoutMs = 2000;

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

    // 節奏量測：一律 Information，使用者的記錄等級會濾掉 Debug/Verbose。
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

    // 一整輪（一次自動繳交）的統計，用來算提早送出的成功率與淨效益。
    private static int StatCycles;
    private static int StatEarlyOk;
    private static int StatReadyOk;
    private static int StatResendOk;
    private static int StatResends;
    private static int StatFallback;

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
    }

    /// <summary>
    /// 軍需品清單 addon 是否處於舊版閘門要求的「完全就緒」狀態。
    /// 主動刷新已經不需要它（見 GCSupplyRefresh 的反組譯註解），但補送與診斷還是要用。
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
        PluginLog.Information(
            $"[GCHandin] 每件 {now - CycleStartedAt}ms | " +
            $"送出→按交付 {(CycleDeliveredAt == 0 ? -1 : CycleDeliveredAt - CycleStartedAt)}ms | " +
            $"交付→主動刷新 {(CycleDeliveredAt == 0 || CycleRefreshedAt == 0 ? -1 : CycleRefreshedAt - CycleDeliveredAt)}ms " +
            // 拆開「交付→主動刷新」的兩段，用來回答那 250~375ms 到底是誰花掉的：
            // 前段是遊戲自己把獎勵視窗收掉（＝伺服器確認繳交），後段才是我們送刷新事件的閘門。
            // 如果前段幾乎等於全部，那就是下限，不要再往這裡調。
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
            $"閘門@視窗關閉 {CycleGateAtRewardGone}");
        CycleStartedAt = CycleDeliveredAt = CycleRewardGoneAt = CycleRefreshedAt = 0;
    }

    /// <summary>一輪繳交結束時把統計倒出來，用來算提早送出的成功率。</summary>
    private static void LogSessionStats()
    {
        if(StatCycles == 0) return;
        PluginLog.Information(
            $"[GCHandin] 本輪統計 共 {StatCycles} 件 | 提早送出即成功 {StatEarlyOk} | 等就緒後送出 {StatReadyOk} | " +
            $"補送後成功 {StatResendOk} | 退回等遊戲重建 {StatFallback} | 補送總次數 {StatResends}");
        StatCycles = StatEarlyOk = StatReadyOk = StatResendOk = StatResends = StatFallback = 0;
    }

    private static bool HandleConfirmation()
    {
        if(TryGetAddonByName<AddonGrandCompanySupplyReward>("GrandCompanySupplyReward", out var addon) && IsAddonReady(&addon->AtkUnitBase))
        {
            var deliverButton = addon->DeliverButton;
            // 這個節流器的名字刻意跟掃描閘門分開。舊版兩者共用一個名字，
            // 送出繳交時的 rethrottle 會連帶把「按交付」也延後最多 10 幀。
            if(deliverButton != null && deliverButton->IsEnabled && FrameThrottler.Throttle(ThrottlerDeliver, 10))
            {
                new AddonMaster.GrandCompanySupplyReward(addon).Deliver();
                DebugLog($"Delivering Item");
                CycleDeliveredAt = NowMs;
                SetPhase(HandinPhase.AwaitingRefresh);
                return true;
            }
        }
        return false;
    }

    private static bool HandleYesno()
    {
        if(TryGetAddonByName<AddonSelectYesno>("SelectYesno", out var addon) && IsAddonReady(&addon->AtkUnitBase) && Operation)
        {
            if(addon->YesButton->IsEnabled)
            {
                var str = addon->PromptText->NodeText.GetText().Cleanup();
                DebugLog($"SelectYesno encountered: {str}");
                //102434	Do you really want to trade a high-quality item?
                if(str.Equals(GenericHelpers.GetText(Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Addon>().GetRow(102434).Text).Cleanup()))
                {
                    if(FrameThrottler.Throttle(ThrottlerYesno, 10))
                    {
                        new AddonMaster.SelectYesno((IntPtr)addon).Yes();
                        DebugLog($"Selecting yes");
                    }
                }
            }
        }
        return false;
    }

    /// <summary>
    /// 送出繳交之後的等待與主動刷新。回傳 true 代表這一幀已經被這裡吃掉，不要再去掃清單。
    ///
    /// 這裡的閘門刻意比 <see cref="IsReadyToOperate"/> 寬鬆：主動刷新要在「獎勵視窗一關掉」
    /// 就送出去，而不是等清單自己回到完全可操作的狀態 —— 那正是我們想省掉的那半秒。
    /// </summary>
    private static bool HandleWaiting()
    {
        switch(Phase)
        {
            case HandinPhase.Idle:
                return false;

            case HandinPhase.AwaitingReward:
                // 高品質道具的確認視窗會插在中間，它在畫面上的期間不要讓逾時計時器繼續跑。
                if(TryGetAddonByName<AtkUnitBase>("SelectYesno", out var yesno) && yesno != null)
                {
                    PhaseEnteredAt = NowMs;
                    return true;
                }
                // 獎勵視窗由 HandleConfirmation 處理，這裡只負責在它一直不出現時解套。
                if(NowMs - PhaseEnteredAt < RewardTimeoutMs) return true;
                PluginLog.Information($"[GCHandin] 等不到繳交確認視窗（{NowMs - PhaseEnteredAt}ms），重新掃描清單");
                ResetPhase();
                return false;

            case HandinPhase.AwaitingRefresh:
                {
                    // 獎勵視窗還在＝伺服器還沒確認繳交。這一關保留：它是繳交真的成立的證據，
                    // 也是我們能送刷新事件的最早時刻。
                    if(TryGetAddonByName<AtkUnitBase>("GrandCompanySupplyReward", out var reward) && reward != null) return true;
                    // 第一次看到獎勵視窗不見的時刻，順便把各閘門的狀態拍下來當診斷。
                    if(CycleRewardGoneAt == 0)
                    {
                        CycleRewardGoneAt = NowMs;
                        CycleGateAtRewardGone = $"{DescribeSupplyListGate()} {GCSupplyRefresh.DescribeGate()}";
                    }
                    // 🔑 這裡刻意不再等 addon 變成 ready。
                    // 離線反組譯 TC 7.20 的 AgentGrandCompanySupply::ReceiveEvent 確認：這條路徑
                    // 只寫代理人自己的 SelectedTab、用只讀代理人欄位的建表函式重建 AtkValue，
                    // 再交給 RaptureAtkModule::RefreshAddon(agent->AddonId, ...) 去找 addon —— 全程
                    // 沒有讀 addon 的可見旗標／ULD 狀態／節點清單。原本的 IsAddonReady 是照抄
                    // DailyRoutines 的泛用閘門，不是這條路徑的前提。細節見 GCSupplyRefresh。
                    // ⚠️ 必須在送出「之前」取樣：RefreshAddon 是同步的，送完再問就可能問到
                    // 被這次刷新改過的狀態，把「提早送出」誤記成「等就緒後送出」。
                    var addonReadyBeforeSend = IsSupplyListAddonReady();
                    if(GCSupplyRefresh.RequestExpertDeliveryRefresh())
                    {
                        CycleSentEarly = !addonReadyBeforeSend;
                        CycleRefreshedAt = NowMs;
                        RefreshSentAt = NowMs;
                        RefreshSendCount = 1;
                        SetPhase(HandinPhase.AwaitingList);
                        return true;
                    }
                    if(NowMs - PhaseEnteredAt < RefreshTimeoutMs) return true;
                    PluginLog.Information($"[GCHandin] 無法主動刷新清單（{NowMs - PhaseEnteredAt}ms，{DescribeSupplyListGate()} {GCSupplyRefresh.DescribeGate()}），退回等遊戲自己重建");
                    StatCycles++;
                    StatFallback++;
                    ResetPhase();
                    return false;
                }

            case HandinPhase.AwaitingList:
                {
                    // addon 第一次變成「完全就緒」的時刻。這是判斷提早送出有沒有真的省到時間的
                    // 關鍵量測：下一件的掃描本來就被 IsReadyToOperate 擋著，所以只有當刷新事件
                    // 本身讓 addon 更早就緒，提早送出才會反映在每件耗時上。
                    if(CycleAddonReadyAt == 0 && IsSupplyListAddonReady()) CycleAddonReadyAt = NowMs;
                    // 等清單筆數真的變了才准掃描。少了這一步，可能讀到還沒刷新的舊清單、
                    // 挑到剛交掉的那件，然後被 HasInInventory 判成「道具不在背包」而中斷整段繳交。
                    var count = GetListedItemCount();
                    if(count >= 0 && (uint)count != ListCountAtHandin)
                    {
                        LogCycle();
                        ResetPhase();
                        return false;
                    }
                    // 便宜的重試。提早送出的假設萬一不成立，代價要是「多送一次代理人事件」，
                    // 而不是吃滿逾時 —— 一次逾時是秒級的，只要少數幾件踩到就把省下來的全賠光。
                    //
                    // 補送刻意等到 addon 真的 ready 才送：那正是舊版的條件，所以最壞情況
                    // 只是回到舊行為的時間點，不會比改之前更慢。次數上限擋住每幀狂送。
                    if(RefreshSendCount > 0
                        && RefreshSendCount < MaxRefreshSends
                        && NowMs - RefreshSentAt >= RefreshRetryMs
                        && IsSupplyListAddonReady()
                        && GCSupplyRefresh.RequestExpertDeliveryRefresh())
                    {
                        RefreshSentAt = NowMs;
                        RefreshSendCount++;
                        StatResends++;
                    }
                    if(NowMs - PhaseEnteredAt < ListSettleTimeoutMs) return true;
                    PluginLog.Information($"[GCHandin] 刷新後清單筆數沒有變化（{NowMs - PhaseEnteredAt}ms，已送出 {RefreshSendCount} 次），退回等遊戲自己重建");
                    LogCycle(true);
                    ResetPhase();
                    return false;
                }
        }
        return false;
    }

    /// <summary>
    /// 目前清單上的筆數（AtkValues[6]）。取不到時回傳 -1；
    /// AtkReader 在索引越界／型別不符時會丟例外，這裡一律吞掉當成「還沒好」。
    ///
    /// 🔑 這裡刻意不用 IsAddonReady 當前提。反組譯確認代理人是用
    /// RefreshAddon 把整組 AtkValue 推回 addon 的（AtkValues[6] 就是這個筆數），
    /// 那條路徑跟 addon 的可見旗標無關；如果連「讀」都要等 addon 變成 ready，
    /// 提早送出刷新事件就完全看不到效果 —— 我們會在原本那道閘門上等一樣久。
    /// 換成直接驗真正該驗的東西：AtkValue 陣列存不存在、長度夠不夠。
    /// </summary>
    private static int GetListedItemCount()
    {
        try
        {
            if(TryGetAddonByName<AtkUnitBase>("GrandCompanySupplyList", out var addon)
                && addon->AtkValues != null
                && addon->AtkValuesCount > 6)
            {
                return (int)new ReaderGrandCompanySupplyList(addon).NumItems;
            }
        }
        catch(Exception)
        {
            //
        }
        return -1;
    }

    private static void HandleGCList()
    {
        if(TryGetAddonByName<AtkUnitBase>("GrandCompanySupplyList", out var addon) && IsReadyToOperate(addon))
        {
            if(Operation)
            {
                if(IsDone(addon))
                {
                    var s = $"Automatic handin has been completed";
                    DuoLog.Information(s);
                    if(C.GCHandinNotify)
                    {
                        Utils.TryNotify(s);
                    }
                    Operation = false;
                    ResetPhase();
                    LogSessionStats();
                    GCContinuation.EnqueueDeliveryClose();
                    if(Utils.GetGCExchangePlanWithOverrides().FinalizeByPurchasing)
                    {
                        GCContinuation.EnqueueInitiation(false);
                    }
                }
                else
                {
                    Overlay.Allowed = true;
                    // 只有 Idle 才掃描。這取代了舊版「送出後把節流器 rethrottle 10 幀」的作法：
                    // 一樣不會每幀重跑 FindNextHandinItem（那會對每個候選掃遍所有背包），
                    // 但不再為此付出固定的 10 幀延遲。
                    if(Phase != HandinPhase.Idle) return;
                    try
                    {
                        var reader = new ReaderGrandCompanySupplyList(addon);

                        var nextItem = FindNextHandinItem();
                        if(reader.NumItems == GetHandinItems().Count)
                        {
                            if(nextItem != null)
                            {
                                var has = AutoGCHandin.HasInInventory(nextItem.Value.ItemID);
                                var itemName = ExcelItemHelper.GetName(nextItem.Value.ItemID);
                                DebugLog($"Seals: {GetSeals()}/{GetMaxSeals()}, for item {nextItem.Value.Seals} | {ExcelItemHelper.GetName(nextItem.Value.ItemID)}: {has}");
                                if(!has)
                                {
                                    throw new GCHandinInterruptedException($"Item {itemName} was not found in inventory");
                                }
                                // 🔴 索引上下界都要驗：越界的 index 會讓遊戲照著算出去。
                                if(nextItem.Value.Index < 0 || nextItem.Value.Index >= reader.NumItems)
                                {
                                    throw new GCHandinInterruptedException($"Item index {nextItem.Value.Index} out of range (0..{reader.NumItems})");
                                }
                                DebugLog($"Handing in item {itemName} for {nextItem.Value.Seals} seals (index={nextItem.Value.Index})");
                                if(InvokeHandin(addon, nextItem.Value.Index))
                                {
                                    ListCountAtHandin = reader.NumItems;
                                    CycleStartedAt = NowMs;
                                    CycleDeliveredAt = 0;
                                    CycleRewardGoneAt = 0;
                                    CycleRefreshedAt = 0;
                                    SetPhase(HandinPhase.AwaitingReward);
                                }
                            }
                            else
                            {
                                if(FindNextHandinItem(false) == null)
                                {
                                    GCContinuation.EnqueueDeliveryClose();
                                    throw new GCHandinInterruptedException("Auto GC handin completed");
                                }
                                else
                                {
                                    GCContinuation.EnqueueDeliveryClose();
                                    if(C.AutoGCContinuation)
                                    {
                                        GCContinuation.EnqueueInitiation(true);
                                    }
                                    throw new GCHandinInterruptedException("Too many seals, please spend them");
                                }
                            }
                        }
                    }
                    catch(FormatException e)
                    {
                        PluginLog.Verbose($"{e.Message}");
                    }
                    catch(GCHandinInterruptedException e)
                    {
                        Operation = false;
                        ResetPhase();
                        LogSessionStats();
                        DuoLog.Information($"{e.Message}");
                        if(C.GCHandinNotify && !C.AutoGCContinuation)
                        {
                            Utils.TryNotify(e.Message);
                        }
                    }
                    catch(Exception e)
                    {
                        Operation = false;
                        ResetPhase();
                        LogSessionStats();
                        e.Log();
                    }
                }
            }
            else
            {
                Overlay.Allowed = IsReadyToOperate(addon);
            }
        }
        else
        {
            Overlay.Allowed = Operation || IsReadyToOperate(addon);
        }
    }

    private static bool IsReadyToOperate(AtkUnitBase* GCSupplyListAddon)
    {
        try
        {
            return
                GCSupplyListAddon != null
                && IsAddonReady(GCSupplyListAddon)
                && GCSupplyListAddon->UldManager.NodeListCount > 20
                && GCSupplyListAddon->UldManager.NodeList[5]->IsVisible()
                && IsSelectedFilterValid(GCSupplyListAddon);
        }
        catch(Exception)
        {
            return false;
        }
    }
    internal static bool IsDone(AtkUnitBase* addon)
    {
        return addon->UldManager.NodeList[20]->IsVisible();
    }
    internal static bool IsSelectedFilterValid(AtkUnitBase* addon)
    {
        var step1 = addon->UldManager.NodeList[14];
        var step2 = step1->GetAsAtkComponentNode()->Component->UldManager.NodeList[1];
        var step3 = step2->GetAsAtkComponentNode()->Component->UldManager.NodeList[2];
        var text = GenericHelpers.ReadSeString(&step3->GetAsAtkTextNode()->NodeText).GetText();
        //4619	Hide Armoury Chest Items
        //4618	Hide Gear Set Items
        //4617	Show All Items
        var hideArmory = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Addon>().GetRow(4619).Text.ToDalamudString().GetText();
        var hideGearSet = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Addon>().GetRow(4618).Text.ToDalamudString().GetText();
        var showAll = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Addon>().GetRow(4617).Text.ToDalamudString().GetText();
        if(text.Equals(hideArmory))
        {
            return true;
        }
        else
        {
            if(C.OfflineData.TryGetFirst(x => x.CID == Svc.ClientState.LocalContentId, out var data))
            {
                if(text.EqualsAny(hideGearSet))
                {
                    return IsArmoryChestEnabled() || IsAllItemsEnabled();
                }
                if(text.EqualsAny(showAll))
                {
                    return IsAllItemsEnabled();
                }
            }
        }
        return false;
    }

    /// <summary>
    /// 送出「繳交第 which 列」。真的送出去才回傳 true —— 呼叫端要靠這個決定狀態機能不能前進。
    /// 節流只是防手滑重送的保險，流程本身已經由狀態機擋住重入。
    /// </summary>
    internal static bool InvokeHandin(AtkUnitBase* addon, int which)
    {
        if(addon == null || which < 0) return false;
        if(!FrameThrottler.Throttle("AutoGCHandinCallback", 2)) return false;
        Callback.Fire(addon, true, 1, which, Callback.ZeroAtkValue);
        return true;
    }

    internal static bool HasInInventory(uint itemID)
    {
        return InventoryManager.Instance()->GetInventoryItemCount(itemID) + InventoryManager.Instance()->GetInventoryItemCount(itemID, true) > 0;
    }

    public static bool IsListReady()
    {
        if(TryGetAddonByName<AtkUnitBase>("GrandCompanySupplyList", out var addon) && IsAddonReady(addon))
        {
            return true;
        }
        return false;
    }

    public static (uint ItemID, uint Seals, int Index)? FindNextHandinItem(bool checkSealCap = true)
    {
        var sealsRemaining = GetMaxSeals() - GetSeals();
        var items = GetHandinItems();
        if(C.AutoGCContinuation && GCContinuation.GetNextPurchaseListing() == null) checkSealCap = false;
        List<(uint ItemID, uint Seals, int Index)> candidates = [];
        for(var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if(Data.GetIMSettings().IMProtectList.Contains(item.ItemID)) continue;
            var seals = (uint)(item.Seals * Utils.GetGCSealMultiplier());
            if(!checkSealCap || sealsRemaining > seals) candidates.Add((item.ItemID, seals, i));
        }
        if(candidates.Count > 0)
        {
            return candidates
                .OrderByDescending(x => Utils.GetGCExchangePlanWithOverrides().Items.Where(i => i.Quantity > 0 || i.QuantitySingleTime > 0).Any(i => i.ItemID == x.ItemID))
                .ThenByDescending(x => Utils.CountItemsInInventory(x.ItemID, null, Utils.PlayerInvetories))
                .ThenBy(x => x.Seals)
                .First();
        }
        return null;
    }

    public static List<(uint ItemID, uint Seals)> GetHandinItems()
    {
        var ret = new List<(uint ItemID, uint Seals)>();
        if(TryGetAddonByName<AtkUnitBase>("GrandCompanySupplyList", out var addon) && IsAddonReady(addon))
        {
            var reader = new ReaderGrandCompanySupplyList(addon);
            if(IsListReady())
            {
                // AddonGrandCompanySupplyList 自己的項目陣列（未具名欄位）。
                // 🔴 清單重建期間這個指標可能是 null，解參考前一定要檢查 —— AVE 是
                // corrupted-state exception，try/catch 攔不到。
                var ptr = (GCExpectEntry*)*(nint*)((nint)(addon) + 648);
                if(ptr == null) return ret;
                var count = reader.NumItems;
                // 明顯不合理的筆數視為讀到垃圾，寧可不做也不要照著走出去。
                if(count > 1000) return ret;
                for(var i = 0; i < count; i++)
                {
                    var entry = ptr[i];
                    ret.Add((entry.ItemID, entry.Seals));
                }
            }
        }
        return ret;
    }

    public static uint GetSeals()
    {
        return GetGC() == 0 ? 0 : InventoryManager.Instance()->GetCompanySeals(GetGC());
    }

    public static uint GetMaxSeals()
    {
        return GetGC() == 0 ? 0 : InventoryManager.Instance()->GetMaxCompanySeals(GetGC());
    }

    public static byte GetGC()
    {
        return PlayerState.Instance()->GrandCompany;
    }

    public static byte GetRank()
    {
        if(GetGC() == 1) return PlayerState.Instance()->GCRankMaelstrom;
        if(GetGC() == 2) return PlayerState.Instance()->GCRankTwinAdders;
        if(GetGC() == 3) return PlayerState.Instance()->GCRankImmortalFlames;
        return 0;
    }

    public static bool IsValidGCTerritory()
    {
        if(GetGC() == 1) return Svc.ClientState.TerritoryType == MainCities.Limsa_Lominsa_Upper_Decks;
        if(GetGC() == 2) return Svc.ClientState.TerritoryType == MainCities.New_Gridania;
        if(GetGC() == 3) return Svc.ClientState.TerritoryType == MainCities.Uldah_Steps_of_Nald;
        return false;
    }
}
