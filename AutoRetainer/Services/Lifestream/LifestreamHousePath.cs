using ECommons.IPC;
using ECommons.IPC.Subscribers.LifestreamIPC;

namespace AutoRetainer.Services.Lifestream;

/// <summary>
/// GetHousePathData 是我們唯一一個回傳**複合型別**的 Lifestream 呼叫。
/// 它跨外掛時,Lifestream 那頭的 HousePathData 與我們這頭的是兩個不同組件裡的型別,
/// Dalamud 在 CallGateChannel.InvokeFunc 發現型別不同時會走 ConvertObject ——
/// 也就是 Newtonsoft 的 序列化→反序列化 一趟,靠**公開欄位名對得上**才會有值。
///
/// 失敗形式很陰:欄位名對不上就回 null、Lifestream 版本不合就擲例外,而例外又被
/// SafeWrapper.AnyException 吞掉 ⇒ 兩條路都收斂成「回 null,什麼都沒說」,
/// 而呼叫端會把它讀成「這個角色沒登記房子」,完全合理地繼續跑下去。
///
/// 所以這裡在**第一次真的拿到資料**時印一行 Information(使用者跑 LogLevel 1 收得到),
/// 把「複合型別跨 IPC 不通」變成看得見的缺席:log 裡有這行=通了;沒有=沒通。
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
