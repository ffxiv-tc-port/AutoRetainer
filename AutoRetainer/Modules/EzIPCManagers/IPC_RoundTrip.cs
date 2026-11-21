using AutoRetainer.Modules.Multi;
using ECommons.EzIpcManager;

namespace AutoRetainer.Modules.EzIPCManagers;

/// <summary>
/// 「跑一輪就回來」（<see cref="MultiModeRoundTrip"/>）給別的插件與巨集用的端點，前綴 <c>AutoRetainer.RoundTrip.</c>。
/// </summary>
/// <remarks>
/// <para>🔴 端點在呼叫端的執行緒上執行：會改狀態的 <see cref="Start"/>、<see cref="Cancel"/> 經 <see cref="IpcFrameworkGate"/>；
/// 三支查詢只讀 volatile 欄位或鎖住的結果表，刻意不經閘門（巨集每幾秒輪詢一次，不必每次等一幀）。</para>
/// <para>編號：<see cref="Start"/> 回的正數。用它查結果、取消，才不會動到使用者自己在主視窗按的那一趟。</para>
/// <para>舊版 AutoRetainer 沒有這組端點：呼叫端先問 <see cref="ApiVersion"/>，拿不到（0）就退回自己的做法。</para>
/// </remarks>
public class IPC_RoundTrip
{
    public IPC_RoundTrip()
    {
        EzIPC.Init(this, $"{Svc.PluginInterface.InternalName}.RoundTrip");
    }

    /// <summary>這組端點的版本；目前 1。</summary>
    [EzIPC]
    public int ApiVersion() => 1;

    /// <summary>跟主視窗按「跑一輪就回來」一樣，回到的是呼叫當下的角色。</summary>
    /// <returns>
    /// 正數＝開始了，這一趟的編號；-1＝已經有一趟在跑（不論誰開的）；-2＝多角色模式已經開著；
    /// -3＝沒登入或目前角色不在角色清單裡；0＝框架執行緒沒回應，開了沒有不確定（再查 <see cref="ActiveId"/>）。
    /// </returns>
    [EzIPC]
    public int Start()
    {
        return IpcFrameworkGate.Run(nameof(Start),
            () => MultiModeRoundTrip.Start("IPC AutoRetainer.RoundTrip.Start", MultiModeChangeCause.OtherPlugin), 0,
            "the caller is told it is unknown whether a round trip started");
    }

    /// <summary>進行中那一趟的編號（不論誰開的）；沒有進行中＝0。</summary>
    [EzIPC]
    public int ActiveId() => MultiModeRoundTrip.ActiveId;

    /// <summary>空字串＝沒有進行中；"running"＝多角色模式在處理；"returning"＝做完了、正在換回原角色。</summary>
    [EzIPC]
    public string ActivePhase() => MultiModeRoundTrip.ActivePhaseCode;

    /// <summary>
    /// 編號 <paramref name="id"/> 那一趟怎麼結束的。空字串＝還在跑，或不認得這個編號（插件重載前的、或已被後面八趟擠掉）。
    /// 其餘：returned（人在原角色、多角色已關）、return_timeout／return_missing（做完了但沒換回）、
    /// cancelled_multi_off／cancelled_multi_on／cancelled_relog／cancelled_user／cancelled_stop／cancelled_ipc。
    /// </summary>
    [EzIPC]
    public string Result(int id) => MultiModeRoundTrip.TryGetResult(id, out var code) ? code : "";

    /// <summary>只取消編號相符、還在進行的那一趟；不關多角色模式、不停其他功能。</summary>
    /// <returns>true＝取消了；false＝不是進行中的那一趟，或框架執行緒沒回應。</returns>
    [EzIPC]
    public bool Cancel(int id)
    {
        return IpcFrameworkGate.Run(nameof(Cancel),
            () => MultiModeRoundTrip.CancelIfActive(id, "IPC AutoRetainer.RoundTrip.Cancel"), false,
            "the caller is told nothing was cancelled");
    }
}
