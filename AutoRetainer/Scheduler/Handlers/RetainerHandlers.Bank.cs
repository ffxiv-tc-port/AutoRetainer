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
    internal static bool? SetWithdrawGilAmount(int percent)
    {
        if(TryGetAddonByName<AtkUnitBase>("Bank", out var addon) && IsAddonReady(addon) && Utils.TryGetCurrentRetainer(out var name) && Utils.TryGetRetainerByName(name, out var retainer))
        {
            if(percent < 1 || percent > 100) throw new ArgumentOutOfRangeException(nameof(percent), percent, "Percent must be between 1 and 100");
            // 🔴 走 Utils.TryGetNodeText:GetAsAtkTextNode() 對 null 節點是當場 AVE,
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
            var current = (nint)addon;
            if(bankPendingClose == current && DialogGuards.WasPressed("Bank", current))
            {
                // 🔴 上一輪送出的提領／取消 callback 沒有把這扇窗關掉，現在才補送 Close(true)。
                // 危險的是「順序」而不是「多餘」：callback 的處理常式若自己把窗收掉，緊接的 Close 就落在關閉中的窗上（攔不到的原生 AccessViolation）。
                // 把 Close 挪到「下一輪、重新用窗名解出來的位址」上，窗有沒有被那一發關掉兩種可能都安全。
                // 🔴 這裡刻意**不**掛 Utils.GenericThrottle：它數的是繪製幀，過場動畫與隱藏 UI 期間會停住；DialogGuards 自己的時鐘掛在 Framework.Update 上。
                if(!DialogGuards.TryPressOnce("Bank", current, "ProcessBank.Close", escapeIsRoutine: true)) return false;
                bankPendingClose = 0;
                addon->Close(true);
                DebugLog($"Closed bank window");
                return true;
            }
            // 不是那扇待關的窗（位址對不上，或那扇已經消失、現在這扇是新的）⇒ 記號作廢，走正常流程。
            bankPendingClose = 0;
            // 🔴 兩個節點都走 GetNodeSafe,失敗語意刻意分兩層:
            //    ①「提領」節點取不到 → 落到 else 走「取消」分支(與「提領鈕不可見／未啟用」相同,
            //       forceCancel 的既有行為也是靠這條路),不是直接放棄整步;
            //    ②「取消」節點也取不到 → 回 false 讓下一輪重試,絕不回 true(回 true 會讓佇列
            //       以為銀行視窗已經處理掉了,實際上它還開著)。
            var withdrawNode = Utils.GetNodeSafe(&addon->UldManager, 3);
            var withdraw = withdrawNode == null ? null : (AtkComponentButton*)withdrawNode->GetComponent();
            if(withdrawNode != null && withdrawNode->IsVisible() && Utils.IsButtonEnabled(withdraw) && !forceCancel)
            {
                // 🔴 提領/取消按下即關;同一扇 Bank 只按一次。關窗那一步移到下一輪(理由見上面補送 Close 那一段)。
                if(Utils.GenericThrottle && DialogGuards.TryPressOnce("Bank", current, "ProcessBank"))
                {
                    var v = stackalloc AtkValue[]
                    {
                        new() { Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int, Int = 0 },
                        new() { Type = 0, Int = 0 }
                    };
                    addon->FireCallback(2, v);
                    // 🔴 送出之後這一輪不再碰這扇窗（連 Close 都不行）。回 false 的意義是「這一輪沒做完，
                    //    下一輪再來」，與「addon 還沒出現」「節流還沒放行」走的是同一條既有路徑。
                    bankPendingClose = current;
                    DebugLog($"Withdraw callback sent; window close deferred to next round");
                    //new ClickButtonGeneric(addon, "Bank").Click(withdraw);
                    return false;
                }
            }
            else
            {
                var cancelNode = Utils.GetNodeSafe(&addon->UldManager, 2);
                var cancel = cancelNode == null ? null : (AtkComponentButton*)cancelNode->GetComponent();
                if(cancelNode != null && cancelNode->IsVisible() && Utils.IsButtonEnabled(cancel))
                {
                    if(Utils.GenericThrottle && DialogGuards.TryPressOnce("Bank", current, "CancelBank"))
                    {
                        var v = stackalloc AtkValue[]
                        {
                            new() { Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int, Int = 1 },
                            new() { Type = 0, Int = 0 }
                        };
                        addon->FireCallback(2, v);
                        // 理由同提領那一支:Close 移到下一輪重新解位址之後再做。
                        bankPendingClose = current;
                        DebugLog($"Cancel callback sent; window close deferred to next round");
                        //new ClickButtonGeneric(addon, "Bank").Click(cancel);
                        return false;
                    }
                }
            }
        }
        else if(bankPendingClose != 0)
        {
            // 我們送過提領／取消 callback，而那扇窗現在已經不在（或已經進入不 ready 的拆除狀態）⇒
            // 那一發自己把窗關掉了，這一步完成。🔴 這裡刻意不再碰那個位址，只是把記號清掉。
            bankPendingClose = 0;
            DebugLog($"Bank window closed by its own callback");
            return true;
        }
        else
        {
            Utils.RethrottleGeneric();
        }
        return false;
    }
}
