using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using ValueType = FFXIVClientStructs.FFXIV.Component.GUI.ValueType;

namespace AutoRetainer.Modules.GcHandin;

/// <summary>
/// 大國防聯軍軍需品清單的「主動刷新」。
///
/// 繳交一件之後，遊戲要等自己的重建流程跑完，清單才會重新可用（實測每件約 0.56 秒），
/// 這段空等就是繳交速度的瓶頸 —— 加大／縮小我們自己的幀節流完全影響不到它。
///
/// 這裡改抄 DailyRoutines 的作法：不等遊戲自己重建，改成直接對
/// AgentGrandCompanySupply 送出「重新選取籌備稀有品分頁」事件，讓代理人當場
/// （ReceiveEvent 是同步呼叫）重建 ItemArray 並把新的 AtkValues 推回 addon。
/// </summary>
internal static unsafe class GCSupplyRefresh
{
    /// <summary>
    /// 軍需品清單的「籌備稀有品」分頁索引。與 GCContinuation.SelectSupplyListTab(2) 一致；
    /// 自動繳交只會在這個分頁上運作（IsReadyToOperate 檢查的篩選下拉只存在於這一頁）。
    /// </summary>
    internal const int ExpertDeliveryTab = 2;

    /// <summary>
    /// 🔴 每次都重新取得，絕不跨幀保存原生指標。
    /// </summary>
    internal static AgentGrandCompanySupply* GetAgent() => AgentGrandCompanySupply.Instance();

    /// <summary>
    /// 代理人的清單陣列是否還在。這是「可以送刷新事件」的前提，也是 DR 用的同一個閘門。
    /// </summary>
    internal static bool IsAgentListAvailable()
    {
        var agent = GetAgent();
        return agent != null && agent->ItemArray != null;
    }

    /// <summary>
    /// 送出「重選籌備稀有品分頁」事件，逼代理人立刻重建清單。
    /// 回傳 false 代表代理人此刻不在可用狀態（呼叫端應該下一幀再試）。
    /// </summary>
    internal static bool RequestExpertDeliveryRefresh()
    {
        var agent = GetAgent();
        if(agent == null || agent->ItemArray == null) return false;

        // eventKind 0 + (0, 分頁索引)：與遊戲自己切換分頁走的是同一條路徑。
        var values = stackalloc AtkValue[2];
        values[0] = new AtkValue() { Type = ValueType.Int, Int = 0 };
        values[1] = new AtkValue() { Type = ValueType.Int, Int = ExpertDeliveryTab };
        var result = stackalloc AtkValue[1];
        result[0] = new AtkValue() { Type = ValueType.Undefined, Int = 0 };

        // AgentInterface 繼承自 AtkEventInterface（位移 0），ReceiveEvent 是它的 vfunc 0。
        ((AtkModuleInterface.AtkEventInterface*)agent)->ReceiveEvent(result, values, 2, 0);
        return true;
    }
}
