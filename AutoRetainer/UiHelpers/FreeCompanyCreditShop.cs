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
    ///
    /// 這個參數原本是**完全沒被用到**的:不論傳什麼進來,送出去的 eventParam 都寫死 0。
    /// 現行三個呼叫端(TaskAutoBuyFuel、TaskRecursivelyBuyFuel、DebugReader)全都傳 0,
    /// 而整條買燃料的流程本身也寫死只看 Listings[0],所以實際購買行為一直是對的 ——
    /// 真正的缺陷是簽章擺著一個會被靜默忽略的參數:第一個傳 1 的人會安靜地買到第 0 列。
    ///
    /// 沒有順手改成把 index 直接當 eventParam 送出去,是因為那個編碼**沒有被驗證過**。
    /// ATK 的 eventParam 由各 addon 自己在註冊監聽器時決定,不是通用的列索引:對照
    /// ECommons 的 AddonMaster._CharaSelectListMenu.Character.Click,它用的是
    /// (byte)(5 + Index),而且同一個值同時餵給 AtkEvent 的 StateFlags 與 ReceiveEvent 的
    /// eventParam —— 那個 5 是 CharaSelectListMenu 專屬的基底,換一個 addon 就不成立。
    /// 猜錯的失敗形式是「安靜地買錯商品」而不是報錯,所以這裡選 fail-closed:非 0 就不送。
    ///
    /// 真的要支援多列購買時不要在這裡猜。ECommons 已經有走通的實作
    /// AddonMaster.FreeCompanyCreditShop.Item.Buy(quantity),送的是
    /// Callback.Fire(addon, true, 0, Index, quantity),第二個引數就是列索引;
    /// 它同時帶 MaxPurchaseSize 與 CompanyCredits 的前置檢查。
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
