using System.Threading;

namespace AutoRetainer.Modules;

/// <summary>
/// 「別的外掛請 AutoRetainer 先別動」的<b>具名、可計數、會逾時</b>的租用表。
/// </summary>
/// <remarks>
/// 🔴 <b>為什麼需要這個</b>：舊的 <c>AutoRetainer.SetSuppressed(bool)</c> 是一個<b>無主的單一布林</b>。
/// 兩個以上的外掛同時想壓制 AutoRetainer 時，<b>誰先結束誰就把別人的壓制一起解除</b>
/// （Artisan 在做僱員補貨、ICE 在跑宇宙任務、GatherBuddyReborn 在自動採集 —— 這三者會同時發生）。
/// 這裡改成「一個租用者一筆租約，全部還完才真的解除」。<br/>
/// <br/>
/// 🔴 <b>舊端點的語意完全沒有改變</b>：<c>SetSuppressed</c> 寫的是
/// <see cref="IPC.ManualSuppressed"/> 這個獨立的旗標，租約與它是 <b>OR</b> 關係。
/// 既有消費端（Artisan 帶著 <c>ReEnable</c> 所有權旗標的那套）行為逐字不變。<br/>
/// <br/>
/// 🔴 <b>逾時是必要的</b>：租用者當掉／被強制卸載／忘了還，AutoRetainer 不能因此永久停擺。
/// 每一筆租約有 <see cref="LeaseTimeoutMs"/> 的壽命，租用者必須週期性重新 <see cref="Acquire"/>
/// 續租（<see cref="RenewIntervalHintMs"/> 是建議的續租間隔，留了 10 倍餘裕）。
/// 逾時解除會寫 <c>Information</c>（使用者跑 LogLevel 2），這是「某個外掛沒還租約」唯一看得見的證據。<br/>
/// <br/>
/// 📌 <b>這不是自動接手鏈</b>：租約只會讓 AutoRetainer <b>不做事</b>，不觸發任何新的自動化。<br/>
/// ⚠️ IPC 呼叫多半來自 Framework 執行緒，但沒有任何保證，所以整張表用 lock 保護。
/// </remarks>
internal static class SuppressionLeases
{
    /// <summary>一筆租約沒有續租的話，多久之後自動失效。</summary>
    /// <remarks>
    /// 🔑 這個值同時是「租用者當掉之後 AutoRetainer 最久停擺多久」。
    /// 正常路徑上租用者停用時會自己 <see cref="Release"/>，走到逾時一律是異常。
    /// </remarks>
    internal const long LeaseTimeoutMs = 300_000;

    /// <summary>建議租用者多久重新 <see cref="Acquire"/> 一次（重複取得同一個 owner 就是續租）。</summary>
    internal const long RenewIntervalHintMs = 30_000;

    /// <summary>租約上限：租用者名字打錯／每次帶不同字串的話，這張表不會無限長大。</summary>
    private const int OwnerCap = 32;

    private static readonly Dictionary<string, long> Leases = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();

    /// <summary><see cref="Leases"/> 的筆數快照，只給 <see cref="AnyActive"/> 的無鎖快路徑用。</summary>
    /// <remarks>
    /// 🔑 <see cref="IPC.Suppressed"/> 被<b>每一幀</b>讀好幾次（排程器、MultiMode、MiniTA……），
    /// 而絕大多數時候一筆租約都沒有。零的時候直接回 false，連鎖都不用拿。
    /// 只有「零」這個判斷可以無鎖：非零時一律進 lock 重新確認並清逾時。
    /// </remarks>
    private static int liveCount;

    /// <summary>現在有沒有任何一筆還有效的租約（順便清掉逾時的）。</summary>
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
    internal static List<(string Owner, long RemainingMs)> Snapshot()
    {
        lock(Gate)
        {
            PruneExpired();
            var now = Environment.TickCount64;
            var ret = new List<(string, long)>(Leases.Count);
            foreach(var (owner, expiry) in Leases) ret.Add((owner, expiry - now));
            return ret;
        }
    }

    /// <summary>取得（或續租）一筆租約。</summary>
    /// <param name="owner">租用者識別字串，慣例是對方外掛的 InternalName。</param>
    /// <returns><c>true</c>＝這一刻 <paramref name="owner"/> 確實持有租約。名字空白時回 <c>false</c> 且什麼都不做。</returns>
    internal static bool Acquire(string owner)
    {
        if(string.IsNullOrWhiteSpace(owner))
        {
            PluginLog.Warning("[SuppressionLeases] 收到沒有帶名字的壓制租用請求，忽略。租用者必須帶一個識別字串（慣例是自己的 InternalName），否則還租約時無法分辨是誰的。");
            return false;
        }

        owner = owner.Trim();
        lock(Gate)
        {
            PruneExpired();
            var isNew = !Leases.ContainsKey(owner);
            if(isNew && Leases.Count >= OwnerCap)
            {
                PluginLog.Warning($"[SuppressionLeases] 壓制租約已達上限 {OwnerCap} 筆，拒絕「{owner}」的請求。目前持有者：{string.Join(", ", Leases.Keys)}");
                return false;
            }

            Leases[owner] = Environment.TickCount64 + LeaseTimeoutMs;
            Volatile.Write(ref liveCount, Leases.Count);
            if(isNew)
            {
                PluginLog.Information($"[SuppressionLeases] 「{owner}」取得壓制租約，AutoRetainer 的自動化在它還完之前不會動作。目前持有者共 {Leases.Count} 個：{string.Join(", ", Leases.Keys)}");
            }
            return true;
        }
    }

    /// <summary>歸還一筆租約。</summary>
    /// <returns><c>true</c>＝呼叫完之後 <paramref name="owner"/> 確定不再持有租約（本來就沒有也算）。</returns>
    internal static bool Release(string owner)
    {
        if(string.IsNullOrWhiteSpace(owner)) return false;

        owner = owner.Trim();
        lock(Gate)
        {
            var removed = Leases.Remove(owner);
            PruneExpired();
            Volatile.Write(ref liveCount, Leases.Count);
            if(removed)
            {
                PluginLog.Information($"[SuppressionLeases] 「{owner}」歸還壓制租約，剩餘持有者 {Leases.Count} 個{(Leases.Count == 0 ? "（壓制解除）" : $"：{string.Join(", ", Leases.Keys)}")}。");
            }
            return true;
        }
    }

    /// <summary>把所有租約一次清掉。</summary>
    /// <remarks>使用者在主視窗按「取消」時的逃生口，以及外掛卸載時的收尾。</remarks>
    internal static void ReleaseAll(string reason)
    {
        lock(Gate)
        {
            if(Leases.Count == 0) return;
            PluginLog.Information($"[SuppressionLeases] 清掉全部 {Leases.Count} 筆壓制租約（{reason}）：{string.Join(", ", Leases.Keys)}");
            Leases.Clear();
            Volatile.Write(ref liveCount, 0);
        }
    }

    /// <summary>清掉已經逾時的租約。<b>呼叫端必須已經持有 <see cref="Gate"/>。</b></summary>
    private static void PruneExpired()
    {
        if(Leases.Count == 0) return;

        var now = Environment.TickCount64;
        List<string> expired = null;
        foreach(var (owner, expiry) in Leases)
        {
            if(now >= expiry) (expired ??= []).Add(owner);
        }

        if(expired == null) return;
        foreach(var owner in expired)
        {
            Leases.Remove(owner);
            Volatile.Write(ref liveCount, Leases.Count);
            PluginLog.Information($"[SuppressionLeases] 「{owner}」的壓制租約逾時（超過 {LeaseTimeoutMs}ms 沒有續租）自動解除 —— 那個外掛多半已經停用或當掉。AutoRetainer 恢復正常運作。");
        }
    }
}
