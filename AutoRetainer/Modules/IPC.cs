using AutoRetainerAPI;
using AutoRetainerAPI.Configuration;
using ECommons.EzIpcManager;
using ECommons.Reflection;
using System.Reflection;

namespace AutoRetainer.Modules;

internal static class IPC
{
    private static void Log(string s)
    {
        DebugLog($"[IPC] {s}");
    }

    /// <summary>舊端點 <c>AutoRetainer.SetSuppressed</c> 寫的那個無主布林。</summary>
    /// <remarks>
    /// 🔴 語意與行為都<b>沒有</b>改變，只是從 <c>Suppressed</c> 這個名字底下搬出來，
    /// 好讓它與 <see cref="SuppressionLeases"/>（具名、可計數、會逾時的租約）並存。
    /// 舊端點是無主的：誰都可以寫、誰寫的最後一次算數 —— 所以新的消費端請改用租約端點。
    /// </remarks>
    internal static bool ManualSuppressed = false;

    /// <summary>AutoRetainer 的自動化現在是不是被壓制著。</summary>
    /// <remarks>
    /// 讀＝「舊的無主布林」<b>或</b>「還有任何一筆有效租約」。寫＝只寫舊的無主布林
    /// （所以既有呼叫點 <c>IPC.Suppressed = false</c> 的語意逐字不變：它清的是自己那一份，
    /// 清不掉別人的租約 —— 這正是無主布林原本互相踩踏的地方）。
    /// </remarks>
    internal static bool Suppressed
    {
        get => ManualSuppressed || SuppressionLeases.AnyActive;
        set => ManualSuppressed = value;
    }

    internal static void Init()
    {
        Log("IPC init");
        Svc.PluginInterface.GetIpcProvider<object>("AutoRetainer.Init").RegisterAction(() => { });
        Svc.PluginInterface.GetIpcProvider<bool>("AutoRetainer.GetSuppressed").RegisterFunc(GetSuppressed);
        Svc.PluginInterface.GetIpcProvider<bool, object>("AutoRetainer.SetSuppressed").RegisterAction(SetSuppressed);
        Svc.PluginInterface.GetIpcProvider<string, bool>("AutoRetainer.AcquireSuppression").RegisterFunc(AcquireSuppression);
        Svc.PluginInterface.GetIpcProvider<string, bool>("AutoRetainer.ReleaseSuppression").RegisterFunc(ReleaseSuppression);
        Svc.PluginInterface.GetIpcProvider<bool>("AutoRetainer.GetMultiModeEnabled").RegisterFunc(GetMultiModeEnabled);
        Svc.PluginInterface.GetIpcProvider<bool, object>("AutoRetainer.SetMultiModeEnabled").RegisterAction(SetMultiModeEnabled);
        Svc.PluginInterface.GetIpcProvider<bool>("AutoRetainer.IsBusy").RegisterFunc(GetIsBusy);
        Svc.PluginInterface.GetIpcProvider<uint, object>("AutoRetainer.SetVenture").RegisterAction(SetVenture);
        Svc.PluginInterface.GetIpcProvider<ulong, OfflineCharacterData>("AutoRetainer.GetOfflineCharacterData").RegisterFunc(GetOCD);
        Svc.PluginInterface.GetIpcProvider<OfflineCharacterData, object>("AutoRetainer.WriteOfflineCharacterData").RegisterAction(SetOCD);
        Svc.PluginInterface.GetIpcProvider<ulong, string, AdditionalRetainerData>("AutoRetainer.GetAdditionalRetainerData").RegisterFunc(GetARD);
        Svc.PluginInterface.GetIpcProvider<ulong, string, AdditionalRetainerData, object>("AutoRetainer.WriteAdditionalRetainerData").RegisterAction(SetARD);
        Svc.PluginInterface.GetIpcProvider<List<ulong>>("AutoRetainer.GetRegisteredCIDs").RegisterFunc(GetRegisteredCIDs);
        Svc.PluginInterface.GetIpcProvider<string, object>(ApiConsts.RequestRetainerPostProcess).RegisterAction(RequestRetainerPostprocess);
        Svc.PluginInterface.GetIpcProvider<object>(ApiConsts.FinishRetainerPostprocessRequest).RegisterAction(FinishRetainerPostprocessRequest);
        Svc.PluginInterface.GetIpcProvider<string, object>(ApiConsts.RequestCharacterPostProcess).RegisterAction(RequestCharacterPostprocess);
        Svc.PluginInterface.GetIpcProvider<object>(ApiConsts.FinishCharacterPostprocessRequest).RegisterAction(FinishCharacterPostprocessRequest);
        Svc.PluginInterface.GetIpcProvider<string, object>(ApiConsts.OnRetainerListCustomTask).RegisterAction(OnRetainerListCustomTask);
        EzIPC.Init(typeof(IPC));
    }

    private static void OnRetainerListCustomTask(string s)
    {
        P.RetainerListOverlay.PluginToProcess = s;
    }

    internal static void Shutdown()
    {
        Log("IPC Shutdown");
        Svc.PluginInterface.GetIpcProvider<object>("AutoRetainer.Init").UnregisterAction();
        Svc.PluginInterface.GetIpcProvider<bool>("AutoRetainer.GetSuppressed").UnregisterFunc();
        Svc.PluginInterface.GetIpcProvider<bool, object>("AutoRetainer.SetSuppressed").UnregisterAction();
        Svc.PluginInterface.GetIpcProvider<string, bool>("AutoRetainer.AcquireSuppression").UnregisterFunc();
        Svc.PluginInterface.GetIpcProvider<string, bool>("AutoRetainer.ReleaseSuppression").UnregisterFunc();
        SuppressionLeases.ReleaseAll("AutoRetainer 正在卸載");
        Svc.PluginInterface.GetIpcProvider<bool>("AutoRetainer.GetMultiModeEnabled").UnregisterFunc();
        Svc.PluginInterface.GetIpcProvider<bool, object>("AutoRetainer.SetMultiModeEnabled").UnregisterAction();
        Svc.PluginInterface.GetIpcProvider<bool>("AutoRetainer.IsBusy").UnregisterFunc();
        Svc.PluginInterface.GetIpcProvider<uint, object>("AutoRetainer.SetVenture").UnregisterAction();
        Svc.PluginInterface.GetIpcProvider<ulong, OfflineCharacterData>("AutoRetainer.GetOfflineCharacterData").UnregisterFunc();
        Svc.PluginInterface.GetIpcProvider<OfflineCharacterData, object>("AutoRetainer.WriteOfflineCharacterData").UnregisterAction();
        Svc.PluginInterface.GetIpcProvider<ulong, string, AdditionalRetainerData>("AutoRetainer.GetAdditionalRetainerData").UnregisterFunc();
        Svc.PluginInterface.GetIpcProvider<ulong, string, AdditionalRetainerData, object>("AutoRetainer.WriteAdditionalRetainerData").UnregisterAction();
        Svc.PluginInterface.GetIpcProvider<List<ulong>>("AutoRetainer.GetRegisteredCIDs").UnregisterFunc();
        Svc.PluginInterface.GetIpcProvider<string, object>(ApiConsts.RequestRetainerPostProcess).UnregisterAction();
        Svc.PluginInterface.GetIpcProvider<object>(ApiConsts.FinishRetainerPostprocessRequest).UnregisterAction();
        Svc.PluginInterface.GetIpcProvider<string, object>(ApiConsts.RequestCharacterPostProcess).UnregisterAction();
        Svc.PluginInterface.GetIpcProvider<object>(ApiConsts.FinishCharacterPostprocessRequest).UnregisterAction();
        Svc.PluginInterface.GetIpcProvider<string, object>(ApiConsts.OnRetainerListCustomTask).UnregisterAction();
    }

    private static void FinishRetainerPostprocessRequest()
    {
        Log("Received retainer postprocess request finish");
        SchedulerMain.RetainerPostProcessLocked = false;
    }

    private static void FinishCharacterPostprocessRequest()
    {
        Log("Received character postprocess request finish");
        SchedulerMain.CharacterPostProcessLocked = false;
    }

    private static void RequestRetainerPostprocess(string pluginName)
    {
        if(SchedulerMain.RetainerPostprocess.Contains(pluginName))
        {
            throw new Exception($"Retainer Postprocess request from {pluginName} already exist");
        }
        SchedulerMain.RetainerPostprocess = SchedulerMain.RetainerPostprocess.Add(pluginName);
        Log($"Retainer Postprocess requested from {pluginName}");
    }

    private static void RequestCharacterPostprocess(string pluginName)
    {
        if(SchedulerMain.CharacterPostprocess.Contains(pluginName))
        {
            throw new Exception($"Character Postprocess request from {pluginName} already exist");
        }
        SchedulerMain.CharacterPostprocess = SchedulerMain.CharacterPostprocess.Add(pluginName);
        Log($"Character Postprocess requested from {pluginName}");
    }

    private static List<ulong> GetRegisteredCIDs()
    {
        return C.OfflineData.Where(x => !C.Blacklist.Any(z => z.CID == x.CID) && !x.Name.EqualsAny("Unknown", "")).Select(x => x.CID).ToList();
    }

    private static OfflineCharacterData GetOCD(ulong CID)
    {
        return C.OfflineData.FirstOrDefault(x => x.CID == CID);
    }

    private static void SetOCD(OfflineCharacterData OCD)
    {
        var index = C.OfflineData.IndexOf(x => x.CID == OCD.CID);
        if(index != -1)
        {
            //C.OfflineData[index] = OCD;
            var data = C.OfflineData[index];
            foreach(var field in OCD.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if(data.GetFoP(field.Name) != null)
                {
                    data.SetFoP(field.Name, field.GetValue(OCD));
                    PluginLog.Verbose($"Setting {field.Name} to {field.GetValue(data)}");
                }
            }
        }
        else
        {
            C.OfflineData.Add(OCD);
        }
    }

    private static AdditionalRetainerData GetARD(ulong cid, string name)
    {
        return Utils.GetAdditionalData(cid, name);
    }

    private static void SetARD(ulong cid, string name, AdditionalRetainerData data)
    {
        var x = C.AdditionalData[Utils.GetAdditionalDataKey(cid, name)];
        foreach(var field in data.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            if(x.GetFoP(field.Name) != null)
            {
                x.SetFoP(field.Name, field.GetValue(data));
                PluginLog.Verbose($"Setting {field.Name} to {field.GetValue(data)}");
            }
        }
    }

    private static void SetVenture(uint VentureID)
    {
        SchedulerMain.VentureOverride = VentureID;
        DebugLog($"Received venture override to {VentureID} / {VentureUtils.GetVentureName(VentureID)} via IPC");
    }

    private static bool GetSuppressed()
    {
        return Suppressed;
    }

    private static void SetSuppressed(bool s)
    {
        Suppressed = s;
    }

    /// <summary>取得（或續租）一筆具名的壓制租約。<b>這是新的消費端該用的端點。</b></summary>
    /// <remarks>
    /// 🔴 與 <c>SetSuppressed</c> 的差別就是「誰先結束誰就把別人的壓制解除」這個 bug 的解法：
    /// 每個租用者一筆租約，<b>全部還完</b>壓制才真的解除。<br/>
    /// 🔴 租約會逾時（<see cref="SuppressionLeases.LeaseTimeoutMs"/>），租用者必須週期性重新呼叫本端點續租
    /// （建議間隔 <see cref="SuppressionLeases.RenewIntervalHintMs"/>）—— 這同時也是「AutoRetainer 比我晚載入」時的重試機會。<br/>
    /// 📌 回傳值是「這一刻你確實持有租約嗎」。呼叫端拿不到 AutoRetainer（IPC 不存在）時應該<b>照現況跑</b>，
    /// 不要卡住自己的流程。
    /// </remarks>
    /// <param name="owner">租用者識別字串，慣例是對方外掛的 InternalName。</param>
    private static bool AcquireSuppression(string owner) => SuppressionLeases.Acquire(owner);

    /// <summary>歸還一筆具名的壓制租約。</summary>
    /// <returns>呼叫完之後該租用者確定不再持有租約（本來就沒有也算 <c>true</c>）。</returns>
    private static bool ReleaseSuppression(string owner) => SuppressionLeases.Release(owner);

    private static bool GetMultiModeEnabled()
    {
        return MultiMode.Enabled;
    }

    private static void SetMultiModeEnabled(bool s)
    {
        MultiMode.Enabled = s;
        MultiMode.OnMultiModeEnabled();
    }

    /// <summary>
    /// 「AutoRetainer 正在驅動雇員自動化」的唯讀狀態，給市場板類外掛
    /// （如 Marketbuddy）做傳喚鈴互斥用：
    /// PluginEnabled＝鈴自動化已武裝（開著就會在鈴開啟時接手，含
    /// IPC.Suppressed 尊重）、MultiMode.Active＝多角色模式執行期狀態、
    /// TaskManager.IsBusy＝任務引擎正在執行。純暴露狀態，零行為變更。
    /// </summary>
    private static bool GetIsBusy()
    {
        return SchedulerMain.PluginEnabled || MultiMode.Active || P.TaskManager.IsBusy;
    }

    internal static void FireSendRetainerToVentureEvent(string retainer)
    {
        Log($"Firing FireSendRetainerToVentureEvent for {retainer}");
        Svc.PluginInterface.GetIpcProvider<string, object>(ApiConsts.OnSendRetainerToVenture).SendMessage(retainer);
    }

    internal static void FireRetainerPostprocessTaskRequestEvent(string retainer)
    {
        Log($"Firing FireRetainerPostprocessTaskRequestEvent for {retainer}");
        Svc.PluginInterface.GetIpcProvider<string, object>(ApiConsts.OnRetainerAdditionalTask).SendMessage(retainer);
    }

    internal static void FireRetainerPostprocessEvent(string pluginName, string retainer)
    {
        Log($"Firing FireRetainerPostprocessEvent for {retainer} for plugin {pluginName}");
        Svc.PluginInterface.GetIpcProvider<string, string, object>(ApiConsts.OnRetainerReadyForPostprocess).SendMessage(pluginName, retainer);
    }

    internal static void FireCharacterPostprocessTaskRequestEvent()
    {
        Log($"Firing FireCharacterPostprocessTaskRequestEvent");
        Svc.PluginInterface.GetIpcProvider<object>(ApiConsts.OnCharacterAdditionalTask).SendMessage();
    }

    internal static void FireCharacterPostprocessEvent(string pluginName)
    {
        Log($"Firing FireCharacterPostprocessEvent for plugin {pluginName}");
        Svc.PluginInterface.GetIpcProvider<string, object>(ApiConsts.OnCharacterReadyForPostprocess).SendMessage(pluginName);
    }
}
