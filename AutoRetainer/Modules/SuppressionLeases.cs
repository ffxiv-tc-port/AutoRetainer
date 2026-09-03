using System.Threading;

namespace AutoRetainer.Modules;

/// <summary>
/// 「別的外掛請 AutoRetainer 先別動」的<b>具名、可計數、會逾時</b>的租約登記處。
/// 每一把租約有自己的 <see cref="Guid"/> 憑證，只要還有任何一把沒到期，AutoRetainer 就不動作。
/// </summary>
/// <remarks>
/// 🔴 <b>為什麼需要這個</b>：舊的 <c>AutoRetainer.SetSuppressed(bool)</c> 是一個<b>無主的單一布林</b>。
/// 兩個以上的外掛同時想壓制 AutoRetainer 時，<b>誰先結束誰就把別人的壓制一起解除</b>
/// （Artisan 在做僱員補貨、ICE 在跑宇宙任務、GatherBuddyReborn 在自動採集 —— 這三者會同時發生）。
/// 這裡改成「一把租約一個憑證，全部還完才真的解除」。<br/>
/// <br/>
/// 🔴 <b>舊端點的語意完全沒有改變</b>：<c>SetSuppressed</c> 寫的是
/// <see cref="IPC.ManualSuppressed"/> 這個獨立的旗標，租約與它是 <b>OR</b> 關係。
/// 既有消費端（Artisan 帶著 <c>ReEnable</c> 所有權旗標的那套、Marketbuddy 的 AutoRetainerBridge）行為逐字不變。<br/>
/// <br/>
/// 🔴 <b>逾時是必要的</b>：租用者當掉／被強制卸載／忘了還，AutoRetainer 不能因此永久停擺。
/// 每一把租約有 <see cref="MaxLeaseMilliseconds"/> 的硬性壽命上限，長工作必須自己
/// <see cref="Renew"/> 續約（<see cref="RenewIntervalHintMs"/> 是建議的續約間隔，留了 10 倍餘裕）。
/// 逾時解除會寫 <c>Information</c>，這是「某個外掛沒還租約」唯一看得見的證據。<br/>
/// <br/>
/// 📌 <b>這不是自動接手鏈</b>：租約只會讓 AutoRetainer <b>不做事</b>，不觸發任何新的自動化。<br/>
/// ⚠️ IPC 呼叫在呼叫端的執行緒上同步跑（沒有任何「一定在 Framework 執行緒」的保證），所以整張表用 lock 保護。
/// </remarks>
/// <remarks>
/// 🔑🔑 <b>形狀為什麼是 <see cref="Guid"/> 憑證，而不是「用租用者名字當鍵」</b>
/// （2026-09-03 與 YesAlready 的 <c>SuppressionLeases</c> 統一；那邊本來就是這個形狀）：
/// <list type="number">
/// <item><b>同一個外掛可以有兩段並行的序列。</b>名字當鍵的話兩段共用一筆租約，先結束的那段
/// 一還就把另一段的壓制也解掉 —— 那正是這整組改動要消滅的 bug，只是從「跨外掛」搬到「同外掛內」。
/// Marketbuddy 就是活例子：BatchDelist／BatchReprice／MultiRetainerTour／QuickLister 四條流程
/// 各自獨立，它現在得自己在外掛內手刻一層 refcount 才敢碰那個無主布林。</item>
/// <item><b><see cref="Release"/> 的回傳值才有資訊。</b>名字當鍵的版本對任何非空字串都回
/// <c>true</c>（「本來就沒有也算成功」）—— 呼叫端<b>永遠分不出</b>「我剛還掉一把」與
/// 「我那把早就逾時被掃掉了」。憑證版回 <c>false</c> 就是明確的「這把不存在」。</item>
/// <item><b>續約與取得分得開。</b><see cref="Renew"/> 回 <c>false</c> 是呼叫端唯一能知道
/// 「我的租約中途斷過、AutoRetainer 那段時間是醒著的」的管道。名字當鍵的版本把
/// acquire 與 renew 合成一個冪等呼叫，這個轉換完全看不見。</item>
/// <item><b>AutoDuty 已經在用憑證形狀</b>（對 YesAlready），所以統一到憑證這邊，
/// 跨外掛的呼叫端只要記一種形狀。</item>
/// </list>
/// <para>
/// ⚠️ <b>與 YesAlready 的差別只有「時間政策」，形狀完全一致。</b>
/// YesAlready 的預設租期 10 分鐘、上限 60 分鐘；這裡沿用 AutoRetainer 原本的 5 分鐘
/// （<b>刻意不放寬</b>：那個數字同時是「租用者當掉之後 AutoRetainer 最久停擺多久」，
/// 放寬等於回退既有行為）。要求更長的租期會被<b>夾到</b> <see cref="MaxLeaseMilliseconds"/>，
/// 所以呼叫端一律要照 <see cref="RenewIntervalHintMs"/> 續約，不要假設自己拿到了要求的時長。
/// </para>
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
            lock(Gate)
            {
                PruneExpired();
                return Leases.Count > 0;
            }
        }
    }

    /// <summary>目前持有租約的外掛名稱與各自的剩餘毫秒數（給 UI 顯示用）。</summary>
    /// <remarks>
    /// 📌 同一個名字持有多把時<b>只留最晚到期的那一把</b> —— 使用者要看的是
    /// 「還要等多久才會自己解除」，不是「這個外掛開了幾把」。
    /// </remarks>
    internal static List<(string Owner, long RemainingMs)> Snapshot()
    {
        lock(Gate)
        {
            PruneExpired();
            if(Leases.Count == 0) return [];

            var now = Environment.TickCount64;
            var byOwner = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach(var lease in Leases.Values)
            {
                var remaining = lease.ExpiresAt - now;
                if(remaining < 0) remaining = 0;
                if(!byOwner.TryGetValue(lease.Owner, out var existing) || remaining > existing)
                {
                    byOwner[lease.Owner] = remaining;
                }
            }

            var ret = new List<(string, long)>(byOwner.Count);
            foreach(var (owner, remaining) in byOwner) ret.Add((owner, remaining));
            return ret;
        }
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
        var duration = ClampDuration(milliseconds);
        var id = Guid.NewGuid();

        lock(Gate)
        {
            PruneExpired();
            if(Leases.Count >= LeaseCap)
            {
                PluginLog.Warning($"[SuppressionLeases] 壓制租約已達上限 {LeaseCap} 把，拒絕「{owner}」的請求。目前持有者：{string.Join(", ", DistinctOwnersLocked())}");
                return Guid.Empty;
            }

            var firstForOwner = !HasOwnerLocked(owner);
            Leases[id] = new Lease(id, owner, Environment.TickCount64 + duration) { DurationMs = duration };
            Volatile.Write(ref liveCount, Leases.Count);

            if(firstForOwner)
            {
                PluginLog.Information($"[SuppressionLeases] 「{owner}」取得壓制租約 {id}（{duration}ms），AutoRetainer 的自動化在它還完之前不會動作。目前持有者：{string.Join(", ", DistinctOwnersLocked())}");
            }
        }

        return id;
    }

    /// <summary>交回一把租約。</summary>
    /// <returns><c>false</c>＝這把不存在（已經還過、或已經逾時被掃掉）。冪等。</returns>
    internal static bool Release(Guid id)
    {
        string owner;
        int left;
        string remaining;

        lock(Gate)
        {
            if(!Leases.Remove(id, out var lease))
            {
                PruneExpired();
                Volatile.Write(ref liveCount, Leases.Count);
                return false;
            }

            owner = lease.Owner;
            PruneExpired();
            Volatile.Write(ref liveCount, Leases.Count);
            left = Leases.Count;
            remaining = left == 0 ? "（壓制解除）" : $"：{string.Join(", ", DistinctOwnersLocked())}";
        }

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
        lock(Gate)
        {
            PruneExpired();
            Volatile.Write(ref liveCount, Leases.Count);
            if(!Leases.TryGetValue(id, out var lease)) return false;

            var duration = milliseconds is { } ms ? ClampDuration(ms) : lease.DurationMs;
            lease.DurationMs = duration;

            // 🔴 取 max：續約永遠只會往後延，不會把已經談好的到期時間往前搬。
            var until = Environment.TickCount64 + duration;
            if(until > lease.ExpiresAt) lease.ExpiresAt = until;
            return true;
        }
    }

    /// <summary>把所有租約一次清掉。</summary>
    /// <remarks>使用者在主視窗按「取消」時的逃生口，以及外掛卸載時的收尾。</remarks>
    internal static void ReleaseAll(string reason)
    {
        lock(Gate)
        {
            if(Leases.Count == 0)
            {
                Volatile.Write(ref liveCount, 0);
                return;
            }

            PluginLog.Information($"[SuppressionLeases] 清掉全部 {Leases.Count} 把壓制租約（{reason}）：{string.Join(", ", DistinctOwnersLocked())}");
            Leases.Clear();
            Volatile.Write(ref liveCount, 0);
        }
    }

    private static int ClampDuration(int milliseconds)
        => milliseconds < 1 ? 1 : milliseconds > MaxLeaseMilliseconds ? MaxLeaseMilliseconds : milliseconds;

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
    private static void PruneExpired()
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
            PluginLog.Information($"[SuppressionLeases] 「{owner}」的壓制租約 {id} 逾時（超過租期沒有續約）自動解除 —— 那個外掛多半已經停用或當掉。AutoRetainer 恢復正常運作。");
        }

        Volatile.Write(ref liveCount, Leases.Count);
    }
}
