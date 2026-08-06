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
/// 作法（抄自 DailyRoutines）：不等遊戲自己重建，改成直接對 AgentGrandCompanySupply
/// 送出「重新選取籌備稀有品分頁」事件，讓代理人當場重建清單。
///
/// ── 這條路徑到底做了什麼（TC 7.20 ffxiv_dx11.exe 離線反組譯，2026-08-03）──
/// 定位方式：AgentModule::ctor 對 agents[96]（AgentId.GrandCompanySupply）的
/// 配置大小是 0x98，與 FFXIVClientStructs 宣告的結構大小一致，由此取得建構式
/// 與 vtable，vf0 即 ReceiveEvent。eventKind=0 且 values[0]=0 時它只做三件事：
///   1. agent->SelectedTab（word @0x90）＝ values[1]；
///   2. 呼叫建表函式重建 AtkValue 陣列 —— 該函式**只讀代理人自己的欄位**
///      （SelectedTab、NumItems @0x78、ItemArray @0x68），完全沒有碰 addon，
///      而它寫出來的 AtkValues[6] 正是 ReaderGrandCompanySupplyList.NumItems；
///   3. agent->UIModuleInterface->GetRaptureAtkModule2()
///        ->RefreshAddon(agent->AddonId, ...)（AtkModuleInterface 的 vfunc 23）。
///
/// 🔑 結論：整條路徑**沒有讀取 addon 的可視旗標、ULD 載入狀態或節點清單**，
/// addon 是「交一個 ID 給遊戲自己去找」的。所以原先照抄 DR 而來的
/// 「GrandCompanySupplyList 必須 IsAddonReady」不是這條路徑的前提，
/// 真正的前提是 <see cref="AgentInterface.AddonId"/> 不為 0 —— ID 是 0 的話
/// RefreshAddon 找不到對象，事件送出去也只是空轉。
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
    /// 代理人的清單陣列是否還在。
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
        // RefreshAddon 是按 AddonId 找 addon 的：ID 是 0 就代表代理人此刻沒有掛著任何視窗，
        // 事件送出去也不會有人收 —— 與其誤以為刷新過了，不如當成「還不能送」下一幀再試。
        if(agent->AgentInterface.AddonId == 0) return false;

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

    /// <summary>
    /// 取樣代理人這一側的兩個閘門條件。<see cref="RequestExpertDeliveryRefresh"/> 要兩者同時成立才送得出去，
    /// 但它們**不必同時發生** —— 分開取樣才能回答「獎勵視窗關掉之後那半秒是誰欠的」。
    /// <para/>
    /// 🔴 每次都重新取得代理人，絕不跨幀保存原生指標。
    /// </summary>
    /// <param name="listArrayBack">代理人的清單陣列已經重建。</param>
    /// <param name="addonBound">代理人已經重新綁上一個視窗（RefreshAddon 找得到對象）。</param>
    internal static void SampleAgentGates(out bool listArrayBack, out bool addonBound)
    {
        var agent = GetAgent();
        if(agent == null)
        {
            listArrayBack = false;
            addonBound = false;
            return;
        }
        listArrayBack = agent->ItemArray != null;
        addonBound = agent->AgentInterface.AddonId != 0;
    }

    /// <summary>
    /// 診斷字串：代理人這一側的三個條件現在各是什麼狀態。
    /// 用來回答「等刷新閘門那段時間到底是 addon 擋的還是代理人擋的」。
    /// </summary>
    internal static string DescribeGate()
    {
        var agent = GetAgent();
        if(agent == null) return "代理人=無";
        return $"代理人=有 清單陣列={(agent->ItemArray != null ? 1 : 0)} AddonId={agent->AgentInterface.AddonId}";
    }
}
