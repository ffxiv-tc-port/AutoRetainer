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
        .Widget(Loc.T("Put up For Sale"), (x) => UIUtils.QRA(x, ref C.SellMarketKey))

        .Section(Loc.T("Inventory list editing keybinds"))
        .Widget(Loc.T("Hover item: add to Quick Venture Sell List / entrust plan"), (x) => UIUtils.DrawKeybind(x, ref C.FastListAddKey),
            Loc.T("Used by both Inventory Cleanup -> Fast Addition and Removal, and Entrust Manager -> Fast addition/removal. Set to None to disable the action entirely."))
        .Widget(Loc.T("Hover item: add to Unconditional Sell List"), (x) => UIUtils.DrawKeybind(x, ref C.FastListAddHardKey),
            Loc.T("Used by Inventory Cleanup -> Fast Addition and Removal. Set to None to disable the action entirely."))
        .Widget(Loc.T("Hover item: remove from list"), (x) => UIUtils.DrawKeybind(x, ref C.FastListRemoveKey),
            Loc.T("Used by both Inventory Cleanup -> Fast Addition and Removal, and Entrust Manager -> Fast addition/removal. Set to None to disable the action entirely."))
        .Widget(Loc.T("\"Discard now\" button confirmation"), (x) => UIUtils.DrawKeybind(x, ref C.DiscardNowKey),
            Loc.T("Hold this key to make the \"Discard now\" button clickable. Set to None to disable that button completely - discarded items can NOT be bought back, so it never becomes a one-click action."));
}
