namespace AutoRetainer.UI.NeoUI;
public class Keybinds : NeoUIEntry
{
    public override string Path => Loc.T("Keybinds");

    public override NuiBuilder Builder { get; init; } = new NuiBuilder()
        .Section(Loc.T("Access summoning bell/workshop panel keybinds"))
        .Widget(Loc.T("Temporarily prevents AutoRetainer from being automatically enabled when using a Summoning Bell/Workshop Panel"), (x) =>
        {
            UIUtils.DrawKeybind(x, ref C.Suppress);
        })
        .Widget(Loc.T("Temporarily set the Collect Operation mode, preventing ventures from being assigned for the current cycle/Temporarily set Deployables mode to Finalize only"), (x) =>
        {
            UIUtils.DrawKeybind(x, ref C.TempCollectB);
        })

        .Section(Loc.T("Quick Retainer Action"))
        .Widget(Loc.T("Sell Item"), (x) => UIUtils.QRA(x, ref C.SellKey))
        .Widget(Loc.T("Entrust Item"), (x) => UIUtils.QRA(x, ref C.EntrustKey))
        .Widget(Loc.T("Retrieve Item"), (x) => UIUtils.QRA(x, ref C.RetrieveKey))
        .Widget(Loc.T("Put up For Sale"), (x) => UIUtils.QRA(x, ref C.SellMarketKey));
}
