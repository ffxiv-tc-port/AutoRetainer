using System;
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
            // TC(台服)客戶端在 Dalamud 13.0.0.16 之後回報 ClientLanguage 7(TraditionalChinese),
            // 舊版回報 4(ChineseSimplified)。用數值比較才能同時相容 CI 釘的 13.0.0.6(列舉沒有 7 這個名字)與執行期新版。
            (ClientLanguage)4 or (ClientLanguage)5 or (ClientLanguage)7 => "zh_TW",
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
    {
        // C# raw string literals ("""...""") preserve the source file's literal line
        // endings verbatim (unlike regular "..." strings, where \n is always LF). Files
        // saved with CRLF therefore produce \r\n in the runtime string, which would never
        // match an LF-only key in the JSON dictionary. Normalize before lookup so
        // translation matching doesn't depend on a source file's line-ending style.
        var key = fallback.Contains('\r') ? fallback.Replace("\r\n", "\n") : fallback;
        return _strings != null && _strings.TryGetValue(key, out var translated) ? translated : fallback;
    }

    // Builds a display-name dictionary for an underscore-separated enum (e.g. Enable_AutoRetainer),
    // translating the space-converted fallback text via Loc.T for use with EnumComboFullWidth's names: parameter.
    public static IDictionary<TEnum, string> EnumNames<TEnum>() where TEnum : struct, Enum
    {
        var dict = new Dictionary<TEnum, string>();
        foreach(var v in Enum.GetValues<TEnum>())
        {
            dict[v] = T(v.ToString().Replace('_', ' '));
        }
        return dict;
    }
}
