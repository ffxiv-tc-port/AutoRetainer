using AutoRetainer.Internal;
using AutoRetainer.Internal.InventoryManagement;
using AutoRetainer.Scheduler.Handlers;
using AutoRetainer.Scheduler.Tasks;
using AutoRetainerAPI.Configuration;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons.ExcelServices;
using ECommons.GameHelpers;
using ECommons.IPC;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace AutoRetainer.Modules.GcHandin;

internal static unsafe partial class GCExpertDeliveryLoop
{
    /// <summary>有沒有指定「鈴在哪」。設了就代表使用者已經挑好目的地 ——
    /// 🔴 這種時候絕對不可以退回泛用的移動指令:那個指令會把人送到它自己的預設地點
    /// (實測被送到烏爾達哈),而流程接著會在錯的城市裡找鈴。停下來比亂傳好。
    /// <para>⚠️ 一律以**現在登入的角色**去查。收藏項的清單是 Lifestream 依當前角色的傳送面板建出來的,
    /// 上一個角色的目的地對這個角色可能根本不存在(自己的房屋、不同的大國防聯軍城市)。</para></summary>
    internal static bool HasBellTarget => GetBellDestination(Player.CID).Id != 0;

    internal static bool HasGCTarget => GetGCDestination(Player.CID).Id != 0;

    /// <summary>叫 Lifestream 走到某個收藏項。回 false 代表**什麼都沒排進去**,呼叫端要立刻停,
    /// 不要等一個永遠不會來的完成訊號。</summary>
    private static bool GoToFavorite(uint id, byte sub, string what)
    {
        if(id == 0) return false;
        bool ok;
        try
        {
            ok = S.LifestreamExtra.TeleportToFavorite(id, sub);
        }
        catch(Exception e)
        {
            // 舊版 Lifestream 沒有這個門。
            PluginLog.Information($"[ExpertDeliveryLoop] TeleportToFavorite is unavailable: {e.Message}");
            return false;
        }
        if(!ok)
        {
            PluginLog.Information($"[ExpertDeliveryLoop] Lifestream refused to travel to the {what} favourite (id={id}, sub={sub}) - it may have been unstarred, or Lifestream is busy.");
            return false;
        }
        PluginLog.Information($"[ExpertDeliveryLoop] Travelling to the {what} favourite (id={id}, sub={sub}).");
        NavigationDeadline = Environment.TickCount64 + NavigationGraceMs;
        return true;
    }

    /// <summary>目前構得到、離玩家最近的傳喚鈴。
    /// <para>📌 刻意沒有「指定座標挑鈴」:移動一律走使用者選的 Lifestream 我的最愛,而最愛本來就是
    /// 使用者自己加星號的定點 —— Lifestream 把人放到那裡之後,當前區域裡最近的那個鈴就是他要的那個。</para></summary>
    internal static IGameObject GetPreferredBell()
    {
        if(Player.Object is null) return null;

        IGameObject best = null;
        var bestScore = float.MaxValue;

        foreach(var x in Svc.Objects)
        {
            if(x.ObjectKind != ObjectKind.Housing && x.ObjectKind != ObjectKind.EventObj) continue;
            if(!Utils.ObjectNameEqualsAny(x, Lang.BellName)) continue;
            if(!x.IsTargetable) continue;
            if(Vector3.Distance(x.Position, Player.Object.Position) >= Utils.GetValidInteractionDistance(x)) continue;

            var score = Vector3.Distance(x.Position, Player.Object.Position);
            if(score < bestScore)
            {
                bestScore = score;
                best = x;
            }
        }
        return best;
    }

    /// <summary>診斷：抵達軍團那一刻，最近的傳喚鈴（不限構不構得到）與人事官、軍需官的距離。
    /// 每個角色每趟只印一次；任何失敗都只記一行，不影響後面的繳交。</summary>
    private static void LogGcArrivalOnce()
    {
        try
        {
            if(Player.Object is null || GcArrivalLoggedCID == Player.CID) return;
            GcArrivalLoggedCID = Player.CID;
            var me = Player.Object.Position;

            IGameObject bell = null;
            var bellDist = float.MaxValue;
            foreach(var x in Svc.Objects)
            {
                if(x.ObjectKind != ObjectKind.Housing && x.ObjectKind != ObjectKind.EventObj) continue;
                if(!Utils.ObjectNameEqualsAny(x, Lang.BellName)) continue;
                if(!x.IsTargetable) continue;
                var d = Vector3.Distance(x.Position, me);
                if(d < bellDist)
                {
                    bellDist = d;
                    bell = x;
                }
            }

            var gc = GCContinuation.GetGCInfo();
            var officerDist = -1f;
            var shopDist = -1f;
            if(gc != null)
            {
                foreach(var x in Svc.Objects)
                {
                    if(!x.IsTargetable) continue;
                    if(x.BaseId == gc.Value.ExchangeDataID) officerDist = Vector3.Distance(x.Position, me);
                    else if(x.BaseId == gc.Value.ShopDataID) shopDist = Vector3.Distance(x.Position, me);
                }
            }

            string bellText;
            if(bell == null)
            {
                bellText = "物件表裡沒有傳喚鈴";
            }
            else
            {
                var reach = Utils.GetValidInteractionDistance(bell);
                bellText = $"{bellDist:F2}m（{bell.ObjectKind} 位置 <{bell.Position.X:F2}, {bell.Position.Y:F2}, {bell.Position.Z:F2}>，互動門檻 {reach:F2}m，現在構得到={bellDist < reach}）";
            }
            PluginLog.Information($"[ExpertDeliveryLoop] [GCArrival] 抵達軍團：區域 {Player.Territory} 位置 <{me.X:F2}, {me.Y:F2}, {me.Z:F2}>；最近的傳喚鈴 {bellText}；人事官(id {gc?.ExchangeDataID}) {(officerDist < 0 ? "不在物件表" : $"{officerDist:F2}m")}、軍需官(id {gc?.ShopDataID}) {(shopDist < 0 ? "不在物件表" : $"{shopDist:F2}m")}（兩者互動門檻 10m）；第 {HandinRounds} 輪，鈴邊→這裡 {TimeInPhase}ms。");
        }
        catch(Exception e)
        {
            PluginLog.Information($"[ExpertDeliveryLoop] [GCArrival] 診斷失敗（不影響繳交）：{e.Message}");
        }
    }

    private static void TickEnsureBell()
    {
        if(Utils.IsBusy) return;

        if(GetPreferredBell() != null)
        {
            if(BellTravelsThisRound > 0)
            {
                var dest = GetBellDestination(Player.CID);
                BellDestinationsProvenThisRun.Add((dest.Id, dest.Sub));
                if(BellTravelsThisRound > 1) PluginLog.Information($"[ExpertDeliveryLoop] 重送傳送後找到傳喚鈴了(鈴目的地 id={dest.Id} sub={dest.Sub}),繼續。");
            }
            BellTravelsThisRound = 0;
            BellTravelOccupiedSince = 0;
            RetainerIndex = 0;
            RetrievedThisRound = 0;
            RoundBlockedUnique = 0;
            RoundEndReason = RoundEnd.NoGearLeft;
            AfterBellOpen = Phase.SelectRetainer;
            SetPhase(Phase.OpenBell);
            return;
        }

        // 🔴 沒指定目的地就沒有移動手段,而且這是刻意的:不退回泛用移動指令。
        //    那條退路會把人送到指令自己的預設地點(實測是烏爾達哈),流程接著在錯的城市裡找鈴 ——
        //    使用者看到的是「跑去一個沒要求的地方然後說找不到鈴」,比直接停下來難懂得多。
        //    停下來,並且把「該去哪裡設定」講清楚。
        if(!HasBellTarget)
        {
            PluginLog.Information($"[ExpertDeliveryLoop] No bell in reach and no bell destination configured (ExpertDeliveryLoopBellFavoriteId=0). Stopping - travel needs a Lifestream favourite.");
            Stop(Loc.T("Stopped: no summoning bell in reach. Choose a summoning bell destination in this flow's settings - the list comes from your Lifestream teleport panel favourites."));
            return;
        }

        // 使用者指定了目的地:一律走它,而且**只走它**。
        var bell = GetBellDestination(Player.CID);
        if(BellTravelsThisRound >= MaxBellTravelsPerRound)
        {
            // Lifestream 的傳送鏈中途逾時會整條中止、不留訊號給呼叫端,所以「傳送結束卻沒鈴」會重送;
            // 重送有上限,用完才停。
            var arrived = LooksArrivedAtBellDestination(bell.Id, bell.Sub, out var diag);
            var stillBusy = IsOccupied();
            PluginLog.Information($"[ExpertDeliveryLoop] 送了 {BellTravelsThisRound} 次傳送仍沒有構得到的傳喚鈴,停止:判定={(stillBusy ? "角色仍在忙碌／對話中,傳送一直被拒" : arrived ? "已到目的地、那裡沒有鈴" : "傳送沒到目的地")};{diag}");
            Stop(stillBusy
                ? Loc.T("Stopped: the game kept refusing the teleport to the chosen summoning bell destination because the character was still busy (an event, dialog or exchange window is probably still open). Close it and start the loop again.")
                : arrived
                    ? Loc.T("Stopped: arrived at the chosen destination but there is no summoning bell within reach. Pick a favourite that is closer to a bell.")
                    : Loc.T("Stopped: travelling to the chosen summoning bell destination did not get there, even after retrying - Lifestream was probably interrupted on the way. The destination itself does not need changing; start the loop again."));
            return;
        }

        var now = Environment.TickCount64;
        // 重送前先隔一段時間;拒絕原因(Occupied／對話)通常不是幾百毫秒內會消失的。
        if(BellTravelsThisRound > 0 && now - LastBellTravelAt < BellResendGapMs) return;
        // 遊戲在 Occupied／對話／事件中會拒絕傳送(狀態 579),所以送之前先等它清掉;逾時照送,不卡死。
        if(IsOccupied())
        {
            if(BellTravelOccupiedSince == 0)
            {
                BellTravelOccupiedSince = now;
                PluginLog.Information($"[ExpertDeliveryLoop] 角色仍在忙碌／對話中(傳送會被遊戲拒絕),先等它結束再送傳送;已送 {BellTravelsThisRound} 次,最多等 {BellTravelOccupiedWaitMs / 1000} 秒。");
            }
            if(now - BellTravelOccupiedSince < BellTravelOccupiedWaitMs) return;
            PluginLog.Information($"[ExpertDeliveryLoop] 等了 {(now - BellTravelOccupiedSince) / 1000} 秒角色仍在忙碌中,照樣送傳送(第 {BellTravelsThisRound + 1}/{MaxBellTravelsPerRound} 次)。");
        }
        else if(BellTravelOccupiedSince != 0)
        {
            PluginLog.Information($"[ExpertDeliveryLoop] 角色已不在忙碌狀態(等了 {(now - BellTravelOccupiedSince) / 1000} 秒),送傳送(第 {BellTravelsThisRound + 1}/{MaxBellTravelsPerRound} 次)。");
        }
        BellTravelOccupiedSince = 0;

        if(BellTravelsThisRound > 0)
        {
            LooksArrivedAtBellDestination(bell.Id, bell.Sub, out var resendDiag);
            PluginLog.Information($"[ExpertDeliveryLoop] 傳送結束後附近沒有構得到的傳喚鈴,重送傳送(第 {BellTravelsThisRound + 1}/{MaxBellTravelsPerRound} 次);{resendDiag}");
        }
        if(!GoToFavorite(bell.Id, bell.Sub, "bell"))
        {
            Stop(Loc.T("Stopped: could not travel to the chosen summoning bell destination - check that it is still starred in Lifestream."));
            return;
        }
        LastBellTravelAt = now;
        BellTravelsThisRound++;
        SetPhase(Phase.EnsureBellWait);
    }

    /// <summary>傳送結束卻沒鈴時,判斷是「到了但那裡沒鈴」還是「傳送沒到」。
    /// 這趟同一個目的地成功過、或人不在目的地所在的區域＝傳送沒到;其餘(含查不到目的地區域)＝到了。</summary>
    private static bool LooksArrivedAtBellDestination(uint id, byte sub, out string diag)
    {
        var proven = BellDestinationsProvenThisRun.Contains((id, sub));
        uint destTerritory = 0;
        foreach(var f in global::AutoRetainer.UI.NeoUI.InventoryManagementEntries.GCDeliveryEntries.ExpertDeliveryLoop.GetFavorites())
        {
            if(f.Id == id && f.SubIndex == sub) { destTerritory = f.Territory; break; }
        }
        var here = (uint)Player.Territory;
        var wrongZone = destTerritory != 0 && destTerritory != here;
        var pos = Player.Object?.Position ?? default;
        diag = $"鈴目的地 id={id} sub={sub} 區域 {(destTerritory == 0 ? "?" : destTerritory)};現在區域 {here} 位置 <{pos.X:F1}, {pos.Y:F1}, {pos.Z:F1}>;這趟此目的地成功過={proven}";
        return !proven && !wrongZone;
    }

    private static void TickEnsureBellWait()
    {
        if(!ChainFinished) return;
        SetPhase(Phase.EnsureBell);
    }

    private static void TickOpenBell()
    {
        if(Utils.IsBusy) return;

        // 已經在僱員清單裡就不用再點鈴一次。
        if(RetainerListLoaded && TryGetAddonByName<AtkUnitBase>("RetainerList", out var list) && IsAddonReady(list))
        {
            // 清單不是這次點鈴叫出來的，件數可能是更早那次開鈴的快照。
            BellOpenedByLoop = false;
            ListCalibratedThisBell = false;
            SetPhase(AfterBellOpen);
            return;
        }

        // 剛結束導航的那幾幀角色可能還在位移或演出中,這時候送互動等於浪費一發。
        if(!Player.Interactable) return;

        PluginLog.Information($"[ExpertDeliveryLoop] Interacting with the summoning bell.");
        BellOpenedByLoop = true;
        ListCalibratedThisBell = false;
        TaskInteractWithNearestBell.Enqueue();
        BellInteractAttempts = 1;
        LastBellInteractAt = Environment.TickCount64;
        SetPhase(Phase.OpenBellWait);
    }

    private static void TickOpenBellWait()
    {
        // 閘門是「清單真的載入了」,不是「佇列排空了」。兩者差了整整一段遊戲自己的載入時間。
        // 探險收尾另外要等清單視窗就緒：它的下一步直接列舉到期僱員，沒有選僱員任務替它等視窗。
        if(RetainerListLoaded && (AfterBellOpen == Phase.SelectRetainer || IsRetainerListReady()))
        {
            SetPhase(AfterBellOpen);
            return;
        }

        // 互動放槍是沉默的:沒有任何回報說「這一發沒中」,只會表現成清單一直不出現。
        // 所以在等待期間有界地重送,每一次都講出來,才分得出「還在載入」與「根本沒開起來」。
        var now = Environment.TickCount64;
        if(!Utils.IsBusy && Player.Interactable
            && BellInteractAttempts < MaxBellInteractAttempts
            && now - LastBellInteractAt > BellInteractRetryMs)
        {
            BellInteractAttempts++;
            PluginLog.Information($"[ExpertDeliveryLoop] Retrying the summoning bell interaction (attempt {BellInteractAttempts}/{MaxBellInteractAttempts}) - the retainer list has still not loaded after {now - LastBellInteractAt}ms.");
            TaskInteractWithNearestBell.Enqueue();
            LastBellInteractAt = now;
            return;
        }

        if(TimeInPhase > RetainerListLoadTimeoutMs)
        {
            // ⚠️ 走到這裡只說明清單沒載入,**不代表任何一個僱員不存在**。
            //    這條路徑刻意不產生任何「已經不存在」的訊息 —— 那會把載入問題講成資料問題。
            PluginLog.Information($"[ExpertDeliveryLoop] Giving up: the retainer list did not load after {BellInteractAttempts} bell interaction(s) over {TimeInPhase}ms.");
            Stop(Loc.T("Stopped: the retainer list did not load after using the summoning bell."));
        }
    }
}
