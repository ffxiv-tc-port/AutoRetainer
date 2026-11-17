using AutoRetainer.Scheduler.Tasks;
using Dalamud.Utility;
using ECommons.Automation.NeoTaskManager;
using ECommons.Automation.UIInput;
using ECommons.Throttlers;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace AutoRetainer.Scheduler.Handlers;

internal static unsafe partial class RetainerHandlers
{
    internal static bool? ConfirmCantBuyback()
    {
        var yesno = Utils.GetSpecificYesno(Lang.WillBeUnableToProcessBuyback);
        if(yesno != null)
        {
            // 🔴 RetainerBulkOperation 的復原鏈會在幾個 tick 內第二次進到這裡,GetSpecificYesno 對關閉中的窗仍命中;
            //    節流不是防護,同一扇確認框只按一次(記號在 DialogGuards,窗消失後解除)。
            if(Utils.GenericThrottle && EzThrottler.Throttle("WillBeUnableToProcessBuyback")
                && DialogGuards.TryPressOnce("SelectYesno", (nint)yesno, "WillBeUnableToProcessBuyback"))
            {
                new AddonMaster.SelectYesno((nint)yesno).Yes();
                return true;
            }
        }
        if(TryGetAddonByName<AtkUnitBase>("RetainerList", out _))
        {
            return true;
        }
        return false;
    }

    internal static bool? WaitForVentureListUpdate()
    {
        // 🔴 CSFramework.Instance() 是 isPointer:true 的靜態位址，會合法回 null，
        //    裸解參考是攔不到的 AVE。
        //    讀不到就回 false ＝「還沒等到」，下一輪再試；不會謊報清單已更新。
        var framework = CSFramework.Instance();
        if(framework == null) return false;
        if(P.ListUpdateFrame > framework->FrameCounter - 10) return true;
        return false;
    }

    // 下面這幾個選項文字取自 Addon 表：表在插件存活期間不變，第一次用到時算好就重用（同 ViewVentureReportEntry）。
    private static string[] AssignVentureEntries => field ??= [Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Addon>().GetRow(2386).Text.ToDalamudString().GetText(), Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Addon>().GetRow(2387).Text.ToDalamudString().GetText()];
    private static string QuitEntry => field ??= Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Addon>().GetRow(2383).Text.ToDalamudString().GetText();
    private static string EntrustItemsEntry => field ??= Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Addon>().GetRow(2378).Text.ToDalamudString().GetText(true);
    private static string EntrustGilEntry => field ??= Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Addon>().GetRow(2379).Text.ToDalamudString().GetText(true);
    private static string CloseWindowText => field ??= Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Addon>().GetRow(13530).Text.ToDalamudString().GetText();

    internal static bool? SelectAssignVenture()
    {
        return Utils.TrySelectSpecificEntry(AssignVentureEntries);
    }

    internal static bool? SelectQuit()
    {
        if(BailoutManager.SimulateStuckOnQuit) return false;
        if(TryGetAddonByName<AtkUnitBase>("RetainerTaskSupply", out var addon))
        {
            // Close(true) 對關閉中的窗再叫一次同樣未證安全:同一扇只關一次。
            if(Utils.GenericThrottle && DialogGuards.TryPressOnce("RetainerTaskSupply", (nint)addon, "SelectQuit.CloseTaskSupply"))
            {
                addon->Close(true);
            }
            return false;
        }
        return Utils.TrySelectSpecificEntry(QuitEntry);
    }

    internal static void EnforceSelectStringThrottle()
    {
        EzThrottler.Throttle("EnforceSelectString", 3000, true);
    }

    /// <summary>選單上「查看探險報告（已結束）」那一行的文字，取自 Addon#2385，跟隨客戶端語言。</summary>
    /// <remarks>逾時診斷要印出「當時在等什麼」，兩邊必須取同一個來源，不可以各抄一份。</remarks>
    // 表在插件存活期間不變，第一次存取時算好就重用。
    internal static string ViewVentureReportEntry => field ??= Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Addon>().GetRow(2385).Text.ToDalamudString().GetText();

    /// <summary>探險報告選項的等待上限，刻意短於 TaskManager 的預設。</summary>
    /// <remarks>那一行不出現時（僱員手上沒有已結束的探險）久等無用，上限只決定多久放棄。</remarks>
    internal static readonly TaskManagerConfiguration ViewVentureReportConf = new(timeLimitMS: 5000);

    internal static bool? SelectViewVentureReport()
    {
        EnforceSelectStringThrottle();
        return Utils.TrySelectSpecificEntry(ViewVentureReportEntry);
    }

    internal static bool? EnforceSelectString(Func<bool?> Action)
    {
        if(!(TryGetAddonByName<AtkUnitBase>("SelectString", out var a) && a->IsVisible))
        {
            return true;
        }
        if(EzThrottler.Throttle("EnforceSelectString", 3000))
        {
            PluginLog.Warning($"Enforcing {Action.GetType().FullName} ");
            Action();
        }
        return false;
    }

    internal static bool? ClickResultReassign()
    {
        if(TryGetAddonByName<AddonRetainerTaskResult>("RetainerTaskResult", out var addon) && IsAddonReady(&addon->AtkUnitBase))
        {
            const string thrName = "ClickResultReassign.WaitForButtonEnabled";
            if(!Utils.IsButtonEnabled(addon->ReassignButton))
            {
                FrameThrottler.Throttle(thrName, 5, true);
            }
            // 🔴 按鈕按下即關窗;「按過的按鈕會被停用」未證實,5 幀穩定閘+GenericThrottle 都不是防護。
            //    同一扇 RetainerTaskResult 只按一次(Reassign/Confirm 共用一把 key)。
            if(FrameThrottler.Check(thrName) && Utils.IsButtonEnabled(addon->ReassignButton) && Utils.GenericThrottle
                && DialogGuards.TryPressOnce("RetainerTaskResult", (nint)addon, "ClickResultReassign"))
            {
                new AddonMaster.RetainerTaskResult(addon).Reassign();
                DebugLog($"Clicked reassign");
                return true;
            }
        }
        else
        {
            Utils.RethrottleGeneric();
        }
        return false;
    }

    internal static bool? ClickResultConfirm()
    {
        if(TryGetAddonByName<AddonRetainerTaskResult>("RetainerTaskResult", out var addon) && IsAddonReady(&addon->AtkUnitBase))
        {
            const string thrName = "RetainerTaskResult.WaitForButtonEnabled";
            if(!Utils.IsButtonEnabled(addon->ConfirmButton))
            {
                FrameThrottler.Throttle(thrName, 5, true);
            }
            if(FrameThrottler.Check(thrName) && Utils.IsButtonEnabled(addon->ConfirmButton) && Utils.GenericThrottle
                && DialogGuards.TryPressOnce("RetainerTaskResult", (nint)addon, "ClickResultConfirm"))
            {
                new AddonMaster.RetainerTaskResult(addon).Confirm();
                DebugLog($"Clicked confirm");
                return true;
            }
        }
        else
        {
            Utils.RethrottleGeneric();
        }
        return false;
    }

    internal static bool? ClickAskAssign()
    {
        if(TryGetAddonByName<AddonRetainerTaskAsk>("RetainerTaskAsk", out var addon) && IsAddonReady(&addon->AtkUnitBase))
        {
            const string thrName = "ClickAskAssign.WaitForButtonEnabled";
            if(!Utils.IsButtonEnabled(addon->AssignButton))
            {
                FrameThrottler.Throttle(thrName, 5, true);
            }
            if(FrameThrottler.Check(thrName) && Utils.IsButtonEnabled(addon->AssignButton) && Utils.GenericThrottle
                && DialogGuards.TryPressOnce("RetainerTaskAsk", (nint)addon, "ClickAskAssign"))
            {
                new AddonMaster.RetainerTaskAsk((IntPtr)addon).Assign();
                DebugLog("Clicked assign");
                return true;
            }
        }
        else
        {
            Utils.RethrottleGeneric();
        }
        return false;
    }

    internal static bool? ClickAskReturn()
    {
        if(TryGetAddonByName<AddonRetainerTaskAsk>("RetainerTaskAsk", out var addon) && IsAddonReady(&addon->AtkUnitBase))
        {
            const string thrName = "ClickAskReturn.WaitForButtonEnabled";
            if(!Utils.IsButtonEnabled(addon->ReturnButton))
            {
                FrameThrottler.Throttle(thrName, 5, true);
            }
            if(FrameThrottler.Check(thrName) && Utils.IsButtonEnabled(addon->ReturnButton) && Utils.GenericThrottle
                && DialogGuards.TryPressOnce("RetainerTaskAsk", (nint)addon, "ClickAskReturn"))
            {
                new AddonMaster.RetainerTaskAsk((IntPtr)addon).Return();
                DebugLog("Clicked return");
                return true;
            }
        }
        else
        {
            Utils.RethrottleGeneric();
        }
        return false;
    }

    internal static bool? SelectQuickExploration()
    {
        return Utils.TrySelectSpecificEntry(Lang.QuickExploration);
    }

    internal static bool? SelectEntrustItems()
    {
        //2378	Entrust or withdraw items.
        return Utils.TrySelectSpecificEntry(EntrustItemsEntry);
    }

    internal static bool? SelectEntrustGil()
    {
        //2379	Entrust or withdraw gil.
        return Utils.TrySelectSpecificEntry(EntrustGilEntry);
    }

    internal static bool? ClickEntrustDuplicates()
    {
        var invName = Utils.GetActiveRetainerInventoryName();
        if(TryGetAddonByName<AtkUnitBase>(invName.Name, out var addon) && IsAddonReady(addon))
        {
            // 🔴 這個索引不是常數 —— EntrustDuplicatesIndex 依道具欄種類(InventoryLarge / InventoryExpansion /
            //    InventoryRetainerLarge …)切換,不同版面的 NodeListCount 也不同,越界時讀到的是相鄰記憶體而不是 null,
            //    元素判空完全擋不住,所以走 GetNodeSafe。GetComponent() / IsVisible() 都是 [MemberFunction],節點取不到時
            //    等於把 this = 0 交給遊戲原生碼。取不到就當「按鈕還沒出現」回 false 讓下一輪重試 —— 與既有的
            //    「不可見／未啟用」走同一條路,不會謊報已委託。
            var node = Utils.GetNodeSafe(&addon->UldManager, invName.EntrustDuplicatesIndex);
            var button = node == null ? null : (AtkComponentButton*)node->GetComponent();
            // 道具欄窗按下不關(開出 RetainerItemTransferList):帶參數組,15 幀內不對同一扇重送。
            if(node != null && node->IsVisible() && Utils.IsButtonEnabled(button) && Utils.GenericThrottle
                && DialogGuards.TryPressOnce(invName.Name, (nint)addon, "ClickEntrustDuplicates", "EntrustDuplicates", escapeIsRoutine: true))
            {
                //new ClickButtonGeneric(addon, invName.Name).Click(button);
                Callback.Fire(addon, false, (int)0);
                DebugLog($"Clicked entrust duplicates {invName.Name} {invName.EntrustDuplicatesIndex}");
                return true;
            }
        }
        else
        {
            Utils.RethrottleGeneric();
        }
        return false;
    }

    internal static bool? ClickEntrustDuplicatesConfirm()
    {
        if(TryGetAddonByName<AtkUnitBase>("RetainerItemTransferList", out var addon) && IsAddonReady(addon))
        {
            // 🔴 除了上界與元素判空,這裡還多一層:button 是 GetComponent() 的回值,也要判空 ——
            //    ClickAddonButton 內部會讀 AtkComponentBase 的欄位,對 null 一樣是解參考。
            //    取不到就回 false 重試,不謊報已確認。
            var node = Utils.GetNodeSafe(&addon->UldManager, 3);
            var button = node == null ? null : (AtkComponentButton*)node->GetComponent();
            // 🔴 按下即關窗;GenericThrottle 下界 0 幀,不是防護。同一扇只按一次。
            if(node != null && button != null && node->IsVisible() && Utils.IsButtonEnabled(button) && Utils.GenericThrottle
                && DialogGuards.TryPressOnce("RetainerItemTransferList", (nint)addon, "ClickEntrustDuplicatesConfirm"))
            {
                button->ClickAddonButton(addon);
                DebugLog($"Clicked duplicates confirm");
                return true;
            }
        }
        else
        {
            Utils.RethrottleGeneric();
        }
        return false;
    }

    internal static bool? ClickCloseEntrustWindow()
    {
        //13530	Close Window
        var text = CloseWindowText;
        if(TryGetAddonByName<AtkUnitBase>("RetainerItemTransferProgress", out var addon) && IsAddonReady(addon))
        {
            // 🔴 五跳鏈每一跳都要驗:NodeList[2](上界與元素)→ GetComponent()([MemberFunction],
            //    對 null this 呼叫＝當場 AVE)→ 回值可為 null → 內層 NodeList[2] → GetAsAtkTextNode()
            //    → &...->NodeText(毒指標 0xC0,連 ReadSeString 的判空都騙得過去)。
            //    任一跳取不到就當「還沒到可以按的狀態」回 false 讓下一輪重試(與既有的
            //    addon 尚未 ready 路徑同語意),不謊報已關閉。
            var node = Utils.GetNodeSafe(&addon->UldManager, 2);
            var component = node == null ? null : node->GetComponent();
            var button = (AtkComponentButton*)component;
            if(component != null
                && Utils.TryGetNodeText(Utils.GetNodeSafe(&component->UldManager, 2), out var nodetext)
                && nodetext == text
                && node->IsVisible()
                && Utils.IsButtonEnabled(button)
                && Utils.GenericThrottle
                && DialogGuards.TryPressOnce("RetainerItemTransferProgress", (nint)addon, "ClickCloseEntrustWindow"))
            {
                button->ClickAddonButton(addon);
                DebugLog($"Clicked transfer progress close");
                return true;
            }
        }
        else
        {
            Utils.RethrottleGeneric();
        }
        return false;
    }

    internal static bool? CloseAgentRetainer()
    {
        // 🔴 四層鏈每一層都要驗：Framework.Instance()（isPointer:true，可能 null）
        //    → UIModule（+0x2B68 裸欄位）→ GetAgentModule()（可能 null）→ 代理人（可能 null）。
        //    任一層 null 就是攔不到的 AVE（corrupted-state exception，try/catch 無效）。
        //    這支是「把僱員代理人關掉」的任務步驟，回 false ＝ 尚未完成、下一輪再試，
        //    與既有的「代理人不活躍時回 false」同一條路徑（不謊報已關閉）。
        var framework = Framework.Instance();
        if(framework == null || framework->UIModule == null) return false;
        var agentModule = framework->UIModule->GetAgentModule();
        if(agentModule == null) return false;
        var a = agentModule->GetAgentByInternalId(AgentId.Retainer);
        if(a == null) return false;
        if(a->IsAgentActive())
        {
            a->Hide();
            return true;
        }
        return false;
    }

    // 上一輪已經對這個位址的 Bank 視窗送出「提領／取消」callback、而那扇窗還沒消失時記下的位址。
    // 🔴 只做等值比較，永遠不解參（與 DialogGuards 同一條紀律）。位址可能被下一扇窗重用，
    //    所以要判「是不是同一扇還沒關掉的窗」時一律再問一次 DialogGuards.WasPressed：
    //    那邊的紀錄會在窗真的從 addon 清單消失時被清掉，光靠這個欄位分不出
    //    「同一扇窗還開著」與「新的一扇窗剛好落在同一塊記憶體」。
    private static nint bankPendingClose;

    public static bool? GenericSelectByName(params string[] text)
    {
        return Utils.TrySelectSpecificEntry(text);
    }

    /*public static bool? SearchVentureByName(uint id) => SearchVentureByName(VentureUtils.GetVentureName(id));

    public static bool? SearchVentureByName(string name)
    {
        if (TryGetAddonByName<AtkUnitBase>("RetainerTaskSupply", out var addon) && IsAddonReady(addon))
        {
            if (Utils.GenericThrottle) 
            {
                Callback.Fire(addon, true, 2, new AtkValue() { Type = 0, Int = 0}, name);
                return true;
            }
        }
        return false;
    }*/

    [Obsolete]
    internal static bool? ClearTaskSupplylist()
    {
        if(TryGetAddonByName<AtkUnitBase>("RetainerTaskSupply", out var addon) && IsAddonReady(addon))
        {
            if(Utils.GenericThrottle && DialogGuards.TryPressOnce("RetainerTaskSupply", (nint)addon, "ClearTaskSupplylist", "Clear", escapeIsRoutine: true))
            {
                Callback.Fire(addon, true, 1);
                return true;
            }
        }
        return false;
    }
}
