using AutoRetainer.Modules.Voyage;

using Dalamud.Game.ClientState.Conditions;
using ECommons.Throttlers;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.UI;
using Lumina.Excel.Sheets;

namespace AutoRetainer.Modules;

internal static unsafe class MiniTA
{
    internal static void Tick()
    {
        if(!IPC.Suppressed)
        {
            if(VoyageScheduler.Enabled)
            {
                ConfirmCutsceneSkip();
                ConfirmRepair();
                ConfirmRegister();
            }
            if(P.TaskManager.IsBusy || (Svc.Condition[ConditionFlag.OccupiedSummoningBell] && (SchedulerMain.PluginEnabled || P.TaskManager.IsBusy || P.ConditionWasEnabled)))
            {
                if(TryGetAddonByName<AddonTalk>("Talk", out var addon) && addon->AtkUnitBase.IsVisible)
                {
                    new AddonMaster.Talk((nint)addon).Click();
                }
            }
            if(C.SkipItemConfirmations && (P.TaskManager.IsBusy || AutoGCHandin.Operation))
            {
                SkipItemConfirmations();
            }
        }
    }

    internal static void SkipItemConfirmations()
    {
        //397	This item has materia attached. Are you certain you wish to sell it?
        //398	Your spiritbond with this item is 100%. Are you certain you wish to sell it?
        //399 This item is unique and untradable.Are you certain you wish to sell it?
        //4477  Are you certain you wish to sell this item ?
        //102433	Do you really want to trade an item with materia affixed? The materia will be lost.
        //102434	Do you really want to trade a high-quality item?
        var x = Utils.GetSpecificYesno(s => s.Cleanup().ContainsAny(StringComparison.OrdinalIgnoreCase, Ref<string[]>.Get("Skip", () => ((uint[])[397, 398, 399, 4477, 102433, 102434]).Select(a => Svc.Data.GetExcelSheet<Addon>().GetRow(a).Text.GetText().Cleanup()).ToArray())));
        if(x != null && IsAddonReady(x))
        {
            new AddonMaster.SelectYesno(x).Yes();
        }
    }

    internal static void ConfirmRepair()
    {
        var x = Utils.GetSpecificYesno((s) => s.ContainsAny(StringComparison.OrdinalIgnoreCase, Lang.WorkshopRepairConfirm));
        if(x != null && Utils.GenericThrottle)
        {
            VoyageUtils.Log("Confirming repair");
            new AddonMaster.SelectYesno((nint)x).Yes();
        }
    }

    internal static void ConfirmRegister()
    {
        var x = Utils.GetSpecificYesno((s) => s.ContainsAny(StringComparison.OrdinalIgnoreCase, Lang.WorkshopRegisterConfirm));
        if(x != null && Utils.GenericThrottle)
        {
            VoyageUtils.Log("Confirming registration");
            new AddonMaster.SelectYesno((nint)x).Yes();
        }
    }

    internal static void ConfirmCutsceneSkip()
    {
        var addon = Svc.GameGui.GetAddonByName("SelectString", 1);
        if(addon == IntPtr.Zero) return;
        var selectStrAddon = (AddonSelectString*)addon.Address;
        if(!IsAddonReady(&selectStrAddon->AtkUnitBase))
        {
            return;
        }
        //PluginLog.Debug($"1: {selectStrAddon->AtkUnitBase.UldManager.NodeList[3]->GetAsAtkTextNode()->NodeText.ToString()}");
        // 🔴 原本是四跳裸鏈:NodeList[3](上界與元素都沒驗,越界讀到的是相鄰記憶體不是 null)
        //    → GetAsAtkTextNode()([MemberFunction],對 null 節點呼叫＝把 this = 0 交給原生碼)
        //    → ->NodeText(對 null 文字節點靜默算出毒指標 0xC0,不會當場崩)
        //    → ToString() 才真的去讀位址 0xC0 —— 崩潰現場完全指不到這一行。
        //    ⚠️ 這裡刻意不用 Utils.TryGetNodeText:那支走 ReadSeString().GetText() 會剝掉
        //    SeString payload,與 Lang.SkipCutsceneStr 的比對基準不同,換過去等於順手改行為。
        //    讀不到就 return(＝這一幀不跳過過場),與「文字不在跳過清單裡」同語意;
        //    這是每幀輪詢的路徑,所以不寫 log。
        var entryNode = Utils.GetNodeSafe(&selectStrAddon->AtkUnitBase.UldManager, 3);
        var entryTextNode = entryNode == null ? null : entryNode->GetAsAtkTextNode();
        if(entryTextNode == null) return;
        if(!Lang.SkipCutsceneStr.Contains(entryTextNode->NodeText.ToString())) return;
        if(EzThrottler.Throttle("SkipCutsceneConfirm"))
        {
            PluginLog.Debug("Selecting cutscene skipping");
            new AddonMaster.SelectString(addon).Entries[0].Select();
        }
    }

    internal static bool ProcessCutsceneSkip(nint arg)
    {
        return VoyageScheduler.Enabled;
    }
}
