namespace AutoRetainer.UI.NeoUI.AdvancedEntries.DebugSection;
public abstract class DebugSectionBase : NeoUIEntry
{
    public override string Path => $"{Loc.T("Advanced")}/{Loc.T("Debug")}/{Loc.T(GetType().Name.Replace("Debug", ""))}";
    public override bool ShouldDisplay()
    {
        return C.Verbose;
    }
}
