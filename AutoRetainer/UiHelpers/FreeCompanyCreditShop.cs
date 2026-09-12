using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace AutoRetainer.UiHelpers;
public unsafe class FreeCompanyCreditShop : AddonMasterBase
{
    public FreeCompanyCreditShop(nint addon) : base(addon)
    {
    }

    public FreeCompanyCreditShop(void* addon) : base(addon)
    {
    }

    public override string AddonDescription { get; } = "";

    /// <summary>
    /// 送出「點擊商品列購買」的事件。購買數量是呼叫端事先設在該列數值輸入元件上的
    /// (見 TaskAutoBuyFuel.TrySetPurchaseAmount),這裡只負責按下去。
    /// </summary>
    /// <remarks>
    /// 🔴 <paramref name="index"/> 目前只支援 0。
    /// 沒有順手改成把 index 直接當 eventParam 送出去，所以這裡選 fail-closed:非 0 就不送。
    /// 真的要支援多列購買時不要在這裡猜。ECommons 已經有走通的實作 AddonMaster.FreeCompanyCreditShop.Item.Buy(quantity)。
    /// </remarks>
    public void Buy(int index)
    {
        if(index != 0)
        {
            PluginLog.Error($"{nameof(FreeCompanyCreditShop)}.{nameof(Buy)}: 只支援 index 0,收到 {index}。非 0 的事件參數編碼未經驗證,已放棄本次購買以免買到錯的商品列。");
            return;
        }

        var evt = CreateAtkEvent();
        var data = CreateAtkEventData().Build();
        Addon->ReceiveEvent(AtkEventType.ListItemClick, 0, &evt, &data);
    }
}
