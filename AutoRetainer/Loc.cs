using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
    // Published as one reference so T() always sees a table and its aliases from the same Load().
    private sealed class Table(Dictionary<string, string> strings)
    {
        internal readonly Dictionary<string, string> Strings = strings;
        internal readonly Dictionary<string, string> CrLfAliases = BuildCrLfAliases(strings);
    }

    private static Table _table;

    public static void Load(ClientLanguage language)
    {
        var code = language switch
        {
            // TC(台服)客戶端在 Dalamud 13.0.0.16 之後回報 ClientLanguage 7(TraditionalChinese),
            // 舊版回報 4(ChineseSimplified)。用數值比較才能同時相容舊版 pin(列舉沒有 7 這個名字)與執行期新版。
            (ClientLanguage)4 or (ClientLanguage)5 or (ClientLanguage)7 => "zh_TW",
            _ => null,
        };

        if(code == null)
        {
            _table = null;
            return;
        }

        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = $"AutoRetainer.loc.{code}.json";
        using var stream = assembly.GetManifestResourceStream(resourceName);
        if(stream == null)
        {
            _table = null;
            return;
        }

        using var reader = new StreamReader(stream);
        var json = reader.ReadToEnd();
        var strings = JsonConvert.DeserializeObject<Dictionary<string, string>>(json);
        _table = strings == null ? null : new Table(strings);
    }

    /// <summary>
    /// Looks the fallback up with every "\r\n" treated as "\n" (JSON keys are LF-only, while raw
    /// string literals in CRLF source files carry CRLF); returns the translation, or the fallback
    /// itself when there is none. Lookups are allocation-free except for mixed or lone-CR input.
    /// </summary>
    public static string T(string fallback)
    {
        var shape = GetShape(fallback);
        var table = _table;
        if(table == null) return fallback;
        string translated;
        var found = shape switch
        {
            LineShape.NoCr => table.Strings.TryGetValue(fallback, out translated),
            LineShape.PureCrLf => table.CrLfAliases.TryGetValue(fallback, out translated),
            _ => table.Strings.TryGetValue(fallback.Replace("\r\n", "\n"), out translated),
        };
        return found ? translated : fallback;
    }

    private enum LineShape { NoCr, PureCrLf, Other }

    // PureCrLf: every '\r' is followed by '\n' and every '\n' is preceded by '\r'.
    private static LineShape GetShape(string s)
    {
        if(s.IndexOf('\r') < 0) return LineShape.NoCr;
        for(var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if(c == '\r')
            {
                if(i + 1 >= s.Length || s[i + 1] != '\n') return LineShape.Other;
                i++;
            }
            else if(c == '\n')
            {
                return LineShape.Other;
            }
        }
        return LineShape.PureCrLf;
    }

    // Only CR-free keys get an alias: for those, CRLF-expanding and then normalizing gives the key back,
    // so a PureCrLf input hits an alias exactly when its normalized form hits the key itself.
    private static Dictionary<string, string> BuildCrLfAliases(Dictionary<string, string> strings)
    {
        var aliases = new Dictionary<string, string>();
        foreach(var (key, value) in strings)
        {
            if(key.Contains('\n') && !key.Contains('\r')) aliases[key.Replace("\n", "\r\n")] = value;
        }
        return aliases;
    }

    // Builds a display-name dictionary for an underscore-separated enum (e.g. Enable_AutoRetainer), translating the space-converted fallback text via Loc.T.
    // The key handed to Loc.T is exactly what ECommons renders when no names table is supplied (ToString() with '_' replaced by a space).
    // The table is built once per enum type and reused for the rest of the session.
    public static IDictionary<TEnum, string> EnumNames<TEnum>() where TEnum : struct, Enum
        => EnumNameCache<TEnum>.Value;

    // One lazily built table per closed enum type: the runtime runs this static initializer on the
    // first access to EnumNameCache<T>, so every later lookup costs just a field read. Handed out
    // read-only so a consumer cannot mutate the shared instance.
    private static class EnumNameCache<TEnum> where TEnum : struct, Enum
    {
        internal static readonly IDictionary<TEnum, string> Value = Build();

        private static IDictionary<TEnum, string> Build()
        {
            var dict = new Dictionary<TEnum, string>();
            foreach(var v in Enum.GetValues<TEnum>())
            {
                dict[v] = T(v.ToString().Replace('_', ' '));
            }
            return new ReadOnlyDictionary<TEnum, string>(dict);
        }
    }
}
