using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using ECommons.ExcelServices;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace AutoRetainer.Modules.Statistics;

/// <summary>量「背包、兵裝庫或身上已有一件獨佔道具時，探險不會再帶回同一件」這個說法：只寫 Information，不改任何行為。</summary>
/// <remarks>
/// 契約：查看探險報告前拍快照（<see cref="TakeSnapshot"/>），到按下報告按鈕後的 <see cref="Flush"/> 之間收到的 2110 訊息算這份報告的獎勵；
/// 離開傳喚鈴時補寫還沒寫的行並彙總。全部只在框架執行緒跑（任務回呼、聊天事件、狀態事件）。
/// </remarks>
internal static unsafe class VentureUniqueDiag
{
    private static HashSet<uint> HeldBefore;
    private static bool SnapshotComplete;
    private static readonly List<(uint ItemId, bool HeldBefore, bool SnapshotComplete)> Pending = [];
    private static int Reports, Rewards, UniqueRewards, UniqueHeldBefore, Unrecognized;

    internal static void Init() => Svc.Condition.ConditionChange += OnConditionChange;

    internal static void Shutdown()
    {
        Svc.Condition.ConditionChange -= OnConditionChange;
        Reset();
    }

    /// <summary>任務回呼：拍下玩家背包、兵裝庫、身上裝備裡的獨佔道具 ID。永遠回 true，不擋後面的任務。</summary>
    internal static bool? TakeSnapshot()
    {
        try
        {
            FlushPending();
            HashSet<uint> held = [];
            var complete = true;
            var im = InventoryManager.Instance();
            if(im == null) complete = false;
            else
            {
                foreach(var type in Utils.PlayerEntireInventory)
                {
                    var inv = im->GetInventoryContainer(type);
                    if(inv == null || inv->Items == null)
                    {
                        complete = false;
                        continue;
                    }
                    for(var i = 0; i < inv->Size; i++)
                    {
                        var item = inv->GetInventorySlot(i);
                        if(item == null || item->ItemId == 0) continue;
                        if(ExcelItemHelper.Get(item->ItemId)?.IsUnique == true) held.Add(item->ItemId);
                    }
                }
            }
            HeldBefore = held;
            SnapshotComplete = complete;
            Reports++;
        }
        catch(Exception e)
        {
            HeldBefore = null;
            e.Log();
        }
        return true;
    }

    /// <summary>任務回呼：報告按鈕按下之後，寫出這份報告的逐件行並結束收集。永遠回 true。</summary>
    internal static bool? Flush()
    {
        try
        {
            FlushPending();
        }
        catch(Exception e)
        {
            e.Log();
        }
        HeldBefore = null;
        return true;
    }

    /// <summary>由 <see cref="StatisticsManager"/> 處理 2110 訊息的那條路徑呼叫（已確認人在傳喚鈴、僱員在場）。</summary>
    internal static void OnRewardMessage(SeString message)
    {
        if(HeldBefore == null) return;
        try
        {
            ItemPayload payload = null;
            foreach(var x in message.Payloads)
            {
                if(x is ItemPayload p)
                {
                    payload = p;
                    break;
                }
            }
            if(payload == null || payload.ItemId == 0)
            {
                // 遊戲有時把道具名和數量拆成不同訊息；認不出也要留一行，否則「沒寫」和「不是獨佔道具」分不開。
                Unrecognized++;
                PluginLog.Information($"[VentureUnique] 認不出這則探險獎勵訊息（沒有道具連結）：{message.TextValue}");
                return;
            }
            Rewards++;
            if(ExcelItemHelper.Get(payload.ItemId)?.IsUnique != true) return;
            var held = HeldBefore.Contains(payload.ItemId);
            UniqueRewards++;
            if(held) UniqueHeldBefore++;
            Pending.Add((payload.ItemId, held, SnapshotComplete));
        }
        catch(Exception e)
        {
            e.Log();
        }
    }

    // 數量等報告按鈕按下後才數，避開「訊息先到、背包還沒更新」的那幾幀。
    private static void FlushPending()
    {
        if(Pending.Count == 0) return;
        foreach(var (id, held, complete) in Pending)
        {
            var now = Utils.GetItemCount(Utils.PlayerEntireInventory, id);
            var before = held ? "已持有" : complete ? "未持有" : "未持有（快照時有容器讀不到）";
            PluginLog.Information($"[VentureUnique] 探險帶回獨佔道具 {id}「{ExcelItemHelper.GetName(id)}」：領取前{before}，領取後背包＋兵裝庫＋身上共 {now} 件。");
        }
        Pending.Clear();
    }

    private static void OnConditionChange(ConditionFlag flag, bool value)
    {
        if(flag != ConditionFlag.OccupiedSummoningBell || value) return;
        try
        {
            FlushPending();
            if(Reports > 0)
            {
                PluginLog.Information($"[VentureUnique] 這次開鈴：查看探險報告 {Reports} 次、獎勵訊息 {Rewards} 則，其中獨佔道具 {UniqueRewards} 件、領取前已持有 {UniqueHeldBefore} 件；認不出 {Unrecognized} 則。");
            }
        }
        catch(Exception e)
        {
            e.Log();
        }
        Reset();
    }

    private static void Reset()
    {
        HeldBefore = null;
        Pending.Clear();
        Reports = Rewards = UniqueRewards = UniqueHeldBefore = Unrecognized = 0;
    }
}
