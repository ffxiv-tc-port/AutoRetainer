using System.Diagnostics;

namespace AutoRetainer.UI
{
    public static class CustomAboutTab
    {
        private static string GetImageURL()
        {
            return Svc.PluginInterface.Manifest.IconUrl ?? "";
        }

        public static void Draw()
        {
            ImGuiEx.LineCentered("About1", delegate
            {
                ImGuiEx.Text($"{Svc.PluginInterface.Manifest.Name} - {Svc.PluginInterface.Manifest.AssemblyVersion}");
            });

            ImGuiEx.LineCentered("About0", () =>
            {
                ImGuiEx.Text(Loc.T("Published and developed with "));
                ImGui.PushFont(UiBuilder.IconFont);
                ImGui.SameLine(0, 0);
                ImGuiEx.Text(ImGuiColors.DalamudRed, FontAwesomeIcon.Heart.ToIconString());
                ImGui.PopFont();
                ImGui.SameLine(0, 0);
                ImGuiEx.Text(Loc.T(" by Puni.sh and NightmareXIV"));
            });

            ImGuiHelpers.ScaledDummy(10f);
            ImGuiEx.LineCentered("About2", delegate
            {
                if(ThreadLoadImageHandler.TryGetTextureWrap(GetImageURL(), out var texture))
                {
                    ImGui.Image(texture.Handle, new(200f, 200f));
                }
            });
            ImGuiHelpers.ScaledDummy(10f);
            ImGuiEx.LineCentered("About3", delegate
            {
                ImGui.TextWrapped(Loc.T("Join our Discord community for project announcements, updates, and support."));
            });
            ImGuiEx.LineCentered("About4", delegate
            {
                if(ImGui.Button("Discord"))
                {
                    Process.Start(new ProcessStartInfo()
                    {
                        FileName = "https://discord.gg/Zzrcc8kmvy",
                        UseShellExecute = true
                    });
                }
                ImGui.SameLine();
                if(ImGui.Button(Loc.T("Repository")))
                {
                    // 這裡絕對不能指國際服的外掛庫：那裡的 AutoRetainer 內部名與台服版
                    // 完全相同，使用者複製這個網址加進去會裝到 API15/net10 的版本，在台服
                    // 的 API13 Dalamud 上載不起來，而且會撞掉台服版的已安裝鍵。
                    ImGui.SetClipboardText("https://raw.githubusercontent.com/ffxiv-tc-port/DalamudPluginsTC/main/repo.json");
                    Notify.Success(Loc.T("Link copied to clipboard"));
                }
                ImGui.SameLine();
                if(ImGui.Button(Loc.T("Source Code")))
                {
                    Process.Start(new ProcessStartInfo()
                    {
                        FileName = Svc.PluginInterface.Manifest.RepoUrl,
                        UseShellExecute = true
                    });
                }
                ImGui.SameLine();
                if(ImGui.Button(Loc.T("Donate to Puni.sh platform")))
                {
                    Process.Start(new ProcessStartInfo()
                    {
                        FileName = "https://ko-fi.com/spetsnaz",
                        UseShellExecute = true
                    });
                }
            });
        }
    }
}
