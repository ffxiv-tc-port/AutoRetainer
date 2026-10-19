using AutoRetainerAPI;
using Dalamud.Plugin.Ipc;

namespace AutoRetainer.Helpers;

/// <summary>
/// 每幀發送的「畫 UI」IPC 事件提供端：Dalamud 的 <c>GetIpcProvider</c> 每次都 new 一個包裝，這裡留著重用。
/// 只在框架執行緒（Draw）呼叫。
/// </summary>
/// <remarks>
/// 包裝建構時就綁死頻道；沒人用的頻道可能被 Dalamud 資料視窗的「清除空頻道」移除、下次訂閱時重建，
/// 舊包裝就送進孤兒頻道。所以沒有訂閱者時每秒重取一次；有訂閱者的頻道不會被清除。
/// </remarks>
internal sealed class DrawEventProvider<T> where T : class, ICallGateProvider
{
    private const long RefreshIntervalMs = 1000;
    private readonly Func<T> Factory;
    private T Provider;
    private long RefreshAt;

    internal DrawEventProvider(Func<T> factory) => Factory = factory;

    /// <summary>有訂閱者時回包裝，否則回 null（沒有訂閱者時 <c>SendMessage</c> 本來就什麼都不做）。</summary>
    internal T GetIfSubscribed()
    {
        var now = Environment.TickCount64;
        if(Provider == null || (Provider.SubscriptionCount == 0 && now >= RefreshAt))
        {
            Provider = Factory();
            RefreshAt = now + RefreshIntervalMs;
        }
        return Provider.SubscriptionCount > 0 ? Provider : null;
    }
}

internal static class DrawEventProviders
{
    internal static readonly DrawEventProvider<ICallGateProvider<object>> MainControlsDraw =
        new(static () => Svc.PluginInterface.GetIpcProvider<object>(ApiConsts.OnMainControlsDraw));

    internal static readonly DrawEventProvider<ICallGateProvider<object>> RetainerListTaskButtonsDraw =
        new(static () => Svc.PluginInterface.GetIpcProvider<object>(ApiConsts.OnRetainerListTaskButtonsDraw));

    internal static readonly DrawEventProvider<ICallGateProvider<ulong, string, object>> RetainerSettingsDraw =
        new(static () => Svc.PluginInterface.GetIpcProvider<ulong, string, object>(ApiConsts.OnRetainerSettingsDraw));

    internal static readonly DrawEventProvider<ICallGateProvider<ulong, string, object>> RetainerPostVentureTaskDraw =
        new(static () => Svc.PluginInterface.GetIpcProvider<ulong, string, object>(ApiConsts.OnRetainerPostVentureTaskDraw));
}
