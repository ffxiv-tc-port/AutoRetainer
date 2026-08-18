using AutoRetainer.Modules.GcHandin;

using AutoRetainerAPI.Configuration;

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
        if(GCExpertDeliveryLoop.Running)
        {
            var progress = GCExpertDeliveryLoop.BatchProgress;
            // 多角色連跑時「現在做到第幾個角色」是要隨時掃視的資訊,放列上而不是 tooltip。
            ImGuiEx.Text(progress.Total > 0
                ? $"{GCExpertDeliveryLoop.CurrentPhaseName}  ({string.Format(Loc.T("character {0}/{1}"), progress.Current, progress.Total)})"
                : GCExpertDeliveryLoop.CurrentPhaseName);
        }
        else
        {
            ImGuiEx.Text(Loc.T("Idle"));
        }

        // ⚠️ 讀不到的東西畫「?」不要畫 0 —— 把「不知道」顯示成 0 會直接誤導。
        var freeSlots = Player.Available ? Utils.GetInventoryFreeSlotCount().ToString() : "?";
        ImGuiEx.Text($"{Loc.T("Retrieved")}: {GCExpertDeliveryLoop.RetrievedTotal}    " +
            $"{Loc.T("Handin rounds")}: {GCExpertDeliveryLoop.HandinRounds}    " +
            $"{Loc.T("Free slots")}: {freeSlots}");

        // 循環跑著的時候一般僱員自動處理整個讓路(SchedulerMain.RetainerAutomationDeferred)。
        // ⚠️ 這是「起疑才查」的資訊,理由放 tooltip —— 但**暫停這件事本身**要在列上看得見:
        //    不講的話,使用者只會看到探險委託整趟都沒被收走,而畫面上沒有任何東西解釋為什麼,
        //    與「AutoRetainer 壞了」完全同形。
        if(GCExpertDeliveryLoop.Running)
        {
            ImGuiEx.Text(ImGuiColors.DalamudGrey, Loc.T("Normal retainer processing is paused while this runs"));
            ImGuiEx.Tooltip(Loc.T("AutoRetainer's own retainer automation stands down while this loop runs: it does not collect or reassign ventures, does not run the entrust/auto-vendor pass, and does not open a summoning bell by itself. Both drive the same retainer list through the same task queue, so letting them run together makes the normal cycle close the retainer list out from under this loop.\n\nIt is deferred, not cancelled - it resumes on its own the moment this loop stops. Nothing is lost either: venture results that came due stay on the retainer until they are collected."));
        }

        if(!GCExpertDeliveryLoop.StatusText.IsNullOrEmpty())
        {
            ImGuiEx.TextWrapped(ImGuiColors.DalamudYellow, GCExpertDeliveryLoop.StatusText);
        }

        // 多角色模式開著的話這個循環根本啟動不了(GCExpertDeliveryLoop.Start 會拒絕),而且循環跑到
        // 一半被打開多角色模式也會當場停下來。理由跟下面那行「會去找誰」一樣:擋路的東西要在按下去
        // 之前就看得見,不要按了才從錯誤訊息裡發現。
        if(MultiMode.Enabled)
        {
            ImGuiEx.Text(ImGuiColors.DalamudRed, Loc.T("Multi Mode is on - this loop cannot run until you turn it off."));
            ImGuiEx.Tooltip(Loc.T("Multi Mode logs your character out and switches to another one on its own schedule. This loop drives one character's retainers step by step, so a switch in the middle of it leaves the loop sending retainer interactions on a character that is not the one it was working on."));
        }

        // 開始之前就讓使用者看到「會去找誰」,不要按下去才發現名單是空的。
        if(!GCExpertDeliveryLoop.Running && !C.ExpertDeliveryLoopMultiCharacter)
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

        ImGui.Separator();
        DrawMultiCharacter();
    }

    #region 多角色連跑

    private void DrawMultiCharacter()
    {
        ImGui.Checkbox(Loc.T("Run on several characters"), ref C.ExpertDeliveryLoopMultiCharacter);
        ImGuiEx.HelpMarker(Loc.T("""
            The loop runs on each ticked character in turn, logging out and back in between them, and only says it is finished once every one of them is done.

            Nothing here starts by itself either - it still only runs while you press Start. Stop always works, including in the middle of a character switch.
            """));

        if(!C.ExpertDeliveryLoopMultiCharacter) return;

        ImGui.Indent();

        // 🔴 這兩個是「按下開始一定會被拒絕」的狀態,而且原因跟這個面板沒有關係 —— 要在列上看得見。
        if(MultiMode.Enabled)
        {
            ImGuiEx.TextWrapped(ImGuiColors.DalamudRed, Loc.T("Multi Mode is on. Turn it off before starting a multi-character run - two things switching characters at the same time will fight each other."));
        }
        if(C.DontLogout)
        {
            ImGuiEx.TextWrapped(ImGuiColors.DalamudRed, Loc.T("The \"Don't logout\" debug option is on, so characters cannot be switched."));
        }

        DrawCharacterTable();

        ImGuiEx.SetNextItemWidthScaled(150);
        ImGui.SliderInt(Loc.T("Character switch timeout (minutes)"), ref C.ExpertDeliveryLoopRelogTimeoutMinutes, 1, 60);
        ImGuiEx.HelpMarker(Loc.T("Covers logging out, the title screen, character selection, any login queue and the post-login scene settle delay."));

        ImGui.Checkbox(Loc.T("Notify when a multi-character run stops early (requires NotificationMaster)"), ref C.ExpertDeliveryLoopNotifyOnFailure);
        ImGuiEx.HelpMarker(Loc.T("""
            A multi-character run is something you leave running, so a run that dies on the second of five characters otherwise stays silent until you come back - and by then it is hard to tell which part went wrong.

            This covers stopping early: errors, and stopping it by hand. The notification for a run that finished everything is the separate "Tray notification upon handin completion" option under Miscellaneous.
            """));

        DrawPerCharacterDestinations();

        ImGui.Unindent();
    }

    private void DrawCharacterTable()
    {
        if(C.OfflineData.Count == 0)
        {
            ImGuiEx.Text(ImGuiColors.DalamudGrey, Loc.T("No character data yet - log into a character once so AutoRetainer can record it."));
            return;
        }

        if(!ImGui.BeginTable("##ExpertDeliveryLoopCharacters", 4, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg)) return;
        ImGui.TableSetupColumn(Loc.T("Character"), ImGuiTableColumnFlags.WidthStretch);
        // ⚠️ 欄名刻意不用「Retainers」:那個 key 在字典裡已經被外掛的「僱員管理」分頁佔走了,
        //    直接沿用會讓這個「有幾個」的欄位標題變成「僱員管理」。同字不同義要用不同的 key。
        ImGui.TableSetupColumn(Loc.T("Retainers to visit"));
        ImGui.TableSetupColumn(Loc.T("Summoning bell"));
        ImGui.TableSetupColumn(Loc.T("Grand Company"));
        ImGui.TableHeadersRow();

        foreach(var data in C.OfflineData)
        {
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            var selected = C.ExpertDeliveryLoopCharacters.Contains(data.CID);
            if(ImGui.Checkbox($"{Censor.Character(data.Name, data.World)}##ExpertDeliveryLoopChar{data.CID}", ref selected))
            {
                if(selected)
                {
                    if(!C.ExpertDeliveryLoopCharacters.Contains(data.CID)) C.ExpertDeliveryLoopCharacters.Add(data.CID);
                }
                else
                {
                    C.ExpertDeliveryLoopCharacters.Remove(data.CID);
                }
            }
            if(data.CID == Player.CID) ImGuiEx.Tooltip(Loc.T("This is the character you are logged in on. A run starts here, so no character switch is needed for it."));

            ImGui.TableNextColumn();
            DrawRetainerCountCell(data);

            // ⚠️ 括號不能省:conf?.X != 0 在 conf 是 null 時**回 true**(可空比較的規則),
            //    那會把「沿用上面的設定」畫成「這個角色自己設的」。
            var rowConf = GCExpertDeliveryLoop.GetCharacterConfig(data.CID, false);

            ImGui.TableNextColumn();
            DrawDestinationCell(GCExpertDeliveryLoop.GetBellDestination(data.CID), (rowConf?.BellFavoriteId ?? 0u) != 0);

            ImGui.TableNextColumn();
            DrawDestinationCell(GCExpertDeliveryLoop.GetGCDestination(data.CID), (rowConf?.GCFavoriteId ?? 0u) != 0);
        }
        ImGui.EndTable();
    }

    /// <summary>「這個角色有幾個僱員會被拜訪」。
    /// 🔴 從來沒被 AutoRetainer 記錄過僱員的角色要畫「?」不是「0」—— 0 的意思是「查過了,一個都不符合」
    /// (那是設定錯誤,要去修),而「?」的意思是「還不知道,登入一次它就知道了」。兩者要修的東西完全不同。</summary>
    private void DrawRetainerCountCell(OfflineCharacterData data)
    {
        if(data.RetainerData.Count == 0)
        {
            ImGuiEx.Text(ImGuiColors.DalamudGrey, "?");
            ImGuiEx.Tooltip(Loc.T("AutoRetainer has not recorded this character's retainers yet. Log into it once."));
            return;
        }
        var count = GCExpertDeliveryLoop.ResolveRetainers(data).Count;
        if(count == 0)
        {
            ImGuiEx.Text(ImGuiColors.DalamudRed, "0");
            ImGuiEx.Tooltip(Loc.T("No retainer of this character matches the current selection. A run that includes this character refuses to start."));
            return;
        }
        ImGuiEx.Text(count.ToString());
    }

    private void DrawDestinationCell((uint Id, byte Sub, string Name) destination, bool own)
    {
        if(destination.Id == 0)
        {
            ImGuiEx.Text(ImGuiColors.DalamudYellow, Loc.T("not set"));
            return;
        }
        var name = destination.Name.IsNullOrEmpty() ? $"#{destination.Id}" : destination.Name;
        ImGuiEx.Text(own ? ImGuiColors.DalamudWhite : ImGuiColors.DalamudGrey, name);
        ImGuiEx.Tooltip(own
            ? Loc.T("Set for this character.")
            : Loc.T("Inherited from the destinations above, because this character has none of its own."));
    }

    /// <summary>目前登入這個角色的目的地覆寫。
    /// <para>🔴 只能設定**現在登入的**這個角色,而且這不是偷懶:收藏項的清單是 Lifestream 依當前角色的
    /// 傳送面板建出來的(自己的房屋、公司房屋、已學到的乙太之光都在裡面),別的角色的清單根本讀不到。
    /// 硬要在這裡列出來只會列到現在這個角色的東西然後存到別人頭上。</para></summary>
    private void DrawPerCharacterDestinations()
    {
        ImGui.Separator();
        ImGuiEx.Text(ImGuiColors.DalamudWhite, Loc.T("Destinations for this character"));

        if(!Player.Available)
        {
            ImGuiEx.TextWrapped(ImGuiColors.DalamudGrey, Loc.T("Log in to set a character's own destinations."));
            return;
        }

        ImGuiEx.TextWrapped(ImGuiColors.DalamudGrey, Loc.T("Leave these unset to use the destinations chosen above. Set them when this character needs different ones - most often because its Grand Company is in another city. You can only set them while logged in on the character, because the favourite list comes from that character's own teleport panel."));

        var conf = GCExpertDeliveryLoop.GetCharacterConfig(Player.CID, false);
        var bellId = conf?.BellFavoriteId ?? 0u;
        var bellSub = (byte)(conf?.BellFavoriteSub ?? 0);
        var bellName = conf?.BellFavoriteName ?? "";
        if(DrawFavoritePicker(Loc.T("Summoning bell destination for this character"), ref bellId, ref bellSub, ref bellName, allowInherit: true))
        {
            var own = GCExpertDeliveryLoop.GetCharacterConfig(Player.CID, true);
            own.BellFavoriteId = bellId;
            own.BellFavoriteSub = bellSub;
            own.BellFavoriteName = bellName;
        }

        var gcId = conf?.GCFavoriteId ?? 0u;
        var gcSub = (byte)(conf?.GCFavoriteSub ?? 0);
        var gcName = conf?.GCFavoriteName ?? "";
        if(DrawFavoritePicker(Loc.T("Grand Company destination for this character"), ref gcId, ref gcSub, ref gcName, allowInherit: true))
        {
            var own = GCExpertDeliveryLoop.GetCharacterConfig(Player.CID, true);
            own.GCFavoriteId = gcId;
            own.GCFavoriteSub = gcSub;
            own.GCFavoriteName = gcName;
        }
    }

    #endregion

    /// <summary>Lifestream 我的最愛的快取。每幀去問一次會讓 Lifestream 重建索引,所以節流。
    /// 🔴 快取要連**是哪個角色的**一起記:收藏項的清單是依當前角色的傳送面板建出來的,
    /// 換角色之後上一個角色的清單完全不適用,而過期時間只有兩秒的話換角色當下那幾幀會顯示錯的東西。</summary>
    private static List<(uint Id, byte SubIndex, string Name, uint Territory)> FavoritesCache = [];
    private static long FavoritesCachedAt;
    private static ulong FavoritesCachedFor;
    private static bool FavoritesAvailable = true;

    internal static List<(uint Id, byte SubIndex, string Name, uint Territory)> GetFavorites()
    {
        var now = Environment.TickCount64;
        if(now - FavoritesCachedAt < 2000 && FavoritesCachedFor == Player.CID) return FavoritesCache;
        FavoritesCachedAt = now;
        FavoritesCachedFor = Player.CID;
        try
        {
            FavoritesCache = S.LifestreamExtra.GetTeleportFavorites() ?? [];
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

    /// <summary>回傳是否有被改動 —— 呼叫端拿它決定要不要把值寫回設定(每角色覆寫是懶建立的,
    /// 沒被改過就不該替那個角色留下一筆設定)。</summary>
    private bool DrawFavoritePicker(string label, ref uint id, ref byte subIndex, ref string savedName, bool allowInherit = false)
    {
        var favorites = GetFavorites();
        // ref 參數不能被 lambda 捕捉,先取值到區域變數。
        var curId = id;
        var curSub = subIndex;
        var notSelected = allowInherit ? Loc.T("Use the destination above") : Loc.T("Not selected");
        var current = curId == 0
            ? notSelected
            // 選過的項目被取消收藏之後就不在清單裡了。這種狀態要說出來,不能顯示成「未選擇」——
            // 那會讓使用者以為只是還沒選,而不知道原本選的已經失效。
            : favorites.Any(x => x.Id == curId && x.SubIndex == curSub)
                ? savedName
                : $"{savedName} {Loc.T("(no longer a favourite)")}";

        var changed = false;
        ImGuiEx.SetNextItemWidthScaled(280);
        if(ImGui.BeginCombo(label, current))
        {
            if(ImGui.Selectable(notSelected, id == 0))
            {
                id = 0;
                subIndex = 0;
                savedName = "";
                changed = true;
            }
            foreach(var fav in favorites)
            {
                if(ImGui.Selectable($"{fav.Name}##{fav.Id}_{fav.SubIndex}", fav.Id == id && fav.SubIndex == subIndex))
                {
                    id = fav.Id;
                    subIndex = fav.SubIndex;
                    savedName = fav.Name;
                    changed = true;
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
        return changed;
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

    /// <summary>手動僱員名單。
    /// 🔴 名單是**按角色**記的。第一版是跨角色共用的一份,那份現在是「還沒被個別設定過的角色」的預設值 ——
    /// 所以這裡讀的是「目前生效的那份」,而第一次勾選才把它落到這個角色底下(見 GetOwnRetainerNames)。</summary>
    private void DrawManualRetainerPicker()
    {
        var data = Utils.GetCurrentCharacterData();
        if(data == null)
        {
            ImGuiEx.Text(ImGuiColors.DalamudGrey, Loc.T("No data for the current character yet."));
            return;
        }

        ImGui.Indent();
        var names = GCExpertDeliveryLoop.GetRetainerNames(data.CID);
        foreach(var retainer in data.RetainerData)
        {
            var name = retainer.Name.ToString();
            if(name.IsNullOrEmpty()) continue;
            var selected = names.Contains(name);
            if(ImGui.Checkbox($"{name}##ExpertDeliveryLoopRetainer", ref selected))
            {
                var own = GCExpertDeliveryLoop.GetOwnRetainerNames(data.CID);
                if(selected)
                {
                    if(!own.Contains(name)) own.Add(name);
                }
                else
                {
                    own.Remove(name);
                }
            }
        }
        ImGui.Unindent();
        ImGuiEx.Text(ImGuiColors.DalamudGrey, Loc.T("This selection is remembered per character."));
    }
}
