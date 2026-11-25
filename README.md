# AutoRetainer

[AutoRetainer](https://github.com/NightmareXIV/AutoRetainer) 的台服移植版。僱員探險與飛空艇／潛水艇遠航探索自動化插件，並提供多角色切換管理。

## 功能

- **僱員探險**：一鍵重新指派所有僱員的探險任務，自動處理各種確認視窗。
- **遠航探索**：自動管理飛空艇／潛水艇航行，內建最佳航線規劃器與解鎖航線規劃器。
- **多角色模式**：可依角色設定切換不同角色，自動處理僱員與遠航探索，並支援排程登入切換角色（Relog）；另有夜間模式（預設只處理遠航探索）。
- **販售清單 / 委託清單**：自訂清單，自動把雜物賣給 NPC，或把貴重物品委託給僱員保管以節省背包空間。
- **籌備稀有品**：在大國防聯軍自動繳交籌備稀有品，並照兌換清單花掉軍票。
- **探險瀏覽器**：列出各種僱員探險與帶回的物品，方便挑選要指派的探險。
- **定時關機**：可設定倒數時間後自動關閉遊戲。
- **統計**：記錄僱員探險、Gil 與公會戰績等歷史數據。
- **疑難排解分頁**：內建診斷工具協助排除設定問題。

## 指令

- `/autoretainer`（別名 `/ays`）：開啟／關閉主視窗。
- `/autoretainer s|settings`：開啟設定視窗。
- `/autoretainer e|enable`、`d|disable`、`t|toggle`：啟用／停用／切換插件。
- `/autoretainer m|multi`：切換多角色模式（`m e`／`m d` 指定開或關）。
- `/autoretainer n|night`：切換夜間模式（`n e`／`n d` 指定開或關）。
- `/autoretainer relog 角色名@伺服器名`：切換登入到指定角色。
- `/autoretainer b|browser`：開啟探險瀏覽器。
- `/autoretainer l|log`：開啟記錄視窗。
- `/autoretainer debug`：切換除錯選單與詳細輸出。
- `/autoretainer shutdown <小時> [分] [秒]`：排程指定時間後關閉遊戲（不帶時間＝取消）。
- `/autoretainer itemsell`：開始把物品賣給 NPC 或僱員。
- `/autoretainer het`：進入附近自己的房屋或公寓。
- `/autoretainer wet`：進入公會工坊。
- `/autoretainer reset`：重置所有待處理工作。
- `/autoretainer deliver`：前往大國防聯軍繳交籌備稀有品（需要 Lifestream）。

## 台服版的修改

- **跑一輪就回來**：主視窗多角色模式旁的按鈕，按下才開始：開多角色模式處理完到期的僱員、潛水艇與飛空艇後，自動換回按下時的角色並關閉多角色模式。等待途中你關掉多角色模式或自己換角就取消；按鈕不能按時，滑鼠移上去會說明原因。其他插件與巨集也能經 IPC（`AutoRetainer.RoundTrip.*`）開始、查進度、查結果，並只取消自己開的那一趟。
- **稀有品繳交循環**：手動按「開始稀有品繳交循環」才會動，把指定存放計畫底下僱員身上的裝備取出、拿去大國防聯軍繳交，取完為止；可設定自動使用軍票預支單。卸載或重新載入插件時會停下。
- **多角色模式先去交稀有品**：可設定探險幣或背包空格剩下幾個以下時，先去大國防聯軍籌備稀有品再照常處理，並可選交完傳送回家（預設關）。
- **軍票兌換**：交完稀有品後照兌換清單換物品；「保留軍票數量」可以依角色的軍票上限分別設定。換到一半你離開了主城，這一輪就直接停下，不會把你帶回去，也不會在別的區域操作視窗。
- **自動補充燃料**：桶裝青磷水低於門檻時自動用公會戰績買到指定數量（預設關）。
- **存放計畫** 新增「裝備類別只存放可繳交的稀有品」，時裝留在身上。
- **角色列額度欄位**：角色列表可顯示軍票、理符受理限額、籌備委託品、本週神典石，快滿時在列上就看得見；每一欄可各自關閉。另有 E 欄（稀有品存放容量：掛著稀有品存放計畫的僱員已用／總格數，以及大約幾小時後會滿）與 G 欄（角色的 Gil，偏低時變色；預設關）。
- **顯示項目**：設定新增「顯示項目」頁，僱員管理、遠航探索與公會資訊各頁的欄位、圖示與進度條可以逐項隱藏（預設全部顯示）。
- **異常彙整**：多角色模式途中有角色被排除、遊戲設定被改或流程被停止時，集中列在「AutoRetainer 偵測到異常」視窗，跑完回來一次看完。
- **公會購地提醒**：沒有房屋的公會成立滿 30 天（天數可調）時通知可以購買土地；公會資訊分頁顯示成立日期與公會房屋狀態，不知道時照實顯示不知道。
- **探險瀏覽器**：有啟用 GatherBuddy Reborn 時顯示「採集」按鈕（漁師僱員的探險改查釣魚）；TC Toolbox 的「僱員探險收益」模組開著時，「查詢價格」改開它的視窗並定位到該物品。
- **多角色模式選項**：可設定「僱員與遠航探索都沒啟用的角色不自動進入房屋」（預設關）；進屋連續逾時 3 次就放棄這次登入的進屋，不再無限重試。
- **伺服器資訊欄按鈕**：左鍵切換多角色模式，右鍵開關主視窗；主視窗不透明度可調。
- **跨插件整合**：換角前先請 Questionable 停止自動任務；潛水艇點位計畫可向 Submarine Tracker 查「這件物品在哪撈」一鍵填入；僱員 Gil 存提先通知 GilDelta 記成正確來源；探險與遠航返航時間到、稀有品循環跑完時請 TataruPraise 念一句；其他插件要求急停時，連稀有品繳交循環、「跑一輪就回來」與自動籌備稀有品一起停下，之後也不會自己接手僱員或恢復 Artisan。
- **台服相容性修正**：特徵碼、資料表欄位、繁中遊戲文字比對、伺服器清單與新海域的潛水艇航線計算；遊戲改版後有特徵碼對不上時插件照常載入，只停掉對應的功能。
- 多處防閃退修正（確認框連按、登入與換角途中讀寫原生資料）與多處降低記憶體用量。
- 介面繁體中文化。

## 安裝

1. 在遊戲內輸入 `/xlsettings` 開啟「Dalamud 設定」，到「試驗性功能」分頁的「第三方插件倉庫」（Custom Plugin Repositories）貼上
   `https://raw.githubusercontent.com/ffxiv-tc-port/DalamudPluginsTC/main/repo.json`
   勾選啟用後按「儲存變更」。
2. 輸入 `/xlplugins` 開啟「插件管理員」（Plugin Installer），搜尋「AutoRetainer」安裝。

請不要安裝上游（國際服）版本，它不是對著台服客戶端做的。

## 需要搭配的插件

- 選用：[Lifestream](https://github.com/ffxiv-tc-port/Lifestream)（`deliver` 指令、傳送回家）、[Artisan](https://github.com/ffxiv-tc-port/Artisan)、[Questionable](https://github.com/ffxiv-tc-port/Questionable)、[Submarine Tracker](https://github.com/ffxiv-tc-port/SubmarineTracker)、[GilDelta](https://github.com/ffxiv-tc-port/GilDelta)、[TataruPraise](https://github.com/ffxiv-tc-port/TataruPraise)、[GatherBuddy Reborn](https://github.com/ffxiv-tc-port/GatherBuddyReborn)、[TC Toolbox](https://github.com/ffxiv-tc-port/TCToolbox)（探險瀏覽器的按鈕）。

原作者：[NightmareXIV](https://github.com/NightmareXIV/AutoRetainer)

授權：BSD-3-Clause（見 LICENCE.md）
