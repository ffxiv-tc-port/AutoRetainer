namespace AutoRetainer.UI.NeoUI.MultiModeEntries;
public class MultiModeCommon : NeoUIEntry
{
    public override string Path => Loc.T("Multi Mode/Common Settings");

    public override NuiBuilder Builder { get; init; } = new NuiBuilder()
        .Section(Loc.T("Common Settings"))
        .Checkbox(Loc.T("Wait on login screen"), () => ref C.MultiWaitOnLoginScreen, Loc.T("If no character is available for ventures, you will be logged off until any character is available again. Title screen movie will be disabled while this option and MultiMode are enabled."))
        .Checkbox(Loc.T("Disable Multi Mode on Manual Login"), () => ref C.MultiDisableOnRelog, Loc.T("Upon relogging via AutoRetainer's UI or command, disable Multi Mode."))
        .Checkbox(Loc.T("Do not reset Preferred Character on Manual Login"), () => ref C.MultiNoPreferredReset, Loc.T("Upon relogging via AutoRetainer's UI or command, do not reset preferred character."))
        .Checkbox(Loc.T("Allow entering shared houses"), () => ref C.SharedHET)
        .Checkbox(Loc.T("Attempt to enter house on login even when Multi Mode is disabled"), () => ref C.HETWhenDisabled)
        .Checkbox(Loc.T("Do not teleport or enter house for retainers when already next to bell"), () => ref C.NoTeleportHetWhenNextToBell)
        .SliderInt(150f, Loc.T("Post-login scene settle delay, seconds"), () => ref C.PostLoginSceneSettleDelay.ValidateRange(0, 30), 0, 15, Loc.T("After logging into a character during Multi Mode character switching, wait this many extra seconds before AutoRetainer starts interacting with the game. This gives the scene, graphics resources and game settings time to finish loading after the login/zone transition, which is when crashes are most likely to occur. Increase this if you experience crashes during character switching."))

        .Section(Loc.T("Game startup"))
        .Checkbox(Loc.T("Enable Multi Mode on Game Boot"), () => ref C.MultiAutoStart)
        .Widget(Loc.T("Auto-login on Game Boot"), (x) =>
        {
            ImGui.SetNextItemWidth(150f);
            var names = C.OfflineData.Where(s => !s.Name.IsNullOrEmpty()).Select(s => $"{s.Name}@{s.World}");
            var dict = names.ToDictionary(s => s, s => Censor.Character(s));
            dict.Add("", Loc.T("Disabled"));
            dict.Add("~", Loc.T("Last logged in character"));
            ImGuiEx.Combo(x, ref C.AutoLogin, ["", "~", .. names], names: dict);
        })
        .SliderInt(150f, Loc.T("Delay"), () => ref C.AutoLoginDelay.ValidateRange(0, 60), 0, 20, Loc.T("Set appropriate delay to let plugins fully load before logging in and to allow yourself some time to cancel login if needed"))

        .Section(Loc.T("Inventory warnings"))
        .InputInt(100f, Loc.T("Retainer list: remaining inventory slots warning"), () => ref C.UIWarningRetSlotNum.ValidateRange(2, 1000))
        .InputInt(100f, Loc.T("Retainer list: remaining ventures warning"), () => ref C.UIWarningRetVentureNum.ValidateRange(2, 1000))
        .InputInt(100f, Loc.T("Deployables list: remaining inventory slots warning"), () => ref C.UIWarningDepSlotNum.ValidateRange(2, 1000))
        .InputInt(100f, Loc.T("Deployables list: remaining fuel warning"), () => ref C.UIWarningDepTanksNum.ValidateRange(20, 1000))
        .InputInt(100f, Loc.T("Deployables list: remaining repair kit warning"), () => ref C.UIWarningDepRepairNum.ValidateRange(5, 1000))

        // 這裡目前不用 NuiBuilder 的 collapsible:true。原本的理由是 NightmareUI 的
        // Section.cs 把折疊狀態寫成 `var isOpen = ...GetBoolRef(...)`，而那個方法回的是
        // `ref bool`，`var` 會複製成區域變數，所以 `isOpen = !isOpen` 從來沒有寫回 ImGui 的
        // state storage ⇒ 區塊等於永遠收合，內容只在按下滑鼠那一幀閃一下。
        // 📌 那個缺陷已經在 NightmareUI 子模組修掉（接收端改成 `ref var ... = ref`），
        // collapsible 現在是可用的。這裡維持不折疊是刻意的：折疊已經由下面那顆總開關負責，
        // 關掉之後這個區塊只剩一行，而且總開關是設定、會存檔（ImGui 的折疊狀態不會存檔）。
        .Section(Loc.T("Character list allowance columns"))
        .Checkbox(Loc.T("Show allowance columns in character list"), () => ref C.UIShowAllowances, Loc.T("Adds four columns to every character row: S = Grand Company seals, L = levequest allowances, D = custom delivery allowances left this week, T = limited tomestones acquired this week. A grey question mark means AutoRetainer has not managed to read that value on that character yet - it is filled in automatically shortly after logging in. A grey dash on the seal column means the character has not joined a Grand Company. Hover a column for the time it was sampled."))
        .If(() => C.UIShowAllowances)
        .Indent()
        .Checkbox(Loc.T("Column S: Grand Company seals"), () => ref C.UIShowAllowanceSeals)
        .Checkbox(Loc.T("Column L: levequest allowances"), () => ref C.UIShowAllowanceLeves)
        .Checkbox(Loc.T("Column D: custom delivery allowances"), () => ref C.UIShowAllowanceCustomDeliveries)
        .Checkbox(Loc.T("Column T: weekly tomestones"), () => ref C.UIShowAllowanceTomestones)
        .InputInt(100f, Loc.T("Character list: Grand Company seal warning margin"), () => ref C.UIWarningGCSealsMargin.ValidateRange(0, 90000))
        .InputInt(100f, Loc.T("Character list: levequest allowance warning"), () => ref C.UIWarningLeveAllowancesNum.ValidateRange(1, 100))
        .InputInt(100f, Loc.T("Character list: custom delivery allowance warning"), () => ref C.UIWarningCustomDeliveryNum.ValidateRange(0, 12))
        .InputInt(100f, Loc.T("Character list: weekly tomestone warning margin"), () => ref C.UIWarningTomestoneMargin.ValidateRange(0, 2000))
        .Unindent()
        .EndIf()

        .Section(Loc.T("Teleportation"))
        .Widget(() => ImGuiEx.Text(Loc.T("Lifestream plugin is required")))
        .Widget(() => ImGuiEx.PluginAvailabilityIndicator([new("Lifestream", new Version("2.2.1.1"))]))
        .TextWrapped(Loc.T("You must register houses in Lifestream plugin for every character you want this option to work or enable Simple Teleport."))
        .TextWrapped(Loc.T("You can customize these settings per character in character configuration menu."))
        .Widget(() =>
        {
            if(Data != null && Data.GetAreTeleportSettingsOverriden())
            {
                ImGuiEx.TextWrapped(ImGuiColors.DalamudRed, Loc.T("For current character teleport options are customized."));
            }
        })
        .Checkbox(Loc.T("Enabled"), () => ref C.GlobalTeleportOptions.Enabled)
        .Indent()
        .Checkbox(Loc.T("Teleport for retainers..."), () => ref C.GlobalTeleportOptions.Retainers)
        .Indent()
        .Checkbox(Loc.T("...to private house"), () => ref C.GlobalTeleportOptions.RetainersPrivate)
        .Checkbox(Loc.T("...to free company house"), () => ref C.GlobalTeleportOptions.RetainersFC)
        .Checkbox(Loc.T("...to apartment"), () => ref C.GlobalTeleportOptions.RetainersApartment)
        .TextWrapped(Loc.T(SharedText.FallbackTeleportToInn))
        .Unindent()
        .Checkbox(Loc.T(SharedText.TeleportToFcHouseForDeployables), () => ref C.GlobalTeleportOptions.Deployables)
        .Checkbox(Loc.T("Enable Simple Teleport"), () => ref C.AllowSimpleTeleport)
        .Unindent()
        .Widget(() => ImGuiEx.HelpMarker(Loc.T("""
            Allows teleporting to houses without registering them in Lifestream. Note: the Lifestream plugin is still required for teleportation to work.

            Warning: This option is less reliable than registering your houses in Lifestream. Use it only if necessary.
            """), EColor.RedBright, FontAwesomeIcon.ExclamationTriangle.ToIconString()))

        .Section(Loc.T("Bailout Module"))
        .Checkbox(Loc.T("Auto-close and retry logging in on connection errors"), () => ref C.ResolveConnectionErrors, Loc.T("Upon disconnecting, AutoRetainer will attempt to log back in. If the session has expired, no login attempt will be made."))
        .Widget(() => ImGuiEx.PluginAvailabilityIndicator([new("NoKillPlugin")]));
}
