using AutoRetainer.Internal;
using AutoRetainer.Modules.Voyage.Tasks;
using Dalamud.Game.ClientState.Conditions;
using ECommons.Automation;
using ECommons.GameFunctions;
using ECommons.GameHelpers;
using ECommons.Throttlers;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace AutoRetainer.Modules.Voyage;

internal static unsafe class VoyageScheduler
{
    internal static void Log(string t)
    {
        VoyageUtils.Log(t);
    }

    internal static bool Enabled = false;
    internal static bool? SelectQuitVesselMenu()
    {
        return Utils.TrySelectSpecificEntry(Lang.VoyageQuitEntry);
    }

    internal static bool? CloseRepair()
    {
        if(TryGetAddonByName<AtkUnitBase>("CompanyCraftSupply", out var addon) && IsAddonReady(addon))
        {
            // 🔴 關窗即關;同一扇只送一次(GenericThrottle 下界 0 幀,不是防護)。
            if(Utils.GenericThrottle && DialogGuards.TryPressOnce("CompanyCraftSupply", (nint)addon, "CloseRepair"))
            {
                Log("Closing repair window (CompanyCraftSupply)");
                Callback.Fire(addon, true, 5);
                return true;
            }
        }
        else if(TryGetAddonByName<AtkUnitBase>("AirShipPartsMenu", out var addon2) && IsAddonReady(addon2))
        {
            if(Utils.GenericThrottle && DialogGuards.TryPressOnce("AirShipPartsMenu", (nint)addon2, "CloseRepair"))
            {
                Log("Closing repair window (AirShipPartsMenu)");
                Callback.Fire(addon2, true, 5);
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// True while one of the two windows that <see cref="CloseRepair"/> knows how to close is up.
    /// Both of them cover the voyage SelectString menu, which makes
    /// <see cref="VoyageUtils.GetCurrentWorkshopPanelType"/> report <see cref="PanelType.None"/>.
    /// </summary>
    internal static bool IsVesselPartsWindowOpen()
    {
        return (TryGetAddonByName<AtkUnitBase>("CompanyCraftSupply", out var addon) && IsAddonReady(addon))
            || (TryGetAddonByName<AtkUnitBase>("AirShipPartsMenu", out var addon2) && IsAddonReady(addon2));
    }

    /// <summary>
    /// Diagnostic only: the part picker popup that <see cref="PartSwapper.PartSwapperTasks.ChangeComponent"/> drives.
    /// </summary>
    internal static bool IsPartPickerOpen()
    {
        return TryGetAddonByName<AtkUnitBase>("ContextIconMenu", out var addon) && IsAddonReady(addon);
    }

    internal static bool? TryRepair(int slot)
    {
        if(TaskRepairAll.Abort) return true;
        var t = $"VoyageScheduler.TryRepair{slot}";
        if(TryGetAddonByName<AtkUnitBase>("SelectYesno", out var _))
        {
            Log("Found yesno, repair request success");
            return true;
        }
        if(EzThrottler.Check(t))
        {
            if(TryGetAddonByName<AtkUnitBase>("CompanyCraftSupply", out var addon) && IsAddonReady(addon))
            {
                // 修理請求不關窗(開出確認框才 return true):帶參數組(哪一格),同一格 15 幀內不重送。
                if(Utils.GenericThrottle && DialogGuards.TryPressOnce("CompanyCraftSupply", (nint)addon, t, $"Repair{slot}", escapeIsRoutine: true))
                {
                    Callback.Fire(addon, true, (int)3, Utils.ZeroAtkValue, (int)slot, Utils.ZeroAtkValue, Utils.ZeroAtkValue, Utils.ZeroAtkValue);
                    EzThrottler.Throttle(t, 1000, true);
                    Log($"Executing CompanyCraftSupply repair request on slot {slot} ");
                    return false;
                }
            }
            else if(TryGetAddonByName<AtkUnitBase>("AirShipPartsMenu", out var addon2) && IsAddonReady(addon2))
            {
                if(Utils.GenericThrottle && DialogGuards.TryPressOnce("AirShipPartsMenu", (nint)addon2, t, $"Repair{slot}", escapeIsRoutine: true))
                {
                    Callback.Fire(addon2, true, (int)3, Utils.ZeroAtkValue, (int)slot, Utils.ZeroAtkValue, Utils.ZeroAtkValue, Utils.ZeroAtkValue);
                    EzThrottler.Throttle(t, 1000, true);
                    Log($"Executing AirShipPartsMenu repair request on slot {slot} ");
                    return false;
                }
            }
            else
            {
                Utils.RethrottleGeneric();
            }
        }
        return false;
    }

    internal static bool? WaitForYesNoDisappear()
    {
        return !TryGetAddonByName<AtkUnitBase>("SelectYesno", out _);
    }

    internal static bool? WaitForCutscene()
    {
        return Svc.Condition[ConditionFlag.OccupiedInCutSceneEvent]
            || Svc.Condition[ConditionFlag.WatchingCutscene78];
    }

    internal static bool? WaitForNoCutscene()
    {
        return !(Svc.Condition[ConditionFlag.OccupiedInCutSceneEvent]
            || Svc.Condition[ConditionFlag.WatchingCutscene78]);
    }


    internal static bool? Lockon()
    {
        if(VoyageUtils.TryGetNearestVoyagePanel(out var obj))
        {
            if(Svc.Targets.Target?.Address != obj.Address)
            {
                if(Utils.GenericThrottle)
                {
                    Log("Targeting workshop CP");
                    Svc.Targets.Target = obj;
                }
            }
            else
            {
                if(Utils.GenericThrottle)
                {
                    Log("Locking on workshop CP");
                    Chat.ExecuteCommand("/lockon");
                    return true;
                }
            }
        }
        return false;
    }

    internal static bool? Approach()
    {
        if(VoyageUtils.TryGetNearestVoyagePanel(out var obj) && Svc.Targets.Target?.Address == obj.Address)
        {
            if(Utils.GenericThrottle)
            {
                // Same shape as the retainer bell approach: AutomoveOffPanel is a separate queued
                // step that only completes inside ~4 yalms, so it must not be the only thing that
                // can stop autorun. See AutomoveManager.
                AutomoveManager.On();
                Utils.RegenerateRandom();
                return true;
            }
        }
        return false;
    }

    internal static bool? AutomoveOffPanel()
    {
        if(VoyageUtils.TryGetNearestVoyagePanel(out var obj) && Svc.Targets.Target?.Address == obj.Address)
        {
            if(Vector3.Distance(obj.Position, Player.Object.Position) < 4f + Utils.Random * 0.25f)
            {
                if(Utils.GenericThrottle)
                {
                    AutomoveManager.Off();
                    return true;
                }
            }
        }
        return false;
    }

    internal static bool? InteractWithVoyagePanel()
    {
        if(VoyageUtils.TryGetNearestVoyagePanel(out var obj))
        {
            if(Svc.Targets.Target?.Address == obj.Address)
            {
                if(Player.IsAnimationLocked) return false;
                if(Utils.GenericThrottle && EzThrottler.Throttle("Voyage.Interact", 2000))
                {
                    Log("Interacting with workshop CP");
                    TargetSystem.Instance()->InteractWithObject(obj.Struct(), false);
                    return true;
                }
            }
            else
            {
                if(obj.IsTargetable && Utils.GenericThrottle)
                {
                    Svc.Targets.Target = obj;
                }
            }
        }
        return false;
    }

    internal static bool? SelectAirshipManagement()
    {
        return Utils.TrySelectSpecificEntry(Lang.AirshipManagement, () => Utils.GenericThrottle && EzThrottler.Throttle("Voyage.SelectManagement", 1000));
    }

    internal static bool? SelectSubManagement()
    {
        return Utils.TrySelectSpecificEntry(Lang.SubmarineManagement, () => Utils.GenericThrottle && EzThrottler.Throttle("Voyage.SelectManagement", 1000));
    }

    internal static bool? SelectExitMainPanel()
    {
        return Utils.TrySelectSpecificEntry(Lang.CancelVoyage, () => Utils.GenericThrottle && EzThrottler.Throttle("Voyage.ExitMainPanel", 1000));
    }

    internal static bool? SelectVesselByName(string name, VoyageType type)
    {
        var index = VoyageUtils.GetVesselIndex(name, type);
        if(index != null)
        {
            if(TryGetAddonByName<AddonSelectString>("SelectString", out var addon) && IsAddonReady(&addon->AtkUnitBase))
            {
                var entries = Utils.GetEntries(addon);
                // 讀到 U+FFFD 這一幀不碰;選項按下即關窗,同一扇 SelectString 只按一次。
                if(index.Value < entries.Count && !DialogGuards.TextIsUnstable(entries[index.Value]) && entries[index.Value].Contains(name))
                {
                    if(index >= 0 && Utils.GenericThrottle && EzThrottler.Throttle("SelectVesselByName") && DialogGuards.TryPressOnce("SelectString", (nint)addon, "SelectVesselByName"))
                    {
                        DebugLog($"Selecting vessel {name}/{type}/{entries[index.Value]}/{index}");
                        new AddonMaster.SelectString(addon).Entries[index.Value].Select();
                        return true;
                    }
                }
            }
            else
            {
                Utils.RethrottleGeneric();
            }
        }
        return false;
    }


    internal static bool? SelectViewPreviousLog()
    {
        return Utils.TrySelectSpecificEntry(Lang.ViewPrevVoyageLog, () => Utils.GenericThrottle && EzThrottler.Throttle("Voyage.SelectViewPreviousLog", 1000));
    }

    internal static bool WaitUntilFinalizeDeployAddonExists()
    {
        return TryGetAddonByName<AtkUnitBase>("AirShipExplorationResult", out var addon) && IsAddonReady(addon);
    }

    internal static bool? RedeployVessel()
    {
        if(!TryGetAddonByName<AtkUnitBase>("AirShipExplorationResult", out _)) return true;
        if(TryGetAddonByName<AtkUnitBase>("AirShipExplorationDetail", out _)) return true;
        if(TryGetAddonByName<AtkUnitBase>("AirShipExplorationResult", out var addon) && IsAddonReady(addon))
        {
            // 🔴 NodeList[3] 原本上界與元素都沒驗;GetAsAtkComponentButton() 是 [MemberFunction],
            //    對 null 節點呼叫等於把 this = 0 交給遊戲原生碼。
            //    取不到 → button 為 null → IsButtonEnabled 回 false → 走既有的「按鈕還沒能按」
            //    重試路徑(節流 500ms 後再來),不會誤觸發再次出航。
            var node = Utils.GetNodeSafe(&addon->UldManager, 3);
            var button = node == null ? null : node->GetAsAtkComponentButton();
            if(!Utils.IsButtonEnabled(button))
            {
                EzThrottler.Throttle("Voyage.Redeploy", 500, true);
                return false;
            }
            else
            {
                if(Utils.GenericThrottle && EzThrottler.Throttle($"Voyage.Redeploy_{(nint)addon}", 5000) && DialogGuards.TryPressOnce("AirShipExplorationResult", (nint)addon, "Voyage.Redeploy"))
                {
                    Callback.Fire(addon, true, 1);
                    return false;
                }
            }
        }
        else
        {
            Utils.RethrottleGeneric();
        }
        return false;
    }

    internal static bool? FinalizeVessel()
    {
        if(!TryGetAddonByName<AtkUnitBase>("AirShipExplorationResult", out _)) return true;
        if(TryGetAddonByName<AtkUnitBase>("AirShipExplorationResult", out var addon) && IsAddonReady(addon))
        {
            // 🔴 按下即關、按完 return false 輪詢到窗消失:關閉中的窗三關全過,同一扇只按一次。
            if(Utils.GenericThrottle && EzThrottler.Throttle($"Voyage.Redeploy_{(nint)addon}", 1000) && DialogGuards.TryPressOnce("AirShipExplorationResult", (nint)addon, "Voyage.Finalize"))
            {
                Callback.Fire(addon, true, 0);
                return false;
            }
        }
        else
        {
            Utils.RethrottleGeneric();
        }
        return false;
    }

    internal static bool? DeployVessel()
    {
        if(TryGetAddonByName<AtkUnitBase>("AirShipExplorationDetail", out var addon) && IsAddonReady(addon))
        {
            if(Utils.GenericThrottle && EzThrottler.Throttle("Voyage.Deploy") && DialogGuards.TryPressOnce("AirShipExplorationDetail", (nint)addon, "Voyage.Deploy"))
            {
                Callback.Fire(addon, true, 0);
                return true;
            }
        }
        else
        {
            Utils.RethrottleGeneric();
        }
        return false;
    }

    internal static bool? CancelDeployVessel()
    {
        if(TryGetAddonByName<AtkUnitBase>("AirShipExplorationDetail", out var addon) && IsAddonReady(addon))
        {
            if(Utils.GenericThrottle && EzThrottler.Throttle("Voyage.CancelDeploy") && DialogGuards.TryPressOnce("AirShipExplorationDetail", (nint)addon, "Voyage.CancelDeploy"))
            {
                Callback.Fire(addon, true, -1);
                return true;
            }
        }
        else
        {
            Utils.RethrottleGeneric();
        }
        return false;
    }

    internal static bool? SelectQuitVesselSelectorMenu()
    {
        return Utils.TrySelectSpecificEntry(Lang.NothingVoyage, () => EzThrottler.Throttle("Voyage.Quit", 1000));
    }

}
