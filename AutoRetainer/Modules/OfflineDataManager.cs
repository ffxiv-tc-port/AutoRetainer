using AutoRetainer.Internal;
using AutoRetainer.Modules.GcHandin;
using AutoRetainer.Services;
using AutoRetainerAPI.Configuration;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.SubKinds;
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
        var data = FindOfflineData(Svc.PlayerState.ContentId);
        if(data == null)
        {
            data = new()
            {
                CID = Svc.PlayerState.ContentId,
            };
            C.OfflineData.Add(data);
        }
        // 📌 本 pin 的 IPlayerCharacter 包裝是「每格×每種 kind 預配一個、
        // 存取時就地改寫 Address」（Dalamud ObjectTable.cs），所以每寫一次
        // Svc.Objects.LocalPlayer 就重新解一次原生指標。同一格內取一次就好：
        // 既少兩次原生讀取，也讓「中途變 null」這件事不可能發生。
        var localPlayer = Svc.Objects.LocalPlayer;
        if(localPlayer == null) return;
        data.World = GetWorldName(localPlayer.HomeWorld.RowId);
        data.Name = GetLocalPlayerName(localPlayer);
        if(localPlayer.CurrentWorld.RowId != localPlayer.HomeWorld.RowId)
        {
            data.WorldOverride = Player.CurrentWorld;
        }
        else
        {
            data.WorldOverride = null;
        }
        // 🔴 這個函式整條都在讀原生單例：每一處都先判空，假設錯了也不會崩。
        // ⚠️ AccessViolationException 在 .NET Core 是 corrupted-state exception，try/catch 攔不到，所以只能靠事前檢查，不能靠例外處理。
        var inventoryManager = InventoryManager.Instance();
        var uiState = UIState.Instance();
        if(inventoryManager == null || uiState == null) return;

        // 🔴 這兩個欄位的來源在登入初期都回零，不能無條件覆寫。
        // ⚠️ 寫進去的零不是只影響顯示：該角色的練級探險靜默不再派出，而且零值會存進設定檔、在角色離線時被當成真值。
        // 所以讀不到就不寫，保留上一次的值（過期的真值好過寫死的假零）。
        var currency = inventoryManager->GetInventoryContainer(InventoryType.Currency);
        if(currency != null && currency->Items != null && currency->IsLoaded)
        {
            data.Gil = (uint)inventoryManager->GetInventoryItemCount(1);
            LoggedGilSkip = false;
        }
        else
        {
            LogSkipOnce(ref LoggedGilSkip, "gil (InventoryType.Currency container not loaded)");
        }

        // PlayerState.IsLoaded 是原生自己的就緒旗標（PlayerState+0x0），Dalamud 的
        // IPlayerState.ContentId 內部就是拿它當閘門。再加一道「至少有一個職業等級 > 0」：
        // 真實角色不可能整條全零，全零只代表原生還沒填。
        if(Svc.PlayerState.IsLoaded)
        {
            var classJobLevels = uiState->PlayerState.ClassJobLevels;
            var anyLevel = false;
            for(var i = 0; i < classJobLevels.Length; i++)
            {
                if(classJobLevels[i] > 0)
                {
                    anyLevel = true;
                    break;
                }
            }
            if(anyLevel)
            {
                WriteClassJobLevels(data, classJobLevels);
                LoggedClassJobSkip = false;
            }
            else
            {
                LogSkipOnce(ref LoggedClassJobSkip, "class job levels (PlayerState.ClassJobLevels still all zero)");
            }
        }
        else
        {
            LogSkipOnce(ref LoggedClassJobSkip, "class job levels (PlayerState.IsLoaded is false)");
        }
        // 查不到採集紀錄＝不知道：保留上次寫入的清單，不清空。
        if(writeGatherables && P.Memory.CanQueryGatheredItems)
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
            WriteRetainerData(data);
        }
        if(Player.IsInHomeWorld && Player.Available)
        {
            // 🔴 部隊資訊代理在登入初期可能還沒建好：fc 要先判空才能讀 fc->Id。
            var infoModule = InfoModule.Instance();
            var fc = infoModule == null ? null : infoModule->GetInfoProxyFreeCompany();
            if(fc == null) return;

            // 🔴 不走 FFXIVClientStructs 產生的 `*String` 屬性：那種讀法**完全沒有長度上界**。
            // 🔑 每一個 `*String` 都同時有一個**有界的 Span<byte>**版本（FreeCompanyTag / Name），讀那個就沒有無界掃描這回事。
            // ⚠️ 名字沒有終止符就代表那 22 bytes 還不是一個合法字串。沒終止符就不寫，保留上一次的名字。
            var fcName = fc->Name;
            var playerHasFCTag = localPlayer.Struct()->FreeCompanyTag[0] != 0;
            if(playerHasFCTag && (fc->Id == 0 || fcName[0] == 0)) return;
            data.FCID = fc->Id;
            if(!C.FCData.ContainsKey(fc->Id)) C.FCData[fc->Id] = new();
            if(fcName[0] != 0 && fcName.IndexOf((byte)0) >= 0)
            {
                // Span<byte>.Read() 是 ECommons 的有界讀取（只掃這 22 bytes）。
                WriteFCName(C.FCData[fc->Id], fcName);
                LoggedFCNameSkip = false;
            }
            else if(playerHasFCTag)
            {
                // 只在「玩家真的有部隊」時才記：沒部隊的角色名字空白是正常狀態，
                // 印成診斷會讓未來查 log 的人去跟一個不存在的問題。
                LogSkipOnce(ref LoggedFCNameSkip, "free company name (InfoProxyFreeCompany.Name is not a terminated string yet)");
            }

            var uiModule = UIModule.Instance();
            var atkModule = uiModule == null ? null : uiModule->GetRaptureAtkModule();
            // 7.2 → 7.3 在 CastBarEnemy 處插入一項、之後索引都 +1：上游寫死的 58 在台服指到的是
            // ContentsFinderConfirm，部隊金幣在 FreeCompanyChest（59）。引用出貨 CS 的列舉、不寫死數字，
            // 下次版本再位移時它會自己跟著動。
            var numArray = atkModule == null ? null : atkModule->AtkModule.GetNumberArrayData(
                (int)FFXIVClientStructs.FFXIV.Component.GUI.NumberArrayType.FreeCompanyChest);

            // 🔴 只驗 numArray != null 是半套邊界檢查：陣列在登入初期可能還沒配置到那麼長，
            // 讀 IntArray[354]（偏移 1416 位元組）就會跨出去。AtkArrayData.Size 就是為此存在的。
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

    /// <summary>等同 <c>C.OfflineData.TryGetFirst(x =&gt; x.CID == cid, ...)</c>（取第一個符合的），但不配置列舉器。</summary>
    private static OfflineCharacterData FindOfflineData(ulong cid)
    {
        foreach(var x in C.OfflineData)
        {
            if(x.CID == cid) return x;
        }
        return null;
    }

    // 以下兩個快取只給框架執行緒用（WriteOfflineData 的呼叫端都在框架執行緒上）。
    private static readonly Dictionary<uint, string> WorldNames = [];

    /// <summary>與 <c>ExcelWorldHelper.GetName(id)</c> 相同；伺服器名稱只取決於 id 與遊戲資料表，同一個 id 只取一次。</summary>
    private static string GetWorldName(uint id)
    {
        if(!WorldNames.TryGetValue(id, out var name))
        {
            name = ExcelWorldHelper.GetName(id);
            WorldNames[id] = name;
        }
        return name;
    }

    private static byte[] LocalPlayerNameBytes = [];
    private static string LocalPlayerName;

    /// <summary>與 <c>player.Name.ToString()</c> 相同。Dalamud 的 Name 是原生 GameObject.Name 那段位元組解析出來的，位元組沒變就沿用上一次的字串。</summary>
    private static string GetLocalPlayerName(IPlayerCharacter player)
    {
        var raw = ((FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)player.Address)->Name;
        if(LocalPlayerName != null && raw.SequenceEqual(LocalPlayerNameBytes)) return LocalPlayerName;
        var name = player.Name.ToString();
        if(LocalPlayerNameBytes.Length != raw.Length) LocalPlayerNameBytes = new byte[raw.Length];
        raw.CopyTo(LocalPlayerNameBytes);
        LocalPlayerName = name;
        return name;
    }

    /// <summary>等級沒變就沿用現有陣列，變了才換一份新的；存下的值與每次 <c>ToArray()</c> 相同。</summary>
    private static void WriteClassJobLevels(OfflineCharacterData data, Span<short> levels)
    {
        if(!levels.SequenceEqual(data.ClassJobLevelArray)) data.ClassJobLevelArray = levels.ToArray();
    }

    /// <summary>名字沒變就不重建字串（<see cref="NameMatches"/> 與 <c>Read()</c> 同一種解碼）。呼叫端保證 <paramref name="raw"/> 裡有 NUL。</summary>
    private static void WriteFCName(FCData fc, Span<byte> raw)
    {
        if(!NameMatches(fc.Name, raw)) fc.Name = raw.Read();
    }

    /// <remarks>
    /// 清單已經逐欄位等於這次會重建出的內容時整段不動，同一批物件留在清單裡；不同才照舊整份換新物件。
    /// 刻意不就地改欄位：Venture Planner／Browser 會跨幀拿著選到的那一筆，一向看到的是選取當時的值。
    /// </remarks>
    private static void WriteRetainerData(OfflineCharacterData data)
    {
        if(RetainerDataMatchesGame(data.RetainerData)) return;
        PreviousRetainerData.Clear();
        if(data.RetainerData != null) PreviousRetainerData.AddRange(data.RetainerData);
        var cleared = false;
        var retainers = GameRetainerManager.Retainers;
        for(var i = 0; i < retainers.Count; i++)
        {
            var ret = retainers[i];
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
                VentureBeginsAt = CarriedVentureBeginsAt(PreviousRetainerData, ret.RetainerID, ret.VentureCompleteTimeStamp),
            });
        }
        PreviousRetainerData.Clear();
    }

    // 重建前的舊清單，只給 WriteRetainerData 查舊值用（框架執行緒）；用完就清，不留住舊物件。
    private static readonly List<OfflineRetainerData> PreviousRetainerData = [];

    // 必須小於最短探險時長（60 分鐘），否則換了一趟卻漏掉 toast 時，舊一趟的開始時間會被帶過去。
    private const long VentureBeginsAtSlackSeconds = 10 * 60;

    /// <summary>
    /// 重建後這名僱員的 <c>VentureBeginsAt</c>：取 <paramref name="previous"/> 裡第一筆同 RetainerID 的舊值。
    /// 同一趟（<c>VentureEndsAt</c> 沒變）照帶；換了一趟時，只有「上一趟結束之後才記下的」屬於新的一趟，其餘歸零。
    /// 寬限值吸收 <c>P.Time</c> 走本機時鐘時與伺服器時間戳的誤差。
    /// </summary>
    private static long CarriedVentureBeginsAt(List<OfflineRetainerData> previous, ulong retainerId, long ventureEndsAt)
    {
        if(previous == null) return 0;
        OfflineRetainerData old = null;
        for(var i = 0; i < previous.Count; i++)
        {
            var x = previous[i];
            if(x != null && x.RetainerID == retainerId)
            {
                old = x;
                break;
            }
        }
        if(old == null || old.VentureBeginsAt == 0) return 0;
        if(old.VentureEndsAt == ventureEndsAt) return old.VentureBeginsAt;
        return old.VentureBeginsAt >= old.VentureEndsAt - VentureBeginsAtSlackSeconds ? old.VentureBeginsAt : 0;
    }

    /// <summary>
    /// <paramref name="list"/> 是否已經與 <see cref="WriteRetainerData"/> 此刻會重建出的清單逐欄位、同順序相等。
    /// 只在確定相等時回 true，不配置。
    /// </summary>
    /// <remarks><c>VentureBeginsAt</c> 用與重建同一支 <see cref="CarriedVentureBeginsAt"/> 算期望值，所以非 0 的值不會讓這條路失效。</remarks>
    private static bool RetainerDataMatchesGame(List<OfflineRetainerData> list)
    {
        if(list == null) return false;
        var m = RetainerManager.Instance();
        Span<int> order = stackalloc int[m->Retainers.Length];
        var listed = GameRetainerManager.GetListedIndices(order);
        var n = 0;
        for(var k = 0; k < listed; k++)
        {
            var ret = m->Retainers[order[k]];
            if(ret.RetainerId == 0 || !GameRetainerManager.IsAvailable(ref ret)) continue;
            if(n >= list.Count || !RetainerEntryMatches(list[n], ref ret, list)) return false;
            n++;
        }
        // 一名可用的僱員都沒有時，重建那段本來就不動清單。
        return n == 0 || n == list.Count;
    }

    private static bool RetainerEntryMatches(OfflineRetainerData d, ref RetainerManager.Retainer ret, List<OfflineRetainerData> list)
    {
        return d != null
            && d.RetainerID == ret.RetainerId
            && d.VentureEndsAt == ret.VentureComplete
            && d.HasVenture == (ret.VentureId != 0)
            && d.Level == ret.Level
            && d.Job == ret.ClassJob
            && d.VentureID == ret.VentureId
            && d.Gil == ret.Gil
            && d.MBItems == ret.MarketItemCount
            && d.ItemCount == ret.ItemCount
            && d.VentureBeginsAt == CarriedVentureBeginsAt(list, ret.RetainerId, ret.VentureComplete)
            && NameMatches(d.Name, ret.Name);
    }

    /// <summary>與 <see cref="GameRetainerManager.Retainer"/> 建構時的 <c>Read()</c> 同一種解碼（第一個 NUL 之前、UTF-8），只是不建字串。</summary>
    internal static bool NameMatches(string name, Span<byte> raw)
    {
        if(name == null) return false;
        var end = raw.IndexOf((byte)0);
        ReadOnlySpan<byte> bytes = end >= 0 ? raw[..end] : raw;
        if(Encoding.UTF8.GetCharCount(bytes) != name.Length) return false;
        Span<char> chars = stackalloc char[name.Length];
        Encoding.UTF8.GetChars(bytes, chars);
        return chars.SequenceEqual(name.AsSpan());
    }

    /// <remarks>
    /// 🔴 這幾個值的來源在容器讀不到的時候一律**靜默回 0**。
    /// 寫進去的 0 不是只影響顯示：它會存進設定檔，並在角色離線時被當成真值使用。
    /// 所以讀不到就整組不覆寫，維持上一次讀到的舊值。
    /// </remarks>
    internal static void WriteOfflineInventoryData(this OfflineCharacterData data)
    {
        if(!Utils.IsInventoryStateReadable()) return;
        data.Ventures = Utils.GetVenturesAmount();
        data.InventorySpace = (uint)Utils.GetInventoryFreeSlotCount();
        data.Ceruleum = InventoryManager.Instance()->GetInventoryItemCount(AutoBuyFuelManager.FuelItemId);
        data.RepairKits = InventoryManager.Instance()->GetInventoryItemCount(10373);
        // 僱員的寶箱（32161）。來源與上面幾個完全同形（同一個容器、同一個閘門），不需要額外的取樣時間戳。
        data.VentureCoffers = (uint)InventoryManager.Instance()->GetInventoryItemCount(VentureCofferItemId);
    }

    private static bool LoggedGilSkip;
    private static bool LoggedClassJobSkip;
    private static bool LoggedFCNameSkip;
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
    /// 每日／每週配額（理符受理限額、籌備委託品、限定神典石）的登入快照。
    /// 這邊三個值的 <b>0 都是合法值</b>（額度用完就是 0），只能靠結構就緒與否——讀不到就整組不寫，維持上一次的值與時間戳（過期的真值好過寫死的假 0）。
    /// ⚠️ 就緒閘門（<b>PlayerState.IsLoaded＋背包讀得到</b>）是代理不是保證，離線證不了這三組資料一定同時到齊。
    /// 🔴 三組來源都是 FFXIVClientStructs 的簽章式函式，一組失效不該讓另外兩組也讀不到。
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

        // 軍票。這一組不自己碰原生層：用 AutoGCHandin 現成的 GetGC／GetSeals／GetMaxSeals／GetRank 四個取得器。
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
    /// 🔴 上限不寫死：這個數字每個大版本都會換一次，寫死的下場是靜默沿用上一個版本的值。
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

    /// <remarks>「這一輪某個原生結構還沒就緒，所以那個欄位不覆寫」的診斷。
    /// <c>Information</c>：要使用者回報的診斷；
    /// 不用 <c>DuoLog</c> —— 它每一級都會無條件印進聊天視窗。
    /// 🔴 這條路徑每秒都會走到（Tick 的 1000ms 節流），所以用旗標壓成「每個略過的
    /// 區段只印一行」，並在該欄位真的寫成功時把旗標清掉 ⇒ 每一次登入最多各一行，
    /// 不會洗掉實機記錄。</remarks>
    private static void LogSkipOnce(ref bool latch, string what)
    {
        if(latch) return;
        latch = true;
        Svc.Log.Information($"[AutoRetainer] Offline data: skipped writing {what}; keeping the previous value.");
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
