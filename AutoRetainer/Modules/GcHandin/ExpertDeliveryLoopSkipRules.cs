namespace AutoRetainer.Modules.GcHandin;

/// <summary>稀有品繳交循環「這位僱員要不要打開」的判定。純函式、不碰遊戲狀態（離線探針直接編這一檔）。
/// 契約：判斷不出來一律回 <see cref="RetainerVisitSkip.None"/>＝照常打開；猜錯只准多開，不准漏取。</summary>
internal static class ExpertDeliveryLoopSkipRules
{
    internal enum RetainerVisitSkip
    {
        None,
        /// <summary>這個角色這一趟已經把它取到沒有可繳交裝備；循環只取不存，期間也拿著僱員自動化的互斥鎖。</summary>
        AlreadyEmptied,
        /// <summary>這次開鈴讀到的僱員清單說它背包 0 件，而且清單已經通過校準。</summary>
        ListShowsEmpty,
    }

    internal static RetainerVisitSkip Decide(bool emptiedThisCharacter, int listItemCount, bool listTrusted)
    {
        if(emptiedThisCharacter) return RetainerVisitSkip.AlreadyEmptied;
        if(listTrusted && listItemCount == 0) return RetainerVisitSkip.ListShowsEmpty;
        return RetainerVisitSkip.None;
    }

    /// <summary>僱員清單的件數可以拿來跳過的條件，缺一不可：
    /// 這次的清單是循環自己點鈴叫出來的、清單視窗已就緒、這次開鈴至少一位被打開的僱員件數與實際格數相符、這一趟沒有出現過不符。</summary>
    internal static bool IsListTrusted(bool bellOpenedByLoop, bool listWindowReady, bool calibratedThisBell, bool distrustedThisRun)
        => bellOpenedByLoop && listWindowReady && calibratedThisBell && !distrustedThisRun;

    /// <summary>「這位僱員已經取完」只在：最後一趟一件都沒送就找不到可繳交裝備、沒有被判拿不到（獨占等）的、
    /// 沒有還在飛的指令、七頁背包都讀得到。獨占品被擋的那位繳完之後可能拿得到，所以不算取完。</summary>
    internal static bool IsEmptiedVerdict(bool noGearWithNothingFired, int skippedItems, int deferredItems, bool allPagesReadable)
        => noGearWithNothingFired && skippedItems == 0 && deferredItems == 0 && allPagesReadable;

    internal enum Calibration { Inconclusive, Match, Mismatch }

    /// <summary>打開僱員那一刻，清單上的件數與實際佔用格數比對。讀不全＝不下結論。</summary>
    internal static Calibration Calibrate(int listItemCount, int occupiedSlots, bool allPagesReadable)
    {
        if(!allPagesReadable || listItemCount < 0) return Calibration.Inconclusive;
        return listItemCount == occupiedSlots ? Calibration.Match : Calibration.Mismatch;
    }
}
