using AutoRetainer.Scheduler.Tasks;
using AutoRetainerAPI.Configuration;

namespace AutoRetainer.Modules;

/// <summary>「可交易裝備集中到一位僱員」：每個角色可指定一位僱員（<see cref="Config.TradeableGearHub"/>），其他僱員不收可交易的裝備、這位僱員不收不可交易的裝備。</summary>
/// <remarks>
/// 契約：只在框架執行緒呼叫。規則只有在「設了、角色資料裡找得到那位僱員、而且他是稀有品繳交循環會拜訪的僱員、背包沒有快滿」時生效；
/// 任一條不成立就回 <c>default</c>＝照原本的方式存放。
/// </remarks>
internal static class TradeableGearHub
{
    private static readonly HashSet<ulong> IneligibleReported = [];

    /// <summary>這個角色設定的集中僱員名字；沒設、找不到、或已不在繳交循環名單裡回 false。</summary>
    internal static bool TryGetHubName(OfflineCharacterData data, out string name)
    {
        name = null;
        if(data == null || !C.TradeableGearHub.TryGetValue(data.CID, out var hubId) || hubId == 0) return false;
        var hub = data.RetainerData.FirstOrDefault(x => x.RetainerID == hubId);
        if(hub == null || hub.Name.IsNullOrEmpty()) return false;
        if(!GCExpertDeliveryLoop.ResolveRetainers(data).Contains(hub.Name)) return false;
        name = hub.Name;
        return true;
    }

    /// <param name="retainer">要存給的僱員名字；null＝不知道（當成非集中僱員）。</param>
    internal static TaskEntrustDuplicates.EntrustHubContext Resolve(string retainer)
    {
        var cid = Svc.PlayerState.ContentId;
        if(!C.TradeableGearHub.TryGetValue(cid, out var hubId) || hubId == 0) return default;
        var data = C.OfflineData.FirstOrDefault(x => x.CID == cid);
        if(!TryGetHubName(data, out var hubName))
        {
            if(IneligibleReported.Add(cid)) PluginLog.Information("[TED] 可交易裝備集中：設定的集中僱員找不到，或已不在稀有品繳交循環的名單裡，這個角色暫時照原本的方式存放。");
            return default;
        }
        IneligibleReported.Remove(cid);
        // 背包快滿時退回原本的存放，否則多角色模式會因為空格不足停下來。
        if(!Utils.IsInventoryFree()) return default;
        return new(true, retainer == hubName);
    }
}
