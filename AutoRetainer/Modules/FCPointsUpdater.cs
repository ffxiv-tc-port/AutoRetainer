using ECommons.Automation;
using ECommons.Automation.NeoTaskManager;
using ECommons.Events;
using ECommons.EzEventManager;
using ECommons.GameHelpers;
using ECommons.UIHelpers;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace AutoRetainer.Modules;
public sealed unsafe class FCPointsUpdater
{
    private readonly TaskManager TaskManager = new(new(timeLimitMS: 15000, abortOnTimeout: true, showDebug: false));
    private int OldFCPoints;

    private FCPointsUpdater()
    {
        ProperOnLogin.RegisterInteractable(() => ScheduleUpdateIfNeeded(), true);
        new EzLogout(() => TaskManager.Abort());
        new EzTerritoryChanged((x) => ScheduleUpdateIfNeeded());
    }

    public bool IsFCChestReady()
    {
        if(TryGetAddonByName<AtkUnitBase>("FreeCompanyChest", out var addon) && IsAddonReady(addon))
        {
            var reader = new ReaderFreeCompanyChest(addon);
            return reader.Ready;
        }
        return false;
    }

    public class ReaderFreeCompanyChest(AtkUnitBase* UnitBase, int BeginOffset = 0) : AtkReader(UnitBase, BeginOffset)
    {
        public bool Ready => ReadUInt(10) == 0;
    }

    public void ScheduleUpdateIfNeeded(bool force = false)
    {
        if(!Player.Available) return;
        if(!C.UpdateStaleFCData) return;
        if(!Player.IsInHomeWorld) return;
        if(Data != null && Data.FCID != 0 && C.FCData.TryGetValue(Data.FCID, out var fcdata))
        {
            if(force || DateTimeOffset.Now.ToUnixTimeMilliseconds() > fcdata.FCPointsLastUpdate + 30 * 60 * 60 * 1000)
            {
                OldFCPoints = Utils.FCPoints;
                TaskManager.Abort();
                TaskManager.Enqueue(() => !IsOccupied());
                TaskManager.Enqueue(() => IsScreenReady() && Player.Interactable);
                TaskManager.Enqueue(() =>
                {
                    // Close(true) 對關閉中的窗再叫一次同樣未證安全:同一扇 FreeCompany 只關一次。
                    // 🔴 這一顆必須有回傳值(區塊 lambda),不能是 Action。
                    // 被守衛擋下時等於「這一步整個跳過」:那扇 FreeCompany 沒被關掉,整條鏈「/freecompanycmd 開出一扇新窗」的前提就不成立。
                    // 🔴 絕不回 null:NeoTaskManager 的 bool? 三態裡 null 是 Abort(),會清掉整條佇列。
                    if(TryGetAddonByName<AtkUnitBase>("FreeCompany", out var addon))
                    {
                        if(!DialogGuards.TryPressOnce("FreeCompany", (nint)addon, "FCPoints.CloseBefore")) return false;
                        addon->Close(true);
                        TaskManager.InsertDelay(10, true);
                        return true;
                    }
                    // 窗本來就沒開著(常態):這一步已經達成,鏈往下走。
                    // ⚠️ 與 CloseAfter 那顆的 false 刻意相反 —— 那顆要等 /freecompanycmd 把窗開出來,這顆是「有開著才關」。
                    return true;
                }, "FC 點數:先關掉已經開著的部隊視窗");
                TaskManager.Enqueue(() => Chat.ExecuteCommand("/freecompanycmd"));
                /*TaskManager.Enqueue(() =>
								{
										if(TryGetAddonByName<AtkUnitBase>("FreeCompany", out var addon))
										{
												if (addon->IsVisible())
												{
														addon->IsVisible() = false;
														return true;
												}
										}
										return false;
								});*/
                TaskManager.Enqueue(() =>
                {
                    if(TryGetAddonByName<AtkUnitBase>("FreeCompany", out var addon))
                    {
                        // 上一步關掉的那扇若還在關閉中(10 幀延遲與危險窗口同量級),這裡看到的是同一位址:不再關第二次。
                        if(!DialogGuards.TryPressOnce("FreeCompany", (nint)addon, "FCPoints.CloseAfter")) return false;
                        addon->Close(true);
                        return true;
                    }
                    return false;
                }, "FC 點數:讀完點數之後把部隊視窗關掉");
                TaskManager.Enqueue(() => Utils.FCPoints != OldFCPoints, new(abortOnTimeout: false));
                TaskManager.Enqueue(() => OfflineDataManager.WriteOfflineData(false, true));
            }
        }
    }
}
