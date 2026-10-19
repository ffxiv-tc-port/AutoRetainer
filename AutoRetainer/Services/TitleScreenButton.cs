using System.IO;

namespace AutoRetainer.Services;
public class TitleScreenButton : IDisposable
{
    private IReadOnlyTitleScreenMenuEntry TitleScreenMenuEntryButton;
    private TitleScreenButton()
    {
        if(C.UseTitleScreenButton)
        {
            Svc.Framework.Update += RegisterTitleIcon;
        }
    }

    private void RegisterTitleIcon(object f)
    {
        Svc.Framework.Update -= RegisterTitleIcon;
        // 這裡組出來的是檔案系統的絕對路徑，不是遊戲 sqpack 路徑；用 GetFromGame 會找不到檔案，
        // 標題畫面的按鈕就被 Dalamud 整個移除掉。
        var tex = Svc.Texture.GetFromFileAbsolute(Path.Combine(Svc.PluginInterface.AssemblyLocation.DirectoryName, "res", "autoretainer.png"));
        TitleScreenMenuEntryButton = Svc.TitleScreenMenu.AddEntry(Svc.PluginInterface.Manifest.Name, tex, () => P.AutoRetainerWindow.IsOpen = true);
    }

    public void Dispose()
    {
        if(TitleScreenMenuEntryButton != null)
        {
            Svc.TitleScreenMenu.RemoveEntry(TitleScreenMenuEntryButton);
        }
    }
}
