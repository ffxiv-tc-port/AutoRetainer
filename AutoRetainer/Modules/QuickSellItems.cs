using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Hooking;
using Dalamud.Utility.Signatures;
using ECommons.Interop;
using ECommons.MathHelpers;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using ValueType = FFXIVClientStructs.FFXIV.Component.GUI.ValueType;

namespace AutoRetainer.Modules;
#pragma warning disable CS0649
public unsafe class QuickSellItems : IDisposable
{
    internal delegate void* OpenInventoryContext(AgentInventoryContext* agent, InventoryType inventory, ushort slot, int a4, ushort a5, byte a6);
    // 🔴 原上游特徵碼 "83 B9 ?? ?? ?? ?? ?? 7E 11"(cmp [rcx+disp], imm; jle)在台服 7.20 命中 2 個位址:
    //    0x140470940(正解 OpenInventoryContext,函式起點、前接 16 個 int3 pad)與 0x140F072E1(某函式的
    //    函式中段,根本不是函式起點)。Dalamud [Signature] 取位址最低者,目前碰巧是正解,但那是運氣——
    //    台服下次改版排序一變就會靜默把 hook 掛到中段位址上。這裡是「原生程式碼直呼受管理 detour」的 hook,
    //    掛錯位址時 detour 會把別的函式的引數當成 AgentInventoryContext* 解參考(AVE 攔不到)。
    //    延長到鎖進 OpenInventoryContext 專有的引數驗證序列:cmp [rcx+0x6e4],0 / cmp [rcx+0x6dc],edx(=InventoryType)/
    //    cmp [rcx+0x6e0],r8d(=slot)——全部是結構位移常數,不含 rip 相對位移;離線驗證台服 7.20 全映像唯一命中 0x140470940。
    //    jle/jne/je 的 rel8 位移用 ?? 遮罩(允許中段程式碼微調)。特徵碼失配時 Fallible 讓 hook 留 null=功能靜默停用(fail-closed)。
    [Signature("83 B9 E4 06 00 00 00 7E ?? 39 91 DC 06 00 00 75 ?? 44 39 81 E0 06 00 00 74 ??", DetourName = nameof(OpenInventoryContextDetour), Fallibility = Fallibility.Fallible)]
    internal Hook<OpenInventoryContext> openInventoryContextHook;

    public InventoryType[] CanSellFrom = [
        InventoryType.Inventory1,
        InventoryType.Inventory2,
        InventoryType.Inventory3,
        InventoryType.Inventory4,
        InventoryType.ArmoryMainHand,
        InventoryType.ArmoryHead,
        InventoryType.ArmoryBody,
        InventoryType.ArmoryHands,
        InventoryType.ArmoryLegs,
        InventoryType.ArmoryFeets,
        InventoryType.ArmoryEar,
        InventoryType.ArmoryNeck,
        InventoryType.ArmoryWrist,
        InventoryType.ArmoryRings,
        InventoryType.ArmoryOffHand,
        InventoryType.RetainerPage1,
        InventoryType.RetainerPage2,
        InventoryType.RetainerPage3,
        InventoryType.RetainerPage4,
        InventoryType.RetainerPage5,
        InventoryType.RetainerPage6,
        InventoryType.RetainerPage7,
    ];

    private string retainerSellText;
    private string entrustToRetainerText;
    private string retrieveFromRetainerText;
    private string putUpForSaleText;

    public QuickSellItems()
    {
        //5480	Have Retainer Sell Items
        retainerSellText = Svc.Data.GetExcelSheet<Addon>()?.GetRow(5480).Text.ToString() ?? "Have Retainer Sell Items";
        //97	Entrust to Retainer
        entrustToRetainerText = Svc.Data.GetExcelSheet<Addon>()?.GetRow(97).Text.ToString() ?? "Entrust to Retainer";
        //98	Retrieve from Retainer
        retrieveFromRetainerText = Svc.Data.GetExcelSheet<Addon>()?.GetRow(98).Text.ToString() ?? "Retrieve from Retainer";
        //99	Put Up for Sale
        putUpForSaleText = Svc.Data.GetExcelSheet<Addon>()?.GetRow(99).Text.ToString() ?? "Put Up for Sale";
        Svc.Hook.InitializeFromAttributes(this);
        Toggle();
    }

    public void Enable()
    {
        if(openInventoryContextHook?.IsEnabled == false)
        {
            openInventoryContextHook?.Enable();
            PluginLog.Information("QuickSellItems enabled");
        }
    }

    internal static bool IsReadyToUse()
    {
        if(!Svc.Condition[ConditionFlag.OccupiedSummoningBell]) return false;
        if(!Svc.Targets.Target.IsRetainerBell()) return false;
        if(!Svc.Objects.Any(x => x.ObjectKind == ObjectKind.Retainer)) return false;
        { if(TryGetAddonByName<AtkUnitBase>("RetainerSellList", out var addon) && IsAddonReady(addon)) return true; }
        { if(TryGetAddonByName<AtkUnitBase>("RetainerGrid0", out var addon) && IsAddonReady(addon)) return true; }
        { if(TryGetAddonByName<AtkUnitBase>("RetainerGrid1", out var addon) && IsAddonReady(addon)) return true; }
        { if(TryGetAddonByName<AtkUnitBase>("RetainerGrid2", out var addon) && IsAddonReady(addon)) return true; }
        { if(TryGetAddonByName<AtkUnitBase>("RetainerGrid3", out var addon) && IsAddonReady(addon)) return true; }
        { if(TryGetAddonByName<AtkUnitBase>("RetainerGrid4", out var addon) && IsAddonReady(addon)) return true; }
        { if(TryGetAddonByName<AtkUnitBase>("RetainerCrystalGrid", out var addon) && IsAddonReady(addon)) return true; }
        return false;
    }

    internal bool GetAction(out List<string> text)
    {
        text = [];
        // CSFramework.Instance() 是 isPointer:true 的靜態位址，會合法回 null。
        // 這支是從 hook detour 呼叫進來的，讀不到就直接回 false ＝ 不做快捷操作（fail-closed）。
        var framework = CSFramework.Instance();
        if(framework == null || framework->WindowInactive) return false;
        if(IsKeyPressed(C.SellKey))
        {
            text.Add(retainerSellText);
        }
        if(IsKeyPressed(C.RetrieveKey))
        {
            text.Add(retrieveFromRetainerText);
        }
        if(IsKeyPressed(C.EntrustKey))
        {
            text.Add(entrustToRetainerText);
        }
        if(IsKeyPressed(C.SellMarketKey))
        {
            text.Add(putUpForSaleText);
        }
        return text.Count > 0;
    }

    private void* OpenInventoryContextDetour(AgentInventoryContext* agent, InventoryType inventoryType, ushort slot, int a4, ushort a5, byte a6)
    {
        var retVal = openInventoryContextHook.OriginalDisposeSafe(agent, inventoryType, slot, a4, a5, a6);
        InternalLog.Verbose($"Inventory hook: {inventoryType}, {slot}");
        try
        {
            if(CanSellFrom.Contains(inventoryType) && IsReadyToUse() && GetAction(out var text))
            {
                var inventory = InventoryManager.Instance()->GetInventoryContainer(inventoryType);
                if(inventory != null)
                {
                    var itemSlot = inventory->GetInventorySlot(slot);
                    if(itemSlot != null)
                    {
                        var itemId = itemSlot->ItemId;
                        var item = Svc.Data.GetExcelSheet<Item>()?.GetRow(itemId);
                        if(item != null)
                        {
                            var addonId = agent->AgentInterface.GetAddonId();
                            if(addonId == 0) return retVal;
                            // 🔴 半套判空：下面那行 addon == null 護的是**回傳值**，
                            //    護不到 AtkStage.Instance()（isPointer:true，合法回 null）
                            //    與它的 RaptureAtkUnitManager 欄位（+0x20 裸指標）。
                            //    這裡在 hook detour 內，任一層 null 都是攔不到的 AVE。
                            var stage = AtkStage.Instance();
                            if(stage == null || stage->RaptureAtkUnitManager == null) return retVal;
                            var addon = stage->RaptureAtkUnitManager->GetAddonById((ushort)addonId);
                            if(addon == null) return retVal;

                            for(var i = 0; i < agent->ContextItemCount; i++)
                            {
                                var contextItemParam = agent->EventParams[agent->ContexItemStartIndex + i];
                                if(contextItemParam.Type != ValueType.String) continue;
                                var contextItemName = contextItemParam.GetValueAsString();

                                if(text.Contains(contextItemName))
                                {
                                    if(Bitmask.IsBitSet(agent->ContextItemDisabledMask, i))
                                    {
                                        DebugLog($"QRA found {i}:{contextItemName} but it's disabled");
                                        continue;
                                    }
                                    // 🔴 上一扇 ContextMenu 仍在關閉中時同一位址再被交回來就是危險窗口:同一扇只送一次,
                                    //    被擋就不自動選、讓遊戲自己的選單照常開(原函式已經跑過)。
                                    if(!DialogGuards.TryPressOnce("ContextMenu", (nint)addon, "QuickSell"))
                                    {
                                        DebugLog($"QRA skipped {i}:{contextItemName}: same ContextMenu still closing");
                                        return retVal;
                                    }
                                    // 🔴 原本這裡是 Callback.Fire(addon, true, …) 之後緊接 agent->Hide() 與
                                    //    addon->Close(true) —— 在同一個呼叫堆疊裡連續碰同一扇窗三次。
                                    //    ECommons 的 Callback.Fire 第二個參數 updateState 就是原生
                                    //    AtkUnitBase::FireCallback 的 close(Callback.cs:143 的
                                    //    FireRaw(…, (byte)(updateState ? 1 : 0)) → :87 FireCallback(Base, valueCount, values, updateState)),
                                    //    而 close 為真、且處理常式回非零時,原生端會在回到這裡之前就對這扇窗跑完
                                    //    vf6 Hide 或 vf4 Close(台服 7.20 的 0x1406422B0,自 0x1406423B4 起;Hide 與 Close 二選一)。
                                    //    處理常式是執行期綁上去的 agent(這裡是 AgentInventoryContext),不是 addon 自己的 vtable,
                                    //    所以看 addon 判斷不出來。⇒ 那兩發是打在已經關掉的窗與已經收掉的 agent 上;
                                    //    遊戲自己從不這樣做(ContextMenu 的三處關窗全部只送 close=true 的 callback、
                                    //    從不自己呼叫 Close),agent 對第二發關窗事件的健壯性因此沒有任何保證。
                                    //
                                    // 🔑 修法刻意不需要先證明「台服這個選單項到底會不會讓原生端關窗」—— 直接問原生端:
                                    //    FireCallback 的回傳值語意是「我有沒有替你把窗關掉」(台服 0x140642410 的 mov sil,1
                                    //    只出現在關窗區塊內,close:false 走 0x140642415 的 xor sil,sil),於是
                                    //      ・回 true ⇒ 窗已經被原生端關掉 ⇒ 這一輪什麼都不再碰(要擋的曝險正是這一支);
                                    //      ・回 false ⇒ 原生端沒關 ⇒ 這扇窗沒被任何原生關窗程式碼碰過,補上原本那兩發,
                                    //        既有行為逐字保留(選單照樣會被收掉)。
                                    //    ⚠️ 就算這個回傳值語意判斷有誤也不會比現在差:誤判成 true 只是選單多留在畫面上
                                    //    (使用者按 Esc 即可,不會崩);誤判成 false 就退回今天既有的行為。
                                    //
                                    // 🔴 這裡刻意不用「下一輪重新解位址再關」那種形狀(Bank 那條路徑用的是它):
                                    //    ①這裡在 hook detour 內,結構上沒有下一輪;②Bank 那邊的 FireCallback 用的是
                                    //    close 的預設值 false,原生端保證不關窗,所以那個 Close(true) 是必要的;
                                    //    這裡 close 是 true,關窗本來就是原生端的責任。③跨幀之後 addon 可能已經被
                                    //    AtkUnitManager::Update 的 AddonFinalize 釋放(台服全 exe 唯一的釋放點 0x140650190,
                                    //    常態路徑是每幀 vf5 Update),要跨幀就得重新解位址,反而比同一堆疊內判回傳值更弱。
                                    //
                                    // 值的型別、數量與順序與原本的 Callback.Fire(addon, true, 0, i, 0U, 0, 0) 逐格相同
                                    // (Int 0 / Int i / UInt 0 / Int 0 / Int 0);換成 CS 的 FireCallback 純粹是為了拿到回傳值,
                                    // 這個外掛別處(RetainerHandlers、RetainerListHandlers、除錯面板)本來就是這樣呼叫的。
                                    var values = stackalloc AtkValue[]
                                    {
                                        new() { Type = ValueType.Int, Int = 0 },
                                        new() { Type = ValueType.Int, Int = i },
                                        new() { Type = ValueType.UInt, UInt = 0 },
                                        new() { Type = ValueType.Int, Int = 0 },
                                        new() { Type = ValueType.Int, Int = 0 },
                                    };
                                    var closedByGame = addon->FireCallback(5, values, true);
                                    if(!closedByGame)
                                    {
                                        agent->AgentInterface.Hide();
                                        addon->Close(true);
                                    }
                                    DebugLog($"QRA Selected {i}:{contextItemName}");
                                    // 使用者跑 LogLevel 2(Debug 收不到)。這一行同時是「原生端到底替不替我們關窗」
                                    // 這個問題的實測資料點,而且只有在按著快捷鍵右鍵點道具時才各出現一次,不會洗版。
                                    PluginLog.Information($"QuickSellItems:已送出「{contextItemName}」的選單 callback,原生端{(closedByGame ? "已" : "未")}替我們關閉選單");
                                    return retVal;
                                }
                            }
                        }
                    }
                }
            }
        }
        catch(Exception ex)
        {
            ex.Log();
        }

        return retVal;
    }

    public void Disable()
    {
        if(openInventoryContextHook?.IsEnabled == true)
        {
            openInventoryContextHook?.Disable();
            PluginLog.Information("QuickSellItems disabled");
        }
    }

    public void Toggle()
    {
        if(C.SellKey == LimitedKeys.None && C.RetrieveKey == LimitedKeys.None && C.EntrustKey == LimitedKeys.None && C.SellMarketKey == LimitedKeys.None)
        {
            Disable();
        }
        else
        {
            Enable();
        }
    }

    public void Dispose()
    {
        openInventoryContextHook?.Dispose();
    }
}
