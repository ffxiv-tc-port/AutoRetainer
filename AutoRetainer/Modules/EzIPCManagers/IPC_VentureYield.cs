using AutoRetainerAPI.Configuration;
using ECommons.EzIpcManager;
using Lumina.Excel.Sheets;

namespace AutoRetainer.Modules.EzIPCManagers;

/// <summary>
/// 探險帶回數量的唯讀端點，前綴 <c>AutoRetainer.VentureYield.</c>；數量一律由 <see cref="VentureUtils"/> 算。
/// </summary>
/// <remarks>
/// <para>回傳只用 CoreLib 型別（ValueTuple、陣列、字串），兩邊是同一個 <c>Type</c>、不走 JSON。要改形狀就開新名字，不改這幾支。</para>
/// <para>三支查詢都經 <see cref="IpcFrameworkGate"/>（走訪活的 C.OfflineData／C.AdditionalData），回的是當下的複本；<c>null</c>＝沒答案（逾時、查無此角色或僱員、讀取失敗）。</para>
/// <para>🔴 只讀：查不到 AdditionalData 時不補空資料（<c>Utils.GetAdditionalData</c> 會補），屬性一律回 -1。</para>
/// </remarks>
public class IPC_VentureYield
{
    /// <summary>屬性未知（僱員還沒在 AutoRetainer 的記錄下打開過）。</summary>
    public const int UnknownStat = -1;

    private static bool LoggedFailure;

    public IPC_VentureYield()
    {
        EzIPC.Init(this, $"{Svc.PluginInterface.InternalName}.VentureYield");
    }

    /// <summary>這組端點的版本；目前 1。</summary>
    [EzIPC]
    public int ApiVersion() => 1;

    /// <summary>已登錄的角色；挑法與順序同 <c>AutoRetainer.GetRegisteredCIDs</c>。World＝原本所屬的世界。</summary>
    [EzIPC]
    public (ulong CID, string Name, string World)[] CharactersV1()
    {
        return IpcFrameworkGate.Run(nameof(CharactersV1), () => Guard(nameof(CharactersV1), () =>
        {
            var list = new List<(ulong CID, string Name, string World)>();
            foreach(var x in C.OfflineData)
            {
                if(C.Blacklist.Any(z => z.CID == x.CID) || x.Name.EqualsAny("Unknown", "")) continue;
                list.Add((x.CID, x.Name, x.World));
            }
            return list.ToArray();
        }), null, "the caller is given null - no answer this time, not an empty roster");
    }

    /// <summary>某角色的僱員。Ilvl／Gathering／Perception 為 -1＝不知道（不是 0）。</summary>
    [EzIPC]
    public (string Name, uint Job, int Level, uint VentureID, bool HasVenture, long VentureEndsAt, int Ilvl, int Gathering, int Perception)[] RetainersV1(ulong cid)
    {
        return IpcFrameworkGate.Run(nameof(RetainersV1), () => Guard(nameof(RetainersV1), () =>
        {
            var ocd = C.OfflineData.FirstOrDefault(x => x.CID == cid);
            if(ocd?.RetainerData == null) return null;
            var result = new List<(string, uint, int, uint, bool, long, int, int, int)>();
            foreach(var r in ocd.RetainerData)
            {
                if(r == null || string.IsNullOrEmpty(r.Name)) continue;
                var adata = TryGetAdditionalData(cid, r.Name);
                result.Add((r.Name, r.Job, r.Level, r.VentureID, r.HasVenture, r.VentureEndsAt,
                    adata?.Ilvl ?? UnknownStat, adata?.Gathering ?? UnknownStat, adata?.Perception ?? UnknownStat));
            }
            return result.ToArray();
        }), null, "the caller is given null - no answer this time");
    }

    /// <summary>某僱員做這些探險各帶回幾個，與升到下一級的門檻。</summary>
    /// <param name="ventureIds">要問的探險；<c>null</c> 或空陣列＝這位僱員職業類別的全部指定探險（含等級不夠的，看 Available）。查無此 ID 的略過。</param>
    /// <returns>
    /// 每列：ItemID 0＝隨機帶回的探險（沒有固定數量）；Minutes＝所需分鐘；Available＝等級、裝等／採集力、採集過與否都符合；
    /// Amount／Tier＝依目前屬性的數量與級數（0～4）；Stat＝決定級數的屬性（UsesPerception 為真時是鑑別力，否則是平均物品等級），-1＝不知道，這時 Amount 是最低一級的數量；
    /// NextStat／NextAmount＝下一級的門檻與數量，已是最高級時為 0。
    /// </returns>
    [EzIPC]
    public (uint VentureID, uint ItemID, int Minutes, bool Available, int Amount, int Tier, int Stat, int NextStat, int NextAmount, bool UsesPerception)[] YieldsV1(ulong cid, string retainerName, uint[] ventureIds)
    {
        return IpcFrameworkGate.Run(nameof(YieldsV1), () => Guard(nameof(YieldsV1), () =>
        {
            var ocd = C.OfflineData.FirstOrDefault(x => x.CID == cid);
            var retainer = ocd?.RetainerData?.FirstOrDefault(x => x?.Name == retainerName);
            if(retainer == null) return null;
            var adata = TryGetAdditionalData(cid, retainer.Name);
            var usesPerception = VentureUtils.IsDoL(retainer.Job);
            var stat = adata == null ? UnknownStat : usesPerception ? adata.Perception : adata.Ilvl;

            IEnumerable<RetainerTask> tasks = ventureIds == null || ventureIds.Length == 0
                ? VentureUtils.GetHunts(retainer.Job)
                : ventureIds.Select(VentureUtils.GetVentureById).Where(x => x != null).Select(x => x.Value);

            var result = new List<(uint, uint, int, bool, int, int, int, int, int, bool)>();
            foreach(var task in tasks)
            {
                try
                {
                    AddYield(result, task, ocd, retainer, adata, stat, usesPerception);
                }
                catch(Exception e)
                {
                    // 一筆資料表列讀壞只略過那一筆，不讓整位僱員沒答案。
                    LogOnce(nameof(YieldsV1), $"venture {task.RowId} skipped: {e.Message}");
                }
            }
            return result.ToArray();
        }), null, "the caller is given null - no answer this time");
    }

    private static void AddYield(List<(uint, uint, int, bool, int, int, int, int, int, bool)> result, RetainerTask task,
        OfflineCharacterData ocd, OfflineRetainerData retainer, AdditionalRetainerData adata, int stat, bool usesPerception)
    {
        if(task.IsRandom)
        {
            result.Add((task.RowId, 0, task.MaxTimemin, IsAvailable(task, ocd, retainer, adata), 0, 0, stat, 0, 0, usesPerception));
            return;
        }
        var (thresholds, amounts) = task.GetVentureAmounts(retainer);
        int amount, tier;
        if(adata == null)
        {
            tier = 0;
            amount = amounts.Length > 0 ? amounts[0] : 0;
        }
        else
        {
            amount = task.GetVentureItemAmount(ocd, retainer, out tier);
        }
        var hasNext = tier + 1 < thresholds.Length && tier + 1 < amounts.Length;
        result.Add((task.RowId, task.GetVentureItemId(), task.MaxTimemin, IsAvailable(task, ocd, retainer, adata), amount, tier, stat,
            hasNext ? thresholds[tier + 1] : 0, hasNext ? amounts[tier + 1] : 0, usesPerception));
    }

    private static AdditionalRetainerData TryGetAdditionalData(ulong cid, string name)
    {
        return C.AdditionalData.TryGetValue(Utils.GetAdditionalDataKey(cid, name, false), out var adata) ? adata : null;
    }

    /// <remarks>屬性沒有記錄時只看等級：GetFancyVentureNameParts 會替沒有記錄的僱員補一筆空資料。</remarks>
    private static bool IsAvailable(RetainerTask task, OfflineCharacterData ocd, OfflineRetainerData retainer, AdditionalRetainerData adata)
    {
        if(adata == null) return retainer.Level >= task.RetainerLevel;
        task.GetFancyVentureNameParts(ocd, retainer, out var available);
        return available;
    }

    /// <summary>失敗回 null（＝沒答案），整個插件生命週期只記一次 Information；只在 framework 執行緒上跑。</summary>
    private static T Guard<T>(string endpoint, Func<T> body) where T : class
    {
        try
        {
            return body();
        }
        catch(Exception e)
        {
            LogOnce(endpoint, $"failed, the caller is given null: {e}");
            return null;
        }
    }

    private static void LogOnce(string endpoint, string message)
    {
        if(LoggedFailure) return;
        LoggedFailure = true;
        PluginLog.Information($"[IPC VentureYield] {endpoint} {message}");
    }
}
