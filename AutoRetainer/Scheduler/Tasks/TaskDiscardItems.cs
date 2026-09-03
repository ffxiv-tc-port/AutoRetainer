using AutoRetainer.Internal.InventoryManagement;
using Dalamud.Game.ClientState.Conditions;
using ECommons.ExcelServices;
using ECommons.Throttlers;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace AutoRetainer.Scheduler.Tasks;

/// <summary>
/// 手動觸發的道具丟棄。
/// 🔴 丟棄是**永久損失**，所以下面每一道閘門都是承重牆，動任何一道之前先想清楚：
/// ① 功能預設關（<see cref="InventoryManagementSettings.IMEnableItemDiscard"/>）
/// ② 道具必須在**專屬**的丟棄清單裡，不共用任何賣出清單
/// ③ 保護清單一律優先，且在排入與執行前各查一次
/// ④ 只確認**我們自己剛送出**的那個對話框（時間窗 + 文字雙重比對）
/// ⑤ 讀不到的容器／格位一律跳過
/// 🔑 整體設計成「**只可能少丟、不可能多丟**」——少丟＝使用者再按一次按鈕就好；
/// 多丟＝道具永遠回不來。任何取捨都往「少丟」那邊倒。
/// 🔴 本檔不掛任何自動觸發點（尤其不掛 RequestCharacterPostprocess）：只由使用者按鈕觸發。
/// </summary>
public static unsafe class TaskDiscardItems
{
    /// <remarks>
    /// 刻意只含背包四格，**不含裝備庫**：誤丟裝備的代價遠高於材料，
    /// 而賣出功能之所以有 AllowSellFromArmory 是因為賣掉還能從商店買回，丟棄不行。
    /// </remarks>
    private static InventoryType[] DiscardableInventories => Utils.PlayerInvetories;

    /// <summary>
    /// 我們自己送出 DiscardItem 的截止時刻。只有在這個時間窗內才允許按下「是」，
    /// 避免把使用者自己開的其他「捨棄」類對話框（素材超上限、放生陸行鳥…）誤按掉。
    /// </summary>
    private static long ConfirmWindowUntil;

    private const int ConfirmWindowMS = 5000;

    /// <summary>
    /// 「捨棄」這個動詞直接取自遊戲自己的 Addon 表（台服 7.20 為 row 91＝「捨棄」），
    /// 不寫死字面值，換語言／改版都跟著遊戲走。
    /// ⚠️ 確認框本文是 row 110，但它含參數佔位符（道具名與數量），
    /// 逐字比對必定落空，所以改用 row 91 這個**無佔位符**的動詞做包含比對。
    /// </summary>
    private static string DiscardVerb
    {
        get
        {
            if(_discardVerb != null) return _discardVerb;
            // 🔴 Lumina 的 GetRow 查無此列是擲 ArgumentOutOfRangeException 而不是回 null。
            if(Svc.Data.GetExcelSheet<Addon>().TryGetRow(91, out var row))
            {
                _discardVerb = row.Text.GetText().Cleanup();
            }
            return _discardVerb ?? "";
        }
    }

    private static string _discardVerb;

    /// <summary>
    /// 本輪已經試過但沒成功的格位。⚠️ 沒有這個的話會出現一種很難查的卡死：
    /// 送出 DiscardItem 之後確認框始終沒出現（遊戲基於我們沒預期到的理由拒絕），
    /// 時間窗一過就又挑到同一件，於是每 5 秒重試一次、整整空轉 10 分鐘的時限。
    /// 放棄的是「這一輪的這一格」，使用者再按一次按鈕就會重新嘗試。
    /// </summary>
    private static readonly HashSet<(InventoryType Type, uint Slot)> FailedThisRun = [];

    private static (InventoryType Type, uint Slot) LastAttempt;
    private static int LastAttemptCount;

    private const int MaxAttemptsPerSlot = 3;

    public static void Enqueue()
    {
        ConfirmWindowUntil = 0;
        FailedThisRun.Clear();
        LastAttempt = default;
        LastAttemptCount = 0;
        P.TaskManager.Enqueue(DiscardNextItem, new(timeLimitMS: 10 * 60 * 1000, abortOnTimeout: false));
        P.TaskManager.Enqueue(() => !Svc.Condition[ConditionFlag.Occupied39]);
    }

    /// <remarks>
    /// ⚠️ ECommons TaskManager 的 catch 是「記一行就丟掉這個工作繼續跑佇列」，
    /// 在 task 裡 throw 等於帶著壞狀態靜默往下跑 ⇒ 這裡永遠回 <c>false</c>（再試一次）
    /// 或 <c>true</c>（本工作結束），絕不擲例外。
    /// </remarks>
    private static bool? DiscardNextItem()
    {
        var s = Data.GetIMSettings();
        if(!s.IMEnableItemDiscard) return true;

        // ── 閘門④：只確認自己剛送出的那個對話框 ──
        if(Environment.TickCount64 < ConfirmWindowUntil)
        {
            var verb = DiscardVerb;
            if(verb.Length > 0)
            {
                var yesno = Utils.GetSpecificYesno(t => t.Cleanup().Contains(verb, StringComparison.OrdinalIgnoreCase));
                // 🔴 下一件的 ConfirmWindowUntil 重設後,GetSpecificYesno 可能命中前一扇仍在關閉中的窗;
                //    200ms 節流在低 FPS 時短於關閉窗口。同一扇只按一次。
                if(yesno != null && IsAddonReady(yesno) && EzThrottler.Throttle("DiscardConfirm", 200)
                    && DialogGuards.TryPressOnce("SelectYesno", (nint)yesno, "DiscardConfirm"))
                {
                    new AddonMaster.SelectYesno((nint)yesno).Yes();
                    ConfirmWindowUntil = 0;
                }
            }
            return false;
        }

        if(!TryFindNextDiscardable(out var type, out var slot, out var itemId, out var quantity))
        {
            return true;
        }

        // ── 預覽模式：只列出會丟什麼，不動任何道具 ──
        // 這裡直接結束工作（回 true），否則永遠找得到同一件道具會無限迴圈。
        if(s.IMDry)
        {
            foreach(var line in EnumerateDiscardable())
            {
                DuoLog.Warning($"> IMDry > Would discard {line}");
            }
            return true;
        }

        if(Utils.AnimationLock != 0 || IsOccupied())
        {
            Utils.RethrottleGeneric();
            return false;
        }

        if(!(Utils.GenericThrottle && EzThrottler.Throttle("DiscardItem", 1000))) return false;

        // 🔴 重新解析一次容器與格位，指標只在這一格程式碼裡活著，絕不跨幀保存。
        // 排入與執行之間容器可能已經換過（自動賣出、僱員存取…），所以要對「當下」再驗一次。
        var cont = InventoryManager.Instance()->GetInventoryContainer(type);
        if(cont == null || cont->Items == null) return false;
        if(slot >= cont->Size) return false;
        var item = cont->GetInventorySlot((int)slot);
        if(item == null) return false;
        if(item->ItemId != itemId || item->Quantity != quantity)
        {
            // 這一格在我們決定要丟之後被動過了 —— 放棄這一輪，下一輪重新挑。
            PluginLog.Warning($"Discard target slot changed, skipping ({type}#{slot})");
            return false;
        }
        // ── 閘門③：保護清單在執行前再查一次（設定可能在排入後才被改動）──
        if(s.IMProtectList.Contains(item->ItemId))
        {
            PluginLog.Warning($"Item {ExcelItemHelper.GetName(item->ItemId)} is protected and won't be discarded.");
            return false;
        }

        // ── 同一格連續試不成就本輪放棄，避免確認框始終不出現時空轉到時限 ──
        if(LastAttempt == (type, slot))
        {
            LastAttemptCount++;
            if(LastAttemptCount > MaxAttemptsPerSlot)
            {
                // 這行刻意是 Information：使用者跑 LogLevel 1，盲區只有 Verbose,Debug 收得到但單檔數十萬行會淹沒，
                // 而「為什麼有東西沒被丟掉」正是最需要使用者回報的資訊。
                PluginLog.Information($"Giving up on {ExcelItemHelper.GetName(itemId)} [{type}#{slot}] after {MaxAttemptsPerSlot} attempts (no confirmation dialog appeared). Press the button again to retry.");
                FailedThisRun.Add((type, slot));
                LastAttempt = default;
                LastAttemptCount = 0;
                return false;
            }
        }
        else
        {
            LastAttempt = (type, slot);
            LastAttemptCount = 1;
        }

        // AgentInventoryContext.Instance() 是產生器產出的兩層可空取得器，合法回 null。
        // 拿不到就這輪不丟（不開確認視窗、不寫記錄）——「少丟一件」永遠比「對 null 解參考」好，
        // 而且與本檔既有的「讀不到的容器一律跳過 ⇒ 只可能少列」是同一個保守方向。
        var invCtx = AgentInventoryContext.Instance();
        if(invCtx == null)
        {
            PluginLog.Information("AgentInventoryContext 尚未就緒，這一輪不丟棄道具。");
            return false;
        }
        PluginLog.Information($"Discarding {ExcelItemHelper.GetName(item->ItemId)}x{item->Quantity} [Container={type},Slot={slot}]");
        // 第 4 引數 addonId＝0：不綁定任何擁有者 addon（我們不是從背包 UI 的右鍵選單發起的）。
        // 第 5 引數 position 是 YesNoPosition，-1＝讓遊戲用預設位置；明寫出來不靠預設值，
        // 免得日後有人以為這個參數有別的語意。
        invCtx->DiscardItem(item, type, (int)slot, 0, -1);
        ConfirmWindowUntil = Environment.TickCount64 + ConfirmWindowMS;
        InventorySpaceManager.Log.Add($"[{DateTime.Now}] Discarded {ExcelItemHelper.GetName(itemId)}x{quantity} on {Data.Name}");
        return false;
    }

    /// <remarks>
    /// 讀不到的容器／格位一律跳過 ⇒ **只可能少列、不可能多列**。少列＝這件這輪不丟，
    /// 使用者再按一次就補得回來；多列才有代價（丟到不該丟的），而跳過永遠不會造成多列。
    /// ⚠️ 解參考 null 在 .NET Core 是 corrupted-state exception，try/catch 攔不到，只能靠事前檢查。
    /// </remarks>
    private static bool TryFindNextDiscardable(out InventoryType type, out uint slot, out uint itemId, out int quantity)
    {
        type = default; slot = 0; itemId = 0; quantity = 0;
        var im = InventoryManager.Instance();
        if(im == null) return false;
        foreach(var invType in DiscardableInventories)
        {
            var cont = im->GetInventoryContainer(invType);
            if(cont == null || cont->Items == null) continue;
            for(var i = 0; i < cont->Size; i++)
            {
                var item = cont->GetInventorySlot(i);
                if(item == null) continue;
                if(item->ItemId == 0) continue;
                if(FailedThisRun.Contains((invType, (uint)i))) continue;
                if(!IsDiscardable(item->ItemId)) continue;
                type = invType;
                slot = (uint)i;
                itemId = item->ItemId;
                quantity = item->Quantity;
                return true;
            }
        }
        return false;
    }

    /// <summary>預覽用：列出目前所有會被丟棄的道具，不動任何狀態。</summary>
    private static List<string> EnumerateDiscardable()
    {
        List<string> result = [];
        var im = InventoryManager.Instance();
        if(im == null) return result;
        foreach(var invType in DiscardableInventories)
        {
            var cont = im->GetInventoryContainer(invType);
            if(cont == null || cont->Items == null) continue;
            for(var i = 0; i < cont->Size; i++)
            {
                var item = cont->GetInventorySlot(i);
                if(item == null || item->ItemId == 0) continue;
                if(!IsDiscardable(item->ItemId)) continue;
                result.Add($"{ExcelItemHelper.GetName(item->ItemId)}x{item->Quantity} [{invType}#{i}]");
            }
        }
        return result;
    }

    /// <summary>預覽用的公開入口：讓 UI 在使用者按下按鈕前就能顯示「會丟掉這些」。</summary>
    public static List<string> PreviewDiscardable() => EnumerateDiscardable();

    /// <summary>
    /// UI 每幀都會呼叫，所以刻意不配置字串、只數數量。
    /// ⚠️ 這在 ImGui 的 Draw 路徑上執行 —— 一旦擲例外，Dalamud 會把整個外掛的
    /// Draw 設成 null，介面到重開遊戲前都回不來，所以裡面只做有事前檢查的讀取。
    /// </summary>
    public static int CountDiscardable()
    {
        var count = 0;
        var im = InventoryManager.Instance();
        if(im == null) return 0;
        foreach(var invType in DiscardableInventories)
        {
            var cont = im->GetInventoryContainer(invType);
            if(cont == null || cont->Items == null) continue;
            for(var i = 0; i < cont->Size; i++)
            {
                var item = cont->GetInventorySlot(i);
                if(item == null || item->ItemId == 0) continue;
                if(IsDiscardable(item->ItemId)) count++;
            }
        }
        return count;
    }

    /// <summary>
    /// 單一道具是否可丟。閘門①②③都在這裡。
    /// ⚠️ 這裡拿到的 itemId 來自 <c>InventoryItem.ItemId</c>，**未正規化**（HQ 會是 +1,000,000）。
    /// 設定清單存的是正規化後的 id，所以先取基礎 id 再比對，否則 HQ 道具會永遠比不中。
    /// </summary>
    private static bool IsDiscardable(uint rawItemId)
    {
        var s = Data.GetIMSettings();
        if(!s.IMEnableItemDiscard) return false;

        var itemId = rawItemId % 1000000;
        if(itemId == 0) return false;
        if(s.IMProtectList.Contains(itemId)) return false;
        if(!s.IMAutoDiscardList.Contains(itemId)) return false;

        // 遊戲本身就拒絕丟棄的道具（任務道具、獨佔品…）先擋掉，免得對話框永遠不出現而空轉。
        if(!ExcelItemHelper.Get(itemId).TryGetValue(out var data)) return false;
        if(data.IsIndisposable) return false;

        return true;
    }
}
