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
/// 2. 「要哪些點」由使用者決定；「按什麼順序跑」由 Voyage.CalculateDistance 求最短路徑決定。
///    計畫清單的順序＝使用者的優先順序，最想跑的排在最上面。
///    🔴 2026-08-08 修正：初版只評估「清單前綴」（byRank.Take(n)），所以永遠產生不出
///    「跳過中間某個點」的子集。改成枚舉全部非空子集取「航距內點數最多」者，
///    同點數時才回頭用使用者的排列順序決定。細節見 <see cref="PickBest"/>。
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
    /// <param name="PartsRange">四個零件的 Range 加總（不含 Rank 加成）。只拿來記 log。</param>
    /// <param name="RankRangeBonus">SubmarineRank[Rank].RangeBonus。台服 7.20 實測 Rank 52 起才非零、Rank 90 是 60。</param>
    /// <param name="BuildIdentifier">零件組合代號（例如 WSUC++）。⚠️ 工房面板列表上顯示的代號會把「+」拿掉，
    /// 所以使用者看到的「WSUC」有可能其實是全改造的 WSUC++（兩者航距差 20）—— log 這裡刻意保留「+」。</param>
    internal record struct SubmarineInfo(string Name, int Rank, int SheetRange, int NativeRange, int PartsRange, int RankRangeBonus, string BuildIdentifier)
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
            int rankBonus;
            string identifier;
            try
            {
                var build = new Build.SubmarineBuild(rank, hull, stern, bow, bridge);
                sheetRange = build.Range;
                // Build.Range ＝ Bonus.RangeBonus ＋ 四個零件的 Range，所以零件那半用減的還原。
                rankBonus = build.Bonus.RangeBonus;
                identifier = build.FullIdentifier();
            }
            catch(Exception e)
            {
                PluginLog.Information($"[PointPlanRange] 依 Rank={rank} 零件={hull}/{stern}/{bow}/{bridge} 查表建立潛水艇能力失敗，放棄裁切: {e.Message}");
                return false;
            }

            info = new SubmarineInfo(name, rank, sheetRange, nativeRange, sheetRange - rankBonus, rankBonus, identifier);
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
            var trimmed = Compute(plan, out var report, out var detail);
            if(log && report != null) PluginLog.Information($"[PointPlanRange] {report}");
            // 子集表格另起一行：決策那行要能單獨看懂，這行是「為什麼不是別的組合」的證據。
            if(log && detail != null) PluginLog.Information($"[PointPlanRange] {detail}");
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

    /// <summary>
    /// 子集表格用的緊湊寫法。⚠️ 刻意留 "-" 當分隔符而不是直接把字母接起來 ——
    /// 溺沒海有 AA~AD 這種兩個字母的點位，接起來會變成看不出斷點的字串。
    /// </summary>
    private static string DescribeCompact(IEnumerable<uint> points)
    {
        return points.Select(x => VoyageUtils.GetSubmarineExploration(x)?.Location.ToString() ?? $"?{x}").Join("-");
    }

    /// <summary>一個候選子集的評估結果。Indices 是它在「使用者排的清單」裡的位置（遞增）。</summary>
    private record struct Candidate(int Count, int Distance, List<uint> Order, int[] Indices, string Name);

    /// <summary>
    /// 把計畫解析成「同一張航海圖上的點位列 ＋ 出發點」。
    /// 失敗時 reason 是接在 head 後面的說明（呼叫端組成完整訊息）。
    /// </summary>
    private static bool TryResolvePlan(SubmarinePointPlan plan, out SubmarineExploration start, out List<SubmarineExploration> rows, out string reason)
    {
        start = default;
        rows = null;
        reason = null;

        var original = plan.Points;
        if(original.Count is < 1 or > 5)
        {
            reason = $"的點位數是 {original.Count}（合法範圍 1~5），不做裁切";
            return false;
        }
        if(original.Distinct().Count() != original.Count)
        {
            reason = "裡有重複的點位，不做裁切";
            return false;
        }

        var list = new List<SubmarineExploration>(original.Count);
        foreach(var id in original)
        {
            var row = VoyageUtils.GetSubmarineExploration(id);
            if(row == null)
            {
                reason = $"含有查不到的點位 id={id}，不做裁切";
                return false;
            }
            list.Add(row.Value);
        }

        var mapId = list[0].Map.RowId;
        if(list.Any(x => x.Map.RowId != mapId))
        {
            reason = "的點位跨越多張航海圖，不做裁切";
            return false;
        }

        var startRow = GetStartPoint(mapId);
        if(startRow == null)
        {
            reason = $"找不到航海圖 {mapId} 的出發點，不做裁切";
            return false;
        }

        start = startRow.Value;
        rows = list;
        return true;
    }

    /// <summary>
    /// 等級不足的點在遊戲裡本來就選不起來（VoyageUtils.SelectRoutePointSafe 會靜默跳過），
    /// 把它們算進距離會造成過度裁切，所以先濾掉再算。回傳的清單保留使用者排的相對順序。
    /// </summary>
    private static List<SubmarineExploration> FilterByRank(List<SubmarineExploration> rows, int rank, out List<uint> dropped)
    {
        var kept = new List<SubmarineExploration>(rows.Count);
        dropped = [];
        foreach(var row in rows)
        {
            if(row.RankReq <= rank) kept.Add(row);
            else dropped.Add(row.RowId);
        }
        return kept;
    }

    /// <summary>
    /// 枚舉 byRank 的全部非空子集並各算一次最短路徑。
    /// 點數上限是 5（遊戲限制），所以最多 2^5-1 = 31 個子集，每個跑一次完全可行。
    ///
    /// 🔴 保守哲學照舊：只要有任何一個子集算不出距離就整批放棄（回傳 false），
    /// 呼叫端要維持原始清單。這與初版「任何一步算不出來就回傳原樣」是同一個約定，
    /// 只是評估的範圍從 5 個前綴變成 31 個子集。
    /// </summary>
    private static bool TryEvaluateAllSubsets(SubmarineExploration start, List<SubmarineExploration> byRank, out List<Candidate> all, out string failed)
    {
        all = [];
        failed = null;
        var combinations = 1 << byRank.Count;
        for(var mask = 1; mask < combinations; mask++)
        {
            var subset = new List<SubmarineExploration>(byRank.Count);
            var indices = new List<int>(byRank.Count);
            for(var i = 0; i < byRank.Count; i++)
            {
                if((mask & (1 << i)) == 0) continue;
                subset.Add(byRank[i]);
                indices.Add(i);
            }

            var distance = CalculateRouteDistance(start, subset, out var optimizedOrder);
            if(distance == null)
            {
                failed = DescribeCompact(subset.Select(x => x.RowId));
                all = null;
                return false;
            }

            var order = optimizedOrder ?? subset.Select(x => x.RowId).ToList();
            all.Add(new Candidate(subset.Count, distance.Value, order, indices.ToArray(), DescribeCompact(order)));
        }
        return true;
    }

    /// <summary>
    /// 從「航距內跑得完」的候選裡挑一個。
    ///
    /// 主判準＝點數最多。這是使用者要的：同樣一趟 12 小時，能多探一個點就多探一個。
    ///
    /// 同點數時的取捨＝**使用者在計畫裡排的順序**：比較兩個子集的索引向量，逐位取小者
    /// （例如清單是 M,R,O,J,Z 而三點候選有 MRO(0,1,2)／MOJ(0,2,3)／ROJ(1,2,3) 時取 MRO）。
    /// 這是設計約束 2「最想跑的排在最上面」的直接推廣，前綴法是它的退化情形。
    /// ⚠️ 刻意不用預估收益／經驗當排序依據 —— 那會在使用者沒要求的情況下蓋掉他自己排的優先序，
    /// 而收益模型本身在台服沒有可離線驗證的資料來源。被略過的同點數候選一律寫進 log，
    /// 使用者看得到自己可以怎麼調整排序。
    ///
    /// 📌 索引互異 ⇒ 等長索引向量的逐位比較是**全序**，不會平手；
    /// 所以這裡不需要、也不該再串第三個 tiebreak（那會是永遠走不到的死碼）。
    /// </summary>
    private static Candidate PickBest(List<Candidate> feasible)
    {
        var best = feasible[0];
        foreach(var candidate in feasible)
        {
            if(candidate.Count > best.Count || candidate.Count == best.Count && IsEarlierPriority(candidate.Indices, best.Indices))
            {
                best = candidate;
            }
        }
        return best;
    }

    private static bool IsEarlierPriority(int[] a, int[] b)
    {
        for(var i = 0; i < a.Length && i < b.Length; i++)
        {
            if(a[i] != b[i]) return a[i] < b[i];
        }
        return false;
    }

    private static bool SameIndices(int[] a, int[] b)
    {
        if(a.Length != b.Length) return false;
        for(var i = 0; i < a.Length; i++)
        {
            if(a[i] != b[i]) return false;
        }
        return true;
    }

    /// <summary>
    /// 所有評估過的子集與各自的距離，讓使用者從 log 直接看出「為什麼不是那個組合」。
    /// ✓＝這艘船跑得完。
    /// </summary>
    private static string BuildSubsetTable(List<Candidate> all, int range)
    {
        var ordered = all.OrderByDescending(x => x.Count).ThenBy(x => x.Distance);
        return $"評估過的 {all.Count} 個子集（航行距離 {range}，✓＝跑得完）："
            + ordered.Select(x => $"{(x.Distance <= range ? "✓" : "✗")}{x.Name}={x.Distance}").Join(" ");
    }

    /// <summary>
    /// UI 用的預覽：這份計畫套在「目前工房面板選中的那艘潛水艇」上實際會跑到哪幾個點。
    /// 🔴 刻意呼叫與出航時同一套選擇邏輯 —— UI 若自己另算一份，演算法一改兩邊就會不一致，
    /// 而且不一致是靜默的（使用者看到 2 點、實際跑 3 點，沒有任何訊息說明）。
    /// 回傳 false＝算不出來，呼叫端要畫「?」不要畫 0。
    /// </summary>
    internal static bool TryGetTrimPreview(SubmarinePointPlan plan, SubmarineInfo sub, out List<uint> kept, out int distance)
    {
        kept = null;
        distance = 0;
        if(plan == null || sub.Range <= 0) return false;
        try
        {
            if(!TryResolvePlan(plan, out var start, out var rows, out _)) return false;
            var byRank = FilterByRank(rows, sub.Rank, out _);
            if(byRank.Count == 0) return false;
            if(!TryEvaluateAllSubsets(start, byRank, out var all, out _)) return false;
            var feasible = all.Where(x => x.Distance <= sub.Range).ToList();
            if(feasible.Count == 0) return false;
            var best = PickBest(feasible);
            kept = best.Order;
            distance = best.Distance;
            return true;
        }
        catch(Exception e)
        {
            // 這個方法會在 ImGui 的 Draw 裡被呼叫；Dalamud 攔到 Draw 例外會把整個外掛的視窗
            // 關掉到重開遊戲為止，所以一定要把例外吃掉，讓 UI 顯示成「?」就好。
            PluginLog.Information($"[PointPlanRange] 預覽計算失敗: {e.Message}");
            return false;
        }
    }

    private static List<uint> Compute(SubmarinePointPlan plan, out string report, out string detail)
    {
        var original = plan.Points;
        var planName = plan.Name.Length > 0 ? plan.Name : plan.GUID;
        var head = $"計畫「{planName}」";
        detail = null;

        if(!TryResolvePlan(plan, out var start, out var rows, out var reason))
        {
            report = head + reason;
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

        // 航距分項寫進 log：使用者最容易誤判的就是「Rank 加成有沒有算進去」。
        // 台服 7.20 實測 Rank 52 起 RangeBonus 才非零、Rank 90 是 60。
        var rangeText = $"{range}（零件 {sub.PartsRange} ＋ Rank {sub.Rank} 加成 {sub.RankRangeBonus}）";
        var buildText = sub.BuildIdentifier.IsNullOrEmpty() ? "" : $"，零件組合 {sub.BuildIdentifier}";

        var mismatch = sub.RangeMismatch
            ? $"；⚠️ 表算航行距離 {sub.SheetRange} 與遊戲結構讀到的 {sub.NativeRange} 不一致（採用表算值）"
            : "";

        var byRank = FilterByRank(rows, sub.Rank, out var rankDropped);
        var rankNote = rankDropped.Count > 0 ? $"；等級不足略過 {Describe(rankDropped)}（本艇 Rank {sub.Rank}）" : "";

        if(byRank.Count == 0)
        {
            report = $"{head}：所有點位的等級需求都高於本艇 Rank {sub.Rank}，不做裁切，交給遊戲自己判斷{mismatch}";
            return null;
        }

        if(!TryEvaluateAllSubsets(start, byRank, out var all, out var failedSubset))
        {
            report = $"{head}：子集 {failedSubset} 的距離算不出來，不做裁切（維持原樣點滿 {original.Count} 點）{mismatch}";
            return null;
        }

        var subHead = $"潛水艇 {sub.Name}（Rank {sub.Rank}{buildText}，航行距離 {rangeText}）";
        var feasible = all.Where(x => x.Distance <= range).ToList();
        if(feasible.Count == 0)
        {
            report = $"{head}：{subHead}連第一個點都跑不到，不做裁切，交給遊戲自己判斷{rankNote}{mismatch}";
            detail = BuildSubsetTable(all, range);
            return null;
        }

        var best = PickBest(feasible);
        var alternatives = feasible.Where(x => x.Count == best.Count && !SameIndices(x.Indices, best.Indices)).ToList();
        var altNote = alternatives.Count > 0
            ? $"；同樣是 {best.Count} 點的其他候選 {alternatives.OrderBy(x => x.Distance).Select(x => $"{x.Name}={x.Distance}").Join("、")}"
                + "（依計畫的排列順序取用了前者；想換的話把想要的點位在計畫裡往上移）"
            : "";

        report = $"{head}：{subHead}"
            + $"原本 {original.Count} 點 {Describe(original)} → 實際 {best.Count} 點 {Describe(best.Order)}"
            + $"，估算探索距離 {best.Distance} / 航行距離 {range}{altNote}{rankNote}{mismatch}";
        detail = BuildSubsetTable(all, range);
        return best.Order;
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
