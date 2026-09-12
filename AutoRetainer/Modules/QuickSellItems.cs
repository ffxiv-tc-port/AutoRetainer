using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Hooking;
using Dalamud.Utility;
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

    /// <summary>「被守衛擋下」的 <c>Information</c> 診斷最短間隔（毫秒）。</summary>
    /// <remarks>
    /// 🔑 右鍵很頻繁，每次被擋都寫一行會洗版（改動前這個情形實機一場 144 次）。改成「每 3 秒最多一行、
    /// 行內附上這段期間累計被擋幾次」：踩到的使用者一定看得到，而且看得到規模，log 又不會爆。
    /// 🔴 刻意<b>不</b>用 <c>EzThrottler</c>：它是全外掛共用的靜態 <c>Dictionary</c> 且零同步，
    /// 而這裡是在原生 hook 的 detour 裡執行。<c>Environment.TickCount64</c> 只讀一個單調時鐘，沒有共用狀態。
    /// 🔴 也刻意不用 <c>DuoLog</c>：它在<b>每一個等級</b>都會無條件 <c>Svc.Chat.Print</c> 到聊天視窗。
    /// </remarks>
    private const long SkipLogIntervalMs = 3000;
    private long lastSkipLogAt;
    private int skippedSinceLog;
    private long lastNotReadyLogAt;
    private int notReadySinceLog;

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
                                // 🔴 GetValueAsString() 對 ValueType.String 走的是 CStringPointer.ToString()：完全不剝 SeString payload。
                                // 兩端基準不同、text.Contains(...) 恆假,這個快捷功能靜默失效(不報錯、不寫 log)。
                                // 改用 Dalamud 的 CStringPointer.ExtractText(),與另一端走同一支 Lumina 解析器。
                                var contextItemName = contextItemParam.String.ExtractText();

                                if(text.Contains(contextItemName))
                                {
                                    if(Bitmask.IsBitSet(agent->ContextItemDisabledMask, i))
                                    {
                                        DebugLog($"QRA found {i}:{contextItemName} but it's disabled");
                                        continue;
                                    }
                                    // 🔴 送出前的就地就緒檢查。
                                    // 🔑 補上這一關之後,安全性不再取決於任何幀數：這一關的放行條件是「**可見**」(IsAddonReady 三關之一);
                                    // ⚠️ 這一關本身帶一個新假設：假設不成立的後果**不是崩潰**,是這個快捷功能整個停止動作。
                                    if(!IsAddonReady(addon))
                                    {
                                        notReadySinceLog++;
                                        var readyNow = Environment.TickCount64;
                                        if(readyNow - lastNotReadyLogAt >= SkipLogIntervalMs)
                                        {
                                            lastNotReadyLogAt = readyNow;
                                            PluginLog.Information($"QuickSellItems:道具選單這一刻還沒就緒(IsAddonReady 為否),沒有自動選「{contextItemName}」。近期累計 {notReadySinceLog} 次。");
                                            notReadySinceLog = 0;
                                        }
                                        return retVal;
                                    }
                                    // 🔴 上一扇 ContextMenu 仍在關閉中時同一位址再被交回來就是危險窗口:同一扇只送一次,
                                    //    被擋就不自動選、讓遊戲自己的選單照常開(原函式已經跑過)。
                                    if(!DialogGuards.TryPressOnce("ContextMenu", (nint)addon, "QuickSell"))
                                    {
                                        DebugLog($"QRA skipped {i}:{contextItemName}: same ContextMenu still closing");
                                        // 使用者踩到這條時的現象是「按了沒反應」：原函式已經跑完，遊戲自己的右鍵選單
                                        // 就留在畫面上（上面正好是「到市場出售／委託僱員出售物品」兩項），很容易被讀成
                                        // 「外掛選錯項目」。改動前這裡只寫 Debug，使用者完全看不到提示。
                                        // ⇒ 升到 Information（使用者的 LogLevel 是 1），但每 3 秒最多一行、附累計次數。
                                        skippedSinceLog++;
                                        var now = Environment.TickCount64;
                                        if(now - lastSkipLogAt >= SkipLogIntervalMs)
                                        {
                                            lastSkipLogAt = now;
                                            // FramesSincePress 只做位址等值比較、不解參；-1 代表記號剛好在這一瞬間被解除。
                                            var since = DialogGuards.FramesSincePress("ContextMenu", (nint)addon);
                                            PluginLog.Information($"QuickSellItems:上一扇道具選單還沒收乾淨(距上次送出 {since} 幀),這次右鍵沒有自動選「{contextItemName}」,遊戲自己的選單會留在畫面上。近期累計 {skippedSinceLog} 次;等選單關掉再按即可。");
                                            skippedSinceLog = 0;
                                        }
                                        return retVal;
                                    }
                                    // 🔑 直接問原生端: FireCallback 的回傳值語意是「我有沒有替你把窗關掉」。
                                    // 回 true ⇒ 窗已經被原生端關掉 ⇒ 這一輪什麼都不再碰(要擋的曝險正是這一支); 回 false ⇒ 原生端沒關 ⇒ 這扇窗沒被任何原生關窗程式碼碰過,補上原本那兩發,既有行為逐字保留(選單照樣會被收掉)。
                                    // 🔴 這裡刻意不用「下一輪重新解位址再關」那種形狀(Bank 那條路徑用的是它)。
                                    // 值的型別、數量與順序與原本的 Callback.Fire(addon, true, 0, i, 0U, 0, 0) 逐格相同 換成 CS 的 FireCallback 純粹是為了拿到回傳值。
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
                                    // 使用者跑 LogLevel 1(Debug 收得到但單檔數十萬行會淹沒)。這一行同時是「原生端到底替不替我們關窗」
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
