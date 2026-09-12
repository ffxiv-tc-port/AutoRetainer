using System.Threading;

namespace AutoRetainer.Modules;

/// <summary>
/// 「別的外掛請 AutoRetainer 先別動」的<b>具名、可計數、會逾時</b>的租約登記處。
/// 每一把租約有自己的 <see cref="Guid"/> 憑證，只要還有任何一把沒到期，AutoRetainer 就不動作。
/// </summary>
/// <remarks>
/// 🔴 <b>舊端點的語意完全沒有改變</b>：<c>SetSuppressed</c> 寫的是 <see cref="IPC.ManualSuppressed"/> 這個獨立的旗標，租約與它是 <b>OR</b> 關係。
/// 每一把租約有 <see cref="MaxLeaseMilliseconds"/> 的硬性壽命上限，長工作必須自己 <see cref="Renew"/> 續約（<see cref="RenewIntervalHintMs"/> 是建議的續約間隔，留了 10 倍餘裕）。
/// ⚠️ IPC 呼叫在呼叫端的執行緒上同步跑（沒有任何「一定在 Framework 執行緒」的保證），所以整張表用 lock 保護。 🔴 <b>鎖內絕不寫 log、絕不做檔案 I/O、絕不呼叫 ImGui</b>。 📌 <b>這不是自動接手鏈</b>。
/// ⚠️ <b>與 YesAlready 的差別只有「時間政策」，形狀完全一致。</b>這裡沿用 AutoRetainer 原本的 5 分鐘（<b>刻意不放寬</b>）。
/// </remarks>
internal static class SuppressionLeases
{
    /// <summary>沒指定時長時的預設租期。</summary>
    /// <remarks>
    /// 🔑 這個值同時是「租用者當掉之後 AutoRetainer 最久停擺多久」。
    /// 正常路徑上租用者停用時會自己 <see cref="Release"/>，走到逾時一律是異常。
    /// </remarks>
    internal const int DefaultLeaseMilliseconds = 300_000;

    /// <summary>單一把租約的<b>硬性</b>壽命上限。要求更長會被夾到這個值。</summary>
    /// <remarks>
    /// 🔴 這是「租用者當掉不能讓 AutoRetainer 永久停擺」的最後一道保險，<b>不是</b>建議值。
    /// 目前刻意與 <see cref="DefaultLeaseMilliseconds"/> 相同 —— 沿用改動前每把租約固定 5 分鐘的行為。
    /// </remarks>
    internal const int MaxLeaseMilliseconds = 300_000;

    /// <summary>建議租用者多久 <see cref="Renew"/> 一次。</summary>
    internal const long RenewIntervalHintMs = 30_000;

    /// <summary>租約上限：租用者忘了還、或每次都重新取得的話，這張表不會無限長大。</summary>
    private const int LeaseCap = 64;

    private sealed class Lease(Guid id, string owner, long expiresAt)
    {
        public Guid Id { get; } = id;
        public string Owner { get; } = owner;

        /// <summary><see cref="Environment.TickCount64"/> 座標系的到期時刻。</summary>
        public long ExpiresAt { get; set; } = expiresAt;

        /// <summary>續約時沿用的時長（<see cref="Renew"/> 不帶新時長時用）。</summary>
        public int DurationMs { get; set; }
    }

    private static readonly Dictionary<Guid, Lease> Leases = [];
    private static readonly object Gate = new();

    /// <summary><see cref="Leases"/> 的筆數快照，只給 <see cref="AnyActive"/> 的無鎖快路徑用。</summary>
    /// <remarks>
    /// 🔑 <see cref="IPC.Suppressed"/> 被<b>每一幀</b>讀好幾次（排程器、MultiMode、MiniTA……），
    /// 而絕大多數時候一把租約都沒有。零的時候直接回 false，連鎖都不用拿。
    /// 🔴 只有「零」這個方向可以無鎖：<c>0</c> 一定代表沒有租約（清空一定發生在寫 0 之前），
    /// 非零只代表「可能有」，一律進 lock 重新確認並清逾時。反過來寫（樂觀地相信非零）
    /// 會讓已經到期的租約繼續壓著。
    /// </remarks>
    private static int liveCount;

    /// <summary>現在有沒有任何一把還有效的租約（順便清掉逾時的）。</summary>
    internal static bool AnyActive
    {
        get
        {
            if(Volatile.Read(ref liveCount) == 0) return false;

            List<(bool IsWarning, string Message)> logs = null;
            bool active;

            lock(Gate)
            {
                PruneExpired(ref logs);
                active = Leases.Count > 0;
            }

            Flush(logs);
            return active;
        }
    }

    /// <summary>目前持有租約的外掛名稱與各自的剩餘毫秒數（給 UI 顯示用）。</summary>
    /// <remarks>
    /// 📌 同一個名字持有多把時<b>只留最晚到期的那一把</b> —— 使用者要看的是
    /// 「還要等多久才會自己解除」，不是「這個外掛開了幾把」。
    /// </remarks>
    internal static List<(string Owner, long RemainingMs)> Snapshot()
    {
        List<(bool IsWarning, string Message)> logs = null;
        Dictionary<string, long> byOwner = null;

        lock(Gate)
        {
            PruneExpired(ref logs);
            if(Leases.Count != 0)
            {
                var now = Environment.TickCount64;
                byOwner = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                foreach(var lease in Leases.Values)
                {
                    var remaining = lease.ExpiresAt - now;
                    if(remaining < 0) remaining = 0;
                    if(!byOwner.TryGetValue(lease.Owner, out var existing) || remaining > existing)
                    {
                        byOwner[lease.Owner] = remaining;
                    }
                }
            }
        }

        Flush(logs);
        if(byOwner == null) return [];

        var ret = new List<(string, long)>(byOwner.Count);
        foreach(var (owner, remaining) in byOwner) ret.Add((owner, remaining));
        return ret;
    }

    /// <summary>取得一把新的租約。回傳的 <see cref="Guid"/> 就是憑證。</summary>
    /// <param name="owner">租用者識別字串，慣例是對方外掛的 InternalName。</param>
    /// <param name="milliseconds">要求的租期；夾在 <c>1</c> 與 <see cref="MaxLeaseMilliseconds"/> 之間。</param>
    /// <returns>租約憑證；<see cref="Guid.Empty"/>＝沒拿到（名字空白、或已達 <see cref="LeaseCap"/>）。</returns>
    /// <remarks>
    /// 📌 <b>每次呼叫都是一把新的</b>（不是「同名就共用」）：同一個外掛內部有兩段序列並行時
    /// 各自持一把，先結束的那段放開自己那把不會影響另一段。
    /// </remarks>
    internal static Guid Acquire(string owner, int milliseconds)
    {
        if(string.IsNullOrWhiteSpace(owner))
        {
            PluginLog.Warning("[SuppressionLeases] 收到沒有帶名字的壓制租用請求，忽略。租用者必須帶一個識別字串（慣例是自己的 InternalName），否則使用者無從得知是誰壓著 AutoRetainer。");
            return Guid.Empty;
        }

        owner = owner.Trim();
        List<(bool IsWarning, string Message)> logs = null;
        var duration = ClampDuration(milliseconds, owner, ref logs);
        var id = Guid.NewGuid();
        var rejected = false;

        lock(Gate)
        {
            PruneExpired(ref logs);
            if(Leases.Count >= LeaseCap)
            {
                (logs ??= []).Add((true, $"[SuppressionLeases] 壓制租約已達上限 {LeaseCap} 把，拒絕「{owner}」的請求。目前持有者：{string.Join(", ", DistinctOwnersLocked())}"));
                rejected = true;
            }
            else
            {
                var firstForOwner = !HasOwnerLocked(owner);
                Leases[id] = new Lease(id, owner, Environment.TickCount64 + duration) { DurationMs = duration };
                Volatile.Write(ref liveCount, Leases.Count);

                if(firstForOwner)
                {
                    (logs ??= []).Add((false, $"[SuppressionLeases] 「{owner}」取得壓制租約 {id}（{duration}ms），AutoRetainer 的自動化在它還完之前不會動作。目前持有者：{string.Join(", ", DistinctOwnersLocked())}"));
                }
            }
        }

        Flush(logs);
        return rejected ? Guid.Empty : id;
    }

    /// <summary>交回一把租約。</summary>
    /// <returns><c>false</c>＝這把不存在（已經還過、或已經逾時被掃掉）。冪等。</returns>
    internal static bool Release(Guid id)
    {
        List<(bool IsWarning, string Message)> logs = null;
        var found = false;
        string owner = null;
        int left = 0;
        string remaining = null;

        lock(Gate)
        {
            if(!Leases.Remove(id, out var lease))
            {
                PruneExpired(ref logs);
                Volatile.Write(ref liveCount, Leases.Count);
            }
            else
            {
                found = true;
                owner = lease.Owner;
                PruneExpired(ref logs);
                Volatile.Write(ref liveCount, Leases.Count);
                left = Leases.Count;
                remaining = left == 0 ? "（壓制解除）" : $"：{string.Join(", ", DistinctOwnersLocked())}";
            }
        }

        Flush(logs);
        if(!found) return false;

        PluginLog.Information($"[SuppressionLeases] 「{owner}」歸還壓制租約 {id}，剩餘 {left} 把{remaining}。");
        return true;
    }

    /// <summary>續約（心跳），沿用取得時的租期或指定新的租期。</summary>
    /// <param name="id">租約憑證。</param>
    /// <param name="milliseconds">新的租期；<c>null</c>＝沿用取得時的時長。</param>
    /// <returns>
    /// <c>false</c>＝<b>這把已經不在了</b>，呼叫端必須重新 <see cref="Acquire"/>，
    /// <b>不要當成續約成功</b>（那段期間 AutoRetainer 是醒著的）。
    /// </returns>
    internal static bool Renew(Guid id, int? milliseconds = null)
    {
        List<(bool IsWarning, string Message)> logs = null;
        bool renewed;

        lock(Gate)
        {
            PruneExpired(ref logs);
            Volatile.Write(ref liveCount, Leases.Count);
            if(Leases.TryGetValue(id, out var lease))
            {
                var duration = milliseconds is { } ms ? ClampDuration(ms, lease.Owner, ref logs) : lease.DurationMs;
                lease.DurationMs = duration;

                // 🔴 取 max：續約永遠只會往後延，不會把已經談好的到期時間往前搬。
                var until = Environment.TickCount64 + duration;
                if(until > lease.ExpiresAt) lease.ExpiresAt = until;
                renewed = true;
            }
            else
            {
                renewed = false;
            }
        }

        Flush(logs);
        return renewed;
    }

    /// <summary>把所有租約一次清掉。</summary>
    /// <remarks>使用者在主視窗按「取消」時的逃生口，以及外掛卸載時的收尾。</remarks>
    internal static void ReleaseAll(string reason)
    {
        List<(bool IsWarning, string Message)> logs = null;

        lock(Gate)
        {
            if(Leases.Count == 0)
            {
                Volatile.Write(ref liveCount, 0);
            }
            else
            {
                (logs ??= []).Add((false, $"[SuppressionLeases] 清掉全部 {Leases.Count} 把壓制租約（{reason}）：{string.Join(", ", DistinctOwnersLocked())}"));
                Leases.Clear();
                Volatile.Write(ref liveCount, 0);
            }
        }

        Flush(logs);
    }

    /// <summary>租期夾限的「只講一次」去重表。</summary>
    /// <remarks>
    /// 🔴 這裡<b>不能</b>用 <c>ECommons.Throttlers.EzThrottler</c>：它是整個外掛共用的靜態
    /// <c>Dictionary</c> 且零同步，而 <see cref="Acquire"/>／<see cref="Renew"/> 是從 IPC 端點進來的
    /// —— 跑在<b>呼叫端的執行緒</b>上，會與 framework 執行緒並行插入。失敗形式不是「拿到舊值」
    /// 而是<b>字典本身壞掉</b>，還會連帶弄壞這個外掛內所有模組的節流。所以自帶一張表和自己的鎖。
    /// </remarks>
    private static readonly HashSet<string> ClampNotified = [];

    private static readonly object ClampGate = new();

    /// <summary>把要求的租期夾進合法範圍；<b>真的被夾到時寫一次 <c>Information</c></b>。</summary>
    /// <remarks>
    /// 🔴 <b>靜默夾限是壞的失敗形式</b>：夾到就講一次，讓「我的租約怎麼提早失效」在實機 log 上有跡可循。
    /// 📌 只在<b>真的夾到</b>時寫，而且同一個（租用者，要求值）只寫一次。
    /// </remarks>
    private static int ClampDuration(int milliseconds, string owner, ref List<(bool IsWarning, string Message)> logs)
    {
        var clamped = milliseconds < 1 ? 1 : milliseconds > MaxLeaseMilliseconds ? MaxLeaseMilliseconds : milliseconds;
        if(clamped == milliseconds) return clamped;

        lock(ClampGate)
        {
            // 這張表只為了去重，不能無限長大（呼叫端可能每次帶不同的要求值）。滿了就整個丟掉重來，
            // 代價只是同一組合可能再講一次，比無限制成長好。
            if(ClampNotified.Count >= LeaseCap * 4) ClampNotified.Clear();
            if(!ClampNotified.Add($"{owner}|{milliseconds}")) return clamped;
        }

        (logs ??= []).Add((false, $"[SuppressionLeases]「{owner}」要求 {milliseconds} ms 的壓制租期，實際給 {clamped} ms（硬性上限 {MaxLeaseMilliseconds} ms）。長工作要自己每 {RenewIntervalHintMs} ms 續約一次，不要假設拿到了要求的時長。"));
        return clamped;
    }

    /// <summary>這個名字現在有沒有租約。<b>呼叫端必須已經持有 <see cref="Gate"/>。</b></summary>
    private static bool HasOwnerLocked(string owner)
    {
        foreach(var lease in Leases.Values)
        {
            if(string.Equals(lease.Owner, owner, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>目前的租用者名字（去重）。<b>呼叫端必須已經持有 <see cref="Gate"/>。</b></summary>
    private static List<string> DistinctOwnersLocked()
    {
        var ret = new List<string>();
        foreach(var lease in Leases.Values)
        {
            if(!ret.Contains(lease.Owner, StringComparer.OrdinalIgnoreCase)) ret.Add(lease.Owner);
        }
        return ret;
    }

    /// <summary>清掉已經逾時的租約。<b>呼叫端必須已經持有 <see cref="Gate"/>。</b></summary>
    private static void PruneExpired(ref List<(bool IsWarning, string Message)> logs)
    {
        if(Leases.Count == 0)
        {
            Volatile.Write(ref liveCount, 0);
            return;
        }

        var now = Environment.TickCount64;
        List<Guid> expired = null;
        foreach(var (id, lease) in Leases)
        {
            if(now >= lease.ExpiresAt) (expired ??= []).Add(id);
        }

        if(expired == null) return;
        foreach(var id in expired)
        {
            var owner = Leases[id].Owner;
            Leases.Remove(id);

            // 🔴 寫 Information：租約逾時＝「有人壓著 AutoRetainer 卻沒續約」，
            // 這一行是使用者回報「AutoRetainer 突然不動了／突然又動了」時唯一的線索。
            (logs ??= []).Add((false, $"[SuppressionLeases] 「{owner}」的壓制租約 {id} 逾時（超過租期沒有續約）自動解除 —— 那個外掛多半已經停用或當掉。AutoRetainer 恢復正常運作。"));
        }

        Volatile.Write(ref liveCount, Leases.Count);
    }

    /// <summary>把鎖內收集到的診斷訊息寫出去。<b>一定要在鎖外呼叫。</b></summary>
    /// <remarks>
    /// 🔴 <b>鎖內不寫 log</b>：所以逾時／夾值／滿載訊息在鎖內先收進一個 list，出了鎖才送出去。
    /// 🔴 <b>等級跟著訊息走</b>，不是一律 <c>Information</c>。
    /// </remarks>
    private static void Flush(List<(bool IsWarning, string Message)> logs)
    {
        if(logs == null) return;

        foreach(var (isWarning, message) in logs)
        {
            if(isWarning) PluginLog.Warning(message);
            else PluginLog.Information(message);
        }
    }
}
