// 跨檔逐字重複的 Loc.T() 字串,收斂到這裡各留一份。
// 🔴 為什麼:Loc.T 拿英文原文本身當字典 key(loc/zh_TW.json)。同一句被複製到兩個呼叫點時,只要有人改了其中一份的英文,
//    那一份就查不到翻譯而**靜默**退回英文(不擲例外、不寫 log),另一份照樣顯示中文。
// ⚠️ 要改英文原文時,連同 loc/zh_TW.json 裡的那個 key 一起改。
namespace AutoRetainer;

internal static class SharedText
{
    /// <summary>多角色模式執行中時，主視窗與僱員清單疊加層那顆「啟用」核取方塊旁的說明圖示。</summary>
    public const string MultiModeOverridesThisOption = "MultiMode also controls this option. You can always change it by hand and it takes effect immediately, but while MultiMode is running it will switch this back on by itself when it moves on to the next retainer or character - untick \"Multi\" as well if you want it to stay off.";

    /// <summary>設定頁「角色排序」區塊的說明文字，同時當那一個 widget 的標題用。</summary>
    public const string CharacterSortingExplanation = "Here you can sort your characters. This will affect order in which they will be processed by Multi Mode as well as how they will appear in plugin interface and login overlay.";

    /// <summary>快捷鍵設定頁「背包清單編輯快捷鍵」底下，新增鍵與移除鍵共用的說明。</summary>
    public const string FastAddRemoveKeybindHint = "Used by both Inventory Cleanup -> Fast Addition and Removal, and Entrust Manager -> Fast addition/removal. Set to None to disable the action entirely.";

    /// <summary>稀有品繳交循環的多角色批次：多角色模式開著就拒絕開始；同一句也直接顯示在該面板上。</summary>
    public const string MultiModeBlocksMultiCharacterRun = "Multi Mode is on. Turn it off before starting a multi-character run - two things switching characters at the same time will fight each other.";

    /// <summary>軍票交換清單與背包清理兩處計畫選擇器的「設為預設計畫」按鈕 tooltip。</summary>
    public const string MakePlanDefaultHint = "Make this plan default. Current default plan will be overwritten. Hold CTRL and click.";

    /// <summary>設定頁兩個角色排序區塊（僱員標籤頁、遠航探索標籤頁）底下的「只影響顯示順序」說明。</summary>
    public const string VisualOrderOnlyNote = "This is purely visual order and does not affects character processing in any way.";

    /// <summary>稀有品繳交循環的多角色批次：除錯選項「不要登出」開著就拒絕開始；同一句也直接顯示在該面板上。</summary>
    public const string DontLogoutBlocksCharacterSwitch = "The \"Don't logout\" debug option is on, so characters cannot be switched.";

    /// <summary>稀有品繳交循環換角時查不到下一位角色的離線資料而中止的訊息；{0} 是 CID。</summary>
    public const string StoppedNextCharacterDataGone = "Stopped: the saved data for the next character (CID {0}) is gone.";

    /// <summary>傳送選項（角色個別與全域各一處）裡，列在三個房屋選項底下的補充說明。</summary>
    public const string FallbackTeleportToInn = "If all above are disabled or fail, will be teleported to inn.";

    /// <summary>委託管理與背包清理兩處「快速新增/移除」區塊最上面那一行提示。</summary>
    public const string HoverItemsWhileHolding = "While this text is visible, hover over items while holding:";

    /// <summary>稀有品繳交循環在取回與等落地兩個階段偵測到僱員道具管理視窗關閉而中止的訊息。</summary>
    public const string StoppedRetainerStorageClosed = "Stopped: the retainer's item storage closed unexpectedly.";

    /// <summary>遠航探索標籤頁與多角色模式疊加層的等待圖示 tooltip：這位角色開了等待全部派遣。</summary>
    public const string WaitForDeployablesEnabledForCharacter = "Wait for all deployables is enabled for this character.";

    /// <summary>傳送選項（角色個別與全域各一處）裡那一個核取方塊自己的標籤。</summary>
    public const string TeleportToFcHouseForDeployables = "Teleport to free company house for deployables";

    /// <summary>遠航探索標籤頁與多角色模式疊加層的等待圖示 tooltip：全域開了等待全部派遣。</summary>
    public const string WaitForDeployablesEnabledGlobally = "Wait for all deployables is globally enabled.";

    /// <summary>潛水艇目標計畫與解鎖計畫兩個視窗：這份計畫沒有任何潛水艇在用時顯示。</summary>
    public const string PlanNotUsedByAnySubmersibles = "This plan is not used by any submersibles.";
}
