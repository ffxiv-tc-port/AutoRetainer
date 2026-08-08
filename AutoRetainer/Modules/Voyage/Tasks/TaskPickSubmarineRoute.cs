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

    internal static void Enqueue(uint map, params uint[] points)
    {
        VoyageUtils.Log($"Task enqueued: {nameof(TaskPickSubmarineRoute)}, map={map}, points={points.Print()}");
        var names = ResolvePointNames(map, points);
        P.TaskManager.Enqueue(() => PickMap(map), $"PickMap({map})");
        foreach(var name in names)
        {
            P.TaskManager.Enqueue(() => PickPoint(name), $"PickPoint({name})");
        }
    }

    internal static void EnqueueImmediate(uint map, params uint[] points)
    {
        P.TaskManager.BeginStack();
        try
        {
            VoyageUtils.Log($"Task enqueued (immediate): {nameof(TaskPickSubmarineRoute)}, map={map}, points={points.Print()}");
            var names = ResolvePointNames(map, points);
            P.TaskManager.Enqueue(() => PickMap(map), $"PickMap({map})");
            foreach(var name in names)
            {
                P.TaskManager.Enqueue(() => PickPoint(name), $"PickPoint({name})");
            }
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

    internal static bool? PickPoint(string name)
    {
        if(TryGetAddonByName<AtkUnitBase>("AirShipExploration", out var addon) && IsAddonReady(addon))
        {
            if(Utils.GenericThrottle)
            {
                VoyageUtils.SelectRoutePointSafe(name);
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
