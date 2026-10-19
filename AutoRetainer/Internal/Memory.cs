using AutoRetainer.Internal.InventoryManagement;
using Dalamud.Hooking;
using Dalamud.Utility.Signatures;
using ECommons.ExcelServices;
using ECommons.EzHookManager;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace AutoRetainer.Internal;

// 本檔契約：每一條特徵碼都可能在遊戲改版後對不上。對不上時對應欄位留 null、建構子不擲，
// 少掉的功能由 LogUnresolved 各印一行 Warning；每個讀取點都要接得住 null。
internal unsafe class Memory : IDisposable
{
    internal int LastSearchItem = -1;

    private delegate ulong InteractWithObjectDelegate(TargetSystem* system, GameObject* obj, bool los);

    private Hook<InteractWithObjectDelegate> InteractWithObjectHook;

    private delegate byte GetIsGatheringItemGatheredDelegate(ushort item);
    [Signature("48 89 5C 24 ?? 57 48 83 EC 20 8B D9 8B F9", Fallibility = Fallibility.Fallible)]
    private GetIsGatheringItemGatheredDelegate GetIsGatheringItemGathered;

    internal delegate nint OnReceiveMarketPricePacketDelegate(nint a1, nint data);
    [Signature("48 89 5C 24 ?? 57 48 83 EC 20 48 8B 0D ?? ?? ?? ?? 48 8B DA E8", DetourName = nameof(AddonItemSearchResult_OnRequestedUpdateDelegateDetour), Fallibility = Fallibility.Fallible)]
    internal Hook<OnReceiveMarketPricePacketDelegate> OnReceiveMarketPricePacketHook;

    internal delegate byte OutdoorTerritory_IsEstateResidentDelegate(nint a1, byte a2);
    [Signature("8B 05 ?? ?? ?? ?? 44 0F B6 D2 44 8B 81", Fallibility = Fallibility.Fallible)]
    internal OutdoorTerritory_IsEstateResidentDelegate OutdoorTerritory_IsEstateResident;

    internal delegate void RetainerItemCommandDelegate(nint AgentRetainerItemCommandModule, uint slot, InventoryType inventoryType, uint a4, RetainerItemCommand command);
    internal EzHook<RetainerItemCommandDelegate> RetainerItemCommandHook;

    // 一律經 TryGetMyAccountId 讀：這個位址與它指到的值都可能是 0，直接解參是攔不到的 AVE。
    public nint* MyAccountData;

    public delegate nint AddonGrandCompanySupplyList_SetExchangeModeDelegate(nint addon, int mode);
    public AddonGrandCompanySupplyList_SetExchangeModeDelegate AddonGrandCompanySupplyList_SetExchangeMode;

    /// <summary>false ＝ 不知道目前帳號；此時 <paramref name="id"/> 不代表任何帳號，呼叫端不可拿去比對或記錄。</summary>
    internal bool TryGetMyAccountId(out ulong id)
    {
        id = 0;
        if(MyAccountData == null) return false;
        var data = *MyAccountData;
        if(data == 0) return false;
        id = *(ulong*)(data + 8);
        return true;
    }

    /// <summary>false ＝ 查不到採集紀錄：「採過沒」是不知道，不是沒採過。</summary>
    internal bool CanQueryGatheredItems => GetIsGatheringItemGathered != null;

    internal bool IsGatheringItemGathered(uint item)
    {
        return GetIsGatheringItemGathered != null && GetIsGatheringItemGathered((ushort)item) != 0;
    }

    /// <summary>false ＝ 收不到「僱員探險清單更新」：<c>P.ListUpdateFrame</c> 不會前進，指派探險的等待必定逾時。</summary>
    internal bool VentureListUpdateTracked => ReceiveRetainerVentureListUpdateHook != null;

    internal bool CanSellToShop => SellItemHook != null;

    internal Memory()
    {
        if(Svc.SigScanner.TryGetStaticAddressFromSig("48 8B 3D ?? ?? ?? ?? 48 85 FF 74 69", out var accountData)) MyAccountData = (nint*)accountData;
        if(Svc.SigScanner.TryScanText("E8 ?? ?? ?? ?? E9 ?? ?? ?? ?? 8B D6 48 8D 4D F7", out var setExchangeMode)) AddonGrandCompanySupplyList_SetExchangeMode = EzDelegate.Get<AddonGrandCompanySupplyList_SetExchangeModeDelegate>(setExchangeMode);
        Svc.Hook.InitializeFromAttributes(this);
        EzSignatureHelper.Initialize(this);
        if(C.MarketCooldownOverlay) OnReceiveMarketPricePacketHook?.Enable();
        ReceiveRetainerVentureListUpdateHook?.Enable();
        if(Svc.SigScanner.TryScanText(NativeLayout.RetainerItemCommandSignature, out var retainerItemCommand)) RetainerItemCommandHook = new(retainerItemCommand, RetainerItemCommandDetour, false);
        LogUnresolved();
    }

    // 只在載入時印一次：每行＝一條沒對上的特徵碼，與因此停掉的功能。
    private void LogUnresolved()
    {
        static void Warn(bool resolved, string name, string lost)
        {
            if(!resolved) PluginLog.Warning($"[AutoRetainer] 特徵碼對不上（{name}）：{lost}");
        }
        Warn(ReceiveRetainerVentureListUpdateHook != null, nameof(ReceiveRetainerVentureListUpdateHook), "收不到「僱員探險清單更新」，僱員排程（收取／指派探險，以及同一趟的存入、提領）整個停用；多角色模式只處理潛水艇與飛空艇，Artisan 整合與鈴前自動開啟也不接手。");
        Warn(RetainerItemCommandHook != null, nameof(RetainerItemCommandHook), "存入僱員、從僱員取回、委託僱員出售的指令送不出去，這三項每次都會略過。");
        Warn(SellItemHook != null, nameof(SellItemHook), "自動賣給 NPC 商店停用。");
        Warn(GetIsGatheringItemGathered != null, nameof(GetIsGatheringItemGathered), "查不到採集紀錄，探險瀏覽器與探險計畫的「是否採集過」沿用上次存下的記錄。");
        Warn(OnReceiveMarketPricePacketHook != null, nameof(OnReceiveMarketPricePacketHook), "市場查價冷卻的提示不會出現。");
        Warn(MyAccountData != null, nameof(MyAccountData), "讀不到目前帳號（目前沒有功能使用）。");
        Warn(OutdoorTerritory_IsEstateResident != null, nameof(OutdoorTerritory_IsEstateResident), "只影響除錯頁的房屋住戶顯示。");
        Warn(AddonGrandCompanySupplyList_SetExchangeMode != null, nameof(AddonGrandCompanySupplyList_SetExchangeMode), "目前沒有功能使用。");
    }

    internal void RetainerItemCommandDetour(nint AgentRetainerItemCommandModule, uint slot, InventoryType inventoryType, uint a4, RetainerItemCommand command)
    {
        try
        {
            PluginLog.Debug($"RetainerItemCommandDetour: {AgentRetainerItemCommandModule:X16}, slot={slot}, type={inventoryType}, a4={a4}, command={command}");
        }
        catch(Exception e)
        {
            e.Log();
        }
        // 🔴 這支同時是 hook 的 detour（遊戲呼叫）與 AutoRetainer 自己合成呼叫的入口。
        //    遊戲那條路永遠帶合法的 this 指標；會是假位址的只有我們自己合成的那條
        //    —— 取不到代理人時回 0；函式對不上時偏移不推導、也回 0，所以先判函式，log 才說得出真正原因。
        //    把 0 / 明顯無效的低位址交給原生函式，等於叫遊戲對該位址動僱員背包，
        //    後果是攔不到的 AVE 而不是例外。這裡是所有合成呼叫端的共同咽喉，擋一處即可。
        if(RetainerItemCommandHook == null)
        {
            PluginLog.Information($"RetainerItemCommandDetour: 指令函式的特徵碼對不上，略過 {command}（slot={slot}, type={inventoryType}）");
            return;
        }
        if(AgentRetainerItemCommandModule < 0x10000)
        {
            PluginLog.Information($"RetainerItemCommandDetour: 代理人位址無效（{AgentRetainerItemCommandModule:X16}），略過 {command}（slot={slot}, type={inventoryType}）");
            return;
        }
        RetainerItemCommandHook.Original(AgentRetainerItemCommandModule, slot, inventoryType, a4, command);
    }

    private delegate nint ReceiveRetainerVentureListUpdateDelegate(nint a1, int a2, nint a3);
    [Signature("40 53 41 55 41 56 41 57 48 83 EC 28 8B DA", DetourName = nameof(ReceiveRetainerVentureListUpdateDetour), Fallibility = Fallibility.Fallible)]
    private Hook<ReceiveRetainerVentureListUpdateDelegate> ReceiveRetainerVentureListUpdateHook;

    private nint ReceiveRetainerVentureListUpdateDetour(nint a1, int a2, nint a3)
    {
        var ret = ReceiveRetainerVentureListUpdateHook.OriginalDisposeSafe(a1, a2, a3);
        PluginLog.Debug($"{a1:X16}, {a2:X8}, {a3:X16}");
        // 🔴 這裡是 hook detour 內。CSFramework.Instance() 是 isPointer:true 的靜態位址，
        //    合法回 null；在 detour 裡對 null 解參考是攔不到的 AVE（corrupted-state exception）。
        //    讀不到就不更新這個幀號 —— 消費端 RetainerHandlers.WaitForVentureListUpdate
        //    會繼續回「還沒等到」並重試，不會謊報清單已更新。
        var framework = CSFramework.Instance();
        if(framework != null) P.ListUpdateFrame = framework->FrameCounter;
        return ret;
    }

    private nint AddonItemSearchResult_OnRequestedUpdateDelegateDetour(nint a1, nint data)
    {
        var ret = OnReceiveMarketPricePacketHook.OriginalDisposeSafe(a1, data);
        P.MarketCooldownOverlay.UnlockAt = Environment.TickCount64 + 2000;
        return ret;
    }

    public void Dispose()
    {
        InteractWithObjectHook?.Dispose();
        OnReceiveMarketPricePacketHook?.Dispose();
        ReceiveRetainerVentureListUpdateHook?.Dispose();
    }

    /// <returns><see langword="true"/> ＝ 真的送出了選取;<see langword="false"/> ＝ 窗不在/未就緒,或這扇窗對這個點位剛送過(守衛擋下)。</returns>
    internal bool SelectRoutePointUnsafe(int which)
    {
        if(TryGetAddonByName<AtkUnitBase>("AirShipExploration", out var addon) && IsAddonReady(addon))
        {
            // 航線面板選點期間不關窗、多點選取是刻意的:帶參數組(哪個點),同一扇同一點 15 幀內只送一次。
            //    直送 ReceiveEvent 比 callback 更早踩到關閉中的窗,所以也要過守衛。
            if(!DialogGuards.TryPressOnce("AirShipExploration", (nint)addon, "SelectRoutePoint", $"Point{which}", escapeIsRoutine: true)) return false;
            // 遊戲處理點選只讀：SelectedIndex、MouseButtonId、ListItemRenderer→OwnerNode 的 Enabled 旗標。
            // 三層都用 CS 型別造（stackalloc 歸零）：偏移由執行期載入的 CS 決定，版面換代時跟著 CS 走。
            var dummyEvent = stackalloc AtkEvent[] { new() };
            var ownerNode = stackalloc AtkComponentNode[1];
            ownerNode->AtkResNode.Priority = ushort.MaxValue;
            ownerNode->AtkResNode.NodeFlags = (NodeFlags)0x0FFF;
            var renderer = stackalloc AtkComponentListItemRenderer[1];
            renderer->OwnerNode = ownerNode;
            var eventData = stackalloc AtkEventData[1];
            eventData->ListItemData.ListItemRenderer = renderer;
            eventData->ListItemData.SelectedIndex = which;
            eventData->ListItemData.MouseButtonId = 0;
            addon->ReceiveEvent(AtkEventType.ListItemClick, 0, dummyEvent, eventData);
            return true;
        }
        return false;
    }

    private delegate void SellItemDelegate(uint a1, InventoryType a2, uint a3);
    [EzHook("48 89 6C 24 ?? 48 89 74 24 ?? 57 48 83 EC 20 8B F2 8B E9", false)]
    private EzHook<SellItemDelegate> SellItemHook;

    private void SellItemDetour(uint inventorySlot, InventoryType a2, uint a3)
    {
        PluginLog.Debug($"SellItemDetour: {inventorySlot}, {a2}, {a3}");
        SellItemHook.Original(inventorySlot, a2, a3);
    }

    /// <remarks>
    /// 讀不到容器／格位時丟例外，**不是**靜默 return —— 本方法唯一的作用就是送出賣出指令，
    /// 而「讀不到」代表無法確認要賣的是什麼。靜默跳過會讓呼叫端以為賣掉了，
    /// 丟例外則與同方法既有的「找不到 Shop」失敗路徑一致（兩個呼叫端都在 task／UI 裡，接得住）。
    /// ⚠️ 這裡不能省掉容器檢查而依賴呼叫端：<c>NpcSaleManager</c> 雖然自己檢查過，
    /// 但那是**另一次讀取**，本方法重新解析一次容器，狀態可能已經變了。
    /// </remarks>
    public void SellItemToShop(InventoryType type, int slot)
    {
        if(SellItemHook == null) throw new InvalidOperationException("Sell-to-shop function was not found in this game version.");
        if(TryGetAddonByName<AtkUnitBase>("Shop", out var addon) && IsAddonReady(addon))
        {
            var container = InventoryManager.Instance()->GetInventoryContainer(type);
            if(container == null) throw new InvalidOperationException($"Inventory container {type} is not loaded.");
            // GetInventorySlot 是虛擬函式（進遊戲原生碼），對超界索引的行為未經證實，所以先自己夾。
            if(slot < 0 || slot >= container->Size) throw new ArgumentOutOfRangeException(nameof(slot), $"Slot {slot} is out of range for {type} (size {container->Size}).");
            var slotPtr = container->GetInventorySlot(slot);
            if(slotPtr == null) throw new InvalidOperationException($"Inventory slot {type}({slot}) could not be read.");
            if(slotPtr->ItemId != 0)
            {
                if(Data.GetIMSettings().IMProtectList.Contains(slotPtr->ItemId)) throw new InvalidOperationException($"Attempted to sell protected item: {ExcelItemHelper.GetName(slotPtr->ItemId)}");
                SellItemDetour((uint)slot, type, 0);
            }
            else
            {
                PluginLog.Warning($"Requested inventory slot {type}({slot}) had no item in it to sell.");
            }
        }
        else
        {
            throw new InvalidOperationException("Could not find Shop.");
        }
    }
}
