using ECommons.Automation;
using ECommons.ExcelServices.TerritoryEnumeration;
using ECommons.GameHelpers;
using ECommons.Throttlers;
using ECommons.UIHelpers.AddonMasterImplementations;

namespace AutoRetainer.Modules.Multi;

internal static unsafe class HouseEnterTask
{
    // AutorunOffBell 每幀跑:null＝這趟還沒量過,所以重置後的第一幀一定會印一次基準距離。
    private static bool? BellWithinStopRange;

    internal static bool? Approach()
    {
        DebugLog($"Enabling automove");
        BellWithinStopRange = null;
        Utils.RegenerateRandom();
        // Routed through AutomoveManager so the matching "off" is guaranteed even if the queued
        // AutorunOffBell step below never runs - see AutomoveManager for the full rationale.
        AutomoveManager.On();
        return true;
    }

    internal static bool? SelectYesno()
    {
        if(!ResidentalAreas.List.Contains(Svc.ClientState.TerritoryType))
        {
            return null;
        }
        var addon = Utils.GetSpecificYesno(Lang.ConfirmHouseEntrance);
        if(addon != null)
        {
            // 🔴 確認框按下即關;EzThrottler 不是防護。同一扇只按一次。
            if(IsAddonReady(addon) && EzThrottler.Throttle("HET.SelectYesno") && DialogGuards.TryPressOnce("SelectYesno", (nint)addon, "HET.SelectYesno"))
            {
                DebugLog("Select yes");
                new AddonMaster.SelectYesno((nint)addon).Yes();
                return true;
            }
        }
        else
        {
            if(Utils.TrySelectSpecificEntry(Lang.GoToYourApartment, () => EzThrottler.Throttle("HET.SelectYesno")))
            {
                DebugLog("Confirmed going to apartment");
                return true;
            }
        }
        return false;
    }

    internal static bool? WaitUntilLeavingZone()
    {
        return !ResidentalAreas.List.Contains(Svc.ClientState.TerritoryType);
    }

    internal static bool? LockonBell()
    {
        var bell = Utils.GetNearestRetainerBell(out var d);
        if(bell != null && d < 20f)
        {
            if(Svc.Targets.Target?.Address == bell.Address)
            {
                if(EzThrottler.Throttle("HET.LockonBell"))
                {
                    Chat.ExecuteCommand("/lockon");
                    return true;
                }
            }
            else
            {
                if(EzThrottler.Throttle("HET.SetTargetBell", 200))
                {
                    DebugLog($"Setting bell target ({bell})");
                    Svc.Targets.Target = bell;
                }
            }
        }
        return false;
    }


    internal static bool? AutorunOffBell()
    {
        var bell = Utils.GetReachableRetainerBell(false);
        if(bell == null) return false;
        var dist = Vector3.Distance(Player.Object.Position, bell.Position);
        var stopRange = 4f + Utils.Random * 0.25f;
        var within = dist < stopRange;
        // 只在跨過停步門檻那一幀寫 Information,其餘走 Debug:每幀一行 Information 會洗掉 log。
        if(within != BellWithinStopRange)
        {
            BellWithinStopRange = within;
            PluginLog.Information($"Dist {dist} stop range {stopRange} within {within}");
        }
        else
        {
            DebugLog($"Dist {dist}");
        }
        if(within)
        {
            DebugLog($"Disabling automove");
            AutomoveManager.Off();
            return true;
        }
        return false;
    }
}
