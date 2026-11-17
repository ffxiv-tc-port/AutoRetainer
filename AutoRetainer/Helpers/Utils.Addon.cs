using AutoRetainer.Internal;
using AutoRetainer.Modules.Voyage;
using AutoRetainer.Scheduler.Handlers;
using AutoRetainer.Scheduler.Tasks;
using AutoRetainer.Services;
using AutoRetainerAPI.Configuration;
using Dalamud.Game;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Memory;
using Dalamud.Utility;
using ECommons.Events;
using ECommons.ExcelServices;
using ECommons.ExcelServices.TerritoryEnumeration;
using ECommons.GameHelpers;
using ECommons.IPC;
using ECommons.MathHelpers;
using ECommons.Reflection;
using ECommons.Throttlers;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using Lumina.Text.ReadOnly;
using OtterGui.Text.EndObjects;
using System.Text.RegularExpressions;
using CharaData = (string Name, ushort World);
using GrandCompany = ECommons.ExcelServices.GrandCompany;

namespace AutoRetainer.Helpers;

public static unsafe partial class Utils
{
    /// <summary>
    /// 安全地讀取按鈕的啟用狀態，無法判定時回傳 null。
    /// 🔴 ClientStructs 的 <c>AtkComponentButton.IsEnabled</c> 實作是對 <c>OwnerNode</c> <b>零 null 檢查</b>。
    /// ⚠️ 所以檢查 <c>AtkResNode</c> <b>不算守衛</b>。
    /// </summary>
    public static bool? GetButtonEnabled(AtkComponentButton* button)
    {
        if(button == null) return null;
        if(button->AtkComponentBase.OwnerNode == null) return null;
        return button->IsEnabled;
    }

    /// <summary>
    /// <see cref="GetButtonEnabled"/> 的自動化用版本：按鈕還沒準備好時一律視為「不可按」，
    /// 讓呼叫端走既有的等待／重試路徑，而不是中止整條任務佇列。
    /// </summary>
    public static bool IsButtonEnabled(AtkComponentButton* button) => GetButtonEnabled(button) == true;

    /// <summary>
    /// 從 <paramref name="uld"/> 的 <c>NodeList</c> 取第 <paramref name="index"/> 個節點；取不到回 <c>null</c>。
    /// </summary>
    /// <remarks>
    /// 🔴 <c>NodeList</c> 要驗的是<b>兩件事</b>：清單本身不是 null、索引在 NodeListCount 之內。只對取出的元素判空擋不住超界（讀到的是相鄰記憶體）。
    /// </remarks>
    public static AtkResNode* GetNodeSafe(AtkUldManager* uld, int index)
    {
        if(uld == null || uld->NodeList == null) return null;
        if(index < 0 || index >= uld->NodeListCount) return null;
        return uld->NodeList[index];
    }

    /// <summary>
    /// 「<c>NodeList[index]</c> 這個節點現在可見嗎」的安全版本；節點取不到時一律回
    /// <see langword="false"/>（＝視為「不可見」）。
    /// </summary>
    /// <remarks>
    /// 🔴 <c>AtkResNode.IsVisible()</c> 是 <c>[MemberFunction]</c>：對 null 節點呼叫＝把 <c>this</c> = 0 交給遊戲原生碼，那種 AVE <c>try/catch</c> 攔不到。
    /// 🔑 失敗方向刻意選「不可見」而不是「可見」。
    /// </remarks>
    public static bool IsNodeVisible(AtkUldManager* uld, int index)
    {
        var node = GetNodeSafe(uld, index);
        return node != null && node->IsVisible();
    }

    /// <summary>
    /// 安全地讀取一個節點的文字內容。取得到回 <see langword="true"/>；
    /// 鏈上任何一節取不到就回 <see langword="false"/>，<paramref name="text"/> 為空字串。
    /// </summary>
    /// <remarks>
    /// 🔴 擋的是<b>兩種不同的爆法</b>：節點是 null、節點不是文字節點（GetAsAtkTextNode() 回 null 後再取欄位位址）；兩種都攔不住。
    /// 🔑 回傳 <c>bool</c> 而不是「取不到就回空字串」，是為了讓呼叫端分得出「讀到空文字」與「根本沒讀到」。
    /// </remarks>
    public static bool TryGetNodeText(AtkResNode* node, out string text)
    {
        text = "";
        if(node == null) return false;
        var textNode = node->GetAsAtkTextNode();
        if(textNode == null) return false;
        text = GenericHelpers.ReadSeString(&textNode->NodeText).GetText();
        return true;
    }

    /// <inheritdoc cref="TryGetNodeText(AtkResNode*, out string)"/>
    public static bool TryGetNodeText(AtkUldManager* uld, int index, out string text)
        => TryGetNodeText(GetNodeSafe(uld, index), out text);

    /// <summary>
    /// 依 <paramref name="nodeId"/> 取出 <paramref name="addon"/> 裡的單選按鈕元件；
    /// 鏈上任何一節取不到就回 <see langword="false"/>，<paramref name="button"/> 為 <c>null</c>。
    /// </summary>
    /// <remarks>
    /// 🔴 <c>addon-&gt;GetNodeById(id)-&gt;GetAsAtkComponentRadioButton()</c> 這條鏈有 <b>兩個各自獨立的 null 路徑</b>。
    /// <c>GetNodeById</c> 是 <c>[MemberFunction]</c>，<b>找不到該 id 就合法回 <c>null</c></b>。
    /// </remarks>
    public static bool TryGetRadioButtonById(AtkUnitBase* addon, uint nodeId, out AtkComponentRadioButton* button)
    {
        button = null;
        if(addon == null) return false;
        var node = addon->GetNodeById(nodeId);
        if(node == null) return false;
        button = node->GetAsAtkComponentRadioButton();
        return button != null;
    }

    internal static bool TrySelectSpecificEntry(string text, Func<bool> Throttler = null)
    {
        return TrySelectSpecificEntryCore(EntryMatch.Single, text, null, null, Throttler);
    }

    internal static bool TrySelectSpecificEntry(IEnumerable<string> text, Func<bool> Throttler = null)
    {
        return TrySelectSpecificEntryCore(EntryMatch.Many, null, text, null, Throttler);
        /*if (TryGetAddonByName<AddonSelectString>("SelectString", out var addon) && IsAddonReady(&addon->AtkUnitBase))
        {
            var entry = GetEntries(addon).FirstOrDefault(x => x.EqualsAny(text));
            if (entry != null)
            {
                var index = GetEntries(addon).IndexOf(entry);
                if (index >= 0 && IsSelectItemEnabled(addon, index) && (Throttler?.Invoke() ?? GenericThrottle))
                {
                    ClickSelectString.Using((nint)addon).SelectItem((ushort)index);
                    DebugLog($"TrySelectSpecificEntry: selecting {entry}/{index} as requested by {text.Print()}");
                    return true;
                }
            }
        }
        else
        {
            RethrottleGeneric();
        }
        return false;*/
    }

    internal static bool TrySelectSpecificEntry(Func<string, bool> inputTextTest, Func<bool> Throttler = null)
    {
        return TrySelectSpecificEntryCore(EntryMatch.Predicate, null, null, inputTextTest, Throttler);
    }

    /// <remarks>
    /// 與 <c>new AddonMaster.SelectString(addon).Entries.TryGetFirst(x =&gt; !TextIsUnstable(x.Text) &amp;&amp; 比對(x.Text))</c>
    /// 選同一項、送同一個 callback（<c>Entry.Select()</c>＝<c>Callback.Fire(addon, true, 索引)</c>）。
    /// 單一字串＝<c>StartsWith(Ordinal)</c>、多個＝ECommons <c>StartsWithAny</c>（預設 Ordinal）；
    /// 例外：<c>Lang.MenuList</c> 建的清單裡整列沒有巨集的短錨（如「取消」）改成整句相等。
    /// 任務每幀輪詢這支：每項文字只讀一次，不建 AddonMaster、陣列與閉包。
    /// </remarks>
    private static bool TrySelectSpecificEntryCore(EntryMatch mode, string single, IEnumerable<string> many, Func<string, bool> inputTextTest, Func<bool> Throttler)
    {
        if(TryGetAddonByName<AddonSelectString>("SelectString", out var addon) && IsAddonReady(&addon->AtkUnitBase))
        {
            var exact = mode == EntryMatch.Many ? Lang.ExactMenuAnchorsOf(many) : null;
            // 讀到 U+FFFD 的選項這一幀不碰(窗記憶體正在變動);選項按下即關窗,所以整扇 SelectString 一把 key。
            // 🔑 這支是全外掛 SelectString 選項按下的唯一咽喉,在這裡接守衛就罩住全部呼叫端。
            var count = addon->PopupMenu.PopupMenu.EntryCount;
            for(var i = 0; i < count; i++)
            {
                var text = GetSelectStringEntryText(addon, i);
                if(DialogGuards.TextIsUnstable(text)) continue;
                var match = mode switch
                {
                    EntryMatch.Single => text.StartsWith(single, StringComparison.Ordinal),
                    EntryMatch.Many => exact == null ? StartsWithAnyOrdinal(text, many) : MenuMatchesAny(text, (string[])many, exact),
                    _ => inputTextTest(text),
                };
                if(!match) continue;
                if((Throttler?.Invoke() ?? GenericThrottle) && DialogGuards.TryPressOnce("SelectString", (nint)addon, "TrySelectSpecificEntry"))
                {
                    Callback.Fire((AtkUnitBase*)addon, true, i);
                    DebugLog($"TrySelectSpecificEntry: selecting AddonMaster.SelectString.Entry [Text=\"{text}\", Index={i}]");
                    return true;
                }
                return false;
            }
        }
        else
        {
            RethrottleGeneric();
        }
        return false;
    }

    private static bool MenuMatchesAny(string text, string[] anchors, HashSet<string> exact)
    {
        foreach(var x in anchors)
        {
            if(GameTextMatch.MenuEntryMatches(text, x, exact)) return true;
        }
        return false;
    }

    /// <summary>等同 ECommons <c>text.StartsWithAny(values)</c>（Ordinal）；陣列直接走訪，不配列舉器。</summary>
    private static bool StartsWithAnyOrdinal(string text, IEnumerable<string> values)
    {
        if(values is not string[] arr) return text.StartsWithAny(values);
        foreach(var x in arr)
        {
            if(text.StartsWith(x, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    /// <summary>等同 <c>AddonMaster.SelectString.Entry.Text</c>；該項位元組與上次相同就沿用上次的字串（文字只由這段位元組決定）。</summary>
    internal static string GetSelectStringEntryText(AddonSelectString* addon, int index)
    {
        var menu = &addon->PopupMenu.PopupMenu;
        // 讀不到時與 TryGetPopupMenuEntryName 回 false 同一個結果：空字串。
        if(menu->EntryNames == null || index < 0 || index >= menu->EntryCount) return string.Empty;
        var p = menu->EntryNames[index].Value;
        if(p == null) return string.Empty;
        var raw = MemoryMarshal.CreateReadOnlySpanFromNullTerminated(p);
        if(index >= SelectStringEntryTexts.Length)
        {
            Array.Resize(ref SelectStringEntryBytes, index + 1);
            Array.Resize(ref SelectStringEntryTexts, index + 1);
        }
        var cached = SelectStringEntryTexts[index];
        if(cached != null && raw.SequenceEqual(SelectStringEntryBytes[index])) return cached;
        var text = (GenericHelpers.GetPopupMenuEntryName(menu, index) ?? (Dalamud.Game.Text.SeStringHandling.SeString)string.Empty).GetText();
        SelectStringEntryBytes[index] = raw.ToArray();
        SelectStringEntryTexts[index] = text;
        return text;
    }

    internal static List<string> GetEntries(AddonSelectString* addon)
    {
        var list = new List<string>();
        for(var i = 0; i < addon->PopupMenu.PopupMenu.EntryCount; i++)
        {
            list.Add(MemoryHelper.ReadSeStringNullTerminated((nint)addon->PopupMenu.PopupMenu.EntryNames[i].Value).GetText());
        }
        return list;
    }

    internal static AtkUnitBase* GetSpecificYesno(Predicate<string> compare)
    {
        for(var i = 1; i < 100; i++)
        {
            try
            {
                var addon = (AtkUnitBase*)Svc.GameGui.GetAddonByName("SelectYesno", i).Address;
                if(addon == null) return null;
                if(IsAddonReady(addon))
                {
                    // 取不到節點就當這個 SelectYesno「不是我們要找的那個」繼續往下掃(fail-closed):
                    // 沒讀到文字時不能讓 compare 拿空字串去判，否則會把「讀不到」誤判成「內容為空的相符」。
                    // 讀到 U+FFFD ＝ 這扇窗的記憶體正在變動(多半是關閉中),這一幀不碰它、掃下一扇。
                    if(TryGetNodeText(&addon->UldManager, 15, out var text) && !DialogGuards.TextIsUnstable(text) && compare(text))
                    {
                        PluginLog.Verbose($"SelectYesno {text} addon {i} by predicate");
                        return addon;
                    }
                }
            }
            catch(Exception e)
            {
                e.Log();
                return null;
            }
        }
        return null;
    }

    internal static AtkUnitBase* GetSpecificYesno(params string[] s)
    {
        for(var i = 1; i < 100; i++)
        {
            try
            {
                var addon = (AtkUnitBase*)Svc.GameGui.GetAddonByName("SelectYesno", i).Address;
                if(addon == null) return null;
                if(IsAddonReady(addon))
                {
                    // 讀不到（或讀到 U+FFFD）就跳過這個 addon，不要拿空字串去做包含比對。
                    if(TryGetNodeText(&addon->UldManager, 15, out var rawText)
                        && !DialogGuards.TextIsUnstable(rawText)
                        && rawText.Cleanup().ContainsAny(s.Select(x => x.Cleanup())))
                    {
                        PluginLog.Verbose($"SelectYesno {s.Print()} addon {i}");
                        return addon;
                    }
                }
            }
            catch(Exception e)
            {
                e.Log();
                return null;
            }
        }
        return null;
    }

    internal static bool TryMatch(this string s, string pattern, out Match match)
    {
        var m = Regex.Match(s, pattern);
        if(m.Success)
        {
            match = m;
            return true;
        }
        else
        {
            match = null;
            return false;
        }
    }

    internal static string GetAddonText(uint num)
    {
        return Svc.Data.GetExcelSheet<Addon>().GetRow(num).Text.ToString();
    }
}
