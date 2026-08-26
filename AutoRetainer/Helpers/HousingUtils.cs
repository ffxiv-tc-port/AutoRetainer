using AutoRetainerAPI.Configuration;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace AutoRetainer.Helpers;

public static unsafe class HousingUtils
{
    public static bool TryGetCurrentDescriptor(out HouseDescriptor Descriptor)
    {
        try
        {
            var h = HousingManager.Instance();
            // HousingManager.Instance() 是 MemberFunction（呼叫遊戲自己的取得函式），
            // 不在住宅區時遊戲就回 null —— 這不是例外狀況，是常態。
            if(h == null)
            {
                Descriptor = default;
                return false;
            }
            Descriptor = new(Svc.ClientState.TerritoryType, h->GetCurrentWard(), h->GetCurrentPlot());
            return true;
        }
        catch(ArgumentOutOfRangeException)
        {
            Descriptor = default;
            return false;
        }
    }

    public static HouseDescriptor GetCurrentDescriptor()
    {

        var h = HousingManager.Instance();
        // 🔴 讀不到就丟例外，不回 default —— 這個多載的回傳值是「目前所在的房子」，
        // 回一個 default 描述子等於把「不知道在哪」偽裝成一個具體地點。
        // 需要「讀不到就安靜跳過」語意的呼叫端請改用 TryGetCurrentDescriptor。
        if(h == null) throw new InvalidOperationException("HousingManager is not available");
        return new(Svc.ClientState.TerritoryType, h->GetCurrentWard(), h->GetCurrentPlot(), true);
    }

    public static bool IsInThisHouse(this HouseDescriptor Descriptor)
    {
        if(TryGetCurrentDescriptor(out var d) && d == Descriptor) return true;
        return false;
    }
}
