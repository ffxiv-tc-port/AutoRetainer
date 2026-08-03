using Dalamud.Utility;
using ECommons.Throttlers;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using ValueType = FFXIVClientStructs.FFXIV.Component.GUI.ValueType;

namespace AutoRetainer.Modules.Voyage.PartSwapper;
public static unsafe class PartSwapperTasks
{
    public static void Log(string t)
    {
        VoyageUtils.Log(t);
    }

    public static bool? SelectChangeComponents()
    {
        return Utils.TrySelectSpecificEntry(Lang.ChangeSubmersibleComponents, () => Utils.GenericThrottle && EzThrottler.Throttle("Voyage.SelectManagement", 1000));
    }

    public static bool? SelectRegisterSub()
    {
        return Utils.TrySelectSpecificEntry(Lang.RegisterSub, () => Utils.GenericThrottle && EzThrottler.Throttle("Voyage.RegisterSub", 1000));
    }

    public static bool? RegisterSub()
    {
        if(TryGetAddonByName<AtkUnitBase>("SelectYesno", out var _))
        {
            Log("Found yesno, register request success");
            return true;
        }
        if(GenericHelpers.TryGetAddonMaster<AddonMaster.CompanyCraftSupply>(out var addon) && addon.IsAddonReady)
        {
            if(Utils.GenericThrottle)
            {
                if(addon.CloseButton->IsEnabled)
                {
                    Log("Registering sub");
                    addon.Close();
                }
            }
        }
        return false;
    }

    public static bool? SetupNewSub()
    {
        if(TryGetAddonByName<AtkUnitBase>("SelectString", out var addon) && IsAddonReady(addon))
        {
            foreach(var plans in C.LevelAndPartsData)
            {
                if(plans.MinLevel == 1)
                {
                    if(Data.OfflineSubmarineData.Count != Data.NumSubSlots)
                    {
                        PluginLog.Warning($"OfflineSubmarineData has a size of {Data.OfflineSubmarineData.Count} but expected {Data.NumSubSlots}.");
                        return false;
                    }

                    var newSubName = Data.OfflineSubmarineData[Data.NumSubSlots - 1].Name;
                    Data.AdditionalSubmarineData[newSubName].VesselBehavior = Data.NumSubSlots == 1 && plans.FirstSubDifferent ? plans.FirstSubVesselBehavior : plans.VesselBehavior;
                    Data.AdditionalSubmarineData[newSubName].UnlockMode = Data.NumSubSlots == 1 && plans.FirstSubDifferent ? plans.FirstSubUnlockMode : plans.UnlockMode;
                    Data.AdditionalSubmarineData[newSubName].SelectedUnlockPlan = Data.NumSubSlots == 1 && plans.FirstSubDifferent ? plans.FirstSubSelectedUnlockPlan : plans.SelectedUnlockPlan;

                    Data.EnabledSubs.Add(newSubName);

                    return true;
                }
            }
        }

        return false;
    }

    public static bool? ChangeComponent(int slot, uint componentId, string name = "")
    {
        var t = $"VoyageScheduler.ChangeComponent{slot}";
        if(EzThrottler.Check(t))
        {
            if(!string.IsNullOrEmpty(name) && PartSwapperUtils.GetSubPart(name, slot) == componentId)
                return true;

            if(TryGetAddonByName<AddonContextIconMenu>("ContextIconMenu", out var addon) && IsAddonReady(&addon->AtkUnitBase))
            {
                var availablePartAmount = addon->AtkValuesSpan[4];
                if(availablePartAmount.Type != ValueType.UInt) return false;

                // Hoisted out of the loop: this used to re-read the Item sheet once per entry per
                // frame. Comparison is OrdinalIgnoreCase rather than ToLower() == ToLower() so it
                // does not depend on CurrentCulture (both sides go through ExtractText, and on TC
                // every submersible part name is pure CJK, so this is behaviour-identical there).
                var partName = Svc.Data.Excel.GetSheet<Item>().GetRow(componentId).Name.ToString();
                var matched = false;

                for(var i = 0; i < availablePartAmount.UInt; i++)
                {
                    var valueIndex = 13 + (8 * i);
                    // availablePartAmount comes from the addon itself; do not trust it past the
                    // end of the value array, an out-of-range read here would throw and (with the
                    // default abortOnError) wipe the whole task queue.
                    if(valueIndex >= addon->AtkValuesSpan.Length) break;
                    var value = addon->AtkValuesSpan[valueIndex];
                    if(value.Type != ValueType.ManagedString && value.Type != ValueType.String) continue;
                    if(!value.String.ExtractText().Equals(partName, StringComparison.OrdinalIgnoreCase)) continue;

                    Callback.Fire(&addon->AtkUnitBase, true, Utils.ZeroAtkValue, i, componentId, Utils.ZeroAtkValue, Utils.ZeroAtkValue);
                    EzThrottler.Throttle(t, 1500, true);
                    Log($"Executing ContextIconMenu change request on slot {slot} ");
                    matched = true;

                    if(string.IsNullOrEmpty(name))
                        return true;

                    break;
                }

                if(!matched && EzThrottler.Throttle($"Voyage.ChangeComponentNoMatch{slot}", 10000))
                {
                    // The picker is fully populated at this point, so "no match" is final: this task
                    // will keep returning false until it times out. Written at Information (and
                    // throttled) because it is the only place a localisation/text mismatch between
                    // the picker and the Item sheet would ever become visible in a user's log.
                    var entries = new List<string>();
                    for(var i = 0; i < availablePartAmount.UInt; i++)
                    {
                        var valueIndex = 13 + (8 * i);
                        if(valueIndex >= addon->AtkValuesSpan.Length) break;
                        var value = addon->AtkValuesSpan[valueIndex];
                        entries.Add(value.Type is ValueType.ManagedString or ValueType.String ? value.String.ExtractText() : $"<{value.Type}>");
                    }
                    PluginLog.Information($"[Voyage] Part picker has no entry matching item {componentId} \"{partName}\" for slot {slot}; entries=[{entries.Join(" | ")}]");
                }

                return false;
            }

            if(TryGetAddonByName<AtkUnitBase>("CompanyCraftSupply", out var addon2) && IsAddonReady(addon2))
            {
                Callback.Fire(addon2, true, (int)2, (int)1, (int)slot, Utils.ZeroAtkValue, Utils.ZeroAtkValue, Utils.ZeroAtkValue);
                EzThrottler.Throttle(t, 1500, true);
                Log($"Executing CompanyCraftSupply request on slot {slot} ");
            }
            else
            {
                Utils.RethrottleGeneric();
            }
        }

        return false;
    }

    public static bool? CloseChangeComponents()
    {
        if(TryGetAddonByName<AtkUnitBase>("CompanyCraftSupply", out var addon) && IsAddonReady(addon))
        {
            if(Utils.GenericThrottle)
            {
                Log("Closing components window (CompanyCraftSupply)");
                Callback.Fire(addon, true, 5);
                return true;
            }
        }
        return false;
    }
}
