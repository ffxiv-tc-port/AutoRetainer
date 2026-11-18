using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
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
    // 🔴 上游的短特徵碼 "83 B9 ?? ?? ?? ?? ?? 7E 11" 在台服不唯一(另一個命中是某函式的中段),而 [Signature] 取位址最低者 ——
    //    排序一變就會靜默把 hook 掛到中段位址上,detour 會把別的函式的引數當成 AgentInventoryContext* 解參考(AVE 攔不到)。
    //    所以延長到鎖進 OpenInventoryContext 專有的引數驗證序列(全部是結構位移常數,不含 rip 相對位移);
    //    jle/jne/je 的 rel8 位移用 ?? 遮罩。特徵碼失配時 Fallible 讓 hook 留 null=功能靜默停用(fail-closed)。
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
    /// 🔑 右鍵很頻繁，每次被擋都寫一行會洗版。改成「每 3 秒最多一行、
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

    /// <summary>被「選單還沒收乾淨」擋下的那一下最多再等多久補送（毫秒）。</summary>
    private const long RetryWindowMs = 1000;

    /// <summary>
    /// 被守衛擋下的那一下右鍵。只存 ID 與數值；<see cref="Addon"/> 只做等值比較、不解參。
    /// 契約：只來自使用者剛按的那一下、最多補送一次；任何一次右鍵（有沒有按快捷鍵都算）都會先把它清掉。
    /// </summary>
    private readonly record struct PendingRetry(InventoryType Inventory, ushort Slot, uint ItemId, int Quantity, string ActionText, nint Addon, long QueuedAt);
    private PendingRetry? pendingRetry;

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
        if(openInventoryContextHook == null) PluginLog.Warning("[AutoRetainer] 特徵碼對不上（openInventoryContextHook）：按住快捷鍵右鍵道具直接出售／存入／取回的功能停用。");
        Toggle();
        Svc.Framework.Update += RetryTick;
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
        if(KeyHeldAlone(C.SellKey))
        {
            text.Add(retainerSellText);
        }
        if(KeyHeldAlone(C.RetrieveKey))
        {
            text.Add(retrieveFromRetainerText);
        }
        if(KeyHeldAlone(C.EntrustKey))
        {
            text.Add(entrustToRetainerText);
        }
        if(KeyHeldAlone(C.SellMarketKey))
        {
            text.Add(putUpForSaleText);
        }
        return text.Count > 0;
    }

    private static readonly int[] ModifierGroups = [0x10, 0x11, 0x12];

    /// <summary>Shift／Ctrl／Alt（含左右鍵）歸到通用鍵碼；其他鍵回 0。</summary>
    private static int ModifierGroup(int vk) => vk switch
    {
        0x10 or 0xA0 or 0xA1 => 0x10,
        0x11 or 0xA2 or 0xA3 => 0x11,
        0x12 or 0xA4 or 0xA5 => 0x12,
        _ => 0,
    };

    // Marketbuddy 快速上架與 TCToolbox 轉移也在同一支開選單函式上、用修飾鍵觸發；兩邊同時選會打在關閉中的選單上。
    // ⇒ 自己的鍵按著、而且 Shift／Ctrl／Alt 中其他的都沒按才算（自己的鍵是修飾鍵時不算它自己）。
    private static bool KeyHeldAlone(LimitedKeys key)
    {
        if(!IsKeyPressed(key)) return false;
        var own = ModifierGroup((int)key);
        foreach(var group in ModifierGroups)
        {
            if(group != own && IsKeyPressed(group)) return false;
        }
        return true;
    }

    private void* OpenInventoryContextDetour(AgentInventoryContext* agent, InventoryType inventoryType, ushort slot, int a4, ushort a5, byte a6)
    {
        var retVal = openInventoryContextHook.OriginalDisposeSafe(agent, inventoryType, slot, a4, a5, a6);
        InternalLog.Verbose($"Inventory hook: {inventoryType}, {slot}");
        try
        {
            DropPendingRetry("又右鍵了一次");
            if(CanSellFrom.Contains(inventoryType) && IsReadyToUse() && GetAction(out var text))
            {
                // 原函式開頭的「封鎖格」檢查成立時直接返回、不重建選單，detour 照樣被呼叫 ⇒ 選單是上一次的，不選。
                if(agent->TargetInventoryId != inventoryType || agent->TargetInventorySlotId != slot)
                {
                    PluginLog.Debug($"QuickSellItems:這次右鍵原生端沒有重建選單（目標 {inventoryType}#{slot}，選單是 {agent->TargetInventoryId}#{agent->TargetInventorySlotId}），不自動選。");
                    return retVal;
                }
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
                                // 兩端基準不同的話 text.Contains(...) 恆假,這個快捷功能靜默失效(不報錯、不寫 log),
                                // 所以用 Dalamud 的 CStringPointer.ExtractText(),與另一端走同一支 Lumina 解析器。
                                var contextItemName = contextItemParam.String.ExtractText();

                                if(text.Contains(contextItemName))
                                {
                                    if(Bitmask.IsBitSet(agent->ContextItemDisabledMask, i))
                                    {
                                        DebugLog($"QRA found {i}:{contextItemName} but it's disabled");
                                        continue;
                                    }
                                    // 🔴 送出前的就地就緒檢查:放行條件是「**可見**」(IsAddonReady 三關之一),與 DialogGuards 常駐窗的解除條件(連續不可見)
                                    //    是反面 ⇒ 記號就算在拆除途中被解除,這一發也會停在這裡,後果從攔不到的 AVE 降級成「這一次沒送出,再右鍵一次」。
                                    // ⚠️ 這一關的假設:detour 在原函式跑完之後執行,此時 ContextMenu 已經可見。不成立的後果**不是崩潰**,是這個快捷功能停止動作(下面那行 Information 會自證)。
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
                                        // 不丟棄：記下這一格，等守衛放行後由 RetryTick 補送一次（送前重驗）。
                                        pendingRetry = new(inventoryType, slot, itemId, itemSlot->Quantity, contextItemName, (nint)addon, Environment.TickCount64);
                                        skippedSinceLog++;
                                        var now = Environment.TickCount64;
                                        if(now - lastSkipLogAt >= SkipLogIntervalMs)
                                        {
                                            lastSkipLogAt = now;
                                            // FramesSincePress 只做位址等值比較、不解參；-1 代表記號剛好在這一瞬間被解除。
                                            var since = DialogGuards.FramesSincePress("ContextMenu", (nint)addon);
                                            PluginLog.Information($"QuickSellItems:上一扇道具選單還沒收乾淨(距上次送出 {since} 幀),「{contextItemName}」先排隊,選單可以再送時補送(最多等 {RetryWindowMs} 毫秒)。近期累計 {skippedSinceLog} 次。");
                                            skippedSinceLog = 0;
                                        }
                                        return retVal;
                                    }
                                    var closedByGame = SelectContextItem(agent, addon, i);
                                    DebugLog($"QRA Selected {i}:{contextItemName}");
                                    // 「已」是常態，只寫 Debug；「未」＝走上面補關窗的後備路徑，罕見而值得看見，留 Information。
                                    if(closedByGame)
                                        PluginLog.Debug($"QuickSellItems:已送出「{contextItemName}」的選單 callback,原生端已替我們關閉選單");
                                    else
                                        PluginLog.Information($"QuickSellItems:已送出「{contextItemName}」的選單 callback,原生端未替我們關閉選單");
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

    /// <summary>送出道具選單第 <paramref name="index"/> 項；回傳原生端有沒有替我們關掉選單。呼叫前必須已過 <see cref="DialogGuards.TryPressOnce"/>。</summary>
    private static bool SelectContextItem(AgentInventoryContext* agent, AtkUnitBase* addon, int index)
    {
        // 🔑 FireCallback 的回傳值語意是「我有沒有替你把窗關掉」：回 false 才補上 Hide＋Close。
        // 🔴 刻意不用「下一輪重新解位址再關」那種形狀：hook detour 內沒有下一輪。
        // 值的型別、數量與順序與 Callback.Fire(addon, true, 0, i, 0U, 0, 0) 逐格相同；用 CS 的 FireCallback 純粹是為了拿到回傳值。
        var values = stackalloc AtkValue[]
        {
            new() { Type = ValueType.Int, Int = 0 },
            new() { Type = ValueType.Int, Int = index },
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
        return closedByGame;
    }

    private void DropPendingRetry(string reason)
    {
        if(pendingRetry is not { } p) return;
        pendingRetry = null;
        PluginLog.Information($"QuickSellItems:放棄補送「{p.ActionText}」({reason}),這次右鍵沒有自動選,遊戲自己的選單會留在畫面上;等選單關掉再按即可。");
    }

    /// <summary>
    /// 每幀：有排隊中的那一下就重驗並在守衛放行時補送。沒有排隊時只是一個判空。
    /// 「等一下」與「放棄」分開：選單暫時沒就緒／守衛還沒放行＝等；目標格、道具、數量、選單或選項對不上＝當場放棄。
    /// </summary>
    private void RetryTick(IFramework _)
    {
        if(pendingRetry is not { } p) return;
        try
        {
            var elapsed = Environment.TickCount64 - p.QueuedAt;
            if(elapsed > RetryWindowMs)
            {
                DropPendingRetry($"等了 {elapsed} 毫秒選單仍不能送");
                return;
            }
            if(!IsReadyToUse()) return;
            var agent = AgentInventoryContext.Instance();
            if(agent == null) return;
            if(agent->TargetInventoryId != p.Inventory || agent->TargetInventorySlotId != p.Slot)
            {
                DropPendingRetry($"選單已換成 {agent->TargetInventoryId}#{agent->TargetInventorySlotId}");
                return;
            }
            var im = InventoryManager.Instance();
            var container = im == null ? null : im->GetInventoryContainer(p.Inventory);
            var itemSlot = container == null ? null : container->GetInventorySlot(p.Slot);
            if(itemSlot == null || itemSlot->ItemId != p.ItemId || itemSlot->Quantity != p.Quantity)
            {
                DropPendingRetry("那一格的道具或數量變了");
                return;
            }
            var addonId = agent->AgentInterface.GetAddonId();
            if(addonId == 0) return;
            var stage = AtkStage.Instance();
            if(stage == null || stage->RaptureAtkUnitManager == null) return;
            var addon = stage->RaptureAtkUnitManager->GetAddonById((ushort)addonId);
            if(addon == null) return;
            if((nint)addon != p.Addon)
            {
                DropPendingRetry("道具選單換了一扇");
                return;
            }
            if(!IsAddonReady(addon)) return;
            var index = -1;
            for(var i = 0; i < agent->ContextItemCount; i++)
            {
                var param = agent->EventParams[agent->ContexItemStartIndex + i];
                if(param.Type != ValueType.String) continue;
                if(param.String.ExtractText() != p.ActionText) continue;
                if(Bitmask.IsBitSet(agent->ContextItemDisabledMask, i))
                {
                    DropPendingRetry("該選項現在是停用的");
                    return;
                }
                index = i;
                break;
            }
            if(index < 0)
            {
                DropPendingRetry("選單上已經沒有這一項");
                return;
            }
            if(!DialogGuards.TryPressOnce("ContextMenu", (nint)addon, "QuickSell")) return;
            pendingRetry = null;
            var closedByGame = SelectContextItem(agent, addon, index);
            PluginLog.Information($"QuickSellItems:補送「{p.ActionText}」成功(排隊後 {elapsed} 毫秒;原生端{(closedByGame ? "已" : "未")}替我們關閉選單)");
        }
        catch(Exception ex)
        {
            pendingRetry = null;
            ex.Log();
        }
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
        Svc.Framework.Update -= RetryTick;
        pendingRetry = null;
        openInventoryContextHook?.Dispose();
    }
}
