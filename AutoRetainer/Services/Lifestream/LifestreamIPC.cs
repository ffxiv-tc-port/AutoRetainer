using ECommons.EzIpcManager;

namespace AutoRetainer.Services.Lifestream;
public class LifestreamIPC
{
    private LifestreamIPC()
    {
        EzIPC.Init(this, "Lifestream", SafeWrapper.AnyException);
    }

    [EzIPC] public Func<uint, byte, bool> Teleport;
    [EzIPC] public Func<bool> TeleportToHome;
    [EzIPC] public Func<bool> TeleportToFC;
    [EzIPC] public Func<bool> TeleportToApartment;
    [EzIPC] public Func<bool> IsBusy;
    /// <summary>
    /// city aetheryte id
    /// </summary>
    [EzIPC] public Func<int, uint> GetResidentialTerritory;
    /// <summary>
    /// content id
    /// </summary>
    [EzIPC] public Func<ulong, (HousePathData Private, HousePathData FC)> GetHousePathData;
    /// <summary>
    /// territory, plot
    /// </summary>
    [EzIPC] public Func<uint, int, Vector3?> GetPlotEntrance;
    /// <summary>
    /// type(home=1, fc=2, apartment=3), mode(enter house=2)
    /// </summary>
    [EzIPC] public Action<int, int?> EnqueuePropertyShortcut;
    [EzIPC] public Func<(int Kind, int Ward, int Plot)?> GetCurrentPlotInfo;

    [EzIPCEvent]
    public void OnHouseEnterError()
    {
        PluginLog.Warning($"Received house enter error from Lifestream. Current character will be excluded from multi mode.");
        if(Data != null)
        {
            Data.Enabled = false;
            Data.WorkshopEnabled = false;
        }
    }

    [EzIPC] public Action<int?> EnqueueInnShortcut;
    [EzIPC] public Func<bool?> HasApartment;
    [EzIPC] public Action<bool> EnterApartment;
    [EzIPC] public Func<bool?> HasPrivateHouse;
    [EzIPC] public Func<bool?> HasFreeCompanyHouse;
    [EzIPC] public Func<bool> CanMoveToWorkshop;
    [EzIPC] public Action MoveToWorkshop;
    [EzIPC] public Action<string> ExecuteCommand;

    // 使用者在傳送面板收藏好的地點。用它當導航目標比自組路線安全:收藏項都是既知的乙太之光/
    // 乙太網點,走的是面板按鈕本來就在走的那條路。⚠️ Id 與 SubIndex 要一起帶,同一個 id 可能對到多筆。
    [EzIPC] public Func<List<(uint Id, byte SubIndex, string Name, uint Territory)>> GetTeleportFavorites;
    [EzIPC] public Func<uint, byte, bool> TeleportToFavorite;
}
