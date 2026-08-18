using AutoRetainer.Internal;
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
        if(C.Blacklist.Any(x => x.CID == SvcEx.PlayerState.ContentId)) return;
        if(Svc.Condition[ConditionFlag.DutyRecorderPlayback]) return;
        if(!C.OfflineData.TryGetFirst(x => x.CID == SvcEx.PlayerState.ContentId, out var data))
        {
            data = new()
            {
                CID = SvcEx.PlayerState.ContentId,
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
    }

    internal static OfflineRetainerData GetData(SeString name, ulong? CID = null)
    {
        return GetData(name.ToString(), CID);
    }

    internal static OfflineRetainerData GetData(string name, ulong? CID = null)
    {
        var cid = CID ?? SvcEx.PlayerState.ContentId;
        if(C.OfflineData.TryGetFirst(x => x.CID == cid, out var data) && data.RetainerData.TryGetFirst(x => x.Name == name, out var rdata))
        {
            return rdata;
        }
        return null;
    }
}
