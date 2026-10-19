using Lumina.Excel.Sheets;
using static AutoRetainer.Modules.Voyage.VoyageCalculator.Build;

namespace AutoRetainer.Modules.Voyage.VoyageCalculator;

internal unsafe class Calculator
{
    public List<SubmarineExploration> MustInclude = [];

    private uint[] BestPath = Array.Empty<uint>();
    private List<SubmarineExploration> AllowedSectors = [];

    // 建構時（呼叫端的框架執行緒）取一次。FindBestPath 跑在執行緒池，那裡不可呼叫遊戲函式：
    // IsSubmarineExplorationUnlocked 直接讀 HousingManager->WorkshopTerritory（不在工房時為 null）。
    private readonly List<uint> UnlockedSectors;

    internal SubmarineBuild? CurrentBuild;
    internal RouteBuild? RouteBuild;

    internal Calculator()
    {
        var current = CurrentSubmarine.Get();
        // CurrentSubmarine.Get() 會回 null（不在工房／面板沒開）。丟可讀例外，不要讓
        // 下一行去解參考 null —— AVE 是 corrupted-state exception，try/catch 攔不到。
        if(current == null) throw new InvalidOperationException(CurrentSubmarine.Unavailable);
        CurrentBuild = new(current->RankId, current->HullId, current->SternId, current->BowId, current->BridgeId);
        RouteBuild = new(current->RankId, current->HullId, current->SternId, current->BowId, current->BridgeId);
        UnlockedSectors = CurrentSubmarine.GetUnlockedSectors();
    }

    public (uint[] path, TimeSpan duration, double exp)? FindBestPath(uint mapId)
    {
        try
        {
            var routeBuild = RouteBuild.Value;
            if(CurrentBuild != null)
            {
                VoyageUtils.Log($"Starting to get best path for map {mapId}");

                List<SubmarineExploration> valid;
                try
                {
                    valid = Svc.Data.GetExcelSheet<SubmarineExploration>()
                            .Where(r => r.Map.RowId == mapId && !r.StartingPoint && r.RankReq <= routeBuild.Rank)
                            .Where(r => UnlockedSectors.Contains(r.RowId))
                            .ToList();
                    if(AllowedSectors.Any())
                    {
                        valid = Svc.Data.GetExcelSheet<SubmarineExploration>()
                                .Where(r => r.Map.RowId == mapId && !r.StartingPoint && r.RankReq <= routeBuild.Rank)
                                .Where(r => AllowedSectors.Contains(r))
                                .ToList();
                    }
                    // valid 為空時由 Max 擲例外（外層記錯誤並回 null），保留這個行為。
                    _ = valid.Max(r => r.RankReq);
                }
                catch(KeyNotFoundException)
                {
                    return null;
                }

                var startPoint = Svc.Data.GetExcelSheet<SubmarineExploration>().First(r => r.Map.RowId == mapId);
                return RouteSearch.FindBest(valid, startPoint, MustInclude, routeBuild, CurrentBuild.Value, !C.VoyageDisableCalcParallel);
            }
        }
        catch(Exception e)
        {
            PluginLog.Error($"Error calculating best path");
            e.Log();
        }
        return null;
    }
}
