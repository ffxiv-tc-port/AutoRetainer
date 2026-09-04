# AutoRetainer

雇員委託與飛艇／潛水艇派遣自動化插件，並提供多帳號切換管理。

原作者：[NightmareXIV](https://github.com/NightmareXIV/AutoRetainer)

## 功能

- **雇員委託**：一鍵重新指派所有雇員的委託任務，自動處理各種確認視窗。
- **派遣船塢**：自動管理飛艇／潛水艇航行，內建最佳航線規劃器與解鎖航線
  規劃器，可自動補充燃料。
- **多帳號模式**：可依角色設定切換不同帳號，自動處理雇員與船塢，並支援
  排程登入切換角色（Relog）。
- **販售清單 / 委託清單**：自訂清單，自動把雜物賣給 NPC，或把貴重物品
  委託給雇員保管以節省背包空間。
- **大國防聯軍軍需自動繳交**：自動從雇員取出裝備並繳交軍需品換取軍票。
- **定時關機**：可設定倒數時間後自動關閉遊戲。
- **統計**：記錄雇員派遣、金幣與公會點數等歷史數據。
- **疑難排解分頁**：內建診斷工具協助排除設定問題。

## 指令

- `/autoretainer`（別名 `/ays`）：開啟主視窗。
- `/autoretainer e|enable`、`d|disable`、`t|toggle`：啟用／停用／切換插件。
- `/autoretainer m|multi`：切換多帳號模式。
- `/autoretainer relog 角色名@伺服器名`：切換登入到指定角色。
- `/autoretainer b|browser`：開啟委託瀏覽器。
- `/autoretainer expert`：切換進階設定顯示。
- `/autoretainer debug`：切換除錯選單與詳細輸出。
- `/autoretainer shutdown <小時> [分] [秒]`：排程指定時間後關閉遊戲。
- `/autoretainer itemsell`：開始把物品賣給 NPC 或雇員。
- `/autoretainer het`：進入附近自己的房屋或公寓。
- `/autoretainer reset`：重置所有待處理工作。
- `/autoretainer deliver`：執行客戶委託（專家配送）物品交付。
