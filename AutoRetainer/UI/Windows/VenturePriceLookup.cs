namespace AutoRetainer.UI.Windows;

/// <summary>
/// 探險瀏覽器「查詢價格」交給誰：TC Toolbox「僱員探險收益」模組開著就交給它，否則 MarketBoardPlugin（/pmb），都沒有就不畫按鈕。
/// </summary>
/// <remarks>
/// 只在繪製執行緒讀寫。插件清單每秒最多重查一次（走訪一次會配置）；載入與否即時讀 <c>IExposedPlugin.IsLoaded</c>。
/// TC Toolbox 的端點只在模組啟用時註冊，所以 <c>HasFunction</c>＝模組開著。
/// </remarks>
internal static class VenturePriceLookup
{
    internal enum Target
    {
        None,
        TcToolbox,
        MarketBoardPlugin,
    }

    private const string ShowItemEndpoint = "TCToolbox.VentureValue.ShowItem";
    private const long RecheckIntervalMs = 1000;

    private static long NextCheck;
    private static Target Cached;

    internal static Target Current
    {
        get
        {
            var now = Environment.TickCount64;
            if(now >= NextCheck)
            {
                NextCheck = now + RecheckIntervalMs;
                Cached = Resolve();
            }
            return Cached;
        }
    }

    private static Target Resolve()
    {
        var tcToolbox = false;
        var marketBoard = false;
        foreach(var x in Svc.PluginInterface.InstalledPlugins)
        {
            if(!x.IsLoaded) continue;
            if(x.InternalName == "TCToolbox") tcToolbox = true;
            else if(x.InternalName == "MarketBoardPlugin") marketBoard = true;
        }
        if(tcToolbox && Svc.PluginInterface.GetIpcSubscriber<uint, bool>(ShowItemEndpoint).HasFunction) return Target.TcToolbox;
        return marketBoard ? Target.MarketBoardPlugin : Target.None;
    }

    internal static string Tooltip(Target target) => target == Target.TcToolbox
        ? Loc.T("Opens TC Toolbox's Retainer Venture Value window at this item.")
        : Loc.T("Looks the item up with the Market Board plugin (/pmb).");

    internal static void Open(Target target, uint itemId)
    {
        if(target == Target.TcToolbox)
        {
            try
            {
                if(Svc.PluginInterface.GetIpcSubscriber<uint, bool>(ShowItemEndpoint).InvokeFunc(itemId)) return;
                PluginLog.Information($"[VentureBrowser] TC Toolbox declined the price lookup for item {itemId}");
            }
            catch(Exception e)
            {
                PluginLog.Information($"[VentureBrowser] TC Toolbox price lookup failed for item {itemId}: {e.GetType().Name} {e.Message}");
            }
            NextCheck = 0;
            return;
        }
        if(target == Target.MarketBoardPlugin) Svc.Commands.ProcessCommand($"/pmb {itemId}");
    }
}
