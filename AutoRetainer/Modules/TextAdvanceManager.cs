using AutoRetainer.Modules.Voyage;
using ECommons.EzSharedDataManager;

namespace AutoRetainer.Modules;

internal static class TextAdvanceManager
{
    private static bool WasChanged = false;

    private static bool IsBusy => (Utils.IsBusy || VoyageScheduler.Enabled || MultiMode.HoldsOtherAutomationForPanelResume) && !SchedulerMain.CharacterPostProcessLocked;

    /// <summary>true＝已經要求 TextAdvance 暫停（之後哪一次 Tick 看到不忙才解除）。</summary>
    internal static bool IsLocked => WasChanged;
    /// <summary>每幀呼叫（框架執行緒）。持鎖期間每幀補回名字：TextAdvance 重新載入會清空名單、也不檢查持有者還在不在。</summary>
    internal static void Tick()
    {
        if(WasChanged)
        {
            if(!IsBusy)
            {
                WasChanged = false;
                UnlockTA();
                PluginLog.Debug($"TextAdvance unlocked");
            }
            else
            {
                Reassert();
            }
        }
        else
        {
            if(IsBusy)
            {
                WasChanged = true;
                LockTA();
                PluginLog.Debug($"TextAdvance locked");
            }
        }
    }

    /// <summary>
    /// MiniTA 點 Talk 前呼叫：持鎖中而名字不見了就當場補回並回 false（這一幀不點）。
    /// TextAdvance 每幀先決定自己要不要點、之後才看名單，補回那一幀它可能已經放行自己的點擊。
    /// </summary>
    internal static bool MayClickTalk() => !WasChanged || !Reassert();

    /// <returns>true＝名字原本不在、這次才補回。</returns>
    private static bool Reassert()
    {
        if(!LockTA()) return false;
        PluginLog.Information($"[TextAdvanceLock] AutoRetainer 的 TextAdvance 暫停登記被移除了（TextAdvance 重新載入或手動解除），已補回。");
        return true;
    }

    /// <returns>true＝名字原本不在、這次才加進去。</returns>
    internal static bool LockTA()
    {
        // EzSharedData 會快取取到的集合，重複呼叫不會在 Dalamud DataShare 多登記使用者。
        if(EzSharedData.TryGet<HashSet<string>>("TextAdvance.StopRequests", out var data))
        {
            return data.Add(P.Name);
        }
        return false;
    }

    internal static void UnlockTA()
    {
        if(EzSharedData.TryGet<HashSet<string>>("TextAdvance.StopRequests", out var data))
        {
            data.Remove(P.Name);
        }
    }
}
