using AutoRetainer.Internal;
using AutoRetainer.Modules.GcHandin;
using AutoRetainer.Services;
using AutoRetainerAPI.Configuration;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Text.SeStringHandling;
using ECommons.Configuration;
using ECommons.Events;
using ECommons.ExcelServices;
using ECommons.GameFunctions;
using ECommons.GameHelpers;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using Lumina.Excel.Sheets;

namespace AutoRetainer.Modules;

internal static unsafe class OfflineDataManager
{
    internal static void EnqueueWriteWhenPlayerAvailable()
    {
        P.ODMTaskManager.Abort();
        P.ODMTaskManager.Enqueue(() =>
        {
            if(!Player.Available) return false;
            WriteOfflineData(false, false);
            return true;
        });
    }

    internal static void Tick()
    {
        if(Svc.Condition[ConditionFlag.OccupiedSummoningBell])
        {
            WriteOfflineData(false, false);
            if(EzThrottler.Throttle("Periodic.CalculateItemLevel") && Utils.TryGetCurrentRetainer(out var ret))
            {
                var adata = Utils.GetAdditionalData(Player.CID, ret);
                var result = Helpers.ItemLevel.Calculate(out var g, out var p);
                if(result != null)
                {
                    adata.Ilvl = result.Value;
                    adata.Gathering = g;
                    adata.Perception = p;
                }
            }
        }
        if((MultiMode.Active || AutoGCHandin.Operation || Utils.IsBusy || P.AutoRetainerWindow.IsOpen || Svc.Condition[ConditionFlag.LoggingOut] || Svc.Condition[ConditionFlag.OccupiedSummoningBell]) && EzThrottler.Throttle("Periodic.WriteOfflineData", 1000))
        {
            WriteOfflineData(false, EzThrottler.Throttle("Periodic.SaveData", 1000 * 60 * 5));
        }
    }

    internal static void CreateLoggedOutOfflineData(string name, uint world, ulong cid)
    {
        if(C.Blacklist.Any(x => x.CID == cid)) return;
        if(!C.OfflineData.TryGetFirst(x => x.CID == cid, out var data))
        {
            data = new()
            {
                CID = cid,
            };
            C.OfflineData.Add(data);
        }
        data.World = ExcelWorldHelper.GetName(world);
        data.Name = name;
    }

    internal static void WriteOfflineData(bool writeGatherables, bool saveConfig)
    {
        if(!ProperOnLogin.PlayerPresent) return;
        if(C.Blacklist.Any(x => x.CID == Svc.PlayerState.ContentId)) return;
        if(Svc.Condition[ConditionFlag.DutyRecorderPlayback]) return;
        if(!C.OfflineData.TryGetFirst(x => x.CID == Svc.PlayerState.ContentId, out var data))
        {
            data = new()
            {
                CID = Svc.PlayerState.ContentId,
            };
            C.OfflineData.Add(data);
        }
        data.World = ExcelWorldHelper.GetName(Svc.Objects.LocalPlayer.HomeWorld.RowId);
        data.Name = Svc.Objects.LocalPlayer.Name.ToString();
        if(Player.Object.CurrentWorld.RowId != Player.Object.HomeWorld.RowId)
        {
            data.WorldOverride = Player.CurrentWorld;
        }
        else
        {
            data.WorldOverride = null;
        }
        // 🔴 2026-08-01 崩潰防護：這個函式整條都在讀原生單例，但原本一個 null 檢查都沒有。
        // 實機在**登入瞬間**吃到 AccessViolationException 而整個遊戲關閉
        // （crash-20260801022458，堆疊：WriteOfflineData ← EnqueueWriteWhenPlayerAvailable
        //  ← NeoTaskManager.Tick）。⚠️ 無法從 dump 指認是哪一處解參考出事——
        // Dalamud 的崩潰處理器會自己丟一個 0x12345679 的標記例外，dump 是在那個 handler 裡
        // 抓的而不是 AV 現場，`.ecxr` 拿到的是 RaiseException 不是原始錯誤位址。
        // 既然指認不了，就把每一處都補上：假設錯了也不會崩。
        //
        // ⚠️ AccessViolationException 在 .NET Core 是 corrupted-state exception，
        // try/catch 攔不到，所以只能靠事前檢查，不能靠例外處理。
        var inventoryManager = InventoryManager.Instance();
        var uiState = UIState.Instance();
        if(inventoryManager == null || uiState == null) return;

        data.Gil = (uint)inventoryManager->GetInventoryItemCount(1);
        data.ClassJobLevelArray = uiState->PlayerState.ClassJobLevels.ToArray();
        if(writeGatherables)
        {
            try
            {
                data.UnlockedGatheringItems.Clear();
                foreach(var x in Svc.Data.GetExcelSheet<GatheringItem>())
                {
                    if(P.Memory.IsGatheringItemGathered(x.RowId))
                    {
                        data.UnlockedGatheringItems.Add(x.RowId);
                    }
                }
            }
            catch(Exception e)
            {
                e.Log();
            }
        }
        if(GameRetainerManager.Ready && GameRetainerManager.Count > 0 && Player.IsInHomeWorld)
        {
            var cleared = false;
            for(var i = 0; i < GameRetainerManager.Count; i++)
            {
                var ret = GameRetainerManager.Retainers[i];
                if(ret.RetainerID == 0) continue;
                if(!ret.Available) continue;
                if(ret.RetainerID != 0 && !cleared)
                {
                    data.RetainerData.Clear();
                    cleared = true;
                }
                data.RetainerData.Add(new()
                {
                    Name = ret.Name.ToString(),
                    VentureEndsAt = ret.VentureCompleteTimeStamp,
                    HasVenture = ret.VentureID != 0,
                    Level = ret.Level,
                    Job = ret.ClassJob,
                    VentureID = ret.VentureID,
                    Gil = ret.Gil,
                    RetainerID = ret.RetainerID,
                    MBItems = ret.MarkerItemCount,
                    // 僱員自己的背包佔用格數。跟上面的 Gil／MBItems 來自同一份僱員清單資料，
                    // 所以不需要開過該僱員就有值；沒被寫過的舊資料會停在 -1 = 「不知道」。
                    ItemCount = ret.ItemCount,
                });
            }
        }
        if(Player.IsInHomeWorld && Player.Available)
        {
            // 🔴 部隊資訊代理在登入初期可能還沒建好。原本 fc 完全沒驗就直接 fc->Id，
            // 而且 `data.FCID = fc->Id` 那行連前面條件的短路都保護不到。
            var infoModule = InfoModule.Instance();
            var fc = infoModule == null ? null : infoModule->GetInfoProxyFreeCompany();
            if(fc == null) return;

            if(Player.Object.Struct()->FreeCompanyTagString != "" && (fc->Id == 0 || fc->NameString == "")) return;
            data.FCID = fc->Id;
            if(!C.FCData.ContainsKey(fc->Id)) C.FCData[fc->Id] = new();
            C.FCData[fc->Id].Name = fc->NameString;

            var uiModule = UIModule.Instance();
            var atkModule = uiModule == null ? null : uiModule->GetRaptureAtkModule();
            // 同樣是 7.2 → 7.3 的 +1 位移：上游寫死的 58 在台服 7.20 指到的是
            // ContentsFinderConfirm，部隊金幣在 FreeCompanyChest（59）。
            // 這不是外推值 —— 出貨的 CS 直接把 59 命名為 FreeCompanyChest，58 命名為
            // ContentsFinderConfirm，兩個名字都在同一份列舉裡，而那份列舉已含 7.3 插入的
            // CastBarEnemy。一樣引用列舉不寫死數字。
            var numArray = atkModule == null ? null : atkModule->AtkModule.GetNumberArrayData(
                (int)FFXIVClientStructs.FFXIV.Component.GUI.NumberArrayType.FreeCompanyChest);

            // 🔴 原本只驗 numArray != null 就直接索引第 354 格 —— 只有 null 檢查、
            // **完全沒有長度檢查**。這跟 BossModReborn 那個實機爆 2823 次的半套邊界檢查
            // 是同一個形狀：陣列在登入初期可能還沒配置到那麼長，讀 IntArray[354]
            // （偏移 1416 位元組）就會跨出去。AtkArrayData.Size 就是為此存在的。
            const int FcGilIndex = 354;
            if(numArray != null && numArray->IntArray != null && numArray->Size > FcGilIndex)
            {
                var gil = numArray->IntArray[FcGilIndex];
                // 值本身也要合理才採用。負數代表讀到的不是金幣（陣列選錯或還沒填），
                // 這種時候寧可讓部隊金幣維持舊值不更新，也不要寫一個假數字進設定檔。
                if(gil >= 0 && (gil != 0 || S.FCPointsUpdater?.IsFCChestReady() == true))
                {
                    C.FCData[fc->Id].Gil = gil;
                    C.FCData[fc->Id].LastGilUpdate = DateTimeOffset.Now.ToUnixTimeMilliseconds();
                }
            }
            if(Utils.FCPoints != 0)
            {
                C.FCData[fc->Id].FCPoints = Utils.FCPoints;
                C.FCData[fc->Id].FCPointsLastUpdate = DateTimeOffset.Now.ToUnixTimeMilliseconds();
            }
        }
        data.WriteOfflineInventoryData();
        data.WriteOfflineAllowanceData();
        C.OfflineData.RemoveAll(x => x.World == "" && x.Name == "Unknown");
        if(saveConfig) EzConfig.Save();
    }

    /// <remarks>
    /// 🔴 這四個值全部無條件覆寫，而它們的來源在容器讀不到的時候一律**靜默回 0**：
    /// <c>GetInventoryItemCount</c> 與 <see cref="Utils.GetInventoryFreeSlotCount"/> 都是「讀不到就跳過、
    /// 繼續累加」，所以「還沒載入」與「真的是 0」在呼叫端完全同形。而換區、登入初期、多角模式換角當下
    /// 都會踩到這個窗口，偏偏這個函式正是在那些時機被呼叫的（登入、ConditionChange）。
    ///
    /// 寫進去的 0 不是只影響顯示：它會存進設定檔，並在角色離線時被當成真值使用——
    /// 自動購買燃料的觸發條件讀的就是 <c>Data.Ceruleum</c>，多角模式的排程讀 <c>Ventures</c> 與
    /// <c>InventorySpace</c>。一個假的 0 會讓「該買」「該跑」的判斷全部歪掉，而且沒有任何徵兆。
    ///
    /// 所以讀不到就整組不覆寫，維持上一次讀到的舊值——與同檔上面部隊金幣（<c>gil &gt;= 0</c> 才採用）
    /// 是同一個保守策略。舊值只是過期，假的 0 是錯的；下一次讀得到時自然會補上。
    /// </remarks>
    internal static void WriteOfflineInventoryData(this OfflineCharacterData data)
    {
        if(!Utils.IsInventoryStateReadable()) return;
        data.Ventures = Utils.GetVenturesAmount();
        data.InventorySpace = (uint)Utils.GetInventoryFreeSlotCount();
        data.Ceruleum = InventoryManager.Instance()->GetInventoryItemCount(AutoBuyFuelManager.FuelItemId);
        data.RepairKits = InventoryManager.Instance()->GetInventoryItemCount(10373);
        // 僱員的寶箱（32161）。這個欄位同樣是從加進型別以來就沒人寫過，補上：
        // 來源與上面三個完全同形（同一個容器、同一個閘門），不需要額外的取樣時間戳。
        data.VentureCoffers = (uint)InventoryManager.Instance()->GetInventoryItemCount(VentureCofferItemId);
    }

    private static bool LoggedLeveFailure;
    private static bool LoggedCustomDeliveryFailure;
    private static bool LoggedTomestoneFailure;
    private static bool LoggedGCSealsFailure;

    /// <summary>理符受理限額的遊戲內上限。超過這個數就代表讀到的不是這個欄位。</summary>
    private const int MaxLevequestAllowances = 100;
    /// <summary>籌備委託品的每週滿額次數。與 FFXIVClientStructs 的
    /// <c>SatisfactionSupplyManager.GetRemainingAllowances()</c> 內部寫死的 12 同源，
    /// 這裡只拿來當合理範圍檢查，不拿來算剩餘次數。</summary>
    private const int MaxCustomDeliveryAllowances = 12;
    /// <summary>僱員的寶箱。與 <see cref="AutoRetainer.Scheduler.Tasks.TaskOpenAllCoffers"/> 用的是同一個道具 ID。</summary>
    private const uint VentureCofferItemId = 32161;
    /// <summary>大國防聯軍的最大 id（GrandCompany 表：0 平民／1 黑渦團／2 雙蛇黨／3 不滅隊）。</summary>
    private const byte MaxGrandCompanyId = 3;

    /// <remarks>
    /// 每日／每週配額（理符受理限額、籌備委託品、限定神典石）的登入快照。刻意與
    /// <see cref="WriteOfflineInventoryData"/> 在同一個時點呼叫，但保守策略要再走一步：
    /// 那邊可以用「讀不讀得到背包」當閘門，這邊三個值的 <b>0 都是合法值</b>（額度用完就是 0），
    /// 所以不能靠值判斷有沒有讀到，只能靠結構就緒與否——讀不到就整組不寫，
    /// 維持上一次的值與時間戳（過期的真值好過寫死的假 0）。
    ///
    /// 🔴 登入後太早讀會拿到零：角色資料是登入後才由伺服器補齊的，而這個函式的呼叫時機
    /// （登入、ConditionChange、每秒週期）正好蓋在那個窗口上。這裡拿
    /// <see cref="Utils.IsInventoryStateReadable"/> 當就緒判準——它驗的是四個背包容器已經配置
    /// 且有內容，那份資料與配額走同一批登入封包，所以「背包可讀」是現成的代理指標。
    /// ⚠️ 那是<b>代理</b>不是保證，離線證不了兩者一定同時到齊。假設不成立的後果是「某次登入
    /// 寫進一個偏低的值」而不是崩潰——下一次讀到就蓋回去，而且時間戳會誠實反映它是何時取的。
    ///
    /// 🔴 三組來源都是 FFXIVClientStructs 的簽章式函式，簽章解不出來時 CS 擲的是
    /// InvalidOperationException（受管理例外，這裡攔得到；不是 AccessViolation）。
    /// 所以每一組各自 try/catch：一組失效不該讓另外兩組也讀不到。
    /// 📌 2026-09-08 用 tools/sigscan/verify_cs_sigs.py 對台服 7.20 的 ffxiv_dx11.exe 離線驗過，
    /// 五個相關簽章（QuestManager.Instance、SatisfactionSupplyManager.Instance 與
    /// GetUsedAllowances、GetLimitedTomestoneCount、GetSpecialItemId）全部在 .text 唯一命中。
    /// </remarks>
    internal static void WriteOfflineAllowanceData(this OfflineCharacterData data)
    {
        if(!Svc.PlayerState.IsLoaded) return;
        if(!Utils.IsInventoryStateReadable()) return;
        var now = DateTime.Now;

        try
        {
            var questManager = QuestManager.Instance();
            if(questManager != null && questManager->NumLeveAllowances <= MaxLevequestAllowances)
            {
                data.LevequestAllowances = questManager->NumLeveAllowances;
                data.LevequestAllowancesUpdatedAt = now;
            }
        }
        catch(Exception e)
        {
            LogAllowanceFailureOnce(ref LoggedLeveFailure, "levequest allowances", e);
        }

        try
        {
            var satisfaction = SatisfactionSupplyManager.Instance();
            if(satisfaction != null)
            {
                var remaining = satisfaction->GetRemainingAllowances();
                if(remaining >= 0 && remaining <= MaxCustomDeliveryAllowances)
                {
                    data.CustomDeliveryAllowances = remaining;
                    data.CustomDeliveryAllowancesUpdatedAt = now;
                }
            }
        }
        catch(Exception e)
        {
            LogAllowanceFailureOnce(ref LoggedCustomDeliveryFailure, "custom delivery allowances", e);
        }

        try
        {
            var cap = GetLimitedTomestoneWeeklyCap();
            var inventoryManager = InventoryManager.Instance();
            if(cap > 0 && inventoryManager != null)
            {
                var count = inventoryManager->GetWeeklyAcquiredTomestoneCount();
                if(count >= 0 && count <= cap)
                {
                    data.WeeklyTomestoneCount = count;
                    data.WeeklyTomestoneCap = cap;
                    data.WeeklyTomestoneUpdatedAt = now;
                }
            }
        }
        catch(Exception e)
        {
            LogAllowanceFailureOnce(ref LoggedTomestoneFailure, "weekly tomestones", e);
        }

        // 軍票。🔴 這一組不需要自己去碰原生層：AutoGCHandin 早就有 GetGC／GetSeals／GetMaxSeals／
        // GetRank 四個現成的取得器（軍票繳交循環一直在用），只是從來沒有人把結果寫進離線快照。
        // 📌 相關的兩個 FFXIVClientStructs 簽章（GetCompanySeals／GetMaxCompanySeals）2026-09-08 已用
        //    tools/sigscan/verify_cs_sigs.py 對台服 7.20 執行檔驗過，皆在 .text 唯一命中。
        try
        {
            var grandCompany = AutoGCHandin.GetGC();
            if(grandCompany == 0)
            {
                // 平民（GrandCompany 表 row 0）。「沒有軍票這回事」與「還沒讀到」是兩件事，
                // 所以這裡照樣蓋時間戳並把上限寫成 0，讓顯示端畫得出第三種狀態。
                data.GCSeals = 0;
                data.GCSealsMax = 0;
                data.GCRank = 0;
                data.GCSealsUpdatedAt = now;
            }
            else if(grandCompany <= MaxGrandCompanyId)
            {
                var maxSeals = AutoGCHandin.GetMaxSeals();
                // 已加入大國防聯軍卻讀到上限 0 ＝ 原生狀態還沒就緒。寧可整組不寫，
                // 也不要在列上顯示一個分母是 0 的比值（與同檔上面「讀不到就不覆寫」同一個策略）。
                if(maxSeals > 0)
                {
                    data.GCSeals = AutoGCHandin.GetSeals();
                    data.GCSealsMax = (int)maxSeals;
                    data.GCRank = AutoGCHandin.GetRank();
                    data.GCSealsUpdatedAt = now;
                }
            }
        }
        catch(Exception e)
        {
            LogAllowanceFailureOnce(ref LoggedGCSealsFailure, "grand company seals", e);
        }
    }

    private static int LimitedTomestoneWeeklyCap;

    /// <remarks>
    /// 🔴 上限不寫死：台服 7.20 目前的限定神典石是「亞拉戈數理神典石」、每週 450，
    /// 但這個數字每個大版本都會換一次，寫死的下場是靜默沿用上一個版本的值。
    /// <c>Tomestones</c> 資料表裡只有「限定神典石」那一列的 <c>WeeklyLimit</c> 不是 0，
    /// 資料表自己就說得出答案，不需要外部知識。
    /// ⚠️ 版本交接期理論上可能同時有兩列不是 0，取 RowId 最大的那一列（＝比較新的那一階）。
    /// 📌 命中之後才快取：資料表在外掛載入初期可能還沒準備好，把 0 latch 起來會讓這個功能
    /// 整個 session 都不動——這與同檔上面「讀不到就不覆寫」是同一個保守方向。
    /// </remarks>
    private static int GetLimitedTomestoneWeeklyCap()
    {
        if(LimitedTomestoneWeeklyCap > 0) return LimitedTomestoneWeeklyCap;
        var sheet = Svc.Data.GetExcelSheet<Tomestones>();
        if(sheet == null) return 0;
        foreach(var x in sheet)
        {
            if(x.WeeklyLimit > 0) LimitedTomestoneWeeklyCap = x.WeeklyLimit;
        }
        return LimitedTomestoneWeeklyCap;
    }

    /// <remarks>同一個錯誤一次 session 只記一次：這條路徑每秒都會走到，簽章真的失效時
    /// 無節流的 log 會把實機記錄洗掉。用 <c>Svc.Log.Information</c> 是因為這是要使用者回報的
    /// 診斷等級；不用 <c>DuoLog</c>——它每一級都會無條件印進聊天視窗。</remarks>
    private static void LogAllowanceFailureOnce(ref bool latch, string what, Exception e)
    {
        if(latch) return;
        latch = true;
        Svc.Log.Information($"[AutoRetainer] Failed to read {what}; this is logged only once per session: {e.Message}");
    }
    internal static OfflineRetainerData GetData(SeString name, ulong? CID = null)
    {
        return GetData(name.ToString(), CID);
    }

    internal static OfflineRetainerData GetData(string name, ulong? CID = null)
    {
        var cid = CID ?? Svc.PlayerState.ContentId;
        if(C.OfflineData.TryGetFirst(x => x.CID == cid, out var data) && data.RetainerData.TryGetFirst(x => x.Name == name, out var rdata))
        {
            return rdata;
        }
        return null;
    }
}
