using System;
using System.Collections.Generic;

namespace AutoRetainer.Modules;

/// <summary>
/// <see cref="EntrustCapacityForecast"/> 的純計算（不碰遊戲、不碰設定），離線探針直接編這個檔。
/// 時間一律 unix 秒；格數一律已夾在 0～容量之間。
/// </summary>
internal static class EntrustCapacityMath
{
    internal const long SampleEverySeconds = 15 * 60;
    internal const long MergeWithinSeconds = 60;
    internal const long MaxGapSeconds = 3 * 3600;
    internal const long BurstGapSeconds = 10 * 60;
    internal const long WindowSeconds = 24 * 3600;
    internal const long MinSpanSeconds = 2 * 3600;
    internal const int MinSamples = 3;
    internal const int MaxSamples = 400;

    internal enum CapacityState { Unknown, Ok, Low, Full }

    internal readonly record struct Sample(long Time, int Used, int Capacity);

    /// <summary>剩餘格數不超過這個值就算「快滿」。</summary>
    internal static int LowFreeSlots(int capacity) => Math.Max(1, capacity / 10);

    internal static CapacityState StateOf(bool known, int used, int capacity)
    {
        if(!known || capacity <= 0) return CapacityState.Unknown;
        var free = capacity - used;
        if(free <= 0) return CapacityState.Full;
        return free <= LowFreeSlots(capacity) ? CapacityState.Low : CapacityState.Ok;
    }

    /// <summary>記一筆樣本。減少（取出過）或容量改變（計畫掛法改了）時先清掉舊樣本：它們不再代表同一段累積。</summary>
    internal static void AddSample(List<Sample> s, Sample x)
    {
        if(s.Count > 0)
        {
            var last = s[^1];
            if(x.Used < last.Used || x.Capacity != last.Capacity) s.Clear();
            else if(x.Used == last.Used && x.Time - last.Time < SampleEverySeconds) return;
            else if(x.Time - last.Time < MergeWithinSeconds && s.Count > 1) s.RemoveAt(s.Count - 1);
        }
        s.Add(x);
        while(s.Count > MaxSamples || (s.Count > 0 && s[0].Time < x.Time - WindowSeconds)) s.RemoveAt(0);
    }

    /// <summary>
    /// 每小時增加幾格：最近 24 小時內的樣本，相鄰兩筆隔超過 3 小時的那段不計（當成沒在跑）。
    /// 一次登入裡（相隔不到 10 分鐘）的增加屬於「之前那段空檔」：空檔有算進時數才算它，所以第一次登入與長空檔後那次的增加不計。
    /// 樣本少於 3 筆或涵蓋不到 2 小時回 null＝不知道。
    /// </summary>
    internal static double? RatePerHour(List<Sample> s, long now)
    {
        if(s == null) return null;
        long span = 0, grow = 0;
        var count = 0;
        var counted = false;
        for(var i = 0; i < s.Count; i++)
        {
            if(s[i].Time < now - WindowSeconds) continue;
            count++;
            if(i == 0 || s[i - 1].Time < now - WindowSeconds) continue;
            var gap = s[i].Time - s[i - 1].Time;
            if(gap <= 0) continue;
            if(gap > MaxGapSeconds) counted = false;
            else if(gap >= BurstGapSeconds) counted = true;
            if(!counted) continue;
            span += gap;
            grow += Math.Max(0, s[i].Used - s[i - 1].Used);
        }
        if(count < MinSamples || span < MinSpanSeconds) return null;
        return grow * 3600.0 / span;
    }

    /// <summary>還要幾小時滿：已滿回 0，不知道回 null，最近沒有增加回 +∞。</summary>
    internal static double? EtaHours(bool known, int used, int capacity, double? rate)
    {
        if(!known || capacity <= 0) return null;
        var free = capacity - used;
        if(free <= 0) return 0;
        if(rate == null) return null;
        if(rate.Value <= 0) return double.PositiveInfinity;
        return free / rate.Value;
    }

    internal static string FormatHours(double? hours)
    {
        if(hours == null) return "?";
        if(double.IsPositiveInfinity(hours.Value)) return "-";
        if(hours.Value < 1) return "<1";
        return hours.Value >= 99 ? "99+" : Math.Round(hours.Value).ToString("0");
    }
}
