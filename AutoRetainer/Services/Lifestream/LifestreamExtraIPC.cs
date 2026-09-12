using ECommons.EzIpcManager;
using ECommons.IPC.Subscribers.LifestreamIPC;

namespace AutoRetainer.Services.Lifestream;

/// <summary>
/// ECommons.IPC 套件的 LifestreamIPC 已經涵蓋我們大部分的 Lifestream 呼叫,但它是 sealed 的,
/// 無法用繼承補東西。這個側車類用同一個 prefix "Lifestream" 各自 EzIPC.Init,與套件實例並存,收容兩類套件給不了的成員。
/// ⚠ 但「刪掉改用套件實例」仍未實機驗證。要刪請先在實機確認 TaskTeleportToProperty 與 TaskNeoHET 兩個呼叫點仍然動作。
/// </summary>
public class LifestreamExtraIPC
{
    private LifestreamExtraIPC()
    {
        // 維持我方一貫的 AnyException(靜默降級)。套件的 IPCBase 預設是 SafeWrapper.None,
        // 那會把「Lifestream 沒裝」從回傳預設值變成往外擲例外——刻意不採。
        EzIPC.Init(this, "Lifestream", SafeWrapper.AnyException);
    }

    // ---- (甲)套件沒有的成員 ----

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

    // 使用者在傳送面板收藏好的地點。用它當導航目標比自組路線安全:收藏項都是既知的乙太之光/
    // 乙太網點,走的是面板按鈕本來就在走的那條路。⚠️ Id 與 SubIndex 要一起帶,同一個 id 可能對到多筆。
    [EzIPC] public Func<List<(uint Id, byte SubIndex, string Name, uint Territory)>> GetTeleportFavorites;
    [EzIPC] public Func<uint, byte, bool> TeleportToFavorite;

    // ---- (乙)套件有、但本版 ECommons 的 EzIPC 綁不上自訂 delegate,故沿用原形狀 ----

    [EzIPC] public Action<PropertyType, HouseEnterMode?> EnqueuePropertyShortcut;

    [EzIPC] public Action MoveToWorkshop;
}
