using AutoRetainerAPI.Configuration;
using Microsoft.Win32;
using System.Threading;

namespace AutoRetainer.Modules;

internal static unsafe class Safety
{
    internal const string Value = "ForceEnableSafetyFeatures";

    // 繳交時每幀都會問，所以讀一次後快取；別的行程（多開的 AutoRetainer、手動改登錄檔）
    // 也可能改這個值，因此最多每隔這麼久重讀一次，不做到每幀即時。
    private const long RereadIntervalMs = 5000;
    private static volatile bool Cached;
    private static long NextReadAt; // Environment.TickCount64；0＝還沒讀過

    internal static void Set(bool enabled)
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
        using var software = root.CreateSubKey("Software");
        using var ar = software.CreateSubKey("AutoRetainer");
        ar.SetValue(Value, enabled ? 1 : 0, RegistryValueKind.DWord);
        Cached = enabled;
        Volatile.Write(ref NextReadAt, Environment.TickCount64 + RereadIntervalMs);
    }

    /// <summary>登錄檔旗標；回傳快取值，最舊落後 <see cref="RereadIntervalMs"/>。</summary>
    internal static bool Get()
    {
        var now = Environment.TickCount64;
        if(now < Volatile.Read(ref NextReadAt)) return Cached;
        var value = Read();
        Cached = value;
        Volatile.Write(ref NextReadAt, now + RereadIntervalMs);
        return value;
    }

    private static bool Read()
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
            using var software = root.OpenSubKey("Software");
            using var ar = software?.OpenSubKey("AutoRetainer");
            var reg = ar?.GetValue(Value);
            if(reg == null) return false;
            return (int)reg == 1;
        }
        catch(Exception e)
        {
            InternalLog.Verbose($"{e.Message}\n{e.StackTrace}");
        }
        return false;
    }

    internal static void Check()
    {
        if(C.UnsafeProtection || Get())
        {
            foreach(var x in C.OfflineData)
            {
                if(x.GCDeliveryType == GCDeliveryType.Show_All_Items)
                {
                    x.GCDeliveryType = GCDeliveryType.Hide_Armoury_Chest_Items;
                    Notify.Info($"Unsafe option removed: character {Censor.Character(x.Name)} - {nameof(GCDeliveryType.Show_All_Items)}");
                }
            }
        }
    }
}
