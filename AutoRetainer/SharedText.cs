// 跨檔逐字重複的 Loc.T() 字串,收斂到這裡各留一份。
//
// 🔴 為什麼:Loc.T 拿英文原文本身當字典 key(loc/zh_TW.json)。同一句被複製到兩個呼叫點時,
//    只要有人改了其中一份的英文,那一份就查不到翻譯而**靜默**退回英文,另一份照樣顯示中文。
//    表現出來像「漏翻一句」,不像「複製品走散了」——查不到不擲例外、不寫 log。
//
// ⚠️ 這些常數的值是從原本的呼叫點**逐字**搬過來的,所以字典 key 完全沒變,zh_TW.json 不用動。
//    要改英文原文時,連同 loc/zh_TW.json 裡的那個 key 一起改。
//
// 產生與驗證工具:~/.claude/tools/loc/ar_loct_dedup.py(收斂)、loct_check.py(呼叫點↔字典完整性)。
namespace AutoRetainer;

internal static class SharedText
{
    /// <summary>呼叫點: UI/MainWindow/AutoRetainerWindow.cs:136, UI/Overlays/RetainerListOverlay.cs:53</summary>
    public const string MultiModeOverridesThisOption = "MultiMode also controls this option. You can always change it by hand and it takes effect immediately, but while MultiMode is running it will switch this back on by itself when it moves on to the next retainer or character - untick \"Multi\" as well if you want it to stay off.";

    /// <summary>呼叫點: UI/NeoUI/MultiModeEntries/CharaOrder.cs:18, UI/NeoUI/MultiModeEntries/CharaOrder.cs:20</summary>
    public const string CharacterSortingExplanation = "Here you can sort your characters. This will affect order in which they will be processed by Multi Mode as well as how they will appear in plugin interface and login overlay.";

    /// <summary>呼叫點: UI/NeoUI/Keybinds.cs:25, UI/NeoUI/Keybinds.cs:29</summary>
    public const string FastAddRemoveKeybindHint = "Used by both Inventory Cleanup -> Fast Addition and Removal, and Entrust Manager -> Fast addition/removal. Set to None to disable the action entirely.";

    /// <summary>呼叫點: Modules/GcHandin/GCExpertDeliveryLoop.cs:398, UI/NeoUI/InventoryManagementEntries/GCDeliveryEntries/ExpertDeliveryLoop.cs:175</summary>
    public const string MultiModeBlocksMultiCharacterRun = "Multi Mode is on. Turn it off before starting a multi-character run - two things switching characters at the same time will fight each other.";

    /// <summary>呼叫點: UI/NeoUI/InventoryManagementEntries/GCDeliveryEntries/ExchangeLists.cs:92, UI/NeoUI/InventoryManagementEntries/InventoryCleanupEntries/InventoryCleanupCommon.cs:108</summary>
    public const string MakePlanDefaultHint = "Make this plan default. Current default plan will be overwritten. Hold CTRL and click.";

    /// <summary>呼叫點: UI/NeoUI/UserInterface.cs:33, UI/NeoUI/UserInterface.cs:38</summary>
    public const string VisualOrderOnlyNote = "This is purely visual order and does not affects character processing in any way.";

    /// <summary>呼叫點: Modules/GcHandin/GCExpertDeliveryLoop.cs:404, UI/NeoUI/InventoryManagementEntries/GCDeliveryEntries/ExpertDeliveryLoop.cs:179</summary>
    public const string DontLogoutBlocksCharacterSwitch = "The \"Don't logout\" debug option is on, so characters cannot be switched.";

    /// <summary>呼叫點: Modules/GcHandin/GCExpertDeliveryLoop.cs:678, Modules/GcHandin/GCExpertDeliveryLoop.cs:723</summary>
    public const string StoppedNextCharacterDataGone = "Stopped: the saved data for the next character (CID {0}) is gone.";

    /// <summary>呼叫點: UI/MainWindow/MultiModeTab/CharaConfig.cs:57, UI/NeoUI/MultiModeEntries/MultiModeCommon.cs:55</summary>
    public const string FallbackTeleportToInn = "If all above are disabled or fail, will be teleported to inn.";

    /// <summary>呼叫點: UI/NeoUI/InventoryManagementEntries/EntrustManager.cs:157, UI/NeoUI/InventoryManagementEntries/InventoryCleanupEntries/FastAddition.cs:23</summary>
    public const string HoverItemsWhileHolding = "While this text is visible, hover over items while holding:";

    /// <summary>呼叫點: Modules/GcHandin/GCExpertDeliveryLoop.cs:1243, Modules/GcHandin/GCExpertDeliveryLoop.cs:1315</summary>
    public const string StoppedRetainerStorageClosed = "Stopped: the retainer's item storage closed unexpectedly.";

    /// <summary>呼叫點: UI/MainWindow/WorkshopUI.cs:141, UI/Overlays/MultiModeOverlay.cs:315</summary>
    public const string WaitForDeployablesEnabledForCharacter = "Wait for all deployables is enabled for this character.";

    /// <summary>呼叫點: UI/MainWindow/MultiModeTab/CharaConfig.cs:59, UI/NeoUI/MultiModeEntries/MultiModeCommon.cs:57</summary>
    public const string TeleportToFcHouseForDeployables = "Teleport to free company house for deployables";

    /// <summary>呼叫點: UI/MainWindow/WorkshopUI.cs:133, UI/Overlays/MultiModeOverlay.cs:302</summary>
    public const string WaitForDeployablesEnabledGlobally = "Wait for all deployables is globally enabled.";

    /// <summary>呼叫點: UI/Windows/SubmarinePointPlanUI.cs:75, UI/Windows/SubmarineUnlockPlanUI.cs:126</summary>
    public const string PlanNotUsedByAnySubmersibles = "This plan is not used by any submersibles.";
}
