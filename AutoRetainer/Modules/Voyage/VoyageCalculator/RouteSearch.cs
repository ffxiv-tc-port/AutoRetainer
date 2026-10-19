using Lumina.Excel.Sheets;

namespace AutoRetainer.Modules.Voyage.VoyageCalculator;

/// <summary>
/// Calculator.FindBestPath 的搜尋本體。候選路徑的列舉順序：MustInclude 固定在最前，追加點由少到多、同點數依 valid 先後的字典序；
/// 每條依 Voyage.CalculateDistance 的規則定走訪順序，過濾航行距離與 MustInclude（ContainsAllItems 的相等），
/// 再依 CalculateDuration／CalculateExpForSectors 的算式取「經驗值÷分鐘」最大者，同分取先列舉到的。
/// 順序與取捨照原本 HashSet＋LINQ 寫法的結果（同分時選哪條就是出航路線）；點對數值先建表，逐條路徑只查表、不配置記憶體。
/// </summary>
/// <remarks>
/// 平行時各分段只讀共用的表、只寫自己那一格，最後依列舉順序合併＝循序結果（同分時選哪條是固定的）。
/// </remarks>
internal static class RouteSearch
{
    private const int MaxPoints = Voyage.MaxPoints;

    private sealed class Tables
    {
        public int N;                // valid 點數＋1；最後一格（Start）是出發點
        public int Start;
        public int[] Base;           // MustInclude 各點對應的格；valid 裡對不到的當成出發點
        public int[] Candidates;     // 可以追加的格，依 valid 先後
        public uint[] Distance;      // [a * N + b]
        public uint[] VoyageTime;    // [a * N + b]
        public uint[] SurveyTime;
        public byte[] SurveyDistance;
        public uint[] Exp;
        public bool[] MustMatch;     // [j * N + a]：該格與 MustInclude[j] 相等（ContainsAllItems 用的相等）
        public int Range;
    }

    private struct Best
    {
        public bool Found;
        public double Ratio;
        public uint Duration;
        public uint Exp;
        public int Count;
    }

    internal static (uint[] path, TimeSpan duration, double exp)? FindBest(List<SubmarineExploration> valid, SubmarineExploration startPoint, List<SubmarineExploration> mustInclude, Build.RouteBuild routeBuild, Build.SubmarineBuild currentBuild, bool parallel)
    {
        var n = valid.Count;
        var u = new SubmarineExploration[n + 1];
        valid.CopyTo(u);
        u[n] = startPoint;
        foreach(var row in valid)
        {
            // 出發點是該圖第一列、valid 已排除 StartingPoint，兩者不會重疊；重疊時列舉順序的前提不成立，不硬算。
            if(row.RowId == startPoint.RowId)
                throw new InvalidOperationException($"Start point {startPoint.RowId} is also a selectable sector");
        }

        var t = new Tables { N = n + 1, Start = n, Base = new int[mustInclude.Count] };
        var baseRows = new SubmarineExploration[mustInclude.Count];
        for(var j = 0; j < mustInclude.Count; j++)
        {
            t.Base[j] = n;
            for(var i = 0; i < n; i++)
            {
                if(valid[i].RowId == mustInclude[j].RowId)
                {
                    t.Base[j] = i;
                    break;
                }
            }
            baseRows[j] = u[t.Base[j]];
        }
        // 每條路徑都含這幾點，重複時每條都算不出距離；在讀組船資料之前擲例外。
        if(baseRows.Length is >= 2 and <= MaxPoints)
            Voyage.ThrowIfDuplicateRowIds(baseRows);

        var candidates = new List<int>(n);
        for(var i = 0; i < n; i++)
        {
            var inBase = false;
            foreach(var must in mustInclude)
                inBase |= must.RowId == valid[i].RowId;
            if(!inBase)
                candidates.Add(i);
        }
        t.Candidates = candidates.ToArray();

        var build = routeBuild.GetSubmarineBuild;
        var speed = (short)build.Speed;
        t.Range = build.Range;
        var size = t.N;
        t.Distance = new uint[size * size];
        t.VoyageTime = new uint[size * size];
        t.SurveyTime = new uint[size];
        t.SurveyDistance = new byte[size];
        t.Exp = new uint[size];
        t.MustMatch = new bool[mustInclude.Count * size];
        for(var a = 0; a < size; a++)
        {
            for(var b = 0; b < size; b++)
            {
                t.Distance[a * size + b] = Voyage.BestDistance(u[a], u[b]);
                t.VoyageTime[a * size + b] = Voyage.VoyageTime(u[a], u[b], speed);
            }
            t.SurveyTime[a] = Voyage.SurveyTime(u[a], speed);
            t.SurveyDistance[a] = u[a].SurveyDistance;
            t.Exp[a] = Sectors.CalculateBonusExp(Sectors.PredictBonusExp(u[a].RowId, currentBuild).Guaranteed, u[a].ExpReward);
            // 值相等必然 RowId 相等；先比 RowId 省掉 ValueType.Equals 的裝箱與反射。
            for(var j = 0; j < mustInclude.Count; j++)
                t.MustMatch[j * size + a] = u[a].RowId == mustInclude[j].RowId && EqualityComparer<SubmarineExploration>.Default.Equals(u[a], mustInclude[j]);
        }

        // 分段＝(追加點數, 第一個追加點)；依這個順序合併就是列舉順序。
        // 有 MustInclude 時第一條是它本身（追加 0 點）；沒有時從 1 點起。
        var segments = new List<(int Added, int First)>(1 + MaxPoints * (t.Candidates.Length + 1));
        if(mustInclude.Count > 0)
            segments.Add((0, 0));
        var maxAdded = mustInclude.Count == 0 ? MaxPoints : Math.Max(0, MaxPoints - mustInclude.Count);
        for(var added = 1; added <= maxAdded; added++)
        {
            for(var first = 0; first + added <= t.Candidates.Length; first++)
                segments.Add((added, first));
        }

        var best = default(Best);
        var bestOrder = new int[MaxPoints];
        if(parallel)
        {
            var results = new Best[segments.Count];
            var orders = new int[segments.Count * MaxPoints];
            Parallel.For(0, segments.Count, s => EvaluateSegment(t, segments[s].Added, segments[s].First, ref results[s], orders.AsSpan(s * MaxPoints, MaxPoints)));
            for(var s = 0; s < segments.Count; s++)
            {
                if(results[s].Found && (!best.Found || Comparer<double>.Default.Compare(best.Ratio, results[s].Ratio) < 0))
                {
                    best = results[s];
                    orders.AsSpan(s * MaxPoints, MaxPoints).CopyTo(bestOrder);
                }
            }
        }
        else
        {
            foreach(var segment in segments)
                EvaluateSegment(t, segment.Added, segment.First, ref best, bestOrder);
        }

        if(!best.Found)
            return null;
        var path = new uint[best.Count];
        for(var i = 0; i < best.Count; i++)
            path[i] = u[bestOrder[i]].RowId;
        return (path, TimeSpan.FromSeconds(best.Duration), best.Exp);
    }

    private static void EvaluateSegment(Tables t, int added, int first, ref Best best, Span<int> bestOrder)
    {
        var baseCount = t.Base.Length;
        // 超過 5 點時 CalculateDistance 回 (0, 空清單)；會超過就一定含 MustInclude，ContainsAllItems 不成立，整條略過。
        if(baseCount + added > MaxPoints)
            return;
        Span<int> points = stackalloc int[MaxPoints];
        Span<int> order = stackalloc int[MaxPoints];
        Span<int> pick = stackalloc int[MaxPoints];
        t.Base.CopyTo(points);
        if(added == 0)
        {
            Score(t, points[..baseCount], order, ref best, bestOrder);
            return;
        }

        for(var i = 0; i < added; i++)
            pick[i] = first + i;
        var last = t.Candidates.Length - added;
        while(true)
        {
            for(var i = 0; i < added; i++)
                points[baseCount + i] = t.Candidates[pick[i]];
            Score(t, points[..(baseCount + added)], order, ref best, bestOrder);

            var k = added - 1;
            while(k >= 1 && pick[k] == last + k)
                k--;
            if(k < 1)
                return;
            pick[k]++;
            for(var i = k + 1; i < added; i++)
                pick[i] = pick[i - 1] + 1;
        }
    }

    private static void Score(Tables t, ReadOnlySpan<int> points, Span<int> order, ref Best best, Span<int> bestOrder)
    {
        var n = t.N;
        var count = points.Length;
        int distance;
        if(count == 1)
        {
            order[0] = points[0];
            distance = (int)(t.Distance[t.Start * n + points[0]] + t.SurveyDistance[points[0]]);
        }
        else
        {
            Span<uint> startDist = stackalloc uint[MaxPoints];
            Span<uint> dist = stackalloc uint[MaxPoints * MaxPoints];
            for(var a = 0; a < count; a++)
            {
                startDist[a] = t.Distance[t.Start * n + points[a]];
                for(var b = 0; b < count; b++)
                {
                    if(a != b)
                        dist[a * MaxPoints + b] = t.Distance[points[a] * n + points[b]];
                }
            }
            Span<byte> perm = stackalloc byte[MaxPoints];
            var way = Voyage.ShortestOrder(startDist, dist, count, perm);
            var surveyD = 0;
            for(var i = 0; i < count; i++)
            {
                order[i] = points[perm[i]];
                surveyD += t.SurveyDistance[order[i]];
            }
            distance = (int)way + surveyD;
        }
        if(distance > t.Range)
            return;

        for(var j = 0; j < t.Base.Length; j++)
        {
            var found = false;
            for(var i = 0; i < count && !found; i++)
                found = t.MustMatch[j * n + order[i]];
            if(!found)
                return;
        }

        var duration = Duration(t, order[..count]);
        var exp = 0u;
        for(var i = 0; i < count; i++)
            exp += t.Exp[order[i]];
        var ratio = (double)exp / TimeSpan.FromSeconds(duration).TotalMinutes;
        if(best.Found && Comparer<double>.Default.Compare(best.Ratio, ratio) >= 0)
            return;
        best = new Best { Found = true, Ratio = ratio, Duration = duration, Exp = exp, Count = count };
        order[..count].CopyTo(bestOrder);
    }

    // Voyage.CalculateDuration 的同一套算式（各段 uint 相加、long 加總後截成 uint），只是改查表。
    private static uint Duration(Tables t, ReadOnlySpan<int> order)
    {
        var n = t.N;
        if(order.Length == 1)
            return t.VoyageTime[t.Start * n + order[0]] + t.SurveyTime[order[0]] + Voyage.FixedVoyageTime;

        long sum = 0;
        for(var i = 0; i < order.Length; i++)
        {
            var voyage = i == 0 ? t.VoyageTime[t.Start * n + order[0]] : t.VoyageTime[order[i - 1] * n + order[i]];
            var survey = t.SurveyTime[order[i]];
            sum += voyage + survey;
        }
        return (uint)sum + Voyage.FixedVoyageTime;
    }
}
