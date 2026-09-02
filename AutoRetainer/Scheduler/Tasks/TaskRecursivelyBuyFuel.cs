using AutoRetainer.UiHelpers;
using ECommons.Throttlers;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace AutoRetainer.Scheduler.Tasks;
public unsafe class TaskRecursivelyBuyFuel
{
    private static uint Amount = 0;
    public static void Enqueue()
    {
        P.TaskManager.Enqueue(() =>
        {
            if(TryGetAddonMaster<AddonMaster.SelectYesno>(out var m))
            {
                // 同 TaskAutoBuyFuel:同一扇確認框只按一次;讀到 U+FFFD 這一幀不碰。
                var text = m.Text;
                if(!DialogGuards.TextIsUnstable(text) && text.ContainsAny(StringComparison.OrdinalIgnoreCase, Lang.WorkshopBuyFuelConfirm))
                {
                    if(EzThrottler.Throttle("CeruleumYesNo") && DialogGuards.TryPressOnce("SelectYesno", (nint)m.Base, "CeruleumYesNo")) m.Yes();
                }
            }
            if(TryGetAddonByName<AtkUnitBase>("FreeCompanyCreditShop", out var a) && IsAddonReady(a))
            {
                var reader = new ReaderFreeCompanyCreditShop(a);
                if(Amount != reader.Credits)
                {
                    EzThrottler.Reset("CeruleumYesNo");
                    EzThrottler.Reset("FCBuy");
                    Amount = reader.Credits;
                }
                if(reader.Credits < 100) return true;
                if(EzThrottler.Throttle("FCBuy", 2000) && DialogGuards.TryPressOnce("FreeCompanyCreditShop", (nint)a, "FCBuy", "Buy0", escapeIsRoutine: true))
                {
                    new FreeCompanyCreditShop(a).Buy(0);
                }
            }
            else
            {
                return null;
            }
            return false;
        }, new(timeLimitMS: 1000 * 60 * 10));
    }
}
