using ECommons.Configuration;
using ECommons.Reflection;

namespace AutoRetainer.UI.NeoUI.AdvancedEntries;
public class ExpertTab : NeoUIEntry
{
    public override string Path => Loc.T("Advanced/Expert Settings");

    public override NuiBuilder Builder { get; init; } = new NuiBuilder()
        .Section(Loc.T("Behavior"))
        .EnumComboFullWidth(null, Loc.T("Action on accessing retainer bell if no ventures available:"), () => ref C.OpenBellBehaviorNoVentures)
        .EnumComboFullWidth(null, Loc.T("Action on accessing retainer bell if any ventures available:"), () => ref C.OpenBellBehaviorWithVentures)
        .EnumComboFullWidth(null, Loc.T("Task completion behavior after accessing bell:"), () => ref C.TaskCompletedBehaviorAccess)
        .EnumComboFullWidth(null, Loc.T("Task completion behavior after manual enabling:"), () => ref C.TaskCompletedBehaviorManual)
        .EnumComboFullWidth(null, Loc.T("Task completion behavior during plugin operation:"), () => ref C.TaskCompletedBehaviorAuto)
        .TextWrapped(ImGuiColors.DalamudGrey, Loc.T("\"Close retainer list and disable plugin\" option for 3 previous settings is enforced during MultiMode operation."))
        .Checkbox(Loc.T("Stay in retainer menu if there are retainers to finish ventures within 5 minutes or less"), () => ref C.Stay5, Loc.T("This option is enforced during MultiMode operation."))
        .Checkbox($"Auto-disable plugin when closing retainer list", () => ref C.AutoDisable, Loc.T("Only applies when you exit menu by yourself. Otherwise, settings above apply."))
        .Checkbox($"Do not show plugin status icons", () => ref C.HideOverlayIcons)
        .Checkbox($"Display multi mode type selector", () => ref C.DisplayMMType)
        .Checkbox($"Display deployables checkbox in workshop", () => ref C.ShowDeployables)
        .Checkbox(Loc.T("Enable bailout module"), () => ref C.EnableBailout)
        .InputInt(150f, Loc.T("Timeout before AutoRetainer will attempt to unstuck, seconds"), () => ref C.BailoutTimeout)

        .Section(Loc.T("Settings"))
        .Checkbox($"Disable sorting and collapsing/expanding", () => ref C.NoCurrentCharaOnTop)
        .Checkbox($"Show MultiMode checkbox on plugin UI bar", () => ref C.MultiModeUIBar)
        .SliderIntAsFloat(100f, "Retainer menu delay, seconds", () => ref C.RetainerMenuDelay.ValidateRange(0, 2000), 0, 2000)
        .Checkbox($"Allow venture timer to display negative values", () => ref C.TimerAllowNegative)
        .Checkbox($"Do not error check venture planner", () => ref C.NoErrorCheckPlanner2)
        .Checkbox(Loc.T("Enable Manual relogs character postprocess"), () => ref C.AllowManualPostprocess, Loc.T("Allow manual command invocation while AutoRetainer locked in postprocess. "))
        .Widget(Loc.T("Market Cooldown Overlay"), (x) =>
        {
            if(ImGui.Checkbox(x, ref C.MarketCooldownOverlay))
            {
                if(C.MarketCooldownOverlay)
                {
                    P.Memory.OnReceiveMarketPricePacketHook?.Enable();
                }
                else
                {
                    P.Memory.OnReceiveMarketPricePacketHook?.Disable();
                }
            }
        })

        .Section(Loc.T("Integrations"))
        .Checkbox($"Artisan integration", () => ref C.ArtisanIntegration, Loc.T("Automatically enables AutoRetainer while Artisan is Pauses Artisan operation when ventures are ready to be collected and a retainer bell is within range. Once ventures have been dealt with Artisan will be enabled and resume whatever it was doing."))

        .Section(Loc.T("Server Time"))
        .Checkbox(Loc.T("Use server time instead of PC time"), () => ref C.UseServerTime)

        .Section(Loc.T("Utility"))
        .Widget(Loc.T("Cleanup ghost retainers"), (x) =>
        {
            if(ImGui.Button(x))
            {
                var i = 0;
                foreach(var d in C.OfflineData)
                {
                    i += d.RetainerData.RemoveAll(x => x.Name == "");
                }
                DuoLog.Information($"Cleaned {i} entries");
            }
        })

        .Section(Loc.T("Import/Export"))
        .Widget(() =>
        {
            if(ImGui.Button(Loc.T("Export without character data")))
            {
                var clone = C.JSONClone();
                clone.OfflineData = null;
                clone.AdditionalData = null;
                clone.FCData = null;
                clone.SelectedRetainers = null;
                clone.Blacklist = null;
                clone.AutoLogin = "";
                Copy(EzConfig.DefaultSerializationFactory.Serialize(clone, false));
            }
            if(ImGui.Button(Loc.T("Import and merge with character data")))
            {
                try
                {
                    var c = EzConfig.DefaultSerializationFactory.Deserialize<Config>(Paste());
                    c.OfflineData = C.OfflineData;
                    c.AdditionalData = C.AdditionalData;
                    c.FCData = C.FCData;
                    c.SelectedRetainers = C.SelectedRetainers;
                    c.Blacklist = C.Blacklist;
                    c.AutoLogin = C.AutoLogin;
                    if(c.GetType().GetFieldPropertyUnions().Any(x => x.GetValue(c) == null)) throw new NullReferenceException();
                    EzConfig.SaveConfiguration(C, $"Backup_{DateTimeOffset.Now.ToUnixTimeMilliseconds()}.json");
                    P.SetConfig(c);
                }
                catch(Exception e)
                {
                    e.LogDuo();
                }
            }
        });
}
