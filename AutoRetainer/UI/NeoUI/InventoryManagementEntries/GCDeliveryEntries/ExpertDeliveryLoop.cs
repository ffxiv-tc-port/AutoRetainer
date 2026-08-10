using AutoRetainer.Modules.GcHandin;

using ECommons.GameHelpers;

namespace AutoRetainer.UI.NeoUI.InventoryManagementEntries.GCDeliveryEntries;
public sealed unsafe class ExpertDeliveryLoop : InventoryManagemenrBase
{
    public override string Name { get; } = Loc.T("Grand Company Delivery/Expert Delivery Loop");

    public override void Draw()
    {
        ImGuiEx.TextWrapped(Loc.T("""
            Retrieves gear from the retainers you select, hands it in at the Grand Company, and repeats until the retainers have nothing left.

            This only ever runs while you press the button below - nothing starts it automatically.
            """));
        ImGui.Separator();

        DrawControls();
        ImGui.Separator();
        DrawSettings();
    }

    private void DrawControls()
    {
        if(GCExpertDeliveryLoop.Running)
        {
            if(ImGui.Button(Loc.T("Stop")))
            {
                GCExpertDeliveryLoop.Stop(Loc.T("Stopped by the user."));
            }
        }
        else
        {
            if(ImGui.Button(Loc.T("Start expert delivery loop")))
            {
                GCExpertDeliveryLoop.Start();
            }
        }

        ImGui.SameLine();
        ImGuiEx.Text(GCExpertDeliveryLoop.Running ? GCExpertDeliveryLoop.CurrentPhaseName : Loc.T("Idle"));

        // ⚠️ 讀不到的東西畫「?」不要畫 0 —— 把「不知道」顯示成 0 會直接誤導。
        var freeSlots = Player.Available ? Utils.GetInventoryFreeSlotCount().ToString() : "?";
        ImGuiEx.Text($"{Loc.T("Retrieved")}: {GCExpertDeliveryLoop.RetrievedTotal}    " +
            $"{Loc.T("Handin rounds")}: {GCExpertDeliveryLoop.HandinRounds}    " +
            $"{Loc.T("Free slots")}: {freeSlots}");

        if(!GCExpertDeliveryLoop.StatusText.IsNullOrEmpty())
        {
            ImGuiEx.TextWrapped(ImGuiColors.DalamudYellow, GCExpertDeliveryLoop.StatusText);
        }

        // 開始之前就讓使用者看到「會去找誰」,不要按下去才發現名單是空的。
        if(!GCExpertDeliveryLoop.Running)
        {
            var targets = GCExpertDeliveryLoop.ResolveRetainers();
            if(targets.Count == 0)
            {
                ImGuiEx.Text(ImGuiColors.DalamudRed, Loc.T("No retainers match the current selection."));
            }
            else
            {
                ImGuiEx.Text(ImGuiColors.DalamudGrey, $"{Loc.T("Will visit")}: {targets.Print()}");
            }
        }
    }

    private void DrawSettings()
    {
        ImGui.Checkbox(Loc.T("Select retainers manually"), ref C.ExpertDeliveryLoopManualRetainers);

        if(C.ExpertDeliveryLoopManualRetainers)
        {
            DrawManualRetainerPicker();
        }
        else
        {
            DrawEntrustPlanPicker();
        }

        ImGuiEx.SetNextItemWidthScaled(150);
        ImGui.SliderInt(Loc.T("Reserved inventory slots"), ref C.ExpertDeliveryLoopReservedSlots, 0, 30);
        // 這一行不是裝飾:設定值比 MultiMinInventorySlots 小的時候完全不會生效,
        // 而那看起來就像「設定壞掉了」。
        ImGuiEx.Text(ImGuiColors.DalamudGrey,
            string.Format(Loc.T("Effective reserve: {0} (the larger of this and Minimum inventory slots = {1})"),
                GCExpertDeliveryLoop.EffectiveReservedSlots, C.MultiMinInventorySlots));

        ImGui.Checkbox(Loc.T("Use Priority Seal Allowance when no seal bonus is active"), ref C.ExpertDeliveryLoopUseSealAllowance);
        if(C.ExpertDeliveryLoopUseSealAllowance)
        {
            ImGui.Indent();
            ImGui.Checkbox(Loc.T("Stop if the seal bonus cannot be applied"), ref C.ExpertDeliveryLoopStopWithoutSealBonus);
            ImGuiEx.Text(ImGuiColors.DalamudGrey, string.Format(Loc.T("Allowances held: {0}"),
                Player.Available ? GCExpertDeliveryLoop.GetSealAllowanceCount().ToString() : "?"));
            ImGui.Unindent();
        }

        ImGui.Separator();
        ImGuiEx.Text(ImGuiColors.DalamudWhite, Loc.T("Where to go"));
        ImGuiEx.TextWrapped(ImGuiColors.DalamudGrey, Loc.T("Pick destinations from your Lifestream teleport panel favourites. This is the reliable way: a favourite is a place you have already starred, so travelling there cannot wander off to some other city the way a generic travel command can."));

        DrawFavoritePicker(Loc.T("Summoning bell destination"),
            ref C.ExpertDeliveryLoopBellFavoriteId, ref C.ExpertDeliveryLoopBellFavoriteSub, ref C.ExpertDeliveryLoopBellFavoriteName);
        DrawFavoritePicker(Loc.T("Grand Company destination"),
            ref C.ExpertDeliveryLoopGCFavoriteId, ref C.ExpertDeliveryLoopGCFavoriteSub, ref C.ExpertDeliveryLoopGCFavoriteName);

        // 「沒設目的地」是會讓流程停在第一步的狀態,所以要在列上看得見,不是藏在 tooltip 裡。
        if(C.ExpertDeliveryLoopBellFavoriteId == 0)
        {
            ImGuiEx.Text(ImGuiColors.DalamudYellow, Loc.T("Without a summoning bell destination the loop only works with a bell already in reach, and stops otherwise."));
        }

        ImGui.Separator();

        ImGuiEx.SetNextItemWidthScaled(150);
        ImGui.SliderInt(Loc.T("Handin round timeout (minutes)"), ref C.ExpertDeliveryLoopHandinTimeoutMinutes, 1, 60);
    }

    /// <summary>Lifestream 我的最愛的快取。每幀去問一次會讓 Lifestream 重建索引,所以節流。</summary>
    private static List<(uint Id, byte SubIndex, string Name, uint Territory)> FavoritesCache = [];
    private static long FavoritesCachedAt;
    private static bool FavoritesAvailable = true;

    internal static List<(uint Id, byte SubIndex, string Name, uint Territory)> GetFavorites()
    {
        var now = Environment.TickCount64;
        if(now - FavoritesCachedAt < 2000) return FavoritesCache;
        FavoritesCachedAt = now;
        try
        {
            FavoritesCache = S.LifestreamIPC.GetTeleportFavorites() ?? [];
            FavoritesAvailable = true;
        }
        catch(Exception)
        {
            // Lifestream 沒裝或版本太舊。這與「一個最愛都沒加」是兩件事,要分開講。
            FavoritesCache = [];
            FavoritesAvailable = false;
        }
        return FavoritesCache;
    }

    private void DrawFavoritePicker(string label, ref uint id, ref byte subIndex, ref string savedName)
    {
        var favorites = GetFavorites();
        // ref 參數不能被 lambda 捕捉,先取值到區域變數。
        var curId = id;
        var curSub = subIndex;
        var current = curId == 0
            ? Loc.T("Not selected")
            // 選過的項目被取消收藏之後就不在清單裡了。這種狀態要說出來,不能顯示成「未選擇」——
            // 那會讓使用者以為只是還沒選,而不知道原本選的已經失效。
            : favorites.Any(x => x.Id == curId && x.SubIndex == curSub)
                ? savedName
                : $"{savedName} {Loc.T("(no longer a favourite)")}";

        ImGuiEx.SetNextItemWidthScaled(280);
        if(ImGui.BeginCombo(label, current))
        {
            if(ImGui.Selectable(Loc.T("Not selected"), id == 0))
            {
                id = 0;
                subIndex = 0;
                savedName = "";
            }
            foreach(var fav in favorites)
            {
                if(ImGui.Selectable($"{fav.Name}##{fav.Id}_{fav.SubIndex}", fav.Id == id && fav.SubIndex == subIndex))
                {
                    id = fav.Id;
                    subIndex = fav.SubIndex;
                    savedName = fav.Name;
                }
            }
            ImGui.EndCombo();
        }

        if(favorites.Count == 0)
        {
            ImGuiEx.Text(ImGuiColors.DalamudYellow, FavoritesAvailable
                ? Loc.T("(no favourites - star a destination in the Lifestream teleport panel first)")
                : Loc.T("(Lifestream is not available)"));
        }
    }

    private void DrawEntrustPlanPicker()
    {
        var current = C.EntrustPlans.FirstOrDefault(x => x.Guid == C.ExpertDeliveryLoopEntrustPlan);
        // 計畫被刪掉之後設定裡的 Guid 還在,但它已經指不到任何東西。這種狀態要說出來,
        // 不能顯示成「沒有選」—— 那會讓使用者以為只要再選一次就好,而不知道原本選的已經沒了。
        var label = C.ExpertDeliveryLoopEntrustPlan == Guid.Empty
            ? Loc.T("Not selected")
            : current?.Name ?? Loc.T("(deleted plan)");

        ImGuiEx.SetNextItemWidthScaled(250);
        if(ImGui.BeginCombo(Loc.T("Entrust plan"), label))
        {
            if(ImGui.Selectable(Loc.T("Not selected"), C.ExpertDeliveryLoopEntrustPlan == Guid.Empty))
            {
                C.ExpertDeliveryLoopEntrustPlan = Guid.Empty;
            }
            foreach(var plan in C.EntrustPlans)
            {
                if(ImGui.Selectable(plan.Name, plan.Guid == C.ExpertDeliveryLoopEntrustPlan))
                {
                    C.ExpertDeliveryLoopEntrustPlan = plan.Guid;
                }
            }
            ImGui.EndCombo();
        }
        ImGuiEx.HelpMarker(Loc.T("Only retainers that have this entrust plan assigned are visited."));

        if(C.ExpertDeliveryLoopEntrustPlan != Guid.Empty && current == null)
        {
            ImGuiEx.Text(ImGuiColors.DalamudRed, Loc.T("The selected entrust plan no longer exists - pick another one."));
        }
    }

    private void DrawManualRetainerPicker()
    {
        var data = Utils.GetCurrentCharacterData();
        if(data == null)
        {
            ImGuiEx.Text(ImGuiColors.DalamudGrey, Loc.T("No data for the current character yet."));
            return;
        }

        ImGui.Indent();
        foreach(var retainer in data.RetainerData)
        {
            var name = retainer.Name.ToString();
            if(name.IsNullOrEmpty()) continue;
            var selected = C.ExpertDeliveryLoopRetainerNames.Contains(name);
            if(ImGui.Checkbox($"{name}##ExpertDeliveryLoopRetainer", ref selected))
            {
                if(selected) C.ExpertDeliveryLoopRetainerNames.Add(name);
                else C.ExpertDeliveryLoopRetainerNames.Remove(name);
            }
        }
        ImGui.Unindent();
    }
}
