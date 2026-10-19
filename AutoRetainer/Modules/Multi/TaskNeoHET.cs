using AutoRetainer.Internal.InventoryManagement;
using AutoRetainer.Modules.Voyage;
using AutoRetainer.Modules.Voyage.Tasks;
using AutoRetainer.StaticData;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons.Automation.NeoTaskManager;
using ECommons.Automation.NeoTaskManager.Tasks;
using ECommons.GameHelpers;
using ECommons.IPC;
using ECommons.MathHelpers;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System.Diagnostics;

namespace AutoRetainer.Modules.Multi;
public static unsafe class TaskNeoHET
{
    public static readonly uint[] PrivateMarkers = Enum.GetValues<PrivateHousingMarker>().Select(x => (uint)x).ToArray();
    public static readonly uint[] SharedMarkers = Enum.GetValues<SharedHousingMarker>().Select(x => (uint)x).ToArray();
    public static readonly uint[] FcMarkers = Enum.GetValues<FCHousingMarker>().Select(x => (uint)x).ToArray();
    public static readonly uint[] ApartmentMarkers = Enum.GetValues<ApartmentHousingMarker>().Select(x => (uint)x).ToArray();
    // 進屋流程每幀找入口：兩種組合各合併一次，內容與順序同原本每次呼叫時現組的陣列。
    private static readonly uint[] FcOrPrivateMarkers = [.. PrivateMarkers, .. FcMarkers];
    private static readonly uint[] FcOrPrivateOrSharedMarkers = [.. PrivateMarkers, .. FcMarkers, .. SharedMarkers];
    internal static uint[] CurrentFcOrPrivateMarkers => C.SharedHET ? FcOrPrivateOrSharedMarkers : FcOrPrivateMarkers;
    public static readonly float ValidPlayerToApartmentDistance = 24f;

    public static void Enqueue(Action onFailure, bool tryForceWorkshop = false)
    {
        PluginLog.Debug($"Enqueued HouseEnterTask from {new StackTrace().GetFrames().Select(x => x.GetMethod()?.Name).Prepend("      ").Print("\n")}");
        P.TaskManager.EnqueueTask(NeoTasks.WaitForNotOccupied(new(timeLimitMS: 10 * 60 * 1000)));
        P.TaskManager.Enqueue(() =>
        {
            var entrance = GetFcOrPrivateEntranceFromMarkers();
            if(entrance != null && Player.DistanceTo(entrance) < 40f)
            {
                P.TaskManager.InsertMulti(
                    NeoTasks.WaitForNotOccupied(),
                    NeoTasks.ApproachObjectViaAutomove(GetFcOrPrivateEntranceFromMarkers, 4f),
                    NeoTasks.InteractWithObject(GetFcOrPrivateEntranceFromMarkers),
                    new(HouseEnterTask.SelectYesno),
                    new(HouseEnterTask.WaitUntilLeavingZone),
                    NeoTasks.WaitForScreenAndPlayer(),
                    new(NpcSaleManager.EnqueueIfItemsPresent),
                    new(() =>
                    {
                        if(GetWorkshopEntrance() != null && (tryForceWorkshop || Utils.GetReachableRetainerBell(false) == null))
                        {
                            P.TaskManager.BeginStack();
                            try
                            {
                                TryEnterWorkshop(null);
                            }
                            catch(Exception e) { e.Log(); }
                            P.TaskManager.InsertStack();
                        }
                    })
                    );
            }
            else if(GetApartmentEntrance() != null && Player.DistanceTo(GetApartmentEntrance()) < 40f)
            {
                P.TaskManager.InsertMulti(
                NeoTasks.WaitForNotOccupied(),
                    NeoTasks.ApproachObjectViaAutomove(GetApartmentEntrance, 4f),
                    NeoTasks.InteractWithObject(GetApartmentEntrance),
                    new(HouseEnterTask.SelectYesno),
                    new(HouseEnterTask.WaitUntilLeavingZone),
                    NeoTasks.WaitForScreenAndPlayer()
                    );
            }
            else
            {
                onFailure?.Invoke();
            }
        }, "TaskNeoHET");
    }

    public static bool HasEntranceNearby()
    {
        return GetApartmentEntrance() != null || GetFcOrPrivateEntranceFromMarkers() != null;
    }

    public static IGameObject GetApartmentEntrance()
    {
        return GetHouseEntranceFromMarkers(ApartmentMarkers);
    }

    public static void TryEnterWorkshop(Action onFailure)
    {
        P.TaskManager.Enqueue(NpcSaleManager.EnqueueIfItemsPresent);
        P.TaskManager.Enqueue(() =>
        {
            if(GetWorkshopEntrance() != null)
            {
                var tasks = new List<TaskManagerTask>();
                if(ECommonsIPC.Lifestream.CanMoveToWorkshop())
                {
                    tasks.AddRange([
                        new(S.LifestreamExtra.MoveToWorkshop),
                        new(() => !ECommonsIPC.Lifestream.IsBusy())
                        ]);
                }
                tasks.AddRange([
                    NeoTasks.ApproachObjectViaAutomove(GetWorkshopEntrance, 4f),
                    NeoTasks.InteractWithObject(GetWorkshopEntrance),
                    new(TaskContinueHET.SelectEnterWorkshop),
                    new(() => VoyageUtils.Workshops.Contains(Svc.ClientState.TerritoryType), "Wait Until entered workshop"),
                    NeoTasks.WaitForScreenAndPlayer(),
                    ]);
                if(C.FCChestGilCheck && DateTimeOffset.Now.ToUnixTimeMilliseconds() - C.FCChestGilCheckTimes.SafeSelect(Player.CID) > C.FCChestGilCheckCd * 60 * 60 * 1000)
                {
                    tasks.AddRange([
                    new(UpdateGilFromChest, new(timeLimitMS:10000, abortOnTimeout:false)),
                    new(WaitUntilChestVisible, new(timeLimitMS: 10000, abortOnTimeout: false)),
                    new(() => S.FCPointsUpdater.IsFCChestReady() == true, new(timeLimitMS: 10000, abortOnTimeout: false)),
                    new DelayTask(5000),
                    new(() => OfflineDataManager.WriteOfflineData(false, false)),
                    new(() => C.FCChestGilCheckTimes[Player.CID] = DateTimeOffset.Now.ToUnixTimeMilliseconds()),
                    new(CloseFCChest)
                    ]);
                }
                P.TaskManager.InsertMulti([.. tasks]);
            }
            else
            {
                onFailure?.Invoke();
            }
        }, "TryEnterWorkshop");
    }

    public static bool UpdateGilFromChest()
    {
        if(!TryGetAddonByName<AtkUnitBase>("FreeCompanyChest", out _))
        {
            static IGameObject chest() => Svc.Objects.FirstOrDefault(x => x.BaseId == 2000470 && Player.DistanceTo(x) < 4.6f);
            if(chest() != null && EzThrottler.Throttle("EnqueueInteractFCChest", 5000))
            {
                P.TaskManager.InsertTask(NeoTasks.InteractWithObject(chest, configuration: new(abortOnTimeout: false, timeLimitMS: 30000)));
                return true;
            }
        }
        return false;
    }

    public static bool WaitUntilChestVisible()
    {
        return TryGetAddonByName<AtkUnitBase>("FreeCompanyChest", out var addon) && IsAddonReady(addon);
    }

    public static bool CloseFCChest()
    {
        if(TryGetAddonByName<AtkUnitBase>("FreeCompanyChest", out var addon))
        {
            if(IsAddonReady(addon))
            {
                // 🔴 關窗即關、窗仍在就 return false 重進:關閉中的窗三關全過,同一扇只送一次。
                if(EzThrottler.Throttle("CloseFCChest") && DialogGuards.TryPressOnce("FreeCompanyChest", (nint)addon, "CloseFCChest"))
                {
                    Callback.Fire(addon, true, -1, Callback.ZeroAtkValue);
                }
            }
            return false;
        }
        else
        {
            return true;
        }
    }

    public static IGameObject GetWorkshopEntrance()
    {
        return Svc.Objects.FirstOrDefault(x => x.IsTargetable && Utils.ObjectNameEqualsAny(x, Lang.AdditionalChambersEntrance));
    }

    public static IGameObject GetFcOrPrivateEntranceFromMarkers()
    {
        return GetHouseEntranceFromMarkers(CurrentFcOrPrivateMarkers);
    }

    // 入口名單＝Lang.Entrance 之後接公寓入口，與原本每個物件現組的陣列同內容同順序。
    private static string[] EntranceNames => field ??= [.. Lang.Entrance, Lang.ApartmentEntrance];

    /// <remarks>
    /// 進屋時每幀呼叫，所以不走 LINQ：結果同「標記依離玩家距離排序取第一個」＋「入口依離標記距離排序、取第一個距離 &lt; 24 的」。
    /// 標記用 <see cref="float.CompareTo(float)"/> 比（NaN 最小）、同距離取先列舉到的，與 OrderBy 的穩定排序一致。
    /// </remarks>
    public static IGameObject GetHouseEntranceFromMarkers(uint[] markers)
    {
        /*var entrance = Svc.Objects.Where(x => x.IsTargetable && x.Name.ToString().EqualsIgnoreCaseAny([.. Lang.Entrance, Lang.ApartmentEntrance])).OrderBy(Player.DistanceTo).FirstOrDefault();
        PluginLog.Warning($"Temporary HUD bypass is being applied");
        return entrance;*/
        var hud = AgentHUD.Instance();
        // AgentHUD.Instance() 是產生器產出的取得子,AgentModule 還沒好時回 null。
        // 取不到就當「找不到入口」回 null,與下面找不到標記時的回傳值一致。
        if(hud == null) return null;
        return FindHouseEntrance(hud->MapMarkers.AsSpan(), markers);
    }

    internal static IGameObject FindHouseEntrance(ReadOnlySpan<FFXIVClientStructs.FFXIV.Client.Game.UI.MapMarkerData> mapMarkers, uint[] markers)
    {
        var foundMarker = false;
        var mpos = default(Vector2);
        var markerKey = 0f;
        foreach(ref readonly var x in mapMarkers)
        {
            if(Array.IndexOf(markers, x.IconId) < 0) continue;
            var key = Player.DistanceTo(new Vector2(x.Position.X, x.Position.Z));
            if(!foundMarker || key.CompareTo(markerKey) < 0)
            {
                foundMarker = true;
                markerKey = key;
                mpos = new Vector2(x.Position.X, x.Position.Z);
            }
        }
        if(!foundMarker) return null;
        IGameObject entrance = null;
        var entranceKey = 0f;
        foreach(var x in Svc.Objects)
        {
            if(!x.IsTargetable || !Utils.ObjectNameEqualsAny(x, EntranceNames)) continue;
            // 原本排序後用 FirstOrDefault(距離 < 24) 取：NaN 排最前但永遠不成立，等於只在非 NaN 裡找最近的。
            var key = Vector2.Distance(x.Position.ToVector2(), mpos);
            if(float.IsNaN(key)) continue;
            if(entrance == null || key < entranceKey)
            {
                entrance = x;
                entranceKey = key;
            }
        }
        return entrance != null && entranceKey < ValidPlayerToApartmentDistance ? entrance : null;
    }

    public static bool IsInMarkerHousingPlot(uint[] markers)
    {
        var housing = HousingManager.Instance();
        // 讀不到就當「不在這個地塊」。回 false 只是讓呼叫端多走一次一般進屋流程；
        // 回 true 會讓流程以為人已經到位而跳過真正的進入步驟。
        if(housing == null) return false;
        if(housing->GetCurrentPlot() < 0) return false;
        /*PluginLog.Warning($"Temporary HUD bypass is being applied (2)");
        return true;*/
        var hud = AgentHUD.Instance();
        // 同上:取不到就回 false =「不在這個地塊」。
        // 回 false 只是讓呼叫端多走一次一般進屋流程;回 true 會讓流程以為人已到位而跳過真正的進入步驟。
        if(hud == null) return false;
        return AnyMarkerNearPlayer(hud->MapMarkers.AsSpan(), markers);
    }

    internal static bool AnyMarkerNearPlayer(ReadOnlySpan<FFXIVClientStructs.FFXIV.Client.Game.UI.MapMarkerData> mapMarkers, uint[] markers)
    {
        foreach(ref readonly var x in mapMarkers)
        {
            if(Array.IndexOf(markers, x.IconId) >= 0 && Player.DistanceTo(new Vector2(x.Position.X, x.Position.Z)) < ValidPlayerToApartmentDistance) return true;
        }
        return false;
    }
}
