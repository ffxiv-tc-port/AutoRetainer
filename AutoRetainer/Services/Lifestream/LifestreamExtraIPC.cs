using ECommons.EzIpcManager;
using ECommons.IPC.Subscribers.LifestreamIPC;

namespace AutoRetainer.Services.Lifestream;

/// <summary>
/// ECommons.IPC 套件的 LifestreamIPC 已經涵蓋我們大部分的 Lifestream 呼叫,但它是 sealed 的,
/// 無法用繼承補東西。這個側車類用同一個 prefix "Lifestream" 各自 EzIPC.Init,與套件實例並存,
/// 收容兩類套件給不了的成員:
///
/// (甲)套件根本沒有的成員
///   - OnHouseEnterError:🔴 這是**活的**——Lifestream 的 TaskPropertyShortcut 會發這個事件,
///     AutoRetainer 收到後把目前角色移出多角模式。上游遷移到套件時把它丟了,那一步不可跟。
///   - GetTeleportFavorites / TeleportToFavorite:台服稀有品繳交循環用的,套件沒有。
///
/// (乙)套件有、但我們這版 ECommons 綁不上去的成員
///   🔴 套件把 EnqueuePropertyShortcut / MoveToWorkshop / Teleport / MoveEx 宣告成**自訂 delegate 型別**
///   (為了帶預設參數)。我們釘的 ECommons(pin-wrathcombo-tc-api13)裡的 EzIPC 訂閱端只認
///   非泛型 Action 與泛型 Action&lt;&gt;/Func&lt;&gt;——見 EzIPC.cs 的
///   `reference.UnionType.GetGenericTypeDefinition().EqualsAny([.. FuncTypes, .. ActionTypes])`,
///   對自訂 delegate 會擲 InvalidOperationException,被外層 catch 吃掉,**欄位就停在 null**。
///   上游 ECommons 後來加了 ReflectionHelper.AnalyzeDelegateField/AssignDelegateToField 才支援,
///   我們的 pin 沒有那段。若照搬到套件實例,呼叫時會是 NullReferenceException,而且
///   SafeWrapper 攔不到(欄位從沒被指派,根本沒有 wrapper 可言)。
///   ⇒ 這兩個仍用我們原本的 Action 形狀留在這裡,**行為與遷移前逐字相同**。
///   📌 2026-09-03 更新:ECommons repin 到 4906fd97 之後,上面那個限制已經解除——
///   EzIPC 的訂閱端改走 TryGetDelegateSignature(EzIPC.cs:272),接受任何委派型別,
///   自訂具名委派會正常綁上。修好它的函式不叫 AnalyzeDelegateField,我們這條分支也不會
///   出現那個名字,所以不要再拿那個名字當「可以刪了」的判準。
///   ⚠ 但「刪掉改用套件實例」仍未實機驗證:套件的參數型別是 PropertyType /
///   HouseEnterMode? 列舉;本檔已於 2026-09-05 改用同一組列舉,型別不再是差異點。
///   要刪請先在實機確認 TaskTeleportToProperty 與 TaskNeoHET 兩個呼叫點仍然動作。
/// </summary>
public class LifestreamExtraIPC
{
    private LifestreamExtraIPC()
    {
        // 維持我方一貫的 AnyException(靜默降級)。套件的 IPCBase 預設是 SafeWrapper.None,
        // 那會把「Lifestream 沒裝」從回傳預設值變成往外擲例外——刻意不採。
        EzIPC.Init(this, "Lifestream", SafeWrapper.AnyException);
    }

    // ---- (甲)套件沒有的成員 ----

    [EzIPCEvent]
    public void OnHouseEnterError()
    {
        PluginLog.Warning($"Received house enter error from Lifestream. Current character will be excluded from multi mode.");
        if(Data != null)
        {
            Data.Enabled = false;
            Data.WorkshopEnabled = false;
        }
    }

    // 使用者在傳送面板收藏好的地點。用它當導航目標比自組路線安全:收藏項都是既知的乙太之光/
    // 乙太網點,走的是面板按鈕本來就在走的那條路。⚠️ Id 與 SubIndex 要一起帶,同一個 id 可能對到多筆。
    [EzIPC] public Func<List<(uint Id, byte SubIndex, string Name, uint Territory)>> GetTeleportFavorites;
    [EzIPC] public Func<uint, byte, bool> TeleportToFavorite;

    // ---- (乙)套件有、但本版 ECommons 的 EzIPC 綁不上自訂 delegate,故沿用原形狀 ----

    [EzIPC] public Action<PropertyType, HouseEnterMode?> EnqueuePropertyShortcut;

    [EzIPC] public Action MoveToWorkshop;
}
