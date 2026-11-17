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
    internal static IEnumerable<string> GetEObjNames(params uint[] values)
    {
        foreach(var x in values)
        {
            yield return Svc.Data.GetExcelSheet<EObjName>().GetRow(x).Singular.GetText();
        }
    }

    internal static IGameObject GetNearestRetainerBell(out float Distance)
    {
        var currentDistance = float.MaxValue;
        IGameObject currentObject = null;
        foreach(var x in Svc.Objects)
        {
            if(x.IsTargetable && (x.ObjectKind == ObjectKind.Housing || x.ObjectKind == ObjectKind.EventObj) && ObjectNameEqualsAny(x, Lang.BellName))
            {
                var distance = Vector3.Distance(Svc.Objects.LocalPlayer.Position, x.Position);
                if(distance < currentDistance)
                {
                    currentDistance = distance;
                    currentObject = x;
                }
            }
        }
        Distance = currentDistance;
        return currentObject;
    }

    internal static IGameObject GetReachableRetainerBell(bool extend)
    {
        if(Player.Object is null) return null;

        foreach(var x in Svc.Objects)
        {
            if((x.ObjectKind == ObjectKind.Housing || x.ObjectKind == ObjectKind.EventObj) && ObjectNameEqualsAny(x, Lang.BellName))
            {
                var distance = extend && VoyageUtils.Workshops.Contains(Svc.ClientState.TerritoryType) ? 20f : GetValidInteractionDistance(x);
                if(Vector3.Distance(x.Position, Svc.Objects.LocalPlayer.Position) < distance && x.IsTargetable)
                {
                    return x;
                }
            }
        }
        return null;
    }

    // Finds the Company Workshop's "adventurer doll" NPC, used to reach the
    // Free Company Credit Shop. Matched by name substring since it carries a
    // per-instance numeric suffix (e.g. "冒險人偶014號") that rules out an
    // exact-name match like the bell/panel use.
    internal static IGameObject GetNearestAdventurerDoll(out float Distance)
    {
        var currentDistance = float.MaxValue;
        IGameObject currentObject = null;
        foreach(var x in Svc.Objects)
        {
            if(x.IsTargetable && x.Name.ToString().ContainsAny(StringComparison.OrdinalIgnoreCase, Lang.AdventurerDollNamePart))
            {
                var distance = Vector3.Distance(Svc.Objects.LocalPlayer.Position, x.Position);
                if(distance < currentDistance)
                {
                    currentDistance = distance;
                    currentObject = x;
                }
            }
        }
        Distance = currentDistance;
        return currentObject;
    }

    internal static IGameObject GetReachableAdventurerDoll()
    {
        if(Player.Object is null) return null;
        foreach(var x in Svc.Objects)
        {
            if(x.Name.ToString().ContainsAny(StringComparison.OrdinalIgnoreCase, Lang.AdventurerDollNamePart))
            {
                if(Vector3.Distance(x.Position, Svc.Objects.LocalPlayer.Position) < GetValidInteractionDistance(x) && x.IsTargetable)
                {
                    return x;
                }
            }
        }
        return null;
    }

    internal static float GetValidInteractionDistance(IGameObject bell)
    {
        if(bell.ObjectKind == ObjectKind.Housing)
        {
            return 6.5f;
        }
        else if(Inns.List.Contains(Svc.ClientState.TerritoryType))
        {
            return 4.75f;
        }
        else
        {
            return 4.6f;
        }
    }

    internal static float GetAngleTo(Vector2 pos)
    {
        return (MathHelper.GetRelativeAngle(Svc.Objects.LocalPlayer.Position.ToVector2(), pos) + Svc.Objects.LocalPlayer.Rotation.RadToDeg()) % 360;
    }

    internal static bool IsApartmentEntrance(this IGameObject obj)
    {
        return ObjectNameEquals(obj, Lang.ApartmentEntrance);
    }

    internal static IGameObject GetNearestEntrance(out float Distance)
    {
        var currentDistance = float.MaxValue;
        IGameObject currentObject = null;

        foreach(var x in Svc.Objects)
        {
            if(x.IsTargetable && ObjectNameEqualsAny(x, Lang.Entrance))
            {
                var distance = Vector3.Distance(Svc.Objects.LocalPlayer.Position, x.Position);
                if(distance < currentDistance)
                {
                    currentDistance = distance;
                    currentObject = x;
                }
            }
        }
        Distance = currentDistance;
        if(Distance > 20) return null;
        return currentObject;
    }

    internal static IGameObject GetEntranceAtLocation(Vector3 pos)
    {
        foreach(var x in Svc.Objects)
        {
            if(x.IsTargetable && ObjectNameEqualsAny(x, Lang.Entrance))
            {
                var distance = Vector3.Distance(pos, x.Position);
                if(distance < 1f)
                {
                    return x;
                }
            }
        }
        return null;
    }

    internal static bool IsRetainerBell(this IGameObject o)
    {
        return o != null &&
            (o.ObjectKind == ObjectKind.EventObj || o.ObjectKind == ObjectKind.Housing)
            && ObjectNameEqualsAny(o, Lang.BellName);
    }

    /// <summary>
    /// 把物件名字解碼到 <paramref name="buffer"/>；名字裡有 SeString 巨集（0x02）時回 false，呼叫端改走 <c>obj.Name.ToString()</c>。
    /// 不含 0x02 時 Dalamud 的 <c>Name.ToString()</c> 就是第一個 NUL 之前那段的 UTF-8 解碼，兩者逐字相同。
    /// </summary>
    private static bool TryReadPlainObjectName(IGameObject obj, Span<char> buffer, out int length)
    {
        var raw = ((FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)obj.Address)->Name;
        var end = raw.IndexOf((byte)0);
        ReadOnlySpan<byte> bytes = end >= 0 ? raw[..end] : raw;
        if(bytes.Length > buffer.Length || bytes.IndexOf((byte)2) >= 0)
        {
            length = 0;
            return false;
        }
        length = Encoding.UTF8.GetChars(bytes, buffer);
        return true;
    }

    /// <summary>即 <c>obj.Name.ToString().EqualsIgnoreCase(value)</c>，但不建字串。<paramref name="value"/> 不可為 null（呼叫端給的都是讀表字串）。</summary>
    internal static bool ObjectNameEquals(IGameObject obj, string value)
    {
        Span<char> buffer = stackalloc char[ObjectNameBytes];
        if(!TryReadPlainObjectName(obj, buffer, out var length)) return obj.Name.ToString().EqualsIgnoreCase(value);
        return value.AsSpan().Equals(buffer[..length], StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>即 <c>obj.Name.ToString().EqualsIgnoreCaseAny(values)</c>（依序比、第一個相符就停），但不建字串。元素不可為 null。</summary>
    internal static bool ObjectNameEqualsAny(IGameObject obj, string[] values)
    {
        Span<char> buffer = stackalloc char[ObjectNameBytes];
        if(!TryReadPlainObjectName(obj, buffer, out var length)) return obj.Name.ToString().EqualsIgnoreCaseAny(values);
        var name = buffer[..length];
        foreach(var v in values)
        {
            if(v.AsSpan().Equals(name, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    internal static IGameObject GetNearestWorkshopEntrance(out float Distance)
    {
        Utils.ExtraLog($"GetNearestWorkshopEntrance: Begin");
        var currentDistance = float.MaxValue;
        IGameObject currentObject = null;
        foreach(var x in Svc.Objects)
        {
            Utils.ExtraLog($"GetNearestWorkshopEntrance: Scanning object table: object={x}, targetable={x.IsTargetable}");
            if(x.IsTargetable && ObjectNameEqualsAny(x, Lang.AdditionalChambersEntrance))
            {
                var distance = Vector3.Distance(Svc.Objects.LocalPlayer.Position, x.Position);
                Utils.ExtraLog($"GetNearestWorkshopEntrance: check passed, object={x}, targetable={x.IsTargetable}, distance={distance}");
                if(distance < currentDistance)
                {
                    Utils.ExtraLog($"GetNearestWorkshopEntrance: distance is less than current {currentDistance}, assigning from {currentObject}, object={x}, targetable={x.IsTargetable}, distance={distance}");
                    currentDistance = distance;
                    currentObject = x;
                }
            }
        }
        Distance = currentDistance;
        Utils.ExtraLog($"GetNearestWorkshopEntrance: End with distance={currentDistance}, obj={currentObject}");
        return currentObject;
    }
}
