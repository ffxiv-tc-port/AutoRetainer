using AutoRetainer.Internal;
using AutoRetainer.Services;
using AutoRetainerAPI.Configuration;
using ECommons.Events;
using ECommons.GameHelpers;
using static AutoRetainer.Modules.EntrustCapacityMath;

namespace AutoRetainer.Modules;

/// <summary>
/// 稀有品存放容量：掛著「只存可繳交稀有品」計畫（或稀有品繳交循環指定的計畫）的僱員，背包已用格數合計與預估幾小時後滿。
/// 契約：只顯示與記錄，不做任何動作。已用格數取自僱員清單（<see cref="OfflineRetainerData.ItemCount"/>，-1＝沒讀過）；
/// 速度樣本只存在記憶體，插件重載即清。全部只在框架執行緒呼叫（<see cref="Tick"/> 與繪製）。
/// </summary>
internal static class EntrustCapacityForecast
{
    internal const int RetainerCapacity = 7 * 25;
    private const long StaleAfterSeconds = 6 * 3600;

    private static readonly Dictionary<ulong, List<Sample>> Samples = [];
    private static readonly Dictionary<ulong, long> LiveSeenAt = [];
    private static readonly Dictionary<ulong, CapacityState> States = [];
    private static readonly Dictionary<(ulong CID, string Name), string> AdditionalDataKeys = [];
    private static long NextTickAt;
    private static bool TickFailed;

    /// <summary>這個計畫的僱員算不算「稀有品存放」的容量。</summary>
    internal static bool IsDeliverablePlan(Guid guid)
    {
        if(guid == Guid.Empty) return false;
        if(guid == C.ExpertDeliveryLoopEntrustPlan) return true;
        foreach(var p in C.EntrustPlans)
        {
            if(p.Guid == guid) return p.EntrustOnlyDeliverableGear;
        }
        return false;
    }

    // 刻意用不建立條目的查法：Utils.GetAdditionalData 會替查不到的鍵補一筆空資料，而這裡每秒掃過每個角色的每個僱員。
    private static bool IsPlanRetainer(ulong cid, string name)
    {
        if(name.IsNullOrEmpty()) return false;
        if(!AdditionalDataKeys.TryGetValue((cid, name), out var key))
        {
            key = Utils.GetAdditionalDataKey(cid, name, false);
            AdditionalDataKeys[(cid, name)] = key;
        }
        return C.AdditionalData.TryGetValue(key, out var ad) && IsDeliverablePlan(ad.EntrustPlan);
    }

    internal record struct Measurement(int PlanRetainers, int Unknown, int Used, long VentureProxyAt)
    {
        public readonly int Capacity => PlanRetainers * RetainerCapacity;
        public readonly bool Known => PlanRetainers > 0 && Unknown == 0;
        public readonly int Free => Capacity - Used;
    }

    private static void Accumulate(ref Measurement m, ulong cid, string name, int itemCount)
    {
        if(!IsPlanRetainer(cid, name)) return;
        m.PlanRetainers++;
        if(itemCount < 0) m.Unknown++;
        else m.Used += Math.Clamp(itemCount, 0, RetainerCapacity);
    }

    /// <summary>離線資料裡這個角色的量測。<c>VentureProxyAt</c>＝任一僱員最後一次在傳喚鈴前派出探險的時間：清單快照至少新到那一刻。</summary>
    internal static Measurement Measure(OfflineCharacterData data)
    {
        var m = new Measurement();
        foreach(var r in data.RetainerData)
        {
            if(r == null) continue;
            if(r.VentureBeginsAt > m.VentureProxyAt) m.VentureProxyAt = r.VentureBeginsAt;
            Accumulate(ref m, data.CID, r.Name, r.ItemCount);
        }
        return m;
    }

    /// <summary>資料最後確定是新的時間（unix 秒）；0＝不知道。</summary>
    private static long FreshAt(ulong cid, in Measurement m, long now)
    {
        var at = m.VentureProxyAt;
        if(LiveSeenAt.TryGetValue(cid, out var live) && live > at) at = live;
        return Math.Min(at, now);
    }

    private static double? Rate(ulong cid, long now) => Samples.TryGetValue(cid, out var s) ? RatePerHour(s, now) : null;

    /// <summary>只顯示用的功能：例外只記一次，不讓同一個 Update 處理器後面的模組那一幀被跳過。</summary>
    internal static void SafeTick()
    {
        try
        {
            Tick();
        }
        catch(Exception e)
        {
            if(TickFailed) return;
            TickFailed = true;
            PluginLog.Warning($"[StorageForecast] Tick failed, further errors are not logged: {e}");
        }
    }

    /// <summary>目前登入的角色：讀僱員清單現值記一筆樣本，容量狀態翻轉時記錄。每 5 秒最多做一次。</summary>
    internal static void Tick()
    {
        var nowMs = Environment.TickCount64;
        if(nowMs < NextTickAt) return;
        NextTickAt = nowMs + 5000;
        if(!ProperOnLogin.PlayerPresent || !Player.IsInHomeWorld) return;
        if(!GameRetainerManager.Ready || GameRetainerManager.Count == 0) return;
        var cid = Svc.PlayerState.ContentId;
        var m = new Measurement();
        foreach(var r in GameRetainerManager.Retainers)
        {
            if(r.RetainerID == 0 || !r.Available) continue;
            Accumulate(ref m, cid, r.Name, r.ItemCount);
        }
        if(!m.Known) return;
        var now = P.Time;
        LiveSeenAt[cid] = now;
        if(!Samples.TryGetValue(cid, out var s)) Samples[cid] = s = [];
        AddSample(s, new(now, m.Used, m.Capacity));
        ReportTransition(cid, m, now);
    }

    private static void ReportTransition(ulong cid, in Measurement m, long now)
    {
        var state = StateOf(m.Known, m.Used, m.Capacity);
        var prev = States.GetValueOrDefault(cid, CapacityState.Unknown);
        if(state == prev) return;
        States[cid] = state;
        if(prev == CapacityState.Unknown && state == CapacityState.Ok) return;
        var rate = Rate(cid, now);
        var eta = EtaHours(m.Known, m.Used, m.Capacity, rate);
        PluginLog.Information($"[StorageForecast] {Censor.Character(Player.NameWithWorld ?? "")}: {prev} -> {state}, {m.Used}/{m.Capacity} across {m.PlanRetainers} retainer(s), rate={(rate == null ? "?" : rate.Value.ToString("0.0"))}/h, eta={FormatHours(eta)}h");
        if(state == CapacityState.Full)
        {
            S.AnomalyWindow?.Add(string.Format(Loc.T("The retainers with a rare item (deliverable gear) entrust plan are full ({0}/{1}). New rare items will stay in your inventory, and ventures stop being collected once it runs out of space. Consider running the expert delivery loop."), m.Used, m.Capacity));
        }
        else if(state == CapacityState.Low && prev is CapacityState.Ok or CapacityState.Unknown)
        {
            S.AnomalyWindow?.Add(string.Format(Loc.T("The retainers with a rare item (deliverable gear) entrust plan are almost full ({0}/{1}, {2} slots left); at the recent rate they are full in about {3} hours. Consider running the expert delivery loop."), m.Used, m.Capacity, m.Free, FormatHours(eta)));
        }
    }

    #region 角色列「E」欄

    private sealed class ColumnCache
    {
        public long NextAt;
        public int Signature;
        public (bool Warning, string Text, bool Unknown, string Tooltip) Column;
    }

    private static readonly Dictionary<ulong, ColumnCache> Columns = [];

    /// <summary>角色列右側「E」欄。每個角色每秒最多重算一次，輸入沒變就沿用同一組字串（繪製每幀呼叫）。</summary>
    internal static (bool Warning, string Text, bool Unknown, string Tooltip) GetColumn(OfflineCharacterData data)
    {
        var nowMs = Environment.TickCount64;
        if(Columns.TryGetValue(data.CID, out var c) && nowMs < c.NextAt) return c.Column;
        if(c == null) Columns[data.CID] = c = new();
        c.NextAt = nowMs + 1000;
        var now = P.Time;
        var m = Measure(data);
        var freshAt = FreshAt(data.CID, m, now);
        var rate = Rate(data.CID, now);
        var eta = EtaHours(m.Known, m.Used, m.Capacity, rate);
        var age = freshAt <= 0 ? -1 : now - freshAt;
        var h = new HashCode();
        h.Add(m);
        h.Add(age < 0 ? -1 : age < 3600 ? age / 60 : 1000 + (age / 3600));
        h.Add(FormatHours(eta));
        h.Add(rate == null ? -1 : Math.Round(rate.Value, 1));
        h.Add(data.InventorySpace);
        foreach(var r in data.RetainerData) h.Add(r?.ItemCount ?? -2);
        var sig = h.ToHashCode();
        if(c.Column.Text != null && c.Signature == sig) return c.Column;
        c.Signature = sig;
        c.Column = BuildColumn(data, m, freshAt, age, rate, eta, now);
        return c.Column;
    }

    private static (bool Warning, string Text, bool Unknown, string Tooltip) BuildColumn(OfflineCharacterData data, in Measurement m, long freshAt, long age, double? rate, double? eta, long now)
    {
        var title = Loc.T("Rare item storage: retainers with an entrust plan that only takes deliverable rare items (or the plan chosen for the expert delivery loop).");
        if(m.PlanRetainers == 0)
        {
            return (false, "E: -", true, $"{title}\n{Loc.T("No retainer on this character uses such a plan.")}");
        }
        var sb = new StringBuilder();
        sb.Append(title).Append('\n');
        foreach(var r in data.RetainerData)
        {
            if(r == null || !IsPlanRetainer(data.CID, r.Name)) continue;
            sb.Append("  ").Append(Censor.Retainer(r.Name)).Append(": ");
            sb.Append(r.ItemCount < 0 ? "?" : $"{Math.Clamp(r.ItemCount, 0, RetainerCapacity)}/{RetainerCapacity}").Append('\n');
        }
        sb.Append(Loc.T("Player inventory free slots")).Append(": ").Append(data.InventorySpace).Append('\n');
        // 「多舊」只能給下界保證：清單快照至少新到 freshAt；不知道或超過 6 小時一律灰字。
        var stale = freshAt <= 0 || now - freshAt > StaleAfterSeconds;
        if(age < 0) sb.Append(Loc.T("It is not known how old these numbers are; they refresh whenever this character is logged in.")).Append('\n');
        else
        {
            sb.Append(string.Format(Loc.T("Last confirmed about {0} ago."), age < 3600 ? string.Format(Loc.T("{0} min"), age / 60) : string.Format(Loc.T("{0} h"), age / 3600))).Append('\n');
            if(stale) sb.Append(Loc.T("Not refreshed for more than 6 hours; the real numbers may be higher. They refresh whenever this character is logged in.")).Append('\n');
        }
        if(!m.Known)
        {
            sb.Append(Loc.T("Some of these retainers have never been read yet (shown as ?)."));
            return (false, $"E: ?/{m.Capacity}", true, sb.ToString());
        }
        sb.Append('\n');
        if(m.Free <= 0) sb.Append(Loc.T("Full: new rare items stay in your inventory."));
        else if(rate == null) sb.Append(Loc.T("Time until full: not enough data yet. It needs at least 3 readings covering 2 hours while this character is logged in; readings are only kept until the plugin reloads."));
        else if(rate.Value <= 0) sb.Append(Loc.T("Time until full: these retainers have not grown recently."));
        else sb.Append(string.Format(Loc.T("Full in about {0} h at {1:0.0} slots per hour."), FormatHours(eta), rate.Value));
        sb.Append('\n').Append(Loc.T("The rate is how much these retainers grew over the last 24 hours (since items were last taken out), divided by the hours covered; stretches of more than 3 hours without this character logged in are left out."));
        var noEta = m.Free <= 0 || (rate is { } r0 && r0 <= 0);
        var text = noEta ? $"E: {m.Used}/{m.Capacity}" : $"E: {m.Used}/{m.Capacity} ~{FormatHours(eta)}h";
        return (m.Free <= LowFreeSlots(m.Capacity), text, stale, sb.ToString());
    }

    #endregion
}
