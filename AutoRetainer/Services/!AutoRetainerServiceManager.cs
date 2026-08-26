using AutoRetainer.Modules.EzIPCManagers;
using AutoRetainer.Services.Lifestream;
using AutoRetainer.UI.NeoUI;
using AutoRetainer.UI.Overlays;
using AutoRetainer.UI.Statistics;

namespace AutoRetainer.Services;
public static class AutoRetainerServiceManager
{
    public static NeoWindow NeoWindow;
    public static EzIPCManager EzIPCManager;
    public static FCPointsUpdater FCPointsUpdater;
    public static FcDataManager FCData;
    public static GilDisplayManager GilDisplay;
    public static VentureStatsManager VentureStats;
    // Lifestream 的主要介面已改用 ECommons.IPC 套件的 ECommonsIPC.Lifestream。
    // 這裡只留套件給不了的那幾個成員(事件 + 收藏傳送 + 兩個綁不上自訂 delegate 的呼叫)。
    public static LifestreamExtraIPC LifestreamExtra;
    //public static EventLogger EventLogger;
    public static AutoBuyFuelOverlay AutoBuyFuelOverlay;
    public static TitleScreenButton TitleScreenButton;
    public static AddonWatcher AddonWatcher;
    public static DataMigrator DataMigrator;
}
