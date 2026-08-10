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

        ImGui.Checkbox(Loc.T("Travel to a summoning bell when none is in reach"), ref C.ExpertDeliveryLoopTravelToBell);
        if(C.ExpertDeliveryLoopTravelToBell)
        {
            ImGui.Indent();
            ImGuiEx.SetNextItemWidthScaled(150);
            ImGui.InputText(Loc.T("Lifestream command"), ref C.ExpertDeliveryLoopBellCommand, 50);
            ImGuiEx.HelpMarker(Loc.T("Sent to Lifestream only when no summoning bell is already in reach, so standing at one means it is never used. Examples: mb (market board), home, fc, apt, inn. Must not be empty."));
            ImGui.Unindent();
        }

        DrawSavedBell();

        ImGuiEx.SetNextItemWidthScaled(150);
        ImGui.SliderInt(Loc.T("Handin round timeout (minutes)"), ref C.ExpertDeliveryLoopHandinTimeoutMinutes, 1, 60);
    }

    private void DrawSavedBell()
    {
        ImGui.Checkbox(Loc.T("Use a specific summoning bell"), ref C.ExpertDeliveryLoopUseSavedBell);
        ImGuiEx.HelpMarker(Loc.T("Where several bells stand within reach of each other, picking \"whichever is nearest\" can land on the wrong one. Save the spot you actually use and the loop will prefer the bell closest to it. ⚠️ This only chooses between bells you can already reach - getting to the zone is still done by the travel command above."));

        if(!C.ExpertDeliveryLoopUseSavedBell) return;

        ImGui.Indent();
        if(ImGui.Button(Loc.T("Set current position as the bell")))
        {
            if(Player.Available)
            {
                C.ExpertDeliveryLoopBellTerritory = Svc.ClientState.TerritoryType;
                C.ExpertDeliveryLoopBellPosition = Player.Object.Position;
                DuoLog.Information(Loc.T("Saved the current position as this flow's summoning bell."));
            }
        }

        if(C.ExpertDeliveryLoopBellTerritory == 0)
        {
            ImGuiEx.Text(ImGuiColors.DalamudYellow, Loc.T("No position saved yet - the nearest bell is used until you save one."));
        }
        else
        {
            var here = C.ExpertDeliveryLoopBellTerritory == Svc.ClientState.TerritoryType;
            var zone = GenericHelpers.GetTerritoryName(C.ExpertDeliveryLoopBellTerritory);
            ImGuiEx.Text(here ? ImGuiColors.DalamudGrey : ImGuiColors.DalamudYellow,
                string.Format(Loc.T("Saved bell: {0} ({1:F1}, {2:F1}, {3:F1})"), zone,
                    C.ExpertDeliveryLoopBellPosition.X, C.ExpertDeliveryLoopBellPosition.Y, C.ExpertDeliveryLoopBellPosition.Z));
            if(!here)
            {
                ImGuiEx.Text(ImGuiColors.DalamudGrey, Loc.T("You are in a different zone, so the nearest bell is used instead."));
            }
        }
        ImGui.Unindent();
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
