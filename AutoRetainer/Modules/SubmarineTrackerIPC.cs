using Dalamud.Plugin.Ipc.Exceptions;

namespace AutoRetainer.Modules;

/// <summary>
/// 唯讀橋接到潛水艇追蹤器（SubmarineTracker）：問「這件物品會在哪幾個點位撈到」。
/// 🔴 零組件相依——只用 Dalamud 原生 CallGate 的字串契約；對方沒裝或契約版本不合
/// 就一律當作不可用，呼叫端只會得到「按鈕灰掉」而不是例外。
/// </summary>
/// <remarks>
/// 🔴 只從繪製／framework 執行緒呼叫：IPC 實作跑在呼叫端的執行緒上，而下面的快取沒有鎖。
/// 端點名與簽章逐字取自 SubmarineTracker 的 <c>IPC/IpcProvider.cs</c>。
/// </remarks>
internal static class SubmarineTrackerIPC
{
    /// <summary>本端認得的契約版本。對方回別的數字就不碰其他端點。</summary>
    internal const int RequiredApiVersion = 1;

    internal const string TagApiVersion = "SubmarineTracker.ApiVersion";
    internal const string TagSectorsForItem = "SubmarineTracker.GetSectorsForItem";

    /// <summary>重探間隔。對方可以在遊戲中途才被啟用，所以「不可用」不是永久結論。</summary>
    private const int ProbeIntervalMs = 5000;

    private static long NextProbeAt;
    private static bool Available;
    private static bool FailureReported;

    /// <summary>對方最後回報的契約版本；-1＝問不到。畫在畫面上讓「版本不合」看得見。</summary>
    internal static int LastSeenVersion { get; private set; } = -1;

    /// <summary>對方在不在、版本合不合。每 <see cref="ProbeIntervalMs"/> 毫秒重探一次，其餘的幀讀快取。</summary>
    internal static bool IsAvailable
    {
        get
        {
            if(Environment.TickCount64 < NextProbeAt) return Available;
            NextProbeAt = Environment.TickCount64 + ProbeIntervalMs;
            try
            {
                LastSeenVersion = Svc.PluginInterface.GetIpcSubscriber<int>(TagApiVersion).InvokeFunc();
                Available = LastSeenVersion == RequiredApiVersion;
            }
            catch(IpcNotReadyError)
            {
                // 沒安裝／還沒載入是正常狀態，不寫 log：這條每 5 秒會走一次。
                LastSeenVersion = -1;
                Available = false;
            }
            catch(Exception e)
            {
                LastSeenVersion = -1;
                Available = false;
                if(FailureReported) return Available;
                FailureReported = true;
                PluginLog.Information($"[SubmarineTracker] 探測 {TagApiVersion} 失敗：{e.Message}。本次遊戲不再重複這行。");
            }
            return Available;
        }
    }

    /// <summary>
    /// 問這件物品會在哪幾個點位撈到，順序是對方給的 Tier 高→低。
    /// 對方不可用或呼叫失敗回 false；有回應但沒資料回 true ＋ 空清單。
    /// </summary>
    internal static bool TryGetSectorsForItem(uint itemId, out List<uint> sectors)
    {
        sectors = [];
        if(!IsAvailable) return false;
        try
        {
            sectors = Svc.PluginInterface.GetIpcSubscriber<uint, List<uint>>(TagSectorsForItem).InvokeFunc(itemId) ?? [];
            return true;
        }
        catch(Exception e)
        {
            // 標成不可用，讓這一輪剩下的呼叫直接短路，下次重探時再試。
            Available = false;
            PluginLog.Information($"[SubmarineTracker] 呼叫 {TagSectorsForItem}({itemId}) 失敗：{e.Message}");
            return false;
        }
    }
}
