using FFXIVClientStructs.FFXIV.Component.GUI;
using AtkValueType = FFXIVClientStructs.FFXIV.Component.GUI.ValueType;

namespace AutoRetainer.Helpers;

/// <summary>
/// 解參考 addon 的 AtkValues 之前的檢查：陣列非空、格數夠、型別對（先去掉 Managed 旗標）、指標非空。
/// 不符就回 false，由呼叫端跳過該格或這一輪不做；同一個 addon、格號、原因只記一次 Information。
/// 只在框架執行緒呼叫（記錄用的集合沒有鎖）。
/// </summary>
internal static unsafe class AtkValueGuard
{
    private static readonly HashSet<string> Reported = [];

    internal static AtkValueType BaseType(AtkValueType type) => type & AtkValueType.TypeMask;

    internal static bool TryGet(AtkUnitBase* addon, int index, string addonName, out AtkValue value)
    {
        value = default;
        if(addon == null) return false;
        if(addon->AtkValues == null || index < 0 || index >= addon->AtkValuesCount)
        {
            ReportOnce(addonName, index, "out of range", $"AtkValuesCount={addon->AtkValuesCount}");
            return false;
        }
        value = addon->AtkValues[index];
        return true;
    }

    /// <summary>筆數一類的整數格。型別未定義時回 0（遊戲自己的讀法同樣當 0）；不是 Int／UInt 時回 false。</summary>
    internal static bool TryGetCount(AtkUnitBase* addon, int index, string addonName, out uint count)
    {
        count = 0;
        if(!TryGet(addon, index, addonName, out var v)) return false;
        var t = BaseType(v.Type);
        if(t == AtkValueType.Undefined) return true;
        if(t is AtkValueType.Int or AtkValueType.UInt)
        {
            count = v.UInt;
            return true;
        }
        ReportOnce(addonName, index, "not a number", $"type={v.Type}");
        return false;
    }

    /// <summary>
    /// 指標格。值域裝得下 64 位元指標的型別才收：32 位元以下的純量（Int／UInt／Bool／Float）上半部不是值，
    /// 當指標讀會是殘留的位元組。指標為空是合法狀態（遊戲自己也逐格判空），回 false、不記錄。
    /// </summary>
    internal static bool TryGetPointer(AtkUnitBase* addon, int index, string addonName, out nint pointer)
    {
        pointer = 0;
        if(!TryGet(addon, index, addonName, out var v)) return false;
        var t = BaseType(v.Type);
        if(t is not (AtkValueType.Pointer or AtkValueType.Vector or AtkValueType.AtkValues or AtkValueType.Int64 or AtkValueType.UInt64
            or AtkValueType.String or AtkValueType.String8 or AtkValueType.WideString))
        {
            ReportOnce(addonName, index, "not a pointer", $"type={v.Type}");
            return false;
        }
        pointer = (nint)v.Pointer;
        return pointer != 0;
    }

    /// <summary>UTF-8 字串格（String／String8，含 Managed 旗標）。WideString 是 UTF-16，不收。</summary>
    internal static bool TryGetUtf8String(AtkUnitBase* addon, int index, string addonName, out nint pointer)
    {
        pointer = 0;
        if(!TryGet(addon, index, addonName, out var v)) return false;
        var t = BaseType(v.Type);
        if(t is not (AtkValueType.String or AtkValueType.String8))
        {
            ReportOnce(addonName, index, "not a UTF-8 string", $"type={v.Type}");
            return false;
        }
        pointer = (nint)v.String.Value;
        if(pointer == 0)
        {
            ReportOnce(addonName, index, "null string pointer", "");
            return false;
        }
        return true;
    }

    internal static void ReportOnce(string addonName, int index, string reason, string detail)
    {
        if(!Reported.Add($"{addonName}[{index}] {reason}")) return;
        PluginLog.Information($"[AtkValueGuard] {addonName} value [{index}]: {reason} {detail}; skipped (logged once).");
    }
}
