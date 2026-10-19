using AutoRetainer.UI.NeoUI.AdvancedEntries;
using AutoRetainer.UI.NeoUI.AdvancedEntries.DebugSection;
using AutoRetainer.UI.NeoUI.Experiments;
using AutoRetainer.UI.NeoUI.InventoryManagementEntries;
using AutoRetainer.UI.NeoUI.MultiModeEntries;
using ECommons.Configuration;
using NightmareUI.OtterGuiWrapper.FileSystems.Configuration;
using OtterGui.Filesystem;

namespace AutoRetainer.UI.NeoUI;
public sealed class NeoWindow : Window
{
    private readonly NeoUIEntry[] Tabs =
    [
        new MainSettings(),
        new UserInterface(),
        new DeployablesTab(),
        new RetainersTab(),
        new Keybinds(),

        new MultiModeCommon(),
        new MultiModeRetainers(),
        new MultiModeDeployables(),
        new MultiModeContingency(),
        new CharaOrder(),
        new MultiModeFPSLimiter(),
        new MultiModeLockout(),
        new MultiModeAutoBuyFuel(),

        ..ConfigFileSystemHelpers.CreateInstancesOf<InventoryManagemenrBase>(),

        new LoginOverlay(),
        new MiscTab(),

        ..ConfigFileSystemHelpers.CreateInstancesOf<ExperimentUIEntry>(),

        new LogTab(),
        new ExpertTab(),

        ..ConfigFileSystemHelpers.CreateInstancesOf<DebugSectionBase>(),
    ];

    internal ConfigFileSystem FileSystem;

    public NeoWindow() : base("AutoRetainer Configuration")
    {
        P.WindowSystem.AddWindow(this);
        this.SetMinSize();
        FileSystem = new(() => Tabs);
    }

    public void Reload()
    {
        FileSystem.Reload();
    }

    // 左側選單的寬度：與 ConfigFileSystem.Draw 不給寬度時自己算的是同一個式子。
    // 分頁路徑只在 Reload 換一份 DataStorage 時才會變，分段結果跟著那一份留著；字寬每幀照當下的字型量。
    private ICollection<ConfigFileSystemEntry> SegmentsSource;
    private string[][] PathSegments;

    internal float GetSelectorWidth()
    {
        var data = FileSystem.DataStorage;
        if(!ReferenceEquals(data, SegmentsSource))
        {
            PathSegments = [.. data.Select(x => x.Path.SplitDirectories())];
            SegmentsSource = data;
        }
        var width = 0f;
        foreach(var segments in PathSegments)
        {
            for(var i = 0; i < segments.Length; i++)
            {
                var newWidth = ImGui.CalcTextSize(segments[i]).X + (i + 1) * 25f + ImGui.GetStyle().ScrollbarSize;
                if(newWidth > width) width = newWidth;
            }
        }
        return width;
    }

    public override void Draw()
    {
        FileSystem.Draw(GetSelectorWidth());
    }

    public override void OnClose()
    {
        EzConfig.Save();
    }
}
