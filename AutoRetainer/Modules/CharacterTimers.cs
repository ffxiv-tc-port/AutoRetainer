global using CharacterTimerList = System.Collections.Generic.List<(ulong CID, string Name, string World, string WorldOverride,
    (string Name, ulong RetainerID, bool HasVenture, uint VentureID, long VentureEndsAt)[] Retainers,
    (string Name, uint ReturnTime)[] Airships,
    (string Name, uint ReturnTime)[] Submarines)>;
using System.Collections.Generic;
using AutoRetainerAPI.Configuration;

namespace AutoRetainer.Modules;

/// <summary>
/// <c>AutoRetainer.GetCharacterTimersV1</c> 的本體：一次回所有已登錄角色的僱員探險與飛空艇／潛水艇返航時間。
/// </summary>
/// <remarks>
/// 🔴 回傳型別是對外契約，發出去就不能改：只用 System.Private.CoreLib 的型別，兩個插件看到同一個 <c>Type</c>，CallGate 不走 JSON 轉換。
/// 要加欄位就另開新名字的端點，不要改這個。
/// 角色的挑法與順序＝<c>GetRegisteredCIDs</c> 逐一 <c>GetOfflineCharacterData</c>；每個值都是當下的複本。
/// 只能在 framework 執行緒上呼叫（走訪活的 <c>C.OfflineData</c>／<c>C.Blacklist</c>）。
/// </remarks>
internal static class CharacterTimers
{
    internal const string Endpoint = "AutoRetainer.GetCharacterTimersV1";

    internal static CharacterTimerList Build(List<OfflineCharacterData> offline, List<(ulong CID, string Name)> blacklist)
    {
        var result = new CharacterTimerList();
        foreach(var x in offline)
        {
            if(IsBlacklisted(blacklist, x.CID) || x.Name == "Unknown" || x.Name == "") continue;
            var ocd = First(offline, x.CID);
            result.Add((ocd.CID, ocd.Name, ocd.World, ocd.WorldOverride,
                Retainers(ocd.RetainerData), Vessels(ocd.OfflineAirshipData), Vessels(ocd.OfflineSubmarineData)));
        }
        return result;
    }

    private static bool IsBlacklisted(List<(ulong CID, string Name)> blacklist, ulong cid)
    {
        foreach(var b in blacklist)
        {
            if(b.CID == cid) return true;
        }
        return false;
    }

    // 同 GetOfflineCharacterData 的 FirstOrDefault：CID 重複時一律拿第一筆。
    private static OfflineCharacterData First(List<OfflineCharacterData> offline, ulong cid)
    {
        foreach(var x in offline)
        {
            if(x.CID == cid) return x;
        }
        return null;
    }

    private static (string Name, ulong RetainerID, bool HasVenture, uint VentureID, long VentureEndsAt)[] Retainers(List<OfflineRetainerData> src)
    {
        if(src == null) return null;
        var result = new (string Name, ulong RetainerID, bool HasVenture, uint VentureID, long VentureEndsAt)[src.Count];
        for(var i = 0; i < result.Length; i++)
        {
            var r = src[i];
            result[i] = (r.Name, r.RetainerID, r.HasVenture, r.VentureID, r.VentureEndsAt);
        }
        return result;
    }

    private static (string Name, uint ReturnTime)[] Vessels(List<OfflineVesselData> src)
    {
        if(src == null) return null;
        var result = new (string Name, uint ReturnTime)[src.Count];
        for(var i = 0; i < result.Length; i++)
        {
            var v = src[i];
            result[i] = (v.Name, v.ReturnTime);
        }
        return result;
    }
}
