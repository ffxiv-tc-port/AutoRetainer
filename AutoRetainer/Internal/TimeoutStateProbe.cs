using AutoRetainer.Scheduler.Handlers;
using AutoRetainer.Scheduler.Tasks;
using Dalamud.Game.ClientState.Conditions;
using ECommons.Automation.NeoTaskManager;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace AutoRetainer.Internal;

/// <summary>
/// 任務逾時那一刻的現場快照，接在 <see cref="TaskTimeoutLog"/> 的逾時訊息後面。
/// </summary>
/// <remarks>
/// 回 <see langword="null"/> ＝這一步沒有可說的現場，逾時訊息維持原樣。
/// 🔴 只在逾時那一幀跑一次（框架執行緒），不是每幀成本。
/// 🔴 只讀不按：這裡不得出現任何 <c>Select()</c> 或 <c>Callback</c>。
/// </remarks>
internal static unsafe class TimeoutStateProbe
{
    /// <summary>把逾時的那一步分派給對應的現場快照；沒有登記的步驟回 <see langword="null"/>。</summary>
    internal static string Describe(TaskManagerTask task)
    {
        var name = task.Name;
        if(name?.Contains(nameof(RetainerHandlers.SelectViewVentureReport), StringComparison.Ordinal) == true) return DescribeVentureReport();
        if(name == TaskAutoBuyFuel.ApproachTaskName) return DescribeApproachDoll();
        return null;
    }

    /// <summary>
    /// 僱員探險報告那一步逾時時，把 <c>SelectString</c> 當下的選項原樣列出來。
    /// </summary>
    /// <remarks>
    /// 要分辨的是兩件在 log 上長得一模一樣的事：視窗開得慢，或等的那一行根本不會出現
    /// （僱員手上沒有已結束的探險時，選單裡就沒有那一項）。
    /// </remarks>
    private static string DescribeVentureReport()
    {
        var expected = RetainerHandlers.ViewVentureReportEntry;
        var talk = DescribeTalk();
        if(!TryGetAddonByName<AddonSelectString>("SelectString", out var addon))
        {
            return $"現場：SelectString 不存在；等的是「{expected}」；{talk}。";
        }
        if(!IsAddonReady(&addon->AtkUnitBase))
        {
            return $"現場：SelectString 存在但未就緒（IsVisible={addon->AtkUnitBase.IsVisible}）；等的是「{expected}」；{talk}。";
        }
        var entries = new AddonMaster.SelectString(addon).Entries.Select(x => x.Text).ToArray();
        var since = DialogGuards.FramesSincePress("SelectString", (nint)addon);
        var pressed = since < 0 ? "沒有按下記號" : $"按下記號距今 {since} 幀";
        return $"現場：SelectString 就緒（{pressed}），{entries.Length} 個選項＝[{string.Join(" | ", entries)}]；等的是「{expected}」；{talk}。";
    }

    /// <summary>
    /// 選完僱員之後擋在選單前面的 Talk：分辨「Talk 還沒出現」與「出現了卻沒被按」（AutoRetainer 裡只有 MiniTA 會按它）。
    /// </summary>
    private static string DescribeTalk()
    {
        var gate = IPC.Suppressed ? "，AutoRetainer 被別的插件壓著（MiniTA 不按）" : string.Empty;
        if(!TryGetAddonByName<AtkUnitBase>("Talk", out var talk))
        {
            return $"Talk 不存在{gate}";
        }
        var since = DialogGuards.FramesSincePress("Talk", (nint)talk);
        var pressed = since < 0 ? "沒有按下記號" : $"上次按下距今 {since} 幀";
        return $"Talk 存在（IsVisible={talk->IsVisible}，就緒={IsAddonReady(talk)}，{pressed}）{gate}";
    }

    /// <summary>
    /// 走向冒險人偶那一步逾時時，說明是「走不到」還是「根本不准走」。
    /// </summary>
    /// <remarks>
    /// 已知的逾時來源是角色被鎖在公會工坊控制台的對話事件裡：不能移動也不能改目標，
    /// 於是整整 20 秒原地不動。列出擋住移動的旗標才分得出這個與「人偶不在物件表」。
    /// </remarks>
    private static string DescribeApproachDoll()
    {
        var blockers = string.Join("＋", MovementBlockers.Where(x => Svc.Condition[x]).Select(x => x.ToString()));
        var doll = Utils.GetNearestAdventurerDoll(out var distance);
        var dollText = doll == null
            ? "物件表裡沒有可指定的冒險人偶"
            : $"最近的冒險人偶距離 {distance:F1}（可互動門檻 {Utils.GetValidInteractionDistance(doll):F1}）";
        var target = Svc.Targets.Target?.Name.ToString() ?? "（無）";
        return $"現場：{dollText}；目前目標＝{target}；移動被擋＝{(blockers.Length == 0 ? "否" : blockers)}；"
            + $"區域={Svc.ClientState.TerritoryType}。";
    }

    /// <summary>會讓角色無法自行走動的條件旗標。只用來把逾時現場說清楚，不做任何判斷。</summary>
    private static readonly ConditionFlag[] MovementBlockers =
    [
        ConditionFlag.Occupied, ConditionFlag.OccupiedInEvent, ConditionFlag.OccupiedInQuestEvent,
        ConditionFlag.OccupiedInCutSceneEvent, ConditionFlag.OccupiedSummoningBell, ConditionFlag.WatchingCutscene,
        ConditionFlag.BetweenAreas, ConditionFlag.BetweenAreas51, ConditionFlag.Mounting, ConditionFlag.Unconscious,
    ];
}
