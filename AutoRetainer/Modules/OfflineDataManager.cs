using AutoRetainer.Internal;
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
        if(C.Blacklist.Any(x => x.CID == Svc.ClientState.LocalContentId)) return;
        if(Svc.Condition[ConditionFlag.DutyRecorderPlayback]) return;
        if(!C.OfflineData.TryGetFirst(x => x.CID == Svc.ClientState.LocalContentId, out var data))
        {
            data = new()
            {
                CID = Svc.ClientState.LocalContentId,
            };
            C.OfflineData.Add(data);
        }
        data.World = ExcelWorldHelper.GetName(Svc.ClientState.LocalPlayer.HomeWorld.RowId);
        data.Name = Svc.ClientState.LocalPlayer.Name.ToString();
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
            var numArray = atkModule == null ? null : atkModule->AtkModule.GetNumberArrayData(58);

            // 🔴 原本只驗 numArray != null 就直接索引第 354 格 —— 只有 null 檢查、
            // **完全沒有長度檢查**。這跟 BossModReborn 那個實機爆 2823 次的半套邊界檢查
            // 是同一個形狀：陣列在登入初期可能還沒配置到那麼長，讀 IntArray[354]
            // （偏移 1416 位元組）就會跨出去。AtkArrayData.Size 就是為此存在的。
            const int FcGilIndex = 354;
            if(numArray != null && numArray->IntArray != null && numArray->Size > FcGilIndex)
            {
                var gil = numArray->IntArray[FcGilIndex];
                if(gil != 0 || S.FCPointsUpdater?.IsFCChestReady() == true)
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

    internal static void WriteOfflineInventoryData(this OfflineCharacterData data)
    {
        data.Ventures = Utils.GetVenturesAmount();
        data.InventorySpace = (uint)Utils.GetInventoryFreeSlotCount();
        data.Ceruleum = InventoryManager.Instance()->GetInventoryItemCount(10155);
        data.RepairKits = InventoryManager.Instance()->GetInventoryItemCount(10373);
    }

    internal static OfflineRetainerData GetData(SeString name, ulong? CID = null)
    {
        return GetData(name.ToString(), CID);
    }

    internal static OfflineRetainerData GetData(string name, ulong? CID = null)
    {
        var cid = CID ?? Svc.ClientState.LocalContentId;
        if(C.OfflineData.TryGetFirst(x => x.CID == cid, out var data) && data.RetainerData.TryGetFirst(x => x.Name == name, out var rdata))
        {
            return rdata;
        }
        return null;
    }
}
