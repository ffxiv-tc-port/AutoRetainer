using AutoRetainer.Modules.Voyage.Readers;
using Dalamud.Utility;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace AutoRetainer.Modules.Voyage.Tasks;

internal static unsafe class TaskPickSubmarineRoute
{
    /// <summary>
    /// 點位計畫存在設定檔裡、可從剪貼簿貼入，完全沒有驗證，所以 map 與 point 都可能查無此列。
    /// 裸 GetRow 在這種情況下擲的是 Lumina 深處的例外，使用者只會看到一串看不懂的堆疊；
    /// 這裡先把每個 ID 逐一驗過並記下是哪個壞掉，再擲原本就會擲的 ArgumentOutOfRangeException。
    /// </summary>
    private static string[] ResolvePointNames(uint map, uint[] points)
    {
        if(!Svc.Data.GetExcelSheet<SubmarineMap>().TryGetRow(map, out var mapRow) || mapRow.Name.GetText() == "")
        {
            DuoLog.Error($"潛艇航線的海圖 ID {map} 不存在於本地資料表，無法出航。請檢查點位計畫是否來自其他服務版本。");
            throw new ArgumentOutOfRangeException(nameof(map));
        }
        if(points.Length < 1 || points.Length > 5) throw new ArgumentOutOfRangeException(nameof(points));
        var names = new string[points.Length];
        for(var i = 0; i < points.Length; i++)
        {
            if(!Svc.Data.GetExcelSheet<SubmarineExploration>().TryGetRow(points[i], out var pointRow))
            {
                DuoLog.Error($"潛艇航線的點位 ID {points[i]} 不存在於本地資料表，無法出航。請檢查點位計畫是否來自其他服務版本。");
                throw new ArgumentOutOfRangeException(nameof(points));
            }
            names[i] = C.SimpleTweaksCompat ? pointRow.Location.ToDalamudString().GetText().Trim() : pointRow.Destination.ToDalamudString().GetText().Trim();
        }
        return names;
    }

    /// <summary>
    /// 這趟航線「要求了但沒選上」的點位。
    /// ⚠️ 只在 framework 執行緒的 task 裡讀寫（任務是循序執行的，不會有兩趟航線交錯）。
    /// </summary>
    private static readonly List<string> NotPicked = [];
    private static string[] RequestedNames = [];

    private static bool? BeginSelectionReport(string[] names)
    {
        NotPicked.Clear();
        RequestedNames = names;
        return true;
    }

    /// <summary>
    /// 把「這趟實際點到了哪幾個點」寫進 log。
    /// 📌 刻意用 Information 而不是 Debug：使用者跑 LogLevel 2，Debug 收不到，
    /// 而「設了 A 卻跑了 B」正是他唯一會來回報的那件事。
    /// 🔴 只記錄、不改變流程 —— 沒選滿照樣出航，維持既有行為。
    /// </summary>
    private static bool? EndSelectionReport()
    {
        if(NotPicked.Count == 0)
        {
            PluginLog.Information($"[Voyage] 航線選點完成：要求的 {RequestedNames.Length} 個點位（{RequestedNames.Join("→")}）全部選上");
        }
        else
        {
            PluginLog.Information($"[Voyage] ⚠️ 航線選點不完整：要求 {RequestedNames.Length} 點（{RequestedNames.Join("→")}）"
                + $"，其中 {NotPicked.Count} 點沒選上（{NotPicked.Join("、")}）。這趟會用剩下的點出航。"
                + $"最常見的原因是這艘潛水艇的航行距離跑不完整份計畫（可在點位計畫裡開啟「航距不足時自動裁點」），其次是點位尚未解鎖或等級不足。");
        }
        return true;
    }

    internal static void Enqueue(uint map, params uint[] points)
    {
        VoyageUtils.Log($"Task enqueued: {nameof(TaskPickSubmarineRoute)}, map={map}, points={points.Print()}");
        var names = ResolvePointNames(map, points);
        P.TaskManager.Enqueue(() => BeginSelectionReport(names), "BeginRouteSelectionReport");
        P.TaskManager.Enqueue(() => PickMap(map), $"PickMap({map})");
        foreach(var name in names)
        {
            P.TaskManager.Enqueue(() => PickPoint(name), $"PickPoint({name})");
        }
        P.TaskManager.Enqueue(() => EndSelectionReport(), "EndRouteSelectionReport");
    }

    internal static void EnqueueImmediate(uint map, params uint[] points)
    {
        P.TaskManager.BeginStack();
        try
        {
            VoyageUtils.Log($"Task enqueued (immediate): {nameof(TaskPickSubmarineRoute)}, map={map}, points={points.Print()}");
            var names = ResolvePointNames(map, points);
            P.TaskManager.Enqueue(() => BeginSelectionReport(names), "BeginRouteSelectionReport");
            P.TaskManager.Enqueue(() => PickMap(map), $"PickMap({map})");
            foreach(var name in names)
            {
                P.TaskManager.Enqueue(() => PickPoint(name), $"PickPoint({name})");
            }
            P.TaskManager.Enqueue(() => EndSelectionReport(), "EndRouteSelectionReport");
        }
        catch(Exception e) { e.Log(); }
        P.TaskManager.InsertStack();
    }

    internal static bool? PickMap(uint which)
    {
        if(TryGetAddonByName<AtkUnitBase>("AirShipExploration", out _)) return true;
        if(TryGetAddonByName<AtkUnitBase>("SubmarineExplorationMapSelect", out var addon) && IsAddonReady(addon))
        {
            var cnt = new ReaderSubmarineExplorationMapSelect(addon).Maps.Count;
            if(which < 1 || which > cnt)
            {
                PluginLog.Error($"Invalid map index specified (specified {which}, max {cnt})");
                return false;
            }
            if(Utils.GenericThrottle && EzThrottler.Throttle("PickMapVoyage", 2000))
            {
                Callback.Fire(addon, true, 2, Utils.ZeroAtkValue, which);
                return true;
            }
        }
        else
        {
            Utils.RethrottleGeneric();
        }
        return false;
    }

    private static string Explain(RoutePointPickResult result)
    {
        return result switch
        {
            RoutePointPickResult.NotSelectable => "遊戲判定不可選（航行距離不足／未解鎖／等級不足）",
            RoutePointPickResult.NotFound => "這張海圖的點位列表裡沒有這個名字",
            RoutePointPickResult.PanelUnavailable => "航線面板當下不可用",
            _ => $"{result}",
        };
    }

    internal static bool? PickPoint(string name)
    {
        if(TryGetAddonByName<AtkUnitBase>("AirShipExploration", out var addon) && IsAddonReady(addon))
        {
            if(Utils.GenericThrottle)
            {
                // 🔴 這個 task 過去無論有沒有選到都回 true，所以「計畫的點被吃掉」在
                // NeoTaskManager 的 log 上看起來與成功完全一樣。行為維持不變（照樣往下走），
                // 但沒選上一定要留下 Information 級的痕跡。
                var result = VoyageUtils.SelectRoutePointSafe(name, out var statusFlag);
                if(result != RoutePointPickResult.Selected)
                {
                    NotPicked.Add(name);
                    PluginLog.Information($"[Voyage] 點位「{name}」沒有被選上：{Explain(result)}（StatusFlag={statusFlag}）—— 本趟航線會少這個點");
                }
                return true;
            }
        }
        else
        {
            Utils.RethrottleGeneric();
        }
        return false;
    }
}
