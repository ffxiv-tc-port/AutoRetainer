using ECommons.EzIpcManager;

namespace AutoRetainer.Modules.EzIPCManagers;

/// <summary>
/// 🔴 這兩支端點都是<b>在呼叫端的執行緒上</b>執行的，而它們底下一支讀原生
/// （<c>PlayerState.Instance()-&gt;GrandCompany</c>）、一支往 <c>P.TaskManager.Tasks</c>
/// （裸 <c>List&lt;T&gt;</c>，framework 執行緒每幀在增刪）塞五個任務，所以兩支都經
/// <see cref="IpcFrameworkGate"/>。<b>已經在 framework 執行緒上呼叫時是就地執行，行為逐字不變。</b>
/// </summary>
public class IPC_GCContinuation
{
    public IPC_GCContinuation()
    {
        EzIPC.Init(this, $"{Svc.PluginInterface.InternalName}.GC");
    }

    [EzIPC]
    public void EnqueueInitiation()
    {
        IpcFrameworkGate.Run(nameof(EnqueueInitiation), () => GCContinuation.EnqueueInitiation(true));
    }

    [EzIPC]
    public GCInfo? GetGCInfo()
    {
        // ⚠️ null 本來就同時是「這個角色沒有大公司」的答案，逾時沿用它不會引進新語意，
        //    但也因此呼叫端分不出兩者 —— 逾時那一刻會另外寫一行 Information。
        return IpcFrameworkGate.Run<GCInfo?>(nameof(GetGCInfo), GCContinuation.GetGCInfo, null,
            "the caller is told the character has no Grand Company");
    }
}
