namespace AutoRetainer.UI.NeoUI.Experiments;
public abstract class ExperimentUIEntry : NeoUIEntry
{
    public virtual string Name => GetType().Name;
    public override string Path => $"{Loc.T("Experiments")}/{Name}";
}
