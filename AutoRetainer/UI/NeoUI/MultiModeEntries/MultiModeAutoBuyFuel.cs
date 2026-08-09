using AutoRetainer.Scheduler.Tasks;

namespace AutoRetainer.UI.NeoUI.MultiModeEntries;
public class MultiModeAutoBuyFuel : NeoUIEntry
{
    public override string Path => Loc.T("Multi Mode/Auto Refuel");

    public override NuiBuilder Builder { get; init; } = new NuiBuilder()
        .Section(Loc.T("Auto Refuel"))
        .TextWrapped(Loc.T("While enabled, if the character is standing in a Company Workshop and Ceruleum Tanks fall below the threshold, AutoRetainer will walk up to the adventurer doll NPC and buy Ceruleum Tanks from the Free Company Credit Shop up to the target amount."))
        .Checkbox(Loc.T("Enable Auto Refuel"), () => ref C.AutoBuyFuelEnabled)
        .InputInt(150f, Loc.T("Refuel below this many Ceruleum Tanks"), () => ref C.AutoBuyFuelThreshold.ValidateRange(0, 9999),
            Loc.T("Carrying exactly 0 counts as deliberately going without fuel and never triggers a purchase - refuelling only fires between 1 and this threshold. This keeps temporarily stashing your tanks elsewhere from starting a purchase. Use \"Buy now\" below to refuel from zero."))
        .InputInt(150f, Loc.T("Refuel up to this many Ceruleum Tanks"), () => ref C.AutoBuyFuelTarget.ValidateRange(1, 9999))
        .Widget(Loc.T("Buy now"), (x) =>
        {
            if(ImGuiEx.Button(x, !P.TaskManager.IsBusy)) TaskAutoBuyFuel.Enqueue();
        });
}
