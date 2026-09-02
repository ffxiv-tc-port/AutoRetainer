using AutoRetainer.Internal.InventoryManagement;
using AutoRetainer.Scheduler.Tasks;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace AutoRetainer.Scheduler.Handlers;

internal static unsafe class RetainerListHandlers
{
    internal static bool? SelectRetainerByName(string name)
    {
        TaskWithdrawGil.forceCheck = false;
        InventorySpaceManager.SellSlotTasks.Clear();
        if(name.IsNullOrEmpty())
        {
            throw new Exception($"Name can not be null or empty");
        }
        if(TryGetAddonByName<AtkUnitBase>("RetainerList", out var retainerList) && IsAddonReady(retainerList))
        {
            var list = new AddonMaster.RetainerList(retainerList);
            foreach(var retainer in list.Retainers)
            {
                // 🔴 `list.Retainers` 不是「這個角色的雇員」而是**固定 10 格**
                // (`ReaderRetainerList.Retainers => Loop<Retainer>(3, 10, 10)`)，沒用到的格子照樣
                // 產生 Entry，名字讀到的是空字串或殘留值。ECommons 的 `Entry.Select()` 對這種格子
                // 是 `IsActive` 為 false 就直接 no-op **回 false**，而舊碼把回傳值丟掉、無條件
                // `return true` —— 於是整條任務鏈會以為雇員視窗已經開了，接著把該雇員的工作
                // （存入／賣出／提金幣）做到當下真正開著的那個視窗上。先驗 IsActive 才比名字。
                if(!retainer.IsActive)
                {
                    continue;
                }
                if(retainer.Name != name)
                {
                    continue;
                }

                // 這是「找到叫這個名字的那一個」而不是「對每個雇員都做」：雇員名在同一個角色底下
                // 唯一，命中就是終點。節流沒到也一樣結束掃描 —— 回 false 讓 TaskManager 下一幀
                // 重跑整個任務，繼續往下掃只會在同一幀對後面的格子重覆比對甚至重覆點擊。
                if(!Utils.GenericThrottle)
                {
                    return false;
                }
                // 選雇員後清單窗隱藏/關閉;同一扇對同一格 15 幀內只送一次(不同格各准一次),
                // CloseRetainerList 對同一扇按過關閉後任何格都不再送。
                if(!DialogGuards.TryPressOnce("RetainerList", (nint)retainerList, "SelectRetainerByName", $"Select{retainer.Index}", escapeIsRoutine: true))
                {
                    return false;
                }
                DebugLog($"Selecting retainer {retainer.Name} with index {retainer.Index}");
                // Select() 自己還會再驗一次 IsActive；沒送出就回 false 讓上層重試，
                // 不要再宣稱成功。
                return retainer.Select();
            }
        }

        return false;
    }

    internal static bool? CloseRetainerList()
    {
        if(TryGetAddonByName<AtkUnitBase>("RetainerList", out var retainerList) && IsAddonReady(retainerList))
        {
            // 🔴 關窗即關;同一扇只送一次。被擋就不設 IsCloseActionAutomatic(那旗標只該跟著真的送出走)。
            if(Utils.GenericThrottle && DialogGuards.TryPressOnce("RetainerList", (nint)retainerList, "CloseRetainerList"))
            {
                var v = stackalloc AtkValue[1]
                {
                    new()
                    {
                        Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int,
                        Int = -1
                    }
                };
                P.IsCloseActionAutomatic = true;
                retainerList->FireCallback(1, v);
                DebugLog($"Closing retainer window");
                return true;
            }
        }
        return false;
    }
}
