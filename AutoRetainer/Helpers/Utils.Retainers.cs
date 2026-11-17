using AutoRetainer.Internal;
using AutoRetainer.Modules.Voyage;
using AutoRetainer.Scheduler.Handlers;
using AutoRetainer.Scheduler.Tasks;
using AutoRetainer.Services;
using AutoRetainerAPI.Configuration;
using Dalamud.Game;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Memory;
using Dalamud.Utility;
using ECommons.Events;
using ECommons.ExcelServices;
using ECommons.ExcelServices.TerritoryEnumeration;
using ECommons.GameHelpers;
using ECommons.IPC;
using ECommons.MathHelpers;
using ECommons.Reflection;
using ECommons.Throttlers;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using Lumina.Text.ReadOnly;
using OtterGui.Text.EndObjects;
using System.Text.RegularExpressions;
using CharaData = (string Name, ushort World);
using GrandCompany = ECommons.ExcelServices.GrandCompany;

namespace AutoRetainer.Helpers;

public static unsafe partial class Utils
{
    internal static bool TryGetCharacterIndex(string name, uint world, out int index)
    {
        index = GetCharacterNames().IndexOf((name, (ushort)world));
        return index >= 0;
    }

    internal static List<CharaData> GetCharacterNames()
    {
        List<CharaData> ret = [];
        /*var data = CSFramework.Instance()->UIModule->GetRaptureAtkModule()->AtkModule.GetStringArrayData(1);
        if (data != null)
        {
            for (int i = 60; i < data->AtkArrayData.Size; i++)
            {
                if (data->StringArray[i] == null) break;
                var item = data->StringArray[i];
                if (item != null)
                {
                    var str = MemoryHelper.ReadSeStringNullTerminated((nint)item).GetText();
                    if (str == "") break;
                    ret.Add(str);
                }
            }
        }*/
        var agent = AgentLobby.Instance();
        // AgentLobby.Instance() 是 AgentGetterGenerator 產出的兩層可空取得器
        // （agentModule == null ? null : GetAgentByInternalId(...)），合法回 null。
        // 拿不到就回空清單：呼叫端 TryGetCharacterIndex 會得到 index < 0 ＝「找不到角色」，
        // 與「代理人不活躍」走同一條路徑，不會謊報有角色。
        if(agent == null) return ret;
        if(agent->AgentInterface.IsAgentActive())
        {
            var charaSpan = agent->LobbyData.CharaSelectEntries.AsSpan();
            for(var i = 0; i < charaSpan.Length; i++)
            {
                var s = charaSpan[i];
                // 🔴 元素本身是可為 null 的指標（項目還沒填完就是 null），
                // 直接 s.Value->Name.Read() 是 AccessViolation（try/catch 攔不到）。
                // 🔴 但不能用 continue 跳過：本清單的「索引」就是 TryGetCharacterIndex 回傳的
                // 角色選擇索引，少一項會讓後面每個角色往前位移一格 ⇒ 選到錯的角色。
                // 填一個永遠不會被 IndexOf 命中的佔位項（空名字＋世界 0）保住位置對齊。
                if(s.Value == null)
                {
                    ret.Add(("", (ushort)0));
                    continue;
                }
                ret.Add(($"{s.Value->Name.Read()}", s.Value->HomeWorldId));
            }
        }
        return ret;
    }

    internal static OfflineCharacterData GetCurrentCharacterData()
    {
        return C.OfflineData.FirstOrDefault(x => x.CID == Player.CID);
    }

    internal static OfflineCharacterData GetOfflineCharacterDataFromAdditionalRetainerDataKey(string key)
    {
        var cid = ulong.Parse(key.Split(" ")[0].Replace("#", ""), System.Globalization.NumberStyles.HexNumber);
        return C.OfflineData.FirstOrDefault(x => x.CID == cid);
    }

    internal static OfflineRetainerData GetOfflineRetainerDataFromAdditionalRetainerDataKey(string key)
    {
        return GetOfflineCharacterDataFromAdditionalRetainerDataKey(key).RetainerData.FirstOrDefault(x => x.Name == key.Split(" ")[1]);
    }

    internal static uint GetNextPlannedVenture(this AdditionalRetainerData data)
    {
        var index = data.GetNextPlannedVentureIndex();
        if(index == -1)
        {
            return 0;
        }
        else
        {
            return data.VenturePlan.ListUnwrapped[index];
        }
    }

    internal static int GetNextPlannedVentureIndex(this AdditionalRetainerData data)
    {
        if(data.VenturePlan.ListUnwrapped.Count == 0)
        {
            return -1;
        }
        else
        {
            if(data.VenturePlanIndex >= data.VenturePlan.ListUnwrapped.Count)
            {
                if(data.VenturePlan.PlanCompleteBehavior == PlanCompleteBehavior.Restart_plan)
                {
                    return 0;
                }
                else
                {
                    return -1;
                }
            }
            else
            {
                return (int)data.VenturePlanIndex;
            }
        }
    }

    internal static bool IsLastPlannedVenture(this AdditionalRetainerData data)
    {
        return data.VenturePlanIndex >= data.VenturePlan.ListUnwrapped.Count;
    }

    /// <remarks>ListUnwrapped 每次存取都會建一份新清單；它的長度大於 0 ⇔ 至少一項 Num 大於 0。
    /// 刻意走完整份而不提早回傳：與原本建完整份清單一樣，碰到 null 項目照樣擲例外。</remarks>
    internal static bool IsVenturePlannerActive(this AdditionalRetainerData data)
    {
        if(!data.EnablePlanner) return false;
        var any = false;
        foreach(var v in data.VenturePlan.List)
        {
            if(v.Num > 0) any = true;
        }
        return any;
    }

    internal static bool IsAnyRetainersCompletedVenture()
    {
        if(!ProperOnLogin.PlayerPresent) return false;
        if(C.OfflineData.TryGetFirst(x => x.CID == Svc.PlayerState.ContentId, out var data))
        {
            var selectedRetainers = data.GetEnabledRetainers().Where(z => z.HasVenture);
            return selectedRetainers.Any(z => z.GetVentureSecondsRemaining() <= 10);
        }
        return false;
    }

    internal static bool IsAllCurrentCharacterRetainersHaveMoreThan5Mins()
    {
        if(C.OfflineData.TryGetFirst(x => x.CID == Svc.PlayerState.ContentId, out var data))
        {
            foreach(var z in data.GetEnabledRetainers())
            {
                if(z.GetVentureSecondsRemaining() < 5 * 60) return false;
            }
        }
        return true;
    }

    internal static bool AnyRetainersAvailableCurrentChara()
    {
        if(C.OfflineData.TryGetFirst(x => x.CID == Svc.PlayerState.ContentId, out var data))
        {
            return data.GetEnabledRetainers().Any(z => z.GetVentureSecondsRemaining() <= C.UnsyncCompensation);
        }
        return false;
    }

    internal static AdditionalRetainerData GetAdditionalData(ulong cid, string name)
    {
        var key = GetAdditionalDataKey(cid, name, true);
        return C.AdditionalData[key];
    }

    internal static string GetAdditionalDataKey(ulong cid, string name, bool create = true)
    {
        var key = $"#{cid:X16} {name}";
        if(create && !C.AdditionalData.ContainsKey(key))
        {
            C.AdditionalData[key] = new();
        }
        return key;
    }

    internal static bool IsCurrentRetainerEnabled()
    {
        return TryGetCurrentRetainer(out var ret) && C.SelectedRetainers.TryGetValue(Svc.PlayerState.ContentId, out var rets) && rets.Contains(ret);
    }

    /// <summary>離玩家最近的僱員 NPC 的名字。</summary>
    /// <remarks>
    /// 與 <c>Where(僱員).OrderBy(距離).First()</c> 同一個結果：距離最小的那個，同距離取物件表裡排前面的
    /// （<see cref="Comparer{T}.Default"/> 的順序，NaN 最小）。只在框架執行緒呼叫；物件包裝只在這次呼叫內使用。
    /// </remarks>
    internal static bool TryGetCurrentRetainer(out string name)
    {
        if(Svc.Condition[ConditionFlag.OccupiedSummoningBell] && ProperOnLogin.PlayerPresent)
        {
            IGameObject nearest = null;
            var nearestDistance = 0f;
            foreach(var x in Svc.Objects)
            {
                if(x.ObjectKind != ObjectKind.Retainer) continue;
                var distance = Vector3.Distance(Svc.Objects.LocalPlayer.Position, x.Position);
                if(nearest == null || Comparer<float>.Default.Compare(distance, nearestDistance) < 0)
                {
                    nearest = x;
                    nearestDistance = distance;
                }
            }
            if(nearest != null)
            {
                name = GetRetainerObjectName(nearest);
                return true;
            }
        }
        name = default;
        return false;
    }

    /// <summary>與 <c>obj.Name.ToString()</c> 相同。Dalamud 的 Name 是原生 GameObject.Name 那段位元組解析出來的，位元組沒變就沿用上一次的字串。</summary>
    private static string GetRetainerObjectName(IGameObject obj)
    {
        var raw = ((FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)obj.Address)->Name;
        if(RetainerObjectName != null && raw.SequenceEqual(RetainerObjectNameBytes)) return RetainerObjectName;
        var name = obj.Name.ToString();
        if(RetainerObjectNameBytes.Length != raw.Length) RetainerObjectNameBytes = new byte[raw.Length];
        raw.CopyTo(RetainerObjectNameBytes);
        RetainerObjectName = name;
        return name;
    }

    internal static uint GetVenturesAmount()
    {
        return (uint)InventoryManager.Instance()->GetInventoryItemCount(21072);
    }

    internal static long GetVentureSecondsRemaining(this GameRetainerManager.Retainer ret, bool allowNegative = true)
    {
        var x = ret.VentureCompleteTimeStamp - P.Time;
        return allowNegative ? x : Math.Max(0, x);
    }

    internal static long GetVentureSecondsRemaining(this OfflineRetainerData ret, bool allowNegative = true)
    {
        var x = ret.VentureEndsAt - P.Time;
        return allowNegative ? x : Math.Max(0, x);
    }

    internal static bool TryGetRetainerByName(string name, out GameRetainerManager.Retainer retainer)
    {
        if(!GameRetainerManager.Ready)
        {
            retainer = default;
            return false;
        }
        var retainers = GameRetainerManager.Retainers;
        for(var i = 0; i < retainers.Count; i++)
        {
            var r = retainers[i];
            if(r.Name.ToString() == name)
            {
                retainer = r;
                return true;
            }
        }
        retainer = default;
        return false;
    }
}
