using System;
using System.Collections.Generic;
using System.Text;
using Lumina.Text.ReadOnly;

namespace AutoRetainer.Helpers;

/// <summary>
/// 把遊戲文字表的一列變成比對錨。純函式、不碰 Svc：離線探針直接編譯這個檔。
/// 「空白」與 ECommons <c>Cleanup</c> 同一組（空格、LF、CR、tab）；表裡的換行是巨集，本來就是段落邊界。
/// </summary>
internal static class GameTextMatch
{
    /// <summary>第一個巨集之前的文字（去頭尾空白）。整列以巨集開頭或讀不到時回空字串。</summary>
    internal static string Opening(ReadOnlySeString? text)
    {
        if(text is not { } s) return "";
        var sb = new StringBuilder();
        foreach(var p in s)
        {
            if(p.Type != ReadOnlySePayloadType.Text) break;
            sb.Append(Encoding.UTF8.GetString(p.Body.Span));
        }
        return sb.ToString().Trim();
    }

    /// <summary>巨集之間的固定文字，去掉空白、丟掉空段。讀不到時回空陣列。</summary>
    internal static string[] FixedParts(ReadOnlySeString? text)
    {
        if(text is not { } s) return [];
        var parts = new List<string>();
        foreach(var p in s)
        {
            if(p.Type != ReadOnlySePayloadType.Text) continue;
            var part = StripWhitespace(Encoding.UTF8.GetString(p.Body.Span));
            if(part.Length > 0) parts.Add(part);
        }
        return [.. parts];
    }

    /// <summary>
    /// 畫面文字去空白後依序含有某一列的每一段固定文字。
    /// 沒有任何一段的列不算命中：讀表失敗不可以變成「什麼都命中」。
    /// 每幀都可能被呼叫（認不出的視窗兩側都比），一般長度的文字不配置。
    /// </summary>
    internal static bool MatchesAnyRow(string screen, string[][] rows)
    {
        if(string.IsNullOrEmpty(screen) || rows == null) return false;
        Span<char> buffer = screen.Length <= 512 ? stackalloc char[512] : new char[screen.Length];
        var length = 0;
        foreach(var c in screen)
        {
            if(c is ' ' or '\n' or '\r' or '\t') continue;
            buffer[length++] = c;
        }
        ReadOnlySpan<char> text = buffer[..length];
        foreach(var parts in rows)
        {
            if(parts == null || parts.Length == 0) continue;
            var at = 0;
            foreach(var part in parts)
            {
                var i = text[at..].IndexOf(part, StringComparison.OrdinalIgnoreCase);
                if(i < 0) { at = -1; break; }
                at += i + part.Length;
            }
            if(at >= 0) return true;
        }
        return false;
    }

    /// <summary>
    /// 表文字在前、字面值原樣接在後面。短於兩個字的表文字丟掉：
    /// 這些清單走 StartsWith／Contains，空字串或單一字元的錨幾乎什麼都命中。
    /// </summary>
    internal static string[] TableFirst(string[] tableTexts, string[] literals)
    {
        var list = new List<string>(tableTexts.Length + literals.Length);
        foreach(var t in tableTexts)
        {
            if(t != null && t.Length >= 2) list.Add(t);
        }
        list.AddRange(literals);
        return [.. list];
    }

    /// <summary>整句相等的錨最多幾個字：短到會成為別的選項前綴的，實機只見過兩個字的。</summary>
    internal const int ExactMaxLength = 2;

    /// <summary>
    /// 這些列裡「整列沒有巨集、全文不超過 <see cref="ExactMaxLength"/> 個字」的錨（與 <see cref="Opening"/> 同字），
    /// 選單比對時只做整句相等。沒有這種列時回 null。
    /// </summary>
    internal static HashSet<string> ExactAnchors(ReadOnlySeString?[] rows)
    {
        HashSet<string> exact = null;
        foreach(var row in rows)
        {
            if(row is not { } s) continue;
            var sb = new StringBuilder();
            var whole = true;
            foreach(var p in s)
            {
                if(p.Type != ReadOnlySePayloadType.Text) { whole = false; break; }
                sb.Append(Encoding.UTF8.GetString(p.Body.Span));
            }
            var text = sb.ToString().Trim();
            if(whole && text.Length >= 2 && StripWhitespace(text).Length <= ExactMaxLength) (exact ??= new(StringComparer.Ordinal)).Add(text);
        }
        return exact;
    }

    /// <summary>
    /// 選項文字比一個錨：在 <paramref name="exact"/> 裡的錨兩邊去空白後整句相等（忽略大小寫），
    /// 其餘照舊 StartsWith(Ordinal)。
    /// </summary>
    internal static bool MenuEntryMatches(string entry, string anchor, HashSet<string> exact)
        => exact != null && exact.Contains(anchor)
            ? string.Equals(StripWhitespace(entry), StripWhitespace(anchor), StringComparison.OrdinalIgnoreCase)
            : entry.StartsWith(anchor, StringComparison.Ordinal);

    private static string StripWhitespace(string s)
    {
        if(s.AsSpan().IndexOfAny(" \n\r\t") < 0) return s;
        var sb = new StringBuilder(s.Length);
        foreach(var c in s)
        {
            if(c is ' ' or '\n' or '\r' or '\t') continue;
            sb.Append(c);
        }
        return sb.ToString();
    }
}
