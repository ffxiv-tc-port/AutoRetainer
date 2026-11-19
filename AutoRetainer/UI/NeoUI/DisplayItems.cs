namespace AutoRetainer.UI.NeoUI;

/// <summary>
/// 主視窗各資訊頁的「顯示項目」管理。新的開關存在 <see cref="MainWindowDisplay"/>；
/// 已經有既有設定欄位的項目直接綁原欄位（與其他設定頁共用同一個值），不另開第二套。
/// NuiBuilder 不替各區塊 PushID，兩個分頁共用的標籤一律加 ##ret／##dep 後綴。
/// </summary>
public sealed class DisplayItems : NeoUIEntry
{
    public override string Path => Loc.T("Display Items");

    private const string Ret = "##ret";
    private const string Dep = "##dep";

    public override NuiBuilder Builder { get; init; } = new NuiBuilder()
        .Section(Loc.T("Display Items"))
        .TextWrapped(Loc.T("Choose what the information tabs of the main window show. Everything is shown by default. Items marked with * are the same settings found elsewhere in the configuration - changing them here changes them there too."))
        .CheckboxInverted(Loc.T("Character search box") + " *", () => ref C.NoCharaSearch, Loc.T("Shown at the top of the Retainers and Deployables tabs. Same setting as \"Hide character search\" in User Interface."))

        .Section(Loc.T("Retainers tab: character rows"))
        .Checkbox(Loc.T("Completed venture icon") + Ret, () => ref C.Display.RetCharaDoneIcon)
        .Checkbox(Loc.T("Progress bar behind the character name") + Ret, () => ref C.Display.RetCharaProgressBar)
        .Checkbox(Loc.T("House icons") + Ret, () => ref C.Display.RetCharaHouseIcons, Loc.T("Free company / private house icons, shown when teleporting to houses is enabled for that character."))
        .Checkbox(Loc.T("Region lock icon") + Ret, () => ref C.Display.RetCharaLockoutIcon, Loc.T("Shown when the character is on a data center you have temporarily disabled. Hiding it does not change whether that character is processed."))
        .Checkbox(Loc.T("Data center travel icon") + Ret, () => ref C.Display.RetCharaDcTravelIcon, Loc.T("Shown while the character is visiting another data center. Right-clicking the icon clears that status, so hiding it also hides that shortcut."))
        .Checkbox(Loc.T("Column C: Multi Mode login counter"), () => ref C.Display.RetColCharaCount, Loc.T("Only appears while Multi Mode is on and character equalization is enabled."))
        .Checkbox(Loc.T("Column V: ventures"), () => ref C.Display.RetColVentures)
        .Checkbox(Loc.T("Column I: inventory space") + Ret, () => ref C.Display.RetColInventory)
        .Checkbox(Loc.T("Column E: rare item storage"), () => ref C.Display.RetColEntrustCapacity, Loc.T("Used / total slots of the retainers whose entrust plan only takes deliverable rare items (or the plan chosen for the expert delivery loop), plus roughly how many hours until they are full. Grey means unknown or not refreshed for a while; orange means almost full. Hover the column for each retainer and how the estimate is made."))
        .Checkbox(Loc.T("Column G: gil") + " *" + Ret, () => ref C.UIShowGilColumn, Loc.T("Shows the character's gil (as last seen by AutoRetainer) on the right of each character row in the Retainers and Deployables tabs; it turns into the warning color below the gil warning value. Off by default."))
        .Checkbox(Loc.T("Show allowance columns in character list") + " *", () => ref C.UIShowAllowances, Loc.T("Adds four columns to every character row: S = Grand Company seals, L = levequest allowances, D = custom delivery allowances left this week, T = limited tomestones acquired this week. A grey question mark means AutoRetainer has not managed to read that value on that character yet - it is filled in automatically shortly after logging in. A grey dash on the seal column means the character has not joined a Grand Company. Hover a column for the time it was sampled."))
        .If(() => C.UIShowAllowances)
        .Indent()
        .Checkbox(Loc.T("Column S: Grand Company seals") + " *", () => ref C.UIShowAllowanceSeals)
        .Checkbox(Loc.T("Column L: levequest allowances") + " *", () => ref C.UIShowAllowanceLeves)
        .Checkbox(Loc.T("Column D: custom delivery allowances") + " *", () => ref C.UIShowAllowanceCustomDeliveries)
        .Checkbox(Loc.T("Column T: weekly tomestones") + " *", () => ref C.UIShowAllowanceTomestones)
        .Unindent()
        .EndIf()

        .Section(Loc.T("Retainers tab: retainer list"))
        .Checkbox(Loc.T("Job"), () => ref C.Display.RetTableJob)
        .Checkbox(Loc.T("Venture"), () => ref C.Display.RetTableVenture)
        .Checkbox(Loc.T("Slots"), () => ref C.Display.RetTableSlots)
        .Checkbox(Loc.T("Display Extended Retainer Info") + " *", () => ref C.ShowAdditionalInfo, Loc.T("Displays retainer item level/gathering/perception and the name of their current venture in the main UI."))
        .If(() => C.ShowAdditionalInfo)
        .Indent()
        .Checkbox(Loc.T("Item level / gathering / perception (Job column)"), () => ref C.Display.RetExtraStats)
        .Checkbox(Loc.T("Current venture name (Venture column)"), () => ref C.Display.RetExtraVentureName)
        .Unindent()
        .EndIf()
        .Checkbox(Loc.T("Progress bar behind each retainer"), () => ref C.Display.RetRowProgressBar)
        .Checkbox(Loc.T("Entrust plan icon"), () => ref C.Display.RetIconEntrust, Loc.T("Clicking this icon entrusts items to that retainer, so hiding it also hides that shortcut."))
        .Checkbox(Loc.T("Gil withdrawal icon"), () => ref C.Display.RetIconWithdrawGil)
        .Checkbox(Loc.T("Venture planner icon"), () => ref C.Display.RetIconPlanner)

        .Section(Loc.T("Deployables tab: character rows"))
        .Checkbox(Loc.T("Returned voyage icon"), () => ref C.Display.DepCharaDoneIcon)
        .Checkbox(Loc.T("Progress bar behind the character name") + Dep, () => ref C.Display.DepCharaProgressBar)
        .Checkbox(Loc.T("House icons") + Dep, () => ref C.Display.DepCharaHouseIcons, Loc.T("Free company / private house icons, shown when teleporting to houses is enabled for that character."))
        .Checkbox(Loc.T("Region lock icon") + Dep, () => ref C.Display.DepCharaLockoutIcon, Loc.T("Shown when the character is on a data center you have temporarily disabled. Hiding it does not change whether that character is processed."))
        .Checkbox(Loc.T("Data center travel icon") + Dep, () => ref C.Display.DepCharaDcTravelIcon, Loc.T("Shown while the character is visiting another data center. Right-clicking the icon clears that status, so hiding it also hides that shortcut."))
        .Checkbox(Loc.T("Icon: a new submersible can be constructed"), () => ref C.Display.DepIconNewSlot)
        .Checkbox(Loc.T("Icon: some submersibles are not enabled"), () => ref C.Display.DepIconNotAllEnabled)
        .Checkbox(Loc.T("Icon: some submersibles are not on a voyage"), () => ref C.Display.DepIconNotDeployed)
        .Checkbox(Loc.T("Icon: unoptimal configurations found"), () => ref C.Display.DepIconSuboptimal)
        .Checkbox(Loc.T("Icon: redeploy conflicts with an enforced unlock plan"), () => ref C.Display.DepIconInvalidRedeploy)
        .Checkbox(Loc.T("Column R: repair kits"), () => ref C.Display.DepColRepair)
        .Checkbox(Loc.T("Column C: ceruleum tanks"), () => ref C.Display.DepColCeruleum)
        .Checkbox(Loc.T("Column I: inventory space") + Dep, () => ref C.Display.DepColInventory)
        .Checkbox(Loc.T("Column G: gil") + " *" + Dep, () => ref C.UIShowGilColumn, Loc.T("Shows the character's gil (as last seen by AutoRetainer) on the right of each character row in the Retainers and Deployables tabs; it turns into the warning color below the gil warning value. Off by default."))

        .Section(Loc.T("Deployables tab: vessel list"))
        .Checkbox(Loc.T("Hide Airships from Deployables UI") + " *", () => ref C.HideAirships, Loc.T("Only hides airships on characters that have no airship enabled."))
        .Checkbox(Loc.T("Structure"), () => ref C.Display.DepTableStructure)
        .Checkbox(Loc.T("Voyage"), () => ref C.Display.DepTableVoyage)
        .If(() => C.Display.DepTableVoyage)
        .Indent()
        .Checkbox(Loc.T("Route letters"), () => ref C.Display.DepRoute)
        .Unindent()
        .EndIf()
        .Checkbox(Loc.T("Vessel behavior icon"), () => ref C.Display.DepIconBehavior)
        .Checkbox(Loc.T("Index override note"), () => ref C.Display.DepIndexOverride)
        .Checkbox(Loc.T("Progress bar behind each vessel"), () => ref C.Display.DepRowProgressBar)

        .Section(Loc.T("Statistics tab"))
        .Checkbox(Loc.T("Only display character total") + " *", () => ref C.GilOnlyChars)
        .Checkbox(Loc.T("Show only wallet FC") + " *", () => ref C.DisplayOnlyWalletFC)
        .TextWrapped(Loc.T("FC Data columns:"))
        .Indent()
        .Checkbox(Loc.T("Characters"), () => ref C.Display.FcColCharacters)
        .Checkbox(Loc.T("Gil"), () => ref C.Display.FcColGil)
        .Checkbox(Loc.T("FC points"), () => ref C.Display.FcColPoints)
        .Checkbox(Loc.T("Founded"), () => ref C.Display.FcColFounded)
        .Checkbox(Loc.T("FC estate"), () => ref C.Display.FcColEstate)
        .Unindent();
}
