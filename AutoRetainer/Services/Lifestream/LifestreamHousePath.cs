using Dalamud.Plugin.Ipc;
using ECommons.IPC;
using ECommons.IPC.Subscribers.LifestreamIPC;
using HousePathFields = (bool Found, int ResidentialDistrict, int Ward, int Plot, System.Collections.Generic.List<System.Numerics.Vector3> PathToEntrance, System.Collections.Generic.List<System.Numerics.Vector3> PathToWorkshop, bool EnableHouseEnterModeOverride, int EnterModeOverride);

namespace AutoRetainer.Services.Lifestream;

/// <summary>
/// 向 Lifestream 要某角色的個人房／部隊房路徑。只在框架執行緒呼叫（繪製與排程工作都在那裡）。
/// 先用 GetHousePathFields（只回 CoreLib 型別，Dalamud 不轉 JSON）；對方沒有這個端點（舊版）就用 GetHousePathData（每次整份轉 JSON）。
/// 新端點擲過一次例外就不再試，直到 Lifestream 重新載入（ActivePluginsChanged 帶到它）。
/// 第一次真的拿到資料時印一行 Information：log 裡有這行＝通了；沒有＝沒通。
/// </summary>
public static class LifestreamHousePath
{
    private const string FieldsEndpoint = "Lifestream.GetHousePathFields";

    private static ICallGateSubscriber<ulong, bool, HousePathFields> FieldsGate;
    private static bool Subscribed;
    // 事件可能從載入插件的執行緒送來，所以是 volatile；只有事件會把它清成 false。
    private static volatile bool FieldsFailed;
    private static bool LoggedFirstSuccess;

    public static (HousePathData Private, HousePathData FC) Get(ulong cid)
    {
        var viaFields = TryGetFields(cid, out var data);
        if(!viaFields) data = ECommonsIPC.Lifestream.GetHousePathData(cid);
        if(!LoggedFirstSuccess && (data.Private != null || data.FC != null))
        {
            LoggedFirstSuccess = true;
            PluginLog.Information($"[Lifestream IPC] HousePathData crossed the IPC boundary successfully (CID={cid:X16}, via={(viaFields ? "GetHousePathFields" : "GetHousePathData")}). Private={Describe(data.Private)}; FC={Describe(data.FC)}");
        }
        return data;
    }

    /// <summary>卸載時呼叫：解除 <see cref="IDalamudPluginInterface.ActivePluginsChanged"/> 訂閱。</summary>
    internal static void Dispose()
    {
        if(!Subscribed) return;
        Svc.PluginInterface.ActivePluginsChanged -= OnActivePluginsChanged;
        Subscribed = false;
    }

    private static bool TryGetFields(ulong cid, out (HousePathData Private, HousePathData FC) data)
    {
        data = default;
        if(FieldsFailed) return false;
        if(!Subscribed)
        {
            Svc.PluginInterface.ActivePluginsChanged += OnActivePluginsChanged;
            Subscribed = true;
        }
        FieldsGate ??= Svc.PluginInterface.GetIpcSubscriber<ulong, bool, HousePathFields>(FieldsEndpoint);
        if(!FieldsGate.HasFunction) return false;
        try
        {
            var p = FieldsGate.InvokeFunc(cid, true);
            var fc = FieldsGate.InvokeFunc(cid, false);
            data = (ToMirror(p, cid, true), ToMirror(fc, cid, false));
            return true;
        }
        catch(Exception e)
        {
            FieldsFailed = true;
            PluginLog.Warning($"[Lifestream IPC] {FieldsEndpoint} failed, using GetHousePathData until Lifestream reloads: {e.GetType().Name}: {e.Message}");
            return false;
        }
    }

    // 與舊端點逐欄相同：舊端點那兩筆是以「CID 相同且 IsPrivate 相符」挑出來的，所以這兩欄就是查詢參數。
    private static HousePathData ToMirror(in HousePathFields f, ulong cid, bool isPrivate)
        => !f.Found ? null : new HousePathData
        {
            ResidentialDistrict = f.ResidentialDistrict,
            Ward = f.Ward,
            Plot = f.Plot,
            PathToEntrance = f.PathToEntrance,
            PathToWorkshop = f.PathToWorkshop,
            IsPrivate = isPrivate,
            CID = cid,
            EnableHouseEnterModeOverride = f.EnableHouseEnterModeOverride,
            EnterModeOverride = f.EnterModeOverride,
        };

    private static void OnActivePluginsChanged(IActivePluginsChangedEventArgs args)
    {
        if(!FieldsFailed) return;
        foreach(var name in args.AffectedInternalNames)
        {
            if(name == "Lifestream")
            {
                FieldsFailed = false;
                return;
            }
        }
    }

    // ⚠️ Ward/Plot 逐字印 Lifestream 給的原值,不做 +1 —— 它是 0 起算還是 1 起算沒有離線證據,
    // 而這行的用途是「證明資料過得來」,不是給使用者當門牌看。
    private static string Describe(HousePathData d)
        => d == null
            ? "null"
            : $"[district={d.ResidentialDistrict} ward={d.Ward} plot={d.Plot} raw; entrancePath={d.PathToEntrance?.Count ?? 0}pts workshopPath={d.PathToWorkshop?.Count ?? 0}pts]";
}
