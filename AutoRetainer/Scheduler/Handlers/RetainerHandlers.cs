using AutoRetainer.Scheduler.Tasks;
using Dalamud.Utility;
using ECommons.Automation.UIInput;
using ECommons.Throttlers;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace AutoRetainer.Scheduler.Handlers;

internal static unsafe class RetainerHandlers
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
        //    裸解參考是攔不到的 AVE。⚠️ 這一處不在原掃描清單裡，是修 CloseAgentRetainer
        //    時同檔同形一併掃到的。
        //    讀不到就回 false ＝「還沒等到」，下一輪再試；不會謊報清單已更新。
        var framework = CSFramework.Instance();
        if(framework == null) return false;
        if(P.ListUpdateFrame > framework->FrameCounter - 10) return true;
        return false;
    }

    internal static bool? SelectAssignVenture()
    {
        var text = new string[] { Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Addon>().GetRow(2386).Text.ToDalamudString().GetText(), Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Addon>().GetRow(2387).Text.ToDalamudString().GetText() };
        return Utils.TrySelectSpecificEntry(text);
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
        var text = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Addon>().GetRow(2383).Text.ToDalamudString().GetText();
        return Utils.TrySelectSpecificEntry(text);
    }

    internal static void EnforceSelectStringThrottle()
    {
        EzThrottler.Throttle("EnforceSelectString", 3000, true);
    }

    internal static bool? SelectViewVentureReport()
    {
        EnforceSelectStringThrottle();
        //2385	View venture report. (Complete)
        var text = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Addon>().GetRow(2385).Text.ToDalamudString().GetText();
        return Utils.TrySelectSpecificEntry(text);
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
        var text = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Addon>().GetRow(2378).Text.ToDalamudString().GetText(true);
        return Utils.TrySelectSpecificEntry(text);
    }

    internal static bool? SelectEntrustGil()
    {
        //2379	Entrust or withdraw gil.
        var text = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Addon>().GetRow(2379).Text.ToDalamudString().GetText(true);
        return Utils.TrySelectSpecificEntry(text);
    }

    internal static bool? ClickEntrustDuplicates()
    {
        var invName = Utils.GetActiveRetainerInventoryName();
        if(TryGetAddonByName<AtkUnitBase>(invName.Name, out var addon) && IsAddonReady(addon))
        {
            // 🔴 原本上界與元素都沒驗,而這個索引不是常數 —— EntrustDuplicatesIndex 依道具欄種類
            //    (InventoryLarge / InventoryExpansion / InventoryRetainerLarge …)切換,不同版面的
            //    NodeListCount 也不同,越界時讀到的是相鄰記憶體而不是 null,元素判空完全擋不住。
            //    GetComponent() / IsVisible() 都是 [MemberFunction],節點取不到時等於把 this = 0
            //    交給遊戲原生碼。取不到就當「按鈕還沒出現」回 false 讓下一輪重試 —— 與既有的
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
            // 🔴 除了上界與元素判空,這裡還多一層:button 是 GetComponent() 的回值,原本沒判空就
            //    直接 button->ClickAddonButton(addon)。ClickAddonButton 內部會讀 AtkComponentBase
            //    的欄位,對 null 一樣是解參考。取不到就回 false 重試,不謊報已確認。
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
        var text = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Addon>().GetRow(13530).Text.ToDalamudString().GetText();
        if(TryGetAddonByName<AtkUnitBase>("RetainerItemTransferProgress", out var addon) && IsAddonReady(addon))
        {
            // 🔴 原本是五跳裸鏈:NodeList[2](上界與元素都沒驗)→ GetComponent()([MemberFunction],
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
        // 🔴 原本是四層裸鏈：Framework.Instance()（isPointer:true，可能 null）
        //    → UIModule（+0x2B68 裸欄位）→ GetAgentModule()（可能 null）→ 代理人（可能 null）。
        //    任一層 null 就是攔不到的 AVE（corrupted-state exception，try/catch 無效）。
        //    這支是「把雇員代理人關掉」的任務步驟，回 false ＝ 尚未完成、下一輪再試，
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

    internal static bool? SetWithdrawGilAmount(int percent)
    {
        if(TryGetAddonByName<AtkUnitBase>("Bank", out var addon) && IsAddonReady(addon) && Utils.TryGetCurrentRetainer(out var name) && Utils.TryGetRetainerByName(name, out var retainer))
        {
            if(percent < 1 || percent > 100) throw new ArgumentOutOfRangeException(nameof(percent), percent, "Percent must be between 1 and 100");
            // 🔴 NodeList[27] 既沒驗上界也沒判元素;GetAsAtkTextNode() 對 null 節點是當場 AVE,
            //    &...->NodeText 則是靜默的毒指標 0xC0。取不到就回 false 讓下一輪重試 ——
            //    刻意不走下面那個「解析失敗＝視為完成」的 else,因為「節點還沒建好」與
            //    「金額欄位真的不是數字」是兩件事,前者重試就會好。
            if(!Utils.TryGetNodeText(&addon->UldManager, 27, out var gilText)) return false;
            if(uint.TryParse(gilText.RemoveOtherChars("0123456789"), out var numGil))
            {
                DebugLog($"Gil: {numGil}");
                var gilToWithdraw = (uint)(percent == 100 ? numGil : numGil / 100f * percent);
                if(gilToWithdraw > 0 && gilToWithdraw <= numGil)
                {
                    // Bank 窗設金額不關窗:帶參數組;ProcessBankOrCancel 對同一扇按過提領/取消後,任何參數組都不再送。
                    if(Utils.GenericThrottle && DialogGuards.TryPressOnce("Bank", (nint)addon, "SetWithdrawGilAmount", $"Set{gilToWithdraw}", escapeIsRoutine: true))
                    {
                        var v = stackalloc AtkValue[]
                        {
                            new() { Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int, Int = 3 },
                            new() { Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.UInt, UInt = gilToWithdraw }
                        };
                        addon->FireCallback(2, v);
                        DebugLog($"Set gil to withdraw {gilToWithdraw} (total: {numGil})");
                        return true;
                    }
                }
                else
                {
                    return true;
                }
            }
            else
            {
                return true;
            }
        }
        else
        {
            Utils.RethrottleGeneric();
        }
        return false;
    }

    internal static bool? SetDepositGilAmount(int percent)
    {
        if(TryGetAddonByName<AtkUnitBase>("Bank", out var addon) && IsAddonReady(addon))
        {
            if(percent < 1 || percent > 100) throw new ArgumentOutOfRangeException(nameof(percent), percent, "Percent must be between 1 and 100");
            var numGil = TaskDepositGil.Gil;
            DebugLog($"Gil: {numGil}");
            var gilToDeposit = (uint)(percent == 100 ? numGil : numGil / 100f * percent);
            if(gilToDeposit > 0 && gilToDeposit <= numGil)
            {
                if(Utils.GenericThrottle && DialogGuards.TryPressOnce("Bank", (nint)addon, "SetDepositGilAmount", $"Set{gilToDeposit}", escapeIsRoutine: true))
                {
                    var v = stackalloc AtkValue[]
                    {
                        new() { Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int, Int = 3 },
                        new() { Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.UInt, UInt = gilToDeposit }
                    };
                    addon->FireCallback(2, v);
                    DebugLog($"Set gil to deposit {gilToDeposit} (total: {numGil})");
                    return true;
                }
            }
            else
            {
                return true;
            }
        }
        else
        {
            Utils.RethrottleGeneric();
        }
        return false;
    }

    internal static bool? SetDepositGilAmountExact(int amount)
    {
        if(TryGetAddonByName<AtkUnitBase>("Bank", out var addon) && IsAddonReady(addon))
        {
            if(amount < 1) throw new ArgumentOutOfRangeException(nameof(amount), amount, "Amount must be 1 or higher");
            var numGil = TaskDepositGil.Gil;
            DebugLog($"Gil: {numGil}");
            var gilToDeposit = (uint)numGil;
            if(gilToDeposit > 0 && gilToDeposit <= numGil)
            {
                if(Utils.GenericThrottle && DialogGuards.TryPressOnce("Bank", (nint)addon, "SetDepositGilAmountExact", $"Set{gilToDeposit}", escapeIsRoutine: true))
                {
                    var v = stackalloc AtkValue[]
                    {
                        new() { Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int, Int = 3 },
                        new() { Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.UInt, UInt = gilToDeposit }
                    };
                    addon->FireCallback(2, v);
                    DebugLog($"Set gil to deposit {gilToDeposit} (total: {numGil})");
                    return true;
                }
            }
            else
            {
                return true;
            }
        }
        else
        {
            Utils.RethrottleGeneric();
        }
        return false;
    }

    internal static bool? SwapBankMode()
    {
        if(TryGetAddonByName<AtkUnitBase>("Bank", out var addon) && IsAddonReady(addon))
        {
            if(Utils.GenericThrottle && DialogGuards.TryPressOnce("Bank", (nint)addon, "SwapBankMode", "SwapMode", escapeIsRoutine: true))
            {
                var v = stackalloc AtkValue[]
                {
                    new() { Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int, Int = 2 },
                    new() { Type = 0, UInt = 0 }
                };
                addon->FireCallback(2, v);
                DebugLog($"Swapping withdraw mode");
                return true;
            }
        }
        else
        {
            Utils.RethrottleGeneric();
        }
        return false;
    }

    internal static bool? ProcessBankOrCancel()
    {
        return ProcessBankOrCancel(false);
    }

    internal static bool? ProcessBankOrCancel(bool forceCancel = false)
    {
        if(TryGetAddonByName<AtkUnitBase>("Bank", out var addon) && IsAddonReady(addon))
        {
            // 🔴 兩個節點原本都是裸解參考。這裡的失敗語意刻意分兩層:
            //    ①「提領」節點取不到 → 落到 else 走「取消」分支(與「提領鈕不可見／未啟用」相同,
            //       forceCancel 的既有行為也是靠這條路),不是直接放棄整步;
            //    ②「取消」節點也取不到 → 回 false 讓下一輪重試,絕不回 true(回 true 會讓佇列
            //       以為銀行視窗已經處理掉了,實際上它還開著)。
            var withdrawNode = Utils.GetNodeSafe(&addon->UldManager, 3);
            var withdraw = withdrawNode == null ? null : (AtkComponentButton*)withdrawNode->GetComponent();
            if(withdrawNode != null && withdrawNode->IsVisible() && Utils.IsButtonEnabled(withdraw) && !forceCancel)
            {
                // 🔴 提領/取消按下即關(還緊接 Close(true));同一扇 Bank 只按一次。
                if(Utils.GenericThrottle && DialogGuards.TryPressOnce("Bank", (nint)addon, "ProcessBank"))
                {
                    var v = stackalloc AtkValue[]
                    {
                        new() { Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int, Int = 0 },
                        new() { Type = 0, Int = 0 }
                    };
                    addon->FireCallback(2, v);
                    addon->Close(true);

                    DebugLog($"Clicked withdraw");
                    //new ClickButtonGeneric(addon, "Bank").Click(withdraw);
                    return true;
                }
            }
            else
            {
                var cancelNode = Utils.GetNodeSafe(&addon->UldManager, 2);
                var cancel = cancelNode == null ? null : (AtkComponentButton*)cancelNode->GetComponent();
                if(cancelNode != null && cancelNode->IsVisible() && Utils.IsButtonEnabled(cancel))
                {
                    if(Utils.GenericThrottle && DialogGuards.TryPressOnce("Bank", (nint)addon, "CancelBank"))
                    {
                        var v = stackalloc AtkValue[]
                    {
                            new() { Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int, Int = 1 },
                            new() { Type = 0, Int = 0 }
                        };
                        addon->FireCallback(2, v);
                        addon->Close(true);
                        DebugLog($"Clicked cancel");
                        //new ClickButtonGeneric(addon, "Bank").Click(cancel);
                        return true;
                    }
                }
            }
        }
        else
        {
            Utils.RethrottleGeneric();
        }
        return false;
    }

    public static bool? GenericSelectByName(params string[] text)
    {
        return Utils.TrySelectSpecificEntry(text);
    }

    public static bool? SelectSpecificVenture(uint VentureID)
    {
        if(TryGetAddonByName<AtkUnitBase>("RetainerTaskList", out var addon) && IsAddonReady(addon))
        {
            // GetRowOrDefault 而非 GetRow：VentureID 來自委託計畫(設定檔)或 IPC 覆寫。
            // 這個方法是排進 P.TaskManager 的工作,裡面擲例外會被 TaskManager 當成失敗而
            // Abort() 整條佇列(預設 abortOnError),不是只掉這一步。查無此列時
            // GetVentureName 回 null,下面的 Contains(null) 為 false,直接落到既有的
            // Error 分支印出「找不到這個委託」—— 失敗仍然看得見,只是不再炸掉佇列。
            var ventureData = Svc.Data.GetExcelSheet<RetainerTask>().GetRowOrDefault(VentureID);
            var ventureName = ventureData.GetVentureName();
            if(Utils.GenericThrottle && EzThrottler.Throttle("AssignSpecificVenture", 1000))
            {
                if(VentureUtils.GetAvailableVentureNames().Contains(ventureName))
                {
                    // 委託清單窗按下不關(開出 RetainerTaskAsk):帶參數組。
                    if(!DialogGuards.TryPressOnce("RetainerTaskList", (nint)addon, "SelectSpecificVenture", $"Assign{VentureID}", escapeIsRoutine: true)) return false;
                    Callback.Fire(addon, false, (int)11, (int)VentureID);
                    return true;
                }
                else
                {
                    PluginLog.Error($"Can not find venture id {VentureID} [{ventureName}] in list {VentureUtils.GetAvailableVentureNames().Print()}");
                }
            }
        }
        else
        {
            Utils.RethrottleGeneric();
        }
        return false;
    }

    internal static bool? CheckForErrorAssignedVenture(uint ventureID)
    {
        if(TryGetAddonByName<AddonRetainerTaskAsk>("RetainerTaskAsk", out var addon) && IsAddonReady(&addon->AtkUnitBase))
        {
            // 🔴 NodeList[6] 原本上界與元素都沒驗。這一步是「畫面上有沒有跳錯誤訊息」的判別式,
            //    取不到時只能回「沒偵測到錯誤」(＝落到下面的 return false),絕不能反過來當成
            //    有錯誤而重排整條委託指派 —— 那會在版面還沒建好的每一幀無限重排。
            //    這一步是以 timeLimitMS 輪詢的,回 false 就只是繼續等,不會卡住佇列。
            var errorNode = Utils.GetNodeSafe(&addon->AtkUnitBase.UldManager, 6);
            if(errorNode != null && errorNode->IsVisible())
            {
                //An Error is on screen.
                // 🔴 InsertStack 重排的 RedoErrorCheck 可能在這扇 RetainerTaskAsk 關閉中重進:同一扇只按一次 Return,
                //    被擋就當「這一幀沒看到錯誤」回 false,重排也只做一次。
                if(!DialogGuards.TryPressOnce("RetainerTaskAsk", (nint)addon, "CheckForErrorAssignedVenture.Return")) return false;
                new AddonMaster.RetainerTaskAsk((IntPtr)addon).Return();
                DebugLog($"Clicked cancel");
                P.TaskManager.BeginStack();
                try
                {
                    P.TaskManager.Enqueue(() => SelectSpecificVentureByName(ventureID), "SelectSpecificVenture");
                    P.TaskManager.EnqueueDelay(10, true);
                    P.TaskManager.Enqueue(() => CheckForErrorAssignedVenture(ventureID), "RedoErrorCheck", new(timeLimitMS: 500, abortOnTimeout: false));
                }
                catch(Exception e) { e.Log(); }
                P.TaskManager.InsertStack();
                return true;
            }
        }
        else
        {
            Utils.RethrottleGeneric();
        }
        return false;
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
    public static bool? SelectSpecificVentureByName(uint id)
    {
        return SelectSpecificVentureByName(VentureUtils.GetVentureName(id));
    }

    [Obsolete]
    public static bool? ForceSearchVentureByName(uint id)
    {
        return ForceSearchVentureByName(VentureUtils.GetVentureName(id));
    }

    public static bool? SelectSpecificVentureByName(string name)
    {
        if(TryGetAddonByName<AtkUnitBase>("RetainerTaskSupply", out var addon) && IsAddonReady(addon) && addon->AtkValuesCount > 2)
        {
            var state = addon->AtkValues[3];
            if(state.Type == 0)
            {
                FrameThrottler.Throttle("RetainerTaskSupply.InitWait", 10, true);
                PluginLog.Debug($"RetainerTaskSupply waiting (2)...");
                return false;
            }

            if(FrameThrottler.Check("RetainerTaskSupply.InitWait") && Utils.GenericThrottle)
            {
                // 🔴 清單節點取不到時**不能**掉進下面的 else —— 那個 else 會真的送出搜尋 callback。
                //    「讀不到」與「清單不可見」是兩件事,前者一律回 false 等下一輪重試。
                var listNode = Utils.GetNodeSafe(&addon->UldManager, 3);
                if(listNode == null) return false;
                if(listNode->IsVisible())
                {
                    var list = listNode->GetAsAtkComponentList();
                    if(list == null) return false;
                    PluginLog.Debug($"Cnt: {list->ListLength}");
                    for(var i = 0; i < Math.Min(list->ListLength, 16); i++)
                    {
                        // ⚠️ ListLength 是「清單有幾筆」,與 NodeList 的 NodeListCount 是兩個不同的量,
                        //    拿前者當後者的上界就是半套邊界檢查(越界讀到相鄰記憶體、不是 null)。
                        //    GetNodeSafe 會把上界與元素判空一起做掉。
                        var el = Utils.GetNodeSafe(&list->AtkComponentBase.UldManager, 2 + i);
                        var elNode = el == null ? null : el->GetAsAtkComponentNode();
                        if(elNode == null || elNode->Component == null) continue;
                        // 讀不到這一列的文字就跳過這一列(fail-closed:寧可漏配,也不要拿空字串去比對名稱,
                        // 比中的後果是對錯的雇員任務按下去)。
                        if(!Utils.TryGetNodeText(Utils.GetNodeSafe(&elNode->Component->UldManager, 9), out var text)) continue;
                        PluginLog.Debug($"Text: {text}, name: {name}");
                        if(text == name)
                        {
                            PluginLog.Debug($"Match");
                            // RetainerTaskSupply 選列/清空/搜尋都不關窗,是刻意的重試迴圈:帶參數組,
                            // 同位址同參數組 15 幀內只送一次,不同參數組照常。
                            if(!DialogGuards.TryPressOnce("RetainerTaskSupply", (nint)addon, "SelectVentureRow", $"Select{i}", escapeIsRoutine: true)) return false;
                            Callback.Fire(addon, true, 5, i, new AtkValue() { Type = 0, Int = 0 });
                            return true;
                        }
                    }

                    if(DialogGuards.TryPressOnce("RetainerTaskSupply", (nint)addon, "ClearVentureList", "Clear", escapeIsRoutine: true))
                    {
                        Callback.Fire(addon, true, 1);
                    }
                    return false;
                }
                else
                {
                    if(DialogGuards.TryPressOnce("RetainerTaskSupply", (nint)addon, "SearchVenture", $"Search{name}", escapeIsRoutine: true))
                    {
                        Callback.Fire(addon, true, 2, new AtkValue() { Type = 0, Int = 0 }, name);
                    }
                    Utils.RethrottleGeneric();
                    return false;
                }
            }
        }
        else
        {
            FrameThrottler.Throttle("RetainerTaskSupply.InitWait", 10, true);
            PluginLog.Debug($"RetainerTaskSupply waiting...");
        }
        return false;
    }

    public static bool? ForceSearchVentureByName(string name)
    {
        if(TryGetAddonByName<AtkUnitBase>("RetainerTaskSupply", out var addon) && IsAddonReady(addon) && addon->AtkValuesCount > 2)
        {
            var state = addon->AtkValues[3];
            if(state.Type == 0)
            {
                FrameThrottler.Throttle("RetainerTaskSupply.InitWait", 10, true);
                PluginLog.Debug($"RetainerTaskSupply waiting (2)...");
                return false;
            }

            if(FrameThrottler.Check("RetainerTaskSupply.InitWait")
                && DialogGuards.TryPressOnce("RetainerTaskSupply", (nint)addon, "ForceSearchVenture", $"Search{name}", escapeIsRoutine: true))
            {
                Callback.Fire(addon, true, 2, new AtkValue() { Type = 0, Int = 0 }, name);
                Utils.RethrottleGeneric();
                return true;
            }
        }
        else
        {
            FrameThrottler.Throttle("RetainerTaskSupply.InitWait", 10, true);
            PluginLog.Debug($"RetainerTaskSupply waiting...");
        }
        return false;
    }

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
