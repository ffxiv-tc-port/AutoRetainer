using AutoRetainer.Scheduler.Tasks;
using ECommons.ExcelServices;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace AutoRetainer.Internal.InventoryManagement;

public static unsafe class InventorySpaceManager
{
    public static readonly List<string> Log = [];
    public static readonly string[] Addons = ["InventoryRetainer", "InventoryRetainerLarge"];

    // 🔴 AgentModule.Instance() 是 CS 裡的**手寫**包裝（`uiModule == null ? null : uiModule->GetAgentModule()`），
    //    不是產生器的 [StaticAddress] —— 它會合法回 null（登入前 / 切角色期間）。
    //    GetAgentByInternalId() 也可能回 null。裸解參考 null 原生指標是 AVE，
    //    在 .NET Core 屬 corrupted-state exception，try/catch 攔不到，只能事前擋。
    //    ⚠️ 這個值會被原封不動當成 this 指標傳給原生的 RetainerItemCommand
    //       —— 不擋的話傳出去的是 0+40 = 0x28，等於叫遊戲對位址 0x28 動雇員背包。
    //    取不到一律回 0，並由 Memory.RetainerItemCommandDetour 的入口守衛擋掉。
    public static nint AgentRetainerItemCommandModule
    {
        get
        {
            var agentModule = AgentModule.Instance();
            if(agentModule == null) return 0;
            var agent = agentModule->GetAgentByInternalId(AgentId.Retainer);
            if(agent == null) return 0;
            return (nint)agent + 40;
        }
    }

    // 取不到就當「雇員代理人沒開著」（呼叫端會走既有的 warning + 中止路徑）。
    private static bool IsAgentRetainerActive
    {
        get
        {
            var agentModule = AgentModule.Instance();
            if(agentModule == null) return false;
            var agent = agentModule->GetAgentByInternalId(AgentId.Retainer);
            return agent != null && agent->IsAgentActive();
        }
    }

    public static readonly List<SellSlotTask> SellSlotTasks = [];

    public static InventoryType[] GetAllowedToSellInventoryTypes()
    {
        return Data.GetIMSettings().AllowSellFromArmory ? [.. Utils.PlayerInvetories, .. Utils.PlayerArmory] : Utils.PlayerInvetories;
    }

    public static bool? SafeSellSlot(SellSlotTask Task)
    {
        if(Utils.GenericThrottle && EzThrottler.Throttle("SellSlot", 333))
        {
            var inv = InventoryManager.Instance()->GetInventoryContainer(Task.InventoryType);
            if(inv == null || inv->Items == null)
            {
                DuoLog.Warning($"Inventory {Task.InventoryType} is null");
                return true;
            }
            // 🔴 底下是 inv->Items[Task.Slot] 的直接索引，不是 null 解參考——超界讀到的是**其他記憶體**，
            // 不會落在 null page，也就不會變成可攔截的 NullReferenceException。Task 是先入佇列後執行的，
            // 兩者之間容器可能已經換過，所以索引一定要對當下的 Size 驗過再用。
            if(Task.Slot >= inv->Size)
            {
                DuoLog.Warning($"Slot {Task.Slot} is out of range for {Task.InventoryType} (size {inv->Size})");
                return true;
            }
            if(Data.GetIMSettings().IMProtectList.Contains(Task.ItemID))
            {
                DuoLog.Warning($"Item {Task} is protected and won't be sold.");
                return true;
            }
            var slot = inv->Items[Task.Slot];
            if(Task.ItemID != slot.ItemId || slot.ItemId == 0 || slot.Quantity != Task.Quantity)
            {
                DuoLog.Warning($"Slot contains different item {ExcelItemHelper.GetName(slot.ItemId)}x{slot.Quantity}, should be {Task}");
                return true;
            }
            if(!IsRetainerInventoryLoaded())
            {
                DuoLog.Warning($"Could not find retainer inventory");
                return true;
            }
            if(!IsAgentRetainerActive)
            {
                DuoLog.Warning($"AgentRetainer is not active");
                return true;
            }
            if(!Data.GetIMSettings().IMDry)
            {
                P.Memory.RetainerItemCommandDetour(AgentRetainerItemCommandModule, Task.Slot, Task.InventoryType, 0, RetainerItemCommand.HaveRetainerSellItem);
                PluginLog.Debug($"Sold slot {Task}");
            }
            else
            {
                DuoLog.Warning($"> IMDry > Would sell slot {Task}");
            }
            Log.Add($"[{DateTime.Now}] Sold {Task} on {Data.Name}");
            return true;
        }
        return false;
    }

    public static bool IsRetainerInventoryLoaded()
    {
        foreach(var addonCheck in Addons)
        {
            if(TryGetAddonByName<AtkUnitBase>(addonCheck, out var addon) && IsAddonReady(addon))
            {
                return true;
            }
        }
        return false;
    }

    public static bool IsSlotEnqueued(InventoryType type, uint slot)
    {
        return SellSlotTasks.Any(x => x.InventoryType == type && x.Slot == slot);
    }

    /// <remarks>
    /// 讀不到的容器一律跳過，也就是**只可能少排不可能多排**賣出工作。少排＝這件道具這次不賣，
    /// 呼叫端（統計模組收到道具時）下次還會再觸發；多排才有代價（賣掉不該賣的）。
    /// ⚠️ 解參考 null 在 .NET Core 是 corrupted-state exception，try/catch 攔不到，只能靠事前檢查。
    /// </remarks>
    public static void EnqueueSoftItemIfAllowed(uint ItemId, uint Quantity)
    {
        var im = InventoryManager.Instance();
        foreach(var invType in InventorySpaceManager.GetAllowedToSellInventoryTypes())
        {
            var inv = im->GetInventoryContainer(invType);
            if(inv == null || inv->Items == null) continue;
            for(var i = 0; i < inv->Size; i++)
            {
                var item = inv->Items[i];
                if(item.ItemId != 0 && item.ItemId == ItemId && item.Quantity == Quantity)
                {
                    if(Data.GetIMSettings().IMAutoVendorSoft.Contains(item.ItemId))
                    {
                        var task = new SellSlotTask(invType, (uint)i, item.ItemId, item.Quantity);
                        PluginLog.Information($"Enqueueing {task} for soft sale");
                        InventorySpaceManager.SellSlotTasks.Add(task);
                        return;
                    }
                }
            }
        }
    }

    /// <remarks>
    /// 讀不到的容器一律跳過，也就是**只可能少排不可能多排**賣出工作。少排＝這輪少賣幾件，
    /// 呼叫端是每次到商人前重跑的，容器一載入下輪就補回；多排才有代價（賣掉不該賣的）。
    /// ⚠️ 解參考 null 在 .NET Core 是 corrupted-state exception，try/catch 攔不到，只能靠事前檢查。
    /// </remarks>
    public static void EnqueueAllHardItems(bool softAsHard = false)
    {
        var im = InventoryManager.Instance();
        foreach(var invType in InventorySpaceManager.GetAllowedToSellInventoryTypes())
        {
            var inv = im->GetInventoryContainer(invType);
            if(inv == null || inv->Items == null) continue;
            for(var i = 0; i < inv->Size; i++)
            {
                var item = inv->Items[i];
                if(item.ItemId != 0 && (item.Quantity < Data.GetIMSettings().IMAutoVendorHardStackLimit || Data.GetIMSettings().IMAutoVendorHardIgnoreStack.Contains(item.ItemId)))
                {
                    if((Data.GetIMSettings().IMAutoVendorHard.Contains(item.ItemId) || (softAsHard && Data.GetIMSettings().IMAutoVendorSoft.Contains(item.ItemId))) && !TaskDesynthItems.DesynthEligible(item.ItemId))
                    {
                        var task = new SellSlotTask(invType, (uint)i, item.ItemId, item.Quantity);
                        PluginLog.Information($"Enqueueing {task} for hard sale");
                        InventorySpaceManager.SellSlotTasks.Add(task);
                    }
                }
            }
        }
    }
}
