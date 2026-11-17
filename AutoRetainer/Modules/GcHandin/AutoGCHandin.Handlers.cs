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
    private static bool HandleConfirmation()
    {
        if(TryGetAddonByName<AddonGrandCompanySupplyReward>("GrandCompanySupplyReward", out var addon) && IsAddonReady(&addon->AtkUnitBase))
        {
            var deliverButton = addon->DeliverButton;
            // 節流器的名字刻意獨立（ThrottlerDeliver）：與別的閘門共用名字會讓對方的節流連帶把「按交付」延後。
            // 🔴 獎勵窗按「交付」即關;下一幀 Tick 仍會先進到這裡(HandleConfirmation 在 Phase 判斷之前),
            //    關閉中的窗 IsAddonReady 三關全過、按鈕若仍啟用,10 幀節流一到就會對同一扇再按一次。
            //    同一扇獎勵窗只按一次,窗消失後 DialogGuards.Tick 才解除。
            if(Utils.IsButtonEnabled(deliverButton) && FrameThrottler.Throttle(ThrottlerDeliver, 10)
                && DialogGuards.TryPressOnce("GrandCompanySupplyReward", (nint)addon, "GCDeliver"))
            {
                new AddonMaster.GrandCompanySupplyReward(addon).Deliver();
                if(VerboseWanted) PluginLog.Verbose($"Delivering Item");
                CycleDeliveredAt = NowMs;
                SetPhase(HandinPhase.AwaitingRefresh);
                return true;
            }
        }
        return false;
    }

    // 🔴 這支由 Tick 的狀態機每幀驅動，而窗被按下之後有「正在關閉中」的幾幀：GetAddonByName 仍拿得到實例、
    // IsAddonReady 三關也全過，此時再按一次就是攔不到的原生 AccessViolation；10 幀的 FrameThrottler 不是防護。
    // 「按過了」的記號集中在 Helpers/DialogGuards.cs（以窗名為 key：這扇 102434 的確認框 MiniTA 也會比中，
    // 兩邊共用同一把 key 才不會 A 按過、B 再按一次），解除點在 DialogGuards.Tick。
    // ⚠️ Utils.IsButtonEnabled(addon->YesButton) 也不是防護：AddonMaster.SelectYesno.Yes() 會強制啟用停用的「是」鈕。
    private static bool HandleYesno()
    {
        if(TryGetAddonByName<AddonSelectYesno>("SelectYesno", out var addon) && IsAddonReady(&addon->AtkUnitBase) && Operation)
        {
            // 🔴 PromptText 是偏移 0x238 的指標欄位,開窗途中/版面未建好時為 null。
            //    NodeText 在 AtkTextNode 偏移 0xC0,而 GetText(this Utf8String) 的參數是**傳值**的 ——
            //    複製動作發生在呼叫端,等於直接去讀位址 0xC0,炸在這一行(不是在 GetText 裡面)。
            //    讀不到提示文字就不按「是」(fail-closed:比對不成立就不動作,下一輪重試)。
            var promptText = addon->PromptText;
            if(promptText != null && Utils.IsButtonEnabled(addon->YesButton))
            {
                var str = GenericHelpers.ReadSeString(&promptText->NodeText).GetText().Cleanup();
                DebugLog($"SelectYesno encountered: {str}");
                //102434	Do you really want to trade a high-quality item?
                // 讀到 U+FFFD ＝ 窗的記憶體正在變動(多半是關閉中),這一幀不碰。
                if(!DialogGuards.TextIsUnstable(str) && str.Equals(GenericHelpers.GetText(Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Addon>().GetRow(102434).Text).Cleanup()))
                {
                    if(FrameThrottler.Throttle(ThrottlerYesno, 10)
                        && DialogGuards.TryPressOnce("SelectYesno", (nint)addon, ThrottlerYesno))
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
                    // 🔑 這裡刻意不等 addon 變成 ready：AgentGrandCompanySupply::ReceiveEvent 這條路徑只寫代理人自己的欄位、
                    // 再交給 RefreshAddon(agent->AddonId, ...) 去找 addon，全程不讀 addon 的可見旗標／ULD 狀態／節點清單（同 GCSupplyRefresh 的類別註解）。
                    // ⚠️ 必須在送出「之前」取樣：RefreshAddon 是同步的，送完再問就可能問到被這次刷新改過的狀態，
                    // 把「提早送出」誤記成「等就緒後送出」。
                    var addonReadyBeforeSend = SampleRebuildGates();
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
                    // 補送刻意等到 addon 真的 ready 才送：最壞情況只是回到「等就緒才送」的時間點。次數上限擋住每幀狂送。
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
    /// 🔑 這裡刻意不用 IsAddonReady 當前提：代理人是用 RefreshAddon 把整組 AtkValue 推回 addon 的，
    /// 那條路徑跟 addon 的可見旗標無關；連「讀」都要等 addon ready 的話，提早送出刷新事件就看不到效果。
    /// 改成直接驗真正該驗的東西：AtkValue 陣列存不存在、長度夠不夠。
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
                    // 只有 Idle 才掃描：FindNextHandinItem 會對每個候選掃遍所有背包，不能每幀重跑；用狀態機擋而不是固定延遲。
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
                                if(VerboseWanted) PluginLog.Verbose($"Seals: {GetSeals()}/{GetMaxSeals()}, for item {nextItem.Value.Seals} | {ExcelItemHelper.GetName(nextItem.Value.ItemID)}: {has}");
                                if(!has)
                                {
                                    throw new GCHandinInterruptedException($"Item {itemName} was not found in inventory");
                                }
                                // 🔴 這道檢查是承重牆，不是防禦性裝飾，**不要拿掉也不要降成 assert**。
                                // 遊戲那端（AgentGrandCompanySupply 的選列路徑）對稀有品 vector 的長度完全沒有上界檢查。越界＝AccessViolation，那是 corrupted-state exception，try/catch 攔不到。
                                // reader.NumItems 正好是正確的界。
                                if(nextItem.Value.Index < 0 || nextItem.Value.Index >= reader.NumItems)
                                {
                                    throw new GCHandinInterruptedException($"Item index {nextItem.Value.Index} out of range (0..{reader.NumItems})");
                                }
                                if(VerboseWanted) PluginLog.Verbose($"Handing in item {itemName} for {nextItem.Value.Seals} seals (index={nextItem.Value.Index})");
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
                                    SealCapPauses++;
                                    if(C.AutoGCContinuation)
                                    {
                                        GCContinuation.EnqueueInitiation(true);
                                    }
                                    // 🔑 軍票續購開著時這不是故障：GCContinuation.EnqueueInitiation 會自己去把軍票花掉再回來繼續繳交
                                    //    ⇒ 只寫記錄檔，不洗使用者的聊天視窗。
                                    // 🔴 但軍票續購**關掉**時上面那個 if 一個任務都沒排：繳交真的就停在這裡，而這則訊息是使用者唯一會收到的通知
                                    //    （托盤通知也只在關掉時才送）⇒ 那種情況一定要留在聊天視窗，不可以一律降噪。
                                    throw new GCHandinInterruptedException("Too many seals, please spend them", quietInChat: C.AutoGCContinuation);
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
                        if(e.QuietInChat)
                        {
                            // Grep 標記：GCHandin。要使用者回報的診斷。
                            // 訊息本文照原樣保留，既有的「Too many seals」grep 仍然命中得到。
                            PluginLog.Information($"[GCHandin] {e.Message} (本趟第 {SealCapPauses} 次；軍票續購開著，會自己去買完再回來繼續)");
                        }
                        else
                        {
                            DuoLog.Information($"{e.Message}");
                        }
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
}
