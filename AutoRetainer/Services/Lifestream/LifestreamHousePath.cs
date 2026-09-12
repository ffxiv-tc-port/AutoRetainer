using ECommons.IPC;
using ECommons.IPC.Subscribers.LifestreamIPC;

namespace AutoRetainer.Services.Lifestream;

/// <summary>
/// GetHousePathData 是我們唯一一個回傳**複合型別**的 Lifestream 呼叫。
/// Dalamud 在 CallGateChannel.InvokeFunc 發現型別不同時會走 ConvertObject，**公開欄位名對得上**才會有值。
/// 所以這裡在**第一次真的拿到資料**時印一行 Information，log 裡有這行=通了;沒有=沒通。
/// </summary>
public static class LifestreamHousePath
{
    private static bool LoggedFirstSuccess;

    public static (HousePathData Private, HousePathData FC) Get(ulong cid)
    {
        var data = ECommonsIPC.Lifestream.GetHousePathData(cid);
        if(!LoggedFirstSuccess && (data.Private != null || data.FC != null))
        {
            LoggedFirstSuccess = true;
            PluginLog.Information($"[Lifestream IPC] HousePathData crossed the IPC boundary successfully (CID={cid:X16}). Private={Describe(data.Private)}; FC={Describe(data.FC)}");
        }
        return data;
    }

    // ⚠️ Ward/Plot 逐字印 Lifestream 給的原值,不做 +1 ——我沒有離線證據說明它是 0 起算還是 1 起算,
    // 而這行的用途是「證明資料過得來」,不是給使用者當門牌看。
    private static string Describe(HousePathData d)
        => d == null
            ? "null"
            : $"[district={d.ResidentialDistrict} ward={d.Ward} plot={d.Plot} raw; entrancePath={d.PathToEntrance?.Count ?? 0}pts workshopPath={d.PathToWorkshop?.Count ?? 0}pts]";
}
