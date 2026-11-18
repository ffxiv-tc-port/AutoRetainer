namespace AutoRetainer.PluginData;

/// <summary>
/// 主視窗各資訊頁「顯示哪些項目」。🔴 每一項預設 true＝維持既有畫面；只能用來「可以關」，不可拿來改預設值。
/// 缺鍵時 EzConfig（Newtonsoft、ObjectCreationHandling.Replace）先用預設建構子建一份再填有的鍵，所以缺的項目保留 true。
/// 已經另有設定欄位的項目（延伸資訊、配額欄、角色搜尋、隱藏飛空艇、金幣只看總計、只看錢包部隊）不在這裡重複，
/// 管理頁直接綁原欄位，避免兩套設定互相矛盾。
/// </summary>
[Serializable]
public sealed class MainWindowDisplay
{
    // 僱員分頁：角色列
    public bool RetCharaDoneIcon = true;
    public bool RetCharaProgressBar = true;
    public bool RetCharaHouseIcons = true;
    public bool RetCharaLockoutIcon = true;
    public bool RetCharaDcTravelIcon = true;
    public bool RetColCharaCount = true;
    public bool RetColVentures = true;
    public bool RetColInventory = true;

    // 僱員分頁：僱員表格
    public bool RetTableJob = true;
    public bool RetTableVenture = true;
    public bool RetTableSlots = true;
    public bool RetExtraStats = true;
    public bool RetExtraVentureName = true;
    public bool RetRowProgressBar = true;
    public bool RetIconEntrust = true;
    public bool RetIconWithdrawGil = true;
    public bool RetIconPlanner = true;

    // 遠航探索分頁：角色列
    public bool DepCharaDoneIcon = true;
    public bool DepCharaProgressBar = true;
    public bool DepCharaHouseIcons = true;
    public bool DepCharaLockoutIcon = true;
    public bool DepCharaDcTravelIcon = true;
    public bool DepIconNewSlot = true;
    public bool DepIconNotAllEnabled = true;
    public bool DepIconNotDeployed = true;
    public bool DepIconSuboptimal = true;
    public bool DepIconInvalidRedeploy = true;
    public bool DepColRepair = true;
    public bool DepColCeruleum = true;
    public bool DepColInventory = true;

    // 遠航探索分頁：潛水艇／飛空艇表格
    public bool DepTableStructure = true;
    public bool DepTableVoyage = true;
    public bool DepRoute = true;
    public bool DepIconBehavior = true;
    public bool DepIndexOverride = true;
    public bool DepRowProgressBar = true;

    // 統計分頁：部隊資料表格
    public bool FcColCharacters = true;
    public bool FcColGil = true;
    public bool FcColPoints = true;
    public bool FcColFounded = true;
    public bool FcColEstate = true;
}
