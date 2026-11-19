using System.Collections.Generic;

namespace AutoRetainer.Scheduler;

/// <summary>
/// 延後存放段開僱員前用的「這件放不放得進」快照：僱員真的開著時量一次（<c>SchedulerMain.CaptureEntrustNoRoom</c>）。
/// 🔴 契約：<see cref="KnownNoRoom"/> 只在「確定 <c>Utils.GetAmountThatCanFit(RetainerInventoriesWithCrystals, …)</c> 會回 0」時回 true；
/// 任何不確定（沒量到、跨了開鈴、查不到道具、水晶）一律 false＝照常打開，失敗方向是多開一次不是漏存。
/// 純資料，不碰遊戲；離線探針直接編這個檔。
/// </summary>
internal sealed class EntrustRoomSnapshot
{
    /// <summary>量的當下玩家身上、這個僱員一件都放不進的（道具, HQ）。舊有判斷，不受開鈴限制。</summary>
    public HashSet<(uint ItemId, bool Hq)> NoRoom = [];

    /// <summary>七頁背包的空格數；-1＝有容器或格位讀不到。</summary>
    public int FreeSlots { get; private set; } = -1;

    /// <summary>七頁裡「同品質、非收藏品、還沒疊滿」的堆疊（道具, HQ）。</summary>
    public HashSet<(uint ItemId, bool Hq)> FillableStacks { get; } = [];

    /// <summary>僱員身上（七頁、水晶、寄售、裝備欄）有的道具 ID，給獨占道具用；null＝有容器讀不到，不能用來判斷。</summary>
    public HashSet<uint> HeldItemIds { get; private set; } = [];

    /// <summary>量的那一次開鈴；不同就只剩 <see cref="NoRoom"/> 有效。</summary>
    public int BellSession;

    private bool pagesStarted;

    /// <summary>七頁（不含水晶欄）的一格。<paramref name="stackSize"/> null＝道具表查不到。</summary>
    public void AddPageSlot(uint itemId, bool hq, bool collectable, int quantity, int? stackSize)
    {
        if(!pagesStarted)
        {
            pagesStarted = true;
            FreeSlots = 0;
        }
        if(FreeSlots < 0) return;
        if(itemId == 0)
        {
            FreeSlots++;
            return;
        }
        if(!collectable && stackSize is { } s && s - quantity > 0) FillableStacks.Add((itemId, hq));
    }

    /// <summary>七頁有容器或格位讀不到：空格數作廢（之後再加格也不恢復）。</summary>
    public void PageUnreadable() => FreeSlots = -1;

    public void AddHeldItem(uint itemId)
    {
        if(itemId != 0) HeldItemIds?.Add(itemId);
    }

    public void HeldUnreadable() => HeldItemIds = null;

    public enum NoRoomReason { None, MeasuredAtOpen, UniqueAlreadyHeld, NoFreeSlot }

    /// <param name="found">道具表查得到這件。</param>
    /// <param name="isCrystal">水晶類（ItemUICategory 59）：放水晶專用欄，與空格無關。</param>
    public bool KnownNoRoom(uint itemId, bool hq, bool found, bool isUnique, bool isCrystal, int currentBellSession)
        => Reason(itemId, hq, found, isUnique, isCrystal, currentBellSession) != NoRoomReason.None;

    /// <summary><see cref="KnownNoRoom"/> 為什麼回 true；<c>None</c>＝不確定，照常打開。</summary>
    public NoRoomReason Reason(uint itemId, bool hq, bool found, bool isUnique, bool isCrystal, int currentBellSession)
    {
        if(NoRoom != null && NoRoom.Contains((itemId, hq))) return NoRoomReason.MeasuredAtOpen;
        if(currentBellSession != BellSession || !found || isCrystal) return NoRoomReason.None;
        if(isUnique && HeldItemIds != null && HeldItemIds.Contains(itemId)) return NoRoomReason.UniqueAlreadyHeld;
        if(FreeSlots != 0) return NoRoomReason.None;
        // 沒有空格：獨占道具堆疊上限 1 補不進；其餘只剩同品質未滿的堆疊可補。
        if(isUnique) return NoRoomReason.NoFreeSlot;
        return FillableStacks.Contains((itemId, hq)) ? NoRoomReason.None : NoRoomReason.NoFreeSlot;
    }
}
