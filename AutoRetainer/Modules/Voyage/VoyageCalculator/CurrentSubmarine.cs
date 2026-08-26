using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;

namespace AutoRetainer.Modules.Voyage.VoyageCalculator;

internal static unsafe class CurrentSubmarine
{
    /// <summary>Get() 回 null 時的例外訊息。五個呼叫端共用一份,不要各自複製一句。</summary>
    internal const string Unavailable = "Could not read the currently selected submarine (not in workshop panel?)";

    /// <summary>
    /// 取得工房面板目前選中的那一艘潛水艇。<b>會回 null</b>：不在工房、面板還沒開、
    /// 或這一瞬間讀不到都算。呼叫端必須自己判。
    /// </summary>
    /// <remarks>
    /// 整條鏈四層都要判：HousingManager.Instance() 是 MemberFunction（不在住宅區時遊戲自己回 null）、
    /// WorkshopTerritory 只有在工房才非 null、DataPointers 是定長陣列（[4]＝目前操作中的那一艘，
    /// 前 4 個是槽位 0~3）、指標本身也可能是 null。
    /// </remarks>
    internal static HousingWorkshopSubmersibleSubData* Get()
    {
        var housing = HousingManager.Instance();
        if(housing == null) return null;
        var workshop = housing->WorkshopTerritory;
        if(workshop == null) return null;
        var pointers = workshop->Submersible.DataPointers;
        if(pointers.Length <= 4) return null;
        return pointers[4].Value;
    }

    /// <summary>
    /// 目前選中潛水艇的名字。<b>讀不到就丟可讀例外</b> —— 四個呼叫端全部把回傳值直接
    /// 當字典鍵用下去，回空字串會靜默建出一筆名為 "" 的 per-vessel 設定。
    /// </summary>
    internal static string GetCurrentNameOrThrow()
    {
        var current = Get();
        if(current == null) throw new InvalidOperationException(Unavailable);
        return GenericHelpers.Read(current->Name);
    }

    public static List<uint> GetUnlockedSectors()
    {
        var ret = new List<uint>();
        foreach(var submarineExploration in Svc.Data.GetExcelSheet<SubmarineExploration>())
        {
            if(HousingManager.IsSubmarineExplorationUnlocked((byte)submarineExploration.RowId)) ret.Add(submarineExploration.RowId);
        }
        return ret;
    }

    public static List<uint> GetExploredSectors()
    {
        var ret = new List<uint>();
        foreach(var submarineExploration in Svc.Data.GetExcelSheet<SubmarineExploration>())
        {
            if(HousingManager.IsSubmarineExplorationExplored((byte)submarineExploration.RowId)) ret.Add(submarineExploration.RowId);
        }
        return ret;
    }

    public static uint[] GetMaps()
    {
        var current = Get();
        // 讀不到目前這艘就沒有等級可以拿來篩，回空陣列（呼叫端拿到的是「沒有可用地圖」）。
        if(current == null) return [];
        var currentRank = current->RankId;
        var maps = Svc.Data.GetExcelSheet<SubmarineExploration>()
                       .Where(r => r.StartingPoint)
                       .Select(r => Svc.Data.GetExcelSheet<SubmarineExploration>().GetRowOrDefault(r.RowId + 1)!)
                       .Where(r => r?.RankReq <= currentRank)
                       .Where(r => GetUnlockedSectors().ContainsNullable(r?.RowId))
                       .Select(r => r?.Map.Value.RowId)
                       .ToArray();
        return maps.Where(x => x != null).Select(x => x.Value).ToArray();
    }

    public static void GetBestExps()
    {
        var calc = new Calculator();
        var maps = GetMaps();
        Task.Run(() =>
        {
            VoyageMain.WaitOverlay.IsProcessing = true;
            foreach(var x in maps)
            {
                calc.RouteBuild.Value.ChangeMap((int)x);
                var best = calc.FindBestPath(x);
                if(best != null)
                {
                    DuoLog.Information($"Map {x}: {best.Value.path.Select(z => $"{z}/{Svc.Data.GetExcelSheet<SubmarineExploration>().GetRowOrDefault(z)?.Location}").Print()}, {best.Value.duration}, {best.Value.exp} / ");
                }
            }
            VoyageMain.WaitOverlay.IsProcessing = false;
        });
    }
}
