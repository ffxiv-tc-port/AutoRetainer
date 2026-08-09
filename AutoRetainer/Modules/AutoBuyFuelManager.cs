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

    /// <summary>燃料是否由自動購買獨佔管理，因而**不可以**被存進雇員。
    ///
    /// <para>🔴 這是碼級的硬排除，不是可設定的預設值。自動購買整條鏈——這個模組的觸發條件
    /// （<see cref="C"/>.AutoBuyFuelThreshold 比對 <c>Data.Ceruleum</c>）與
    /// <see cref="TaskAutoBuyFuel"/> 的購買數量計算（商店 addon 回報的持有數）——讀的**都只有玩家
    /// 身上的量**，兩層都看不到已經存進雇員的部分。而存入計畫只要把青磷水納入（明確列出、
    /// 涵蓋它的類別、或 DuplicatesMultiStack 配上 keep=0），批次存賣就會把身上的整份搬進雇員，
    /// 於是身上歸零 → 買滿上限 → 下一輪再被搬走 → 再買，形成無限重購，把部隊點數燒光。</para>
    ///
    /// <para>斷這個迴圈最小、最不會誤傷的地方是「不要把燃料存進雇員」：自動購買關著的時候
    /// 完全不生效（<c>AutoBuyFuelEnabled</c> 預設就是 false），開著的時候使用者要的本來就是
    /// 「身上維持一定量的燃料」，那與「把燃料存進雇員」直接互斥。</para>
    ///
    /// <para>⚠️ 只擋存入雇員。自動賣出（IMAutoVendorHard）與任何手動操作都不受影響。</para>
    ///
    /// <para>📌 **刻意不提供逃生口**（使用者裁決）。「一邊開著自動購買燃料、一邊要求把燃料自動存進
    /// 雇員」這個組合本身就是上面那個重購迴圈，不是一個合法的使用情境——兩邊要的東西直接互斥。
    /// 所以這裡沒有設定開關可以放行，也刻意**不**把這個排除移到保護清單
    /// （IMProtectList／ExcludeProtected）之後、讓使用者拿保護清單當反悔的手段：那會是行為變更，
    /// 而它要服務的情境並不存在。真的想把燃料放在雇員身上的人，關掉自動購買燃料，
    /// 或自己手動存放（手動不經過這裡）。</para></summary>
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
        if(Data == null || Data.Ceruleum >= C.AutoBuyFuelThreshold) return;
        if(P.TaskManager.IsBusy) return;
        if(DateTimeOffset.Now.ToUnixTimeMilliseconds() - C.AutoBuyFuelCheckTimes.SafeSelect(Player.CID) < 60_000) return;
        if(!EzThrottler.Throttle("AutoBuyFuel.Trigger", 5000)) return;

        C.AutoBuyFuelCheckTimes[Player.CID] = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        TaskAutoBuyFuel.Enqueue();
    }
}
