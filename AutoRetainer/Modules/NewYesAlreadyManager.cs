using AutoRetainer.Modules.Voyage;
using ECommons.EzSharedDataManager;

namespace AutoRetainer.Modules;

internal static class NewYesAlreadyManager
{
    private static bool WasChanged = false;

    private static bool IsBusy => (Utils.IsBusy || VoyageScheduler.Enabled || MultiMode.HoldsOtherAutomationForPanelResume) && !SchedulerMain.CharacterPostProcessLocked;

    /// <summary>true＝已經要求 YesAlready 暫停（之後哪一次 Tick 看到不忙才解除）。</summary>
    internal static bool IsLocked => WasChanged;
    /// <summary>每幀呼叫（框架執行緒）。持鎖期間每幀補回名字：YesAlready 看門狗會移除掛超過逾時上限的名字，只在邊緣加一次會默默失效。</summary>
    internal static void Tick()
    {
        if(WasChanged)
        {
            if(!IsBusy)
            {
                WasChanged = false;
                Unlock();
                PluginLog.Debug($"YesAlready unlocked");
            }
            else if(Lock())
            {
                PluginLog.Information($"[YesAlreadyLock] AutoRetainer 的 YesAlready 暫停登記被移除了（YesAlready 看門狗逾時、重新載入或手動解除），已補回。");
            }
        }
        else
        {
            if(IsBusy)
            {
                WasChanged = true;
                Lock();
                PluginLog.Debug($"YesAlready locked");
            }
        }
    }

    /// <returns>true＝名字原本不在、這次才加進去。</returns>
    internal static bool Lock()
    {
        // EzSharedData 會快取取到的集合，重複呼叫不會在 Dalamud DataShare 多登記使用者。
        if(EzSharedData.TryGet<HashSet<string>>("YesAlready.StopRequests", out var data))
        {
            return data.Add(P.Name);
        }
        return false;
    }

    internal static void Unlock()
    {
        if(EzSharedData.TryGet<HashSet<string>>("YesAlready.StopRequests", out var data))
        {
            data.Remove(P.Name);
        }
    }

    internal static bool? WaitForYesAlreadyDisabledTask()
    {
        if(EzSharedData.TryGet<HashSet<string>>("YesAlready.StopRequests", out var data))
        {
            return data.Contains(P.Name);
        }
        return true;
    }
}
