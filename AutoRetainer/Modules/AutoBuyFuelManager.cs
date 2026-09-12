using AutoRetainer.Modules.Voyage;
using AutoRetainer.Modules.Voyage.VoyageCalculator;
using AutoRetainer.Scheduler.Tasks;
using ECommons.GameHelpers;
using ECommons.Throttlers;

namespace AutoRetainer.Modules;

// Periodically checks whether the current character is low on Ceruleum
// Tanks while standing in a Company Workshop, and if so, walks up to the
// adventurer doll NPC to buy more from the Free Company Credit Shop.
internal static class AutoBuyFuelManager
{
    /// <summary>桶裝青磷水（Ceruleum Tanks）。<see cref="Items.Tanks"/> 的別名。</summary>
    internal const uint FuelItemId = (uint)Items.Tanks;

    /// <summary>燃料是否由自動購買獨佔管理，因而**不可以**被存進僱員。
    /// 🔴 這是碼級的硬排除，不是可設定的預設值。
    /// 斷這個迴圈最小、最不會誤傷的地方是「不要把燃料存進僱員」。
    /// ⚠️ 只擋存入僱員。自動賣出（IMAutoVendorHard）與任何手動操作都不受影響。
    /// 📌 **刻意不提供逃生口**（使用者裁決）。
    /// </summary>
    internal static bool IsFuelReservedForAutoBuy(uint itemId)
    {
        if(itemId != FuelItemId) return false;
        if(!C.AutoBuyFuelEnabled) return false;
        if(EzThrottler.Throttle("AutoRetainer.AutoBuyFuel.EntrustSkipNotice", 600_000))
        {
            PluginLog.Information($"[AutoBuyFuel] Ceruleum Tanks (item {FuelItemId}) are excluded from entrusting to retainers while \"auto buy fuel\" is enabled - the auto-buy trigger and its purchase amount both only see what you are carrying, so entrusting them away makes it buy the same fuel again on the next pass. Turn off auto buy fuel if you want your entrust plan to store fuel on retainers instead.");
        }
        return true;
    }

    internal static void Tick()
    {
        if(!C.AutoBuyFuelEnabled) return;
        if(!Player.Available) return;
        if(!VoyageUtils.Workshops.Contains(Svc.ClientState.TerritoryType)) return;
        if(Data == null) return;
        // 🔴 恰為 0 ＝「刻意沒帶」，不是「快用完了」。使用者會在 NPC 旁整理背包時把桶裝青磷水
        // 暫時放到別處（僱員、部隊寶物庫、市場委託），那一瞬間身上就是 0，而舊的「低於門檻值就補」
        // 連 0 都涵蓋，於是整理到一半就被拉去買一整批。補充只在 1 ~ 門檻值-1 之間觸發。
        //
        // ⚠️ 這與 IsFuelReservedForAutoBuy 是**互補**的兩件事，不能互相取代：
        // 那邊擋的是**自動**把燃料搬進僱員的路徑，這裡擋的是**手動**暫存造成的誤判。
        //
        // 📌 想從 0 開始補的人有出口：設定頁的「立即購買」按鈕，以及工坊懸浮列的遞迴購買，
        // 兩者都是手動觸發、不看這個門檻。
        if(Data.Ceruleum <= 0) return;
        if(Data.Ceruleum >= C.AutoBuyFuelThreshold) return;
        if(P.TaskManager.IsBusy) return;
        if(DateTimeOffset.Now.ToUnixTimeMilliseconds() - C.AutoBuyFuelCheckTimes.SafeSelect(Player.CID) < 60_000) return;
        if(!EzThrottler.Throttle("AutoBuyFuel.Trigger", 5000)) return;

        C.AutoBuyFuelCheckTimes[Player.CID] = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        TaskAutoBuyFuel.Enqueue();
    }
}
