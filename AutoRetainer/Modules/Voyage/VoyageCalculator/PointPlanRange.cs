using AutoRetainerAPI.Configuration;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;

namespace AutoRetainer.Modules.Voyage.VoyageCalculator;

/// <summary>
/// 點計畫(SubmarinePointPlan)的「航行距離不足就砍點」邏輯。
///
/// 為什麼需要：點計畫原本是把使用者排的點原封不動全部點滿，完全不看潛水艇的航行距離。
/// 潛艇能力還不夠時遊戲端的出航按鈕會維持停用，TaskDeployOnBestExpVoyage.Deploy() 只會
/// 一直等到逾時 —— 表現成「這艘船就是不出航」，而且沒有任何一行訊息說明原因。
///
/// 設計約束（改動前請先讀完）：
/// 1. 預設關閉。啟用與否依「計畫 GUID 是否在 C.SubmarinePointPlansTrimToRange 裡」決定，
///    該集合預設是空的，所以既有使用者行為完全不變（ECommons EzConfig 會把既有鍵的值
///    原樣寫回，改既有鍵的預設值對既有使用者無效，因此這裡刻意用「新增鍵」）。
/// 2. 砍點順序＝使用者在計畫裡排的順序，從尾端往前砍。使用者把優先想要的點排在前面。
///    「要哪些點」由使用者決定；「按什麼順序跑」由 Voyage.CalculateDistance 求最短路徑決定。
/// 3. 🔴 任何一步算不出來，一律回傳「原始清單」＝完全維持現狀，絕不砍成只剩一點。
///    這涵蓋：讀不到潛水艇、等級/零件查表失敗、距離計算失敗、連第一個點都跑不到。
///    理由是砍錯點會讓潛艇跑一趟錯的航線（12 小時），而維持現狀最多是回到今天的行為。
/// </summary>
internal static unsafe class PointPlanRange
{
    /// <summary>
    /// 目前工房面板上選中的那艘潛水艇的能力值快照。
    /// 🔴 這裡刻意只存「值」不存原生指標 —— 指標不跨幀保存。
    /// </summary>
    internal record struct SubmarineInfo(string Name, int Rank, int SheetRange, int NativeRange)
    {
        /// <summary>
        /// 用來下判斷的航行距離。採用 Excel 表算出來的值，因為 Calculator.FindBestPath 早就
        /// 拿同一個值在跟 CalculateDistance 的結果比大小（已經在線上跑了很久）。
        /// NativeRange 只拿來對照記 log，不參與任何判斷 —— 見 <see cref="RangeMismatch"/>。
        /// </summary>
        public int Range => SheetRange;

        /// <summary>
        /// 表算值與遊戲結構裡的 RangeBase+RangeBonus 對不上。
        /// 這兩者理論上應該相等；不相等代表其中一邊的假設錯了（例如 CS 的欄位偏移在台服對不上）。
        /// 只記 log 不改行為，這樣「假設不成立」會被看見而不是被吃掉。
        /// </summary>
        public bool RangeMismatch => NativeRange > 0 && NativeRange != SheetRange;
    }

    internal static bool IsTrimEnabled(SubmarinePointPlan plan)
    {
        return plan != null && C.SubmarinePointPlansTrimToRange.Contains(plan.GUID);
    }

    internal static void SetTrimEnabled(SubmarinePointPlan plan, bool enabled)
    {
        if(plan == null) return;
        if(enabled) C.SubmarinePointPlansTrimToRange.Add(plan.GUID);
        else C.SubmarinePointPlansTrimToRange.Remove(plan.GUID);
    }

    /// <summary>
    /// 讀取工房面板目前選中的潛水艇。每一層都檢查 null；讀到之後立刻把需要的欄位複製成
    /// managed 值再回傳，呼叫端拿不到任何原生指標。
    /// </summary>
    internal static bool TryGetCurrentSubmarineInfo(out SubmarineInfo info)
    {
        info = default;
        try
        {
            var manager = HousingManager.Instance();
            if(manager == null) return false;
            var territory = manager->WorkshopTerritory;
            if(territory == null) return false;
            var pointers = territory->Submersible.DataPointers;
            // DataPointers[4] 是「目前操作中」的那一艘（前 4 個是槽位 0~3）。
            if(pointers.Length <= 4) return false;
            var current = pointers[4].Value;
            if(current == null) return false;

            int rank = current->RankId;
            int hull = current->HullId;
            int stern = current->SternId;
            int bow = current->BowId;
            int bridge = current->BridgeId;
            var nativeRange = current->RangeBase + current->RangeBonus;
            var name = current->Name.Read();
            // 這裡之後不再碰 current。

            if(rank < 1) return false;

            int sheetRange;
            try
            {
                var build = new Build.SubmarineBuild(rank, hull, stern, bow, bridge);
                sheetRange = build.Range;
            }
            catch(Exception e)
            {
                PluginLog.Information($"[PointPlanRange] 依 Rank={rank} 零件={hull}/{stern}/{bow}/{bridge} 查表建立潛水艇能力失敗，放棄裁切: {e.Message}");
                return false;
            }

            info = new SubmarineInfo(name, rank, sheetRange, nativeRange);
            return true;
        }
        catch(Exception e)
        {
            PluginLog.Information($"[PointPlanRange] 讀取目前潛水艇資料失敗，放棄裁切: {e.Message}");
            return false;
        }
    }

    private static SubmarineExploration? GetStartPoint(uint mapId)
    {
        foreach(var row in Svc.Data.GetExcelSheet<SubmarineExploration>())
        {
            if(row.Map.RowId == mapId && row.StartingPoint) return row;
        }
        return null;
    }

    /// <summary>
    /// 算一條航線的探索距離。回傳 null＝算不出來（呼叫端必須當成「維持現狀」而不是 0）。
    /// out 的 optimizedOrder 是 CalculateDistance 求出的最短路徑順序。
    /// </summary>
    private static int? CalculateRouteDistance(SubmarineExploration start, List<SubmarineExploration> points, out List<uint> optimizedOrder)
    {
        optimizedOrder = null;
        if(points.Count is < 1 or > 5) return null;
        try
        {
            var walk = new List<SubmarineExploration>(points.Count + 1) { start };
            walk.AddRange(points);
            var result = Voyage.CalculateDistance(walk);
            // ⚠️ CalculateDistance 的「失敗」也是回 0（點數 0 或 >5 都直接 return (0, [])），
            // 而真實航線最少也有 SurveyDistance≥10，所以 <=0 一律當成算不出來，不要相信 0。
            if(result.Distance <= 0) return null;
            if(result.Points == null || result.Points.Count != points.Count) return null;
            // 重排後的集合必須與輸入完全相同，否則不採信這個結果。
            var a = result.Points.Select(x => x.RowId).OrderBy(x => x);
            var b = points.Select(x => x.RowId).OrderBy(x => x);
            if(!a.SequenceEqual(b)) return null;
            optimizedOrder = result.Points.Select(x => x.RowId).ToList();
            return result.Distance;
        }
        catch(Exception e)
        {
            // CalculateDistance 內部在最佳化失敗時會讓 MinimalWays 變空，接著 min.Points 是 null
            // → NullReferenceException。這是 managed 例外，攔得到。
            PluginLog.Information($"[PointPlanRange] 距離計算丟出例外，視為算不出來: {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// 取得這份計畫實際要點的點位。未啟用裁切、或任何一步算不出來時原樣回傳 plan.Points。
    /// </summary>
    internal static List<uint> GetEffectivePoints(SubmarinePointPlan plan, bool log)
    {
        if(plan == null) return [];
        var original = plan.Points;
        if(!IsTrimEnabled(plan)) return original;
        try
        {
            var trimmed = Compute(plan, out var report);
            if(log && report != null) PluginLog.Information($"[PointPlanRange] {report}");
            return trimmed ?? original;
        }
        catch(Exception e)
        {
            PluginLog.Information($"[PointPlanRange] 裁切過程發生未預期的例外，維持原始點位清單: {e.Message}");
            e.Log();
            return original;
        }
    }

    private static string Describe(IEnumerable<uint> points)
    {
        return points.Select(x => VoyageUtils.GetSubmarineExploration(x)?.Location.ToString() ?? $"?{x}").Join("→");
    }

    private static List<uint> Compute(SubmarinePointPlan plan, out string report)
    {
        var original = plan.Points;
        var planName = plan.Name.Length > 0 ? plan.Name : plan.GUID;
        var head = $"計畫「{planName}」";

        if(original.Count is < 1 or > 5)
        {
            report = $"{head}的點位數是 {original.Count}（合法範圍 1~5），不做裁切";
            return null;
        }
        if(original.Distinct().Count() != original.Count)
        {
            report = $"{head}裡有重複的點位，不做裁切";
            return null;
        }

        var rows = new List<SubmarineExploration>(original.Count);
        foreach(var id in original)
        {
            var row = VoyageUtils.GetSubmarineExploration(id);
            if(row == null)
            {
                report = $"{head}含有查不到的點位 id={id}，不做裁切";
                return null;
            }
            rows.Add(row.Value);
        }

        var mapId = rows[0].Map.RowId;
        if(rows.Any(x => x.Map.RowId != mapId))
        {
            report = $"{head}的點位跨越多張航海圖，不做裁切";
            return null;
        }

        var start = GetStartPoint(mapId);
        if(start == null)
        {
            report = $"{head}找不到航海圖 {mapId} 的出發點，不做裁切";
            return null;
        }

        if(!TryGetCurrentSubmarineInfo(out var sub))
        {
            report = $"{head}：讀不到目前潛水艇的資料，不做裁切（維持原樣點滿 {original.Count} 點）";
            return null;
        }

        var range = sub.Range;
        if(range <= 0)
        {
            report = $"{head}：算出來的航行距離是 {range}，不合理，不做裁切（潛水艇 {sub.Name} Rank {sub.Rank}）";
            return null;
        }

        var mismatch = sub.RangeMismatch
            ? $"；⚠️ 表算航行距離 {sub.SheetRange} 與遊戲結構讀到的 {sub.NativeRange} 不一致（採用表算值）"
            : "";

        // 等級不足的點在遊戲裡本來就選不起來（VoyageUtils.SelectRoutePointSafe 會靜默跳過），
        // 把它們算進距離會造成過度裁切，所以先濾掉再算。
        var byRank = new List<SubmarineExploration>(rows.Count);
        var rankDropped = new List<uint>();
        foreach(var row in rows)
        {
            if(row.RankReq <= sub.Rank) byRank.Add(row);
            else rankDropped.Add(row.RowId);
        }
        var rankNote = rankDropped.Count > 0 ? $"；等級不足略過 {Describe(rankDropped)}（本艇 Rank {sub.Rank}）" : "";

        if(byRank.Count == 0)
        {
            report = $"{head}：所有點位的等級需求都高於本艇 Rank {sub.Rank}，不做裁切，交給遊戲自己判斷{mismatch}";
            return null;
        }

        for(var n = byRank.Count; n >= 1; n--)
        {
            var subset = byRank.Take(n).ToList();
            var distance = CalculateRouteDistance(start.Value, subset, out var optimizedOrder);
            if(distance == null)
            {
                report = $"{head}：{n} 個點的距離算不出來，不做裁切（維持原樣點滿 {original.Count} 點）{mismatch}";
                return null;
            }
            if(distance.Value <= range)
            {
                var kept = optimizedOrder ?? subset.Select(x => x.RowId).ToList();
                report = $"{head}：潛水艇 {sub.Name}（Rank {sub.Rank}，航行距離 {range}）"
                    + $"原本 {original.Count} 點 {Describe(original)} → 實際 {kept.Count} 點 {Describe(kept)}"
                    + $"，估算探索距離 {distance.Value} / 航行距離 {range}{rankNote}{mismatch}";
                return kept;
            }
        }

        report = $"{head}：潛水艇 {sub.Name}（Rank {sub.Rank}，航行距離 {range}）連第一個點都跑不到，"
            + $"不做裁切，交給遊戲自己判斷{rankNote}{mismatch}";
        return null;
    }

    #region UI 用的預估距離階梯（純計畫屬性，與是哪一艘潛水艇無關）

    private static string LadderSignature;
    private static int[] LadderCache;

    /// <summary>
    /// 回傳長度＝plan.Points.Count 的陣列：第 i 項是「保留使用者清單前 i+1 個點」時所需的探索距離。
    /// 算不出來的項是 -1（呼叫端要畫成「?」而不是 0）。結果會依點位清單快取，避免每幀重算。
    /// </summary>
    internal static int[] GetRequiredRangeLadder(SubmarinePointPlan plan)
    {
        if(plan == null || plan.Points.Count == 0) return [];
        var signature = $"{plan.GUID}:{plan.Points.Select(x => x.ToString()).Join(",")}";
        if(LadderSignature == signature && LadderCache != null) return LadderCache;

        var ladder = new int[plan.Points.Count];
        for(var i = 0; i < ladder.Length; i++) ladder[i] = -1;
        try
        {
            var rows = new List<SubmarineExploration>(plan.Points.Count);
            foreach(var id in plan.Points)
            {
                var row = VoyageUtils.GetSubmarineExploration(id);
                if(row == null) { rows = null; break; }
                rows.Add(row.Value);
            }
            if(rows != null && rows.Count is >= 1 and <= 5 && rows.Select(x => x.RowId).Distinct().Count() == rows.Count)
            {
                var mapId = rows[0].Map.RowId;
                if(rows.All(x => x.Map.RowId == mapId))
                {
                    var start = GetStartPoint(mapId);
                    if(start != null)
                    {
                        for(var i = 0; i < rows.Count; i++)
                        {
                            var distance = CalculateRouteDistance(start.Value, rows.Take(i + 1).ToList(), out _);
                            if(distance != null) ladder[i] = distance.Value;
                        }
                    }
                }
            }
        }
        catch(Exception e)
        {
            // 這個方法會在 ImGui 的 Draw 裡被呼叫；Dalamud 攔到 Draw 例外會把整個外掛的視窗關掉
            // 到重開遊戲為止，所以這裡一定要把例外吃掉，讓階梯顯示成「?」就好。
            PluginLog.Information($"[PointPlanRange] 預估距離階梯計算失敗: {e.Message}");
        }

        LadderSignature = signature;
        LadderCache = ladder;
        return ladder;
    }

    #endregion
}
