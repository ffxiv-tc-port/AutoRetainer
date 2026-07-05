using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Dalamud.Game;
using Newtonsoft.Json;

namespace AutoRetainer;

// Lightweight UI localization: Loc.T(fallback) looks up the English fallback
// text itself as the dictionary key and returns the translated string, or the
// fallback unchanged if no translation is loaded/found.
internal static class Loc
{
    private static Dictionary<string, string> _strings;

    public static void Load(ClientLanguage language)
    {
        var code = language switch
        {
            ClientLanguage.ChineseTraditional => "zh_TW",
            // TC private-server clients (e.g. FFXIVSimpleLauncher) are built on the
            // Simplified Chinese client, so Dalamud reports ChineseSimplified even
            // though the actual text and community expect Traditional Chinese.
            ClientLanguage.ChineseSimplified => "zh_TW",
            _ => null,
        };

        if(code == null)
        {
            _strings = null;
            return;
        }

        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = $"AutoRetainer.loc.{code}.json";
        using var stream = assembly.GetManifestResourceStream(resourceName);
        if(stream == null)
        {
            _strings = null;
            return;
        }

        using var reader = new StreamReader(stream);
        var json = reader.ReadToEnd();
        _strings = JsonConvert.DeserializeObject<Dictionary<string, string>>(json);
    }

    public static string T(string fallback)
        => _strings != null && _strings.TryGetValue(fallback, out var translated) ? translated : fallback;
}
