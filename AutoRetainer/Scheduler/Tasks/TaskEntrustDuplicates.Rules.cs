namespace AutoRetainer.Scheduler.Tasks;

// 本檔只放不碰遊戲、不碰設定的純判斷，離線探針直接連結這個檔案來測；不要在這裡加任何相依。
internal static partial class TaskEntrustDuplicates
{
    internal enum EntrustExclusion { None, Protected, ReservedFuel, NonDeliverableGear, TradeableNotHub, ArmoryBound, UntradableAtHub }

    /// <summary>「可交易裝備集中到一位僱員」在這次判斷時的狀態。<c>default</c>＝規則不生效（沒設、設的僱員已不在繳交循環名單、背包快滿）。</summary>
    /// <param name="IsHub">目前開著的就是集中僱員。不知道目前是誰時為 false（可交易裝備留在背包，不會多存）。</param>
    internal readonly record struct EntrustHubContext(bool Active, bool IsHub);

    /// <summary>裝備類別的存放排除，依序：「只存放可繳交的稀有品」→ 放兵裝庫 → 集中僱員（兩個方向）。</summary>
    /// <remarks>
    /// 契約：逐件點名在 EntrustItems 裡的、非裝備類別的，一律不排除。
    /// <paramref name="isDeliverable"/> 只在 <paramref name="onlyDeliverableGear"/> 為 true 時有意義。
    /// 放兵裝庫只收不可交易的，<paramref name="armoryBound"/> 遇到可交易的一律不算；它先判，低等不可交易的先歸兵裝庫。
    /// 集中規則生效時：可交易的只存給集中僱員，不可交易的不存給集中僱員（留給其他僱員）。
    /// </remarks>
    internal static EntrustExclusion DecideGearExclusion(bool onlyDeliverableGear, bool namedInEntrustItems, bool isGear, bool isDeliverable, bool isUntradable, EntrustHubContext hub, bool armoryBound = false)
    {
        if(namedInEntrustItems || !isGear) return EntrustExclusion.None;
        if(onlyDeliverableGear && !isDeliverable) return EntrustExclusion.NonDeliverableGear;
        if(armoryBound && isUntradable) return EntrustExclusion.ArmoryBound;
        if(hub.Active && !hub.IsHub && !isUntradable) return EntrustExclusion.TradeableNotHub;
        if(hub.Active && hub.IsHub && isUntradable) return EntrustExclusion.UntradableAtHub;
        return EntrustExclusion.None;
    }
}
