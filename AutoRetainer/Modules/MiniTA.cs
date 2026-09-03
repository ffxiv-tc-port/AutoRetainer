using AutoRetainer.Modules.Voyage;

using Dalamud.Game.ClientState.Conditions;
using ECommons.Throttlers;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.UI;
using Lumina.Excel.Sheets;

namespace AutoRetainer.Modules;

internal static unsafe class MiniTA
{
    // 🔴 這裡每一個按下點都是**每幀**被 AutoRetainer.Tick() 驅動的,而窗被按下之後有「正在關閉中」的
    //    幾幀:GetAddonByName 仍拿得到實例、IsAddonReady 三關也全過,此時再按一次就是攔不到的原生
    //    AccessViolation。「這扇窗已經按過了」的記號集中在 Helpers/DialogGuards.cs(以窗名為 key,
    //    同一扇 SelectYesno 不管是這裡還是 AutoGCHandin 按的都共用同一把),解除點在 DialogGuards.Tick
    //    (AutoRetainer.Tick 最前面、無條件),機制與「為什麼節流不是防護」也寫在那裡。

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
                    // 🔴 AddonMaster.Talk.Click() 是直接對 addon 送 MouseDown/Click/Up 三個
                    //    ReceiveEvent,對正在拆除的窗送就是同一族的原生 AVE;原本這裡是**每幀**送、
                    //    零節流,包含按完最後一頁之後的那幾幀。
                    // ⚠️ escapeIsRoutine: true —— Talk 與 SelectYesno 不同:它是「按一次翻一頁」,窗不會
                    //    因為被按而消失,所以走逃生口是常態(那才是翻到下一頁的方式),寫 Information 會洗版。
                    //    代價是同一扇 Talk 的翻頁節奏被壓成每 RoutineRepressIntervalFrames(15)幀一頁;
                    //    這是刻意選的:唯一能不靠未證實假設就分辨「換頁」與「關閉中」的判準只有時間。
                    if(DialogGuards.TryPressOnce("Talk", (nint)addon, "Talk", escapeIsRoutine: true))
                    {
                        new AddonMaster.Talk((nint)addon).Click();
                    }
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
            // 🔴 這支原本**零節流零狀態**,由 Tick 每幀驅動 —— 文字一比中就按,包含窗正在關閉的那幾幀。
            //    (同檔只有這支和上面的 Talk 是完全裸奔的:ConfirmRepair/ConfirmRegister 至少還有
            //    GenericThrottle、ConfirmCutsceneSkip 有 EzThrottler —— 但那些也都不是防護。)
            //    IsAddonReady 不是防護:關閉中的窗三關全過。
            if(DialogGuards.TryPressOnce("SelectYesno", (nint)x, "SkipItemConfirmations"))
            {
                new AddonMaster.SelectYesno(x).Yes();
            }
        }
    }

    internal static void ConfirmRepair()
    {
        var x = Utils.GetSpecificYesno((s) => s.ContainsAny(StringComparison.OrdinalIgnoreCase, Lang.WorkshopRepairConfirm));
        // 🔴 GenericThrottle 不是防護:它的幀數是 Utils.FrameDelay = 10 + C.ExtraFrameDelay,而
        //    ExtraFrameDelay 的合法範圍是 ValidateRange(-10, 100)(UI 滑桿只給 0..50,但設定檔可以是負的),
        //    設成 -10 時延遲就是 **0 幀** —— 每一幀都放行。而且它全外掛共用一把 key,記的是
        //    「上一次任何地方動作在哪一幀」,不是「這扇窗已經按過」。
        if(x != null && Utils.GenericThrottle)
        {
            if(DialogGuards.TryPressOnce("SelectYesno", (nint)x, "ConfirmRepair"))
            {
                // log 跟著實際按下走:被守衛擋下時不寫,否則 log 會宣稱按了但其實沒按。
                VoyageUtils.Log("Confirming repair");
                new AddonMaster.SelectYesno((nint)x).Yes();
            }
        }
    }

    internal static void ConfirmRegister()
    {
        var x = Utils.GetSpecificYesno((s) => s.ContainsAny(StringComparison.OrdinalIgnoreCase, Lang.WorkshopRegisterConfirm));
        // 🔴 GenericThrottle 不是防護,理由同 ConfirmRepair。
        if(x != null && Utils.GenericThrottle)
        {
            if(DialogGuards.TryPressOnce("SelectYesno", (nint)x, "ConfirmRegister"))
            {
                VoyageUtils.Log("Confirming registration");
                new AddonMaster.SelectYesno((nint)x).Yes();
            }
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
        var entryText = entryTextNode->NodeText.ToString();
        // 讀到 U+FFFD ＝ 這扇窗的記憶體正在變動(多半是關閉中),這一幀不碰它。
        if(DialogGuards.TextIsUnstable(entryText)) return;
        if(!Lang.SkipCutsceneStr.Contains(entryText)) return;
        // 🔴 選項按下即關窗;EzThrottler 500ms 不是防護(關閉中的窗三關全過)。同一扇 SelectString 只按一次,
        //    與 Utils.TrySelectSpecificEntry 共用同一把 key。
        if(EzThrottler.Throttle("SkipCutsceneConfirm") && DialogGuards.TryPressOnce("SelectString", (nint)selectStrAddon, "SkipCutsceneConfirm"))
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
