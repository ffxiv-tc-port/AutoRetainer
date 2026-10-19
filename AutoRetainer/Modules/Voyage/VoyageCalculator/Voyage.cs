using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace AutoRetainer.Modules.Voyage.VoyageCalculator;

public static class Voyage
{
    internal const int FixedVoyageTime = 43200; // 12h
    internal const int MaxPoints = 5; // 遊戲一趟最多 5 個點；ShortestOrder 的距離表列寬也是它

    private static ExcelSheet<SubmarineExploration> ExplorationSheet = null!;
    private static List<uint> ReversedStartPoints = null!;

    public static void Initialize()
    {
        ExplorationSheet = Svc.Data.GetExcelSheet<SubmarineExploration>();
        ReversedStartPoints = ExplorationSheet.Where(s => s.StartingPoint).Select(s => s.RowId).Reverse().ToList();
    }

    public static uint SectorToMap(uint sector)
    {
        return ExplorationSheet.GetRow(FindVoyageStartPoint(sector))!.Map.RowId;
    }

    public static uint FindVoyageStartPoint(uint point)
    {
        // This works because we reversed the list of start points
        foreach(var possibleStart in ReversedStartPoints)
            if(point > possibleStart)
                return possibleStart;

        return 0;
    }

    #region Optimizer
    public static uint CalculateDuration(IEnumerable<SubmarineExploration> walkingPoints, Build.SubmarineBuild build)
    {
        var walkWay = walkingPoints.ToArray();
        var start = walkWay.First();

        var points = new List<SubmarineExploration>();
        foreach(var p in walkWay.Skip(1))
            points.Add(p);

        switch(points.Count)
        {
            case 0:
                return 0;
            case 1: // 1 point makes no sense to optimize, so just return distance
                {
                    var onlyPoint = points[0];
                    return VoyageTime(start, onlyPoint, (short)build.Speed) + SurveyTime(onlyPoint, (short)build.Speed) + FixedVoyageTime;
                }
            case > 5: // More than 5 points isn't allowed ingame
                return 0;
        }

        var allDurations = new List<long>();
        for(var i = 0; i < points.Count; i++)
        {
            var voyage = i == 0
                             ? VoyageTime(start, points[0], (short)build.Speed)
                             : VoyageTime(points[i - 1], points[i], (short)build.Speed);
            var survey = SurveyTime(points[i], (short)build.Speed);
            allDurations.Add(voyage + survey);
        }

        return (uint)allDurations.Sum() + FixedVoyageTime;
    }

    /// <summary>
    /// 從 walkingPoints[0] 出發走完其餘各點的最短順序與探索距離（含各點 SurveyDistance）。
    /// 同距離時取「各點在清單中的位置」字典序最前的順序。點數 0 或超過 5 回 (0, 空清單)；點位 RowId 重複時擲例外。
    /// </summary>
    public static (int Distance, List<SubmarineExploration> Points) CalculateDistance(IEnumerable<SubmarineExploration> walkingPoints)
    {
        var walkWay = walkingPoints.ToArray();
        var start = walkWay.First();
        var count = walkWay.Length - 1;

        // zero
        if(count == 0)
            return (0, new List<SubmarineExploration>());

        // 1 point makes no sense to optimize, so just return distance
        if(count == 1)
        {
            var onlyPoint = walkWay[1];
            var distance = BestDistance(start, onlyPoint) + onlyPoint.SurveyDistance;
            return ((int)distance, new List<SubmarineExploration> { onlyPoint });
        }

        // More than 5 points isn't allowed ingame
        if(count > MaxPoints)
            return (0, new List<SubmarineExploration>());

        ThrowIfDuplicateRowIds(walkWay.AsSpan(1));

        Span<uint> startDist = stackalloc uint[MaxPoints];
        Span<uint> dist = stackalloc uint[MaxPoints * MaxPoints];
        for(var a = 0; a < count; a++)
        {
            startDist[a] = BestDistance(start, walkWay[1 + a]);
            for(var b = 0; b < count; b++)
            {
                if(a != b)
                    dist[a * MaxPoints + b] = BestDistance(walkWay[1 + a], walkWay[1 + b]);
            }
        }

        Span<byte> order = stackalloc byte[MaxPoints];
        var way = ShortestOrder(startDist, dist, count, order);

        // 第一站是清單裡那一點本身，其餘各站取 walkWay 中第一個同 RowId 的元素。
        var points = new List<SubmarineExploration>(count) { walkWay[1 + order[0]] };
        for(var i = 1; i < count; i++)
            points.Add(FirstWithRowId(walkWay, walkWay[1 + order[i]].RowId));

        var surveyD = 0;
        foreach(var p in points)
            surveyD += p.SurveyDistance;
        return ((int)way + surveyD, points);
    }

    // 點數 2~5 的全部排列（依字典序攤平）；同距離取第一個＝字典序最前。
    private static readonly byte[][] Permutations = [[], [], BuildPermutations(2), BuildPermutations(3), BuildPermutations(4), BuildPermutations(5)];

    private static byte[] BuildPermutations(int count)
    {
        var current = new byte[count];
        for(var i = 0; i < count; i++)
            current[i] = (byte)i;
        var all = new List<byte>();
        while(true)
        {
            all.AddRange(current);
            var pivot = count - 2;
            while(pivot >= 0 && current[pivot] >= current[pivot + 1])
                pivot--;
            if(pivot < 0)
                return all.ToArray();
            var swap = count - 1;
            while(current[swap] <= current[pivot])
                swap--;
            (current[pivot], current[swap]) = (current[swap], current[pivot]);
            Array.Reverse(current, pivot + 1, count - pivot - 1);
        }
    }

    /// <summary>
    /// startDist[a]＝出發點到 a、dist[a * MaxPoints + b]＝a 到 b。回傳最短總距離，order 寫入走訪順序（點的位置）。
    /// 同距離取字典序最前的排列。count 必須是 2~5。
    /// </summary>
    internal static uint ShortestOrder(ReadOnlySpan<uint> startDist, ReadOnlySpan<uint> dist, int count, Span<byte> order)
    {
        var perms = Permutations[count];
        var best = 0u;
        var bestAt = -1;
        for(var p = 0; p < perms.Length; p += count)
        {
            var way = startDist[perms[p]];
            for(var i = 1; i < count; i++)
                way += dist[perms[p + i - 1] * MaxPoints + perms[p + i]];
            if(bestAt < 0 || way < best)
            {
                best = way;
                bestAt = p;
            }
        }
        perms.AsSpan(bestAt, count).CopyTo(order);
        return best;
    }

    // 重複 RowId 時擲例外，兩個呼叫端都會攔下當成算不出來。
    internal static void ThrowIfDuplicateRowIds(ReadOnlySpan<SubmarineExploration> points)
    {
        for(var a = 1; a < points.Length; a++)
        {
            for(var b = 0; b < a; b++)
            {
                if(points[a].RowId == points[b].RowId)
                    throw new InvalidOperationException($"Exploration point {points[a].RowId} appears more than once in the route");
            }
        }
    }

    private static SubmarineExploration FirstWithRowId(SubmarineExploration[] walkWay, uint rowId)
    {
        foreach(var p in walkWay)
        {
            if(p.RowId == rowId)
                return p;
        }
        throw new InvalidOperationException($"Exploration point {rowId} is not in the route");
    }

    public static (uint Distance, List<SubmarineExploration> Points) PathWalker((SubmarineExploration Key, uint Start, Dictionary<uint, uint> Distances) point, Dictionary<uint, Dictionary<uint, uint>> otherPoints, SubmarineExploration[] allPoints)
    {
        List<(uint Distance, List<SubmarineExploration> Points)> possibleDistances = [];
        foreach(var pos1 in otherPoints)
        {
            if(point.Key.RowId == pos1.Key)
                continue;

            var startToFirst = point.Start + point.Distances[pos1.Key];

            if(otherPoints.Count == 1)
            {
                possibleDistances.Add((startToFirst, new List<SubmarineExploration> { point.Key, allPoints.First(t => t.RowId == pos1.Key), }));
                continue;
            }

            foreach(var pos2 in otherPoints)
            {
                if(pos1.Key == pos2.Key || point.Key.RowId == pos2.Key)
                    continue;

                var startToSecond = startToFirst + otherPoints[pos1.Key][pos2.Key];

                if(otherPoints.Count == 2)
                {
                    possibleDistances.Add((startToSecond, new List<SubmarineExploration> { point.Key, allPoints.First(t => t.RowId == pos1.Key), allPoints.First(t => t.RowId == pos2.Key), }));
                    continue;
                }

                foreach(var pos3 in otherPoints)
                {
                    if(pos1.Key == pos3.Key || pos2.Key == pos3.Key || point.Key.RowId == pos3.Key)
                        continue;

                    var startToThird = startToSecond + otherPoints[pos2.Key][pos3.Key];

                    if(otherPoints.Count == 3)
                    {
                        possibleDistances.Add((startToThird, new List<SubmarineExploration> { point.Key, allPoints.First(t => t.RowId == pos1.Key), allPoints.First(t => t.RowId == pos2.Key), allPoints.First(t => t.RowId == pos3.Key), }));
                        continue;
                    }

                    foreach(var pos4 in otherPoints)
                    {
                        if(pos1.Key == pos4.Key || pos2.Key == pos4.Key || pos3.Key == pos4.Key || point.Key.RowId == pos4.Key)
                            continue;

                        var startToLast = startToThird + otherPoints[pos3.Key][pos4.Key];

                        possibleDistances.Add((startToLast, new List<SubmarineExploration> { point.Key, allPoints.First(t => t.RowId == pos1.Key), allPoints.First(t => t.RowId == pos2.Key), allPoints.First(t => t.RowId == pos3.Key), allPoints.First(t => t.RowId == pos4.Key), }));
                    }
                }
            }
        }

        return possibleDistances.MinBy(a => a.Distance);
    }

    public static uint BestDistance(SubmarineExploration pointA, SubmarineExploration pointB)
    {
        return pointA.GetDistance(pointB);
    }

    public static uint VoyageTime(SubmarineExploration pointA, SubmarineExploration pointB, short speed)
    {
        return pointA.GetVoyageTime(pointB, speed);
    }

    public static uint SurveyTime(SubmarineExploration point, short speed)
    {
        return point.GetSurveyTime(speed);
    }
    #endregion
}
