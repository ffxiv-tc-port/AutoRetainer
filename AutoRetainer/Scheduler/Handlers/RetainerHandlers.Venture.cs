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
            // 🔴 NodeList[6] 走 GetNodeSafe。這一步是「畫面上有沒有跳錯誤訊息」的判別式,
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
                        // 比中的後果是對錯的僱員任務按下去)。
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
}
