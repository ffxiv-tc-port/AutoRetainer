using AutoRetainerAPI.Configuration;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Utility;
using ECommons.ExcelServices.Sheets;
using Lumina.Excel.Sheets;
using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using System.Windows.Forms.VisualStyles;

namespace AutoRetainer;

internal static class Lang
{
    internal const string CharPlant = "";
    internal const string CharLevel = "";
    internal const string CharItemLevel = "";
    internal const string CharDice = "";
    internal const string CharDeny = "";
    internal const string CharQuestion = "";
    internal const string CharLevelSync = "";
    internal const string CharP = "";
    internal const string StrDCV = "";

    internal const string IconRefresh = "\uf2f9";
    internal const string IconMultiMode = "\uf021";
    internal const string IconDuplicate = "\uf24d";
    internal const string IconGil = "\uf51e";
    internal const string IconPlanner = "\uf0ae";
    internal const string IconSettings = "\uf013";
    internal const string IconWarning = "\uf071";

    internal const string IconAnchor = "\uf13d";
    internal const string IconLevelup = "\ue098";
    internal const string IconResend = "\ue4bb";
    internal const string IconUnlock = "\uf13e";
    internal const string IconRepeat = "\uf363";
    internal const string IconPath = "\uf55b";
    internal const string IconFire = "\uf06d";

    internal static string LogOutAndExitGame => Svc.Data.GetExcelSheet<Addon>().GetRow(116).Text.GetText(true).Cleanup();

    // Display text for UnlockMode. These are descriptions rather than bare member names on
    // purpose: "MultiSelect" on its own does not tell the user that the mode fills the route with
    // as many unlock destinations as the submersible can reach, so the wording is worth keeping -
    // it only has to become translatable. Upstream fed this hardcoded English table to two of the
    // four unlock-mode combos while the other two fell back to the enum member name, which is how
    // the same enum ended up showing two different sets of words; all four now read from here.
    //
    // Built once on first use and reused for the rest of the session: ImGuiEx.EnumCombo is called
    // from immediate-mode Draw code, so translating at the call site would allocate a dictionary
    // plus one string per member on every frame. The table lives in a nested type rather than in a
    // static field of Lang so its initializer is tied to its own first use instead of to any other
    // Lang member - that is what rules out it running, and caching untranslated text, before the
    // dictionary is loaded. Loc.Load is the first statement of AutoRetainer.Load(), so every draw
    // call is later than it. Handed out read-only so a consumer cannot mutate the shared instance.
    internal static IDictionary<UnlockMode, string> UnlockModeNames => UnlockModeNameCache.Value;

    private static class UnlockModeNameCache
    {
        internal static readonly IDictionary<UnlockMode, string> Value =
            new ReadOnlyDictionary<UnlockMode, string>(new Dictionary<UnlockMode, string>()
            {
                { UnlockMode.MultiSelect, Loc.T("Pick max amount of destinations") },
                { UnlockMode.SpamOne, Loc.T("Spam one destination") },
                { UnlockMode.WhileLevelling, Loc.T("Include one unlock destination while levelling") },
            });
    }

    internal static readonly (string Normal, string GameFont) Digits = ("0123456789", "");

    //招募貝殼選單自己的文字表:客戶端跑哪個語言就讀到哪個語言,不必再逐語言硬編。
    private const string RetainerBellDialogue = "custom/000/CmnDefRetainerCall_00010";

    // 🔴 讀不到/讀到空值時一律回 fallback,**絕不回空字串**:
    //    這些字串的消費端是 StartsWithAny / ContainsAny,而
    //    "任何字串".StartsWith("") 與 "任何字串".Contains("") 恆為 true ——
    //    一個空錨會讓「選單的第一項」或「任何一個 SelectYesno」被誤判成命中然後按下去。
    //    (上游版本的 BellText 讀不到時回 "",這個洞在我方補上。)
    // ⚠️ Lumina 的 GetSheet 在表不存在時是**擲例外**不是回 null,所以 ?. 擋不住,要 try/catch。
    private static string BellText(uint row, string fallback)
    {
        try
        {
            var text = Svc.Data.GetExcelSheet<QuestDialogueText>(name: RetainerBellDialogue).GetRowOrDefault(row)?.Value.GetText();
            if(!string.IsNullOrEmpty(text)) return text;
            PluginLog.Information($"[Lang] {RetainerBellDialogue} row {row} is empty; falling back to the built-in literal.");
        }
        catch(Exception e)
        {
            PluginLog.Information($"[Lang] Could not read {RetainerBellDialogue} row {row} ({e.GetType().Name}: {e.Message}); falling back to the built-in literal.");
        }
        return fallback;
    }

    //Everything a log message says before its first macro, which is fixed text in any language.
    // 🔴 同上:解析不出開頭的固定文字時回 fallback,不回空字串(空前綴會讓每一則訊息都命中)。
    internal static string LogMessageOpening(uint row, string fallback)
    {
        try
        {
            var text = Svc.Data.GetExcelSheet<LogMessage>().GetRowOrDefault(row)?.Text.ToDalamudString();
            if(text != null)
            {
                var opening = "";
                foreach(var payload in text.Payloads)
                {
                    if(payload is not TextPayload literal) break;
                    opening += literal.Text;
                }
                opening = opening.Trim();
                if(!string.IsNullOrEmpty(opening)) return opening;
            }
            PluginLog.Information($"[Lang] LogMessage row {row} has no leading literal text; falling back to the built-in literal.");
        }
        catch(Exception e)
        {
            PluginLog.Information($"[Lang] Could not read LogMessage row {row} ({e.GetType().Name}: {e.Message}); falling back to the built-in literal.");
        }
        return fallback;
    }

    // 只取第一行當比對錨:確認框那幾條原文是兩行的,而 addon 端的斷行位置未必與表裡一致。
    private static string FirstLine(string s)
    {
        var i = s.IndexOf('\n');
        return (i < 0 ? s : s[..i]).TrimEnd('\r');
    }

    //196	TASK_CATEGORY_TREASURE	Field Exploration.
    //198	TASK_CATEGORY_MINER_2	Highland Exploration.
    //200	TASK_CATEGORY_BOTANIST_2	Woodland Exploration.
    //202	TASK_CATEGORY_FISHER_2	Waterside Exploration.
    // 🔴 這四格的**順序有語意**:VentureUtils.GetFieldExVentureName 拿 [0]~[3] 當位置索引用
    //    (平地/山岳/森林/水岸),不可調換,也不可在中間插入別的字串。
    // 📌 台服 7.20 sqpack 實查:196/198/200/202 全部存在且對應正確;字面值只是讀表失敗時的備援。
    internal static string[] FieldExplorationNames => field ??=
    [
        BellText(196, "平地探索委託（需要2枚探險幣）"),
        BellText(198, "山岳探索委託（需要2枚探險幣）"),
        BellText(200, "森林探索委託（需要2枚探險幣）"),
        BellText(202, "水岸探索委託（需要2枚探險幣）"),
    ];

    //195	TASK_CATEGORY_NORMAL	Hunting.
    //197	TASK_CATEGORY_MINER_1	Mining.
    //199	TASK_CATEGORY_BOTANIST_1	Botany.
    //201	TASK_CATEGORY_FISHER_1	Fishing.
    // 🔴 同上:[0]~[3] 被 VentureUtils.GetHuntingVentureName 當位置索引用(狩獵/採礦/採伐/捕魚)。
    // 📌 台服 7.20 sqpack 實查:195/197/199/201 全部存在且對應正確。
    internal static string[] HuntingVentureNames => field ??=
    [
        BellText(195, "狩獵籌集委託（需要1枚探險幣）"),
        BellText(197, "採礦籌集委託（需要1枚探險幣）"),
        BellText(199, "採伐籌集委託（需要1枚探險幣）"),
        BellText(201, "捕魚籌集委託（需要1枚探險幣）"),
    ];

    //402	TASK_CATEGORY_FORTUNE	Quick Exploration.
    internal static string[] QuickExploration => field ??= [BellText(402, "自由尋寶委託（需要2枚探險幣）")];

    internal static readonly string[] Entrance =
    [
        "ハウスへ入る",
        "进入房屋",
        "進入房屋",
        "Eingang",
        "Entrée",
        "Entrance",
        "주택으로 들어가기",
    ];

    internal static string ApartmentEntrance => Svc.Data.GetExcelSheet<EObjName>().GetRow(2007402).Singular.ToString();

    internal static readonly string[] ConfirmHouseEntrance =
    [
        "「ハウス」へ入りますか？",
        "要进入这间房屋吗？",
        "要進入這間房屋嗎？",
        "Das Gebäude betreten?",
        "Entrer dans la maison ?",
        "Enter the estate hall?",
        "'주택'으로 들어가시겠습니까?",
    ];

    //194	ASK_CATEGORY	Select a category.
    internal static string[] RetainerAskCategoryText => field ??= [BellText(194, "請選擇要委託的探險")];

    internal static string[] BellName => [Svc.Data.GetExcelSheet<EObjName>().GetRow(2000401).Singular.GetText(), "リテイナーベル"];

    //0	TEXT_HOUFIXMANSIONENTRANCE_00359_HOUSINGAREA_MENU_ENTER_MYROOM	Go to your apartment
    //0	TEXT_HOUFIXMANSIONENTRANCE_00359_HOUSINGAREA_MENU_ENTER_MYROOM	自分の部屋に移動する
    //0	TEXT_HOUFIXMANSIONENTRANCE_00359_HOUSINGAREA_MENU_ENTER_MYROOM	Die eigene Wohnung betreten
    //0	TEXT_HOUFIXMANSIONENTRANCE_00359_HOUSINGAREA_MENU_ENTER_MYROOM	Aller dans votre appartement

    internal static readonly string[] GoToYourApartment =
    [
        "Go to your apartment",
        "自分の部屋に移動する",
        "移动到自己的房间",
        "移動到自己的房間",
        "Die eigene Wohnung betreten",
        "Aller dans votre appartement",
        "자신의 방으로 이동",
    ];

    internal static readonly string[] SkipCutsceneStr =
    [
        "Skip cutscene?",
        "要跳过这段过场动画吗？",
        "要跳過這段過場動畫嗎？",
        "Videosequenz überspringen?",
        "Passer la scène cinématique ?",
        "このカットシーンをスキップしますか？",
        "영상을 건너뛰시겠습니까?",
    ];
    //11	TEXT_CMNDEFHOUSINGPERSONALROOMENTRANCE_00178_GOTO_WORKSHOP	Move to the company workshop
    //11	TEXT_CMNDEFHOUSINGPERSONALROOMENTRANCE_00178_GOTO_WORKSHOP	地下工房に移動する
    //11	TEXT_CMNDEFHOUSINGPERSONALROOMENTRANCE_00178_GOTO_WORKSHOP	Die Ge<SoftHyphen/>sell<SoftHyphen/>schaftswerkstätte betreten
    //11	TEXT_CMNDEFHOUSINGPERSONALROOMENTRANCE_00178_GOTO_WORKSHOP	Aller dans l'atelier de compagnie
    internal static readonly string[] EnterWorkshop = ["Move to the company workshop", "地下工房に移動する", "移动到部队工房", "移動到部隊工房", "移動到公會工坊", "Die Gesellschaftswerkstätte betreten", "Aller dans l'atelier de compagnie", "지하공방으로 이동"];

    internal static readonly string[] AirshipManagement = ["Airship Management", "飛空艇の管理", "管理飞空艇", "管理飛空艇", "Luftschiff verwalten", "Contrôle aérien", "비공정 관리"];
    internal static readonly string[] SubmarineManagement = ["Submersible Management", "潜水艦の管理", "管理潜水艇", "管理潛水艇", "Tauchboot verwalten", "Contrôle sous-marin", "잠수함 관리"];
    internal static readonly string[] CancelVoyage = ["Cancel", "キャンセル", "取消", "Abbrechen", "Annuler", "취소"];
    internal static readonly string[] NothingVoyage = ["Nothing.", "やめる", "取消", "Nichts", "Annuler", "그만두기"];
    internal static readonly string[] DeployOnSubaquaticVoyage = ["Deploy submersible on subaquatic voyage", "ボイジャー出港", "出发", "出發", "Auf Erkundung gehen", "Expédier le sous-marin", "탐사 출항"];
    internal static readonly string[] ViewPrevVoyageLog = ["View previous voyage log", "前回のボイジャー報告", "上次的远航报告", "上次的遠航報告", "Bericht der letzten Erkundung", "Consulter le journal de la précédente expédition", "이전 탐사 보고서"];
    // TC's per-vessel menu (the 7-entry variant shown after collecting a report) closes with "退出";
    // "取消" only appears on the vessel-selector list and repair-reopened variants, so both are needed.
    internal static readonly string[] VoyageQuitEntry = ["Quit", "やめる", "取消", "退出", "Beenden", "Annuler", "그만두기"];
    internal static readonly string[] ChangeSubmersibleComponents = ["Change submersible components", "パーツの変更", "Bauteile austauschen", "Changer les éléments", "부품 변경", "更换配件", "更換配件"];
    internal static readonly string[] RegisterSub = ["Outfit and register a submersible.", "潜水艦の新規登録", "Registrierung eines neuen Tauchboots", "Enregistrement d'un sous-marin", "새 잠수함 등록", "登记新的潜水艇", "登記新的潛水艇"];

    // Company Workshop's "adventurer doll" NPC name carries a per-instance
    // numeric suffix (e.g. "冒險人偶014號"), so this is matched with Contains
    // rather than Equals. TC/CN confirmed via screenshot; other locales unverified.
    internal static readonly string[] AdventurerDollNamePart = ["冒險人偶", "冒险人偶"]; // English/JP/etc not yet confirmed
    // SelectString entry to open the Free Company Credit Shop from the doll's menu.
    internal static readonly string[] FreeCompanyCreditShopMenu = ["Free Company Credit Shop", "公會戰績交易", "公会战绩交易"]; // JP/DE/FR/KR not yet confirmed
    // Yes/No confirm when exchanging seals/points for an item at that shop; "青磷水" (Ceruleum) is stable across TC/CN.
    internal static readonly string[] WorkshopBuyFuelConfirm = ["ceruleum", "青磷水"];

    internal static readonly string[] PanelAirship = ["Select an airship.", "飛空艇を選択してください。", "请选择飞空艇。", "請選擇飛空艇。", "Wähle ein Luftschiff.", "Choisissez un aéronef.", "비공정을 선택하십시오."];
    internal static readonly string[] PanelSubmersible = ["Select a submersible.", "潜水艦を選択してください。", "请选择潜水艇。", "請選擇潛水艇。", "Wähle ein Tauchboot.", "Choisissez un sous-marin.", "잠수함을 선택하십시오."];

    //2004353	entrance to additional chambers	0	entrances to additional chambers	0	1	1	0	0
    internal static string[] AdditionalChambersEntrance =>
    [
        Svc.Data.GetExcelSheet<EObjName>().GetRow(2004353).Singular.GetText(),
        Regex.Replace(Svc.Data.GetExcelSheet<EObjName>().GetRow(2004353).Singular.GetText(), @"\[.*?\]", "")
    ];

    //2005274	voyage control panel	0	voyage control panels	0	0	1	0	0
    internal static string PanelName => Svc.Data.GetExcelSheet<EObjName>().GetRow(2005274).Singular.GetText();

    //4160	60	9	0	False	Unable to retrieve extracted items. Insufficient inventory/crystal inventory space.
    internal static string VoyageInventoryError => Svc.Data.GetExcelSheet<LogMessage>().GetRow(4160).Text.ToDalamudString().GetText();

    // LogMessage 5800 = 跨資料中心造訪中，無法操作。
    // 台服真值：「由於正前往<DC>遊玩，無法操作。」（DC 名是參數，所以只能比對前綴）。
    // ⚠️ 這裡原本的中文兩條是 LogMessage 6050（「其他玩家正在操作該潛水艇」），
    // 是另一則真實存在的訊息，會讓中文用戶在無關情境下被誤判而關掉 MultiMode。
    internal static string[] UnableToVisitWorld = ["Unable to execute command. Character is currently visiting the", "他のデータセンター", "角色正在", "由於正前往", "Der Vorgang kann nicht ausgeführt werden, da der Charakter gerade das Datenzentrum", "Impossible d'exécuter cette commande. Le personnage se trouve dans un autre centre de traitement de données", "다른 데이터 센터"];

    //4169	60	9	0	False	Unable to repair vessel component without the required <SheetEn(Item,3,IntegerParameter(1),1,1)/>.
    //4272	60	9	0	False Unable to repair vessel.Insufficient<SheetEn(Item,3,IntegerParameter(1),3,1)/>.
    //4169	60	9	0	False	修理に必要な<Sheet(Item,IntegerParameter(1),0)/>を持っていません。
    //4272	60	9	0	False	修理に必要な<Sheet(Item,IntegerParameter(1),0)/>が足りません。
    //4169	60	9	0	False	未持有修理所必需的<Sheet(Item,IntegerParameter(1),0)/>。
    //4272	60	9	0	False	沒有修理所必需的<Sheet(Item,IntegerParameter(1),0)/>。
    //4272	60	9	0	False	Du hast nicht genug <SheetDe(Item,5,IntegerParameter(1),2,4,1)/> für die Reparatur.
    //4169	60	9	0	False	Für die Reparatur ist <SheetDe(Item,1,IntegerParameter(1),1,1,1)/> erforderlich.
    //4169	60	9	0	False	Réparation impossible. Vous n'avez pas <SheetFr(Item,2,IntegerParameter(1),1,1)/> nécessaire.
    //4272	60	9	0	False	Vous n'avez pas <SheetFr(Item,2,IntegerParameter(1),1,1)/> nécessaire à la réparation.

    internal static readonly string[] UnableToRepairVessel = ["修理に必要な", "修理所必需的", "Unable to repair vessel", "Du hast nicht genug", "Für die Reparatur ist", "Réparation impossible. Vous n'avez pas", "nécessaire à la réparation", "수리에 필요한"];

    //11	TEXT_HOUFIXCOMPANYSUBMARINE_00447_SUBMARINE_CMD_REPAIR_PARTS	パーツの修理
    //11	TEXT_HOUFIXCOMPANYSUBMARINE_00447_SUBMARINE_CMD_REPAIR_PARTS	Bauteile reparieren
    //11	TEXT_HOUFIXCOMPANYSUBMARINE_00447_SUBMARINE_CMD_REPAIR_PARTS	Réparer des éléments
    //11	TEXT_HOUFIXCOMPANYSUBMARINE_00447_SUBMARINE_CMD_REPAIR_PARTS	修理配件
    //10	TEXT_CMNDEFCOMPANYCOMMANDERBOARD_00258_AIRSHIP_CMD_REPAIR_PARTS	パーツの修理
    //10	TEXT_CMNDEFCOMPANYCOMMANDERBOARD_00258_AIRSHIP_CMD_REPAIR_PARTS	Bauteile reparieren
    //10	TEXT_CMNDEFCOMPANYCOMMANDERBOARD_00258_AIRSHIP_CMD_REPAIR_PARTS	Réparer des éléments
    //10	TEXT_CMNDEFCOMPANYCOMMANDERBOARD_00258_AIRSHIP_CMD_REPAIR_PARTS	修理配件

    internal static readonly string[] WorkshopRepair =
    [
        "Repair submersible components",
        "Repair airship components",
        "パーツの修理",
        "Bauteile reparieren",
        "Réparer des éléments",
        "パーツの修理",
        "Bauteile reparieren",
        "Réparer des éléments",
        "修理配件",
        "부품 수리",
    ];

    //Use <If(Equal(IntegerParameter(4),1))>your last <SheetEn(Item,3,IntegerParameter(2),1,1)/><Else/><Value>IntegerParameter(3)</Value> of your <Value>IntegerParameter(4)</Value> <SheetEn(Item,3,IntegerParameter(2),2,1)/></If> to repair your vessel's <SheetEn(Item,3,IntegerParameter(1),1,1)/>?
    //6587	<If(Equal(IntegerParameter(3),1))><Clickable(<SheetDe(Item,2,IntegerParameter(2),1,4,1)/>)/><Else/><Value>IntegerParameter(3)</Value> <SheetDe(Item,5,IntegerParameter(2),2,4,1)/></If> (Besitz: <Value>IntegerParameter(4)</Value>) benutzen, um <SheetDe(Item,2,IntegerParameter(1),1,4,1)/> zu reparieren?
    //6587	Utiliser <If(Equal(IntegerParameter(3),1))><SheetFr(Item,1,IntegerParameter(2),1,1)/><Else/><Value>IntegerParameter(3)</Value> <SheetFr(Item,12,IntegerParameter(2),2,1)/></If> pour réparer <SheetFr(Item,2,IntegerParameter(1),1,1)/> de votre appareil<Indent/>? (<Value>IntegerParameter(4)</Value> possédé<If(LessThanOrEqualTo(IntegerParameter(4),1))><Else/>s</If>)
    /*6587	下記のアイテムを修理しますか？
    <Sheet(Item,IntegerParameter(1),0)/>
    消費:<Sheet(Item,IntegerParameter(2),0)/>×<Value>IntegerParameter(3)</Value>(所持数 <Value>IntegerParameter(4)</Value>)
    */

    internal static readonly string[] WorkshopRepairConfirm =
        [
            "repair",
            "下記のアイテムを修理しますか",
            "reparieren",
            "réparer",
            "要修理下列部件吗",
            "要修理下列部件嗎",
            "要修理下列元件嗎",
            "要修理下列組件嗎",
            "수리하시겠습니까?",
        ];

    // Use the components selected and <If(Equal(IntegerParameter(1),1))>the following item<Else/><Value>IntegerParameter(1)</Value> of the following items</If> to outfit and register your submersible?
    /* 6886 Das Tauchboot mit den gewählten Bauteilen registrieren?
     Verbraucht <Value>IntegerParameter(1)</Value> <If(Equal(IntegerParameter(1),1))>Exemplar<Else/>Exemplare</If> des folgenden Gegenstands:
    */
    // 6886 Utiliser les éléments choisis et <If(Equal(IntegerParameter(1),1))>l'objet suivant<Else/><Value>IntegerParameter(1)</Value> des objets suivants</If> pour équiper et enregistrer le sous-marin<Indent/>?
    /*選択したパーツアイテムと以下のアイテムを
       <Value>IntegerParameter(1)</Value>枚消費して潜水艦を登録します。
       よろしいですか？
    */

    internal static readonly string[] WorkshopRegisterConfirm =
    [
            "to outfit and register your submersible",
            "枚消費して潜水艦を登録します",
            "Das Tauchboot mit den gewählten Bauteilen registrieren",
            "pour équiper et enregistrer le sous-marin",
            "잠수함을 등록하시겠습니까",
            // 6886 台服繁中原文：確定要使用選中的配件與<數量>張下列道具登記新的潛水艇嗎？
            // 數量在中間，所以錨點取數字之後的穩定尾段（MiniTA 走 ContainsAny 子字串比對）。
            "张下列道具登记新的潜水艇吗",
            "張下列道具登記新的潛水艇嗎",
            //""    Missing Korean-variant placeholder (Addonsheet - 6886)
    ];

    //Your retainer will be unable to process item buyback requests once recalled. Are you sure you wish to proceed?
    //215	TEXT_CMNDEFRETAINERCALL_00010_ASK_RETURN_WITH_BUYBACK
    // 📌 台服 7.20 sqpack 實查:「讓僱員返回後將無法購回委託賣掉的道具，」+換行+「確定要繼續嗎？」。
    //    比對端 Utils.GetSpecificYesno 走 ContainsAny,錨點只取第一行,避開換行段。
    internal static string[] WillBeUnableToProcessBuyback => field ??= [FirstLine(BellText(215, "讓僱員返回後將無法購回委託賣掉的道具"))];

    internal static readonly string[] LogInPartialText = ["Logging in with", "Log in with", "でログインします。", "einloggen?", "eingeloggt.", "Se connecter avec", "Vous allez vous connecter avec", "Souhaitez-vous vous connecter avec", "登入吗", "登入嗎", "登录吗", "접속하시겠습니까?"];

    //3290	<Sheet(Item,IntegerParameter(1),0)/>×<Value>IntegerParameter(2)</Value>を、<Format(IntegerParameter(3),FF022C)/>枚の軍票と交換します。
    //よろしいですか？
    //3290	<Format(IntegerParameter(3),FF022E)/> Staatstaler gegen <If(Equal(IntegerParameter(2),1))><SheetDe(Item,1,IntegerParameter(1),1,4,1)/><Else/><Format(IntegerParameter(2),FF022E)/> <SheetDe(Item,5,IntegerParameter(1),2,4,1)/></If> eintauschen?
    //3290	Acheter <Value>IntegerParameter(2)</Value> <SheetFr(Item,12,IntegerParameter(1),IntegerParameter(2),1)/> pour <Format(IntegerParameter(3),FF05021D0103)/> sceau<If(LessThanOrEqualTo(IntegerParameter(3),1))><Else/>x</If><Indent/>?

    internal static readonly string[] GCSealExchangeConfirm = ["Exchange", "よろしいですか？", "Staatstaler gegen", "Acheter", "要交换吗", "要交換嗎", "교환하시겠습니까"];
}
