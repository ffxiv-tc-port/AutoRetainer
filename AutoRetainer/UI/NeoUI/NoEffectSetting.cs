namespace AutoRetainer.UI.NeoUI;

/// <summary>
/// 畫「已沒有讀取端」的設定：控制項停用、旁邊灰字標「（無作用）」，滑過與說明圖示都講實際行為。
/// </summary>
/// <remarks>
/// 只讀不寫：畫的是值的複本，使用者存在設定檔裡的值原樣保留。
/// 說明圖示都在 EndDisabled 之後畫，保持全亮。
/// </remarks>
internal static class NoEffectSetting
{
    internal static NuiBuilder NoEffectCheckbox(this NuiBuilder builder, string name, NuiBuilder.RefBoolDelegate value, string note)
    {
        var tag = Loc.T("(No effect)");
        return builder.Widget(name, label =>
        {
            var copy = value();
            ImGui.BeginDisabled();
            ImGui.Checkbox(label, ref copy);
            ImGui.EndDisabled();
            Tooltip(note);
            ImGui.SameLine();
            ImGuiEx.Text(ImGuiColors.DalamudGrey, tag);
        }, note);
    }

    internal static NuiBuilder NoEffectEnumComboFullWidth<T>(this NuiBuilder builder, string name, NuiBuilder.RefEnumDelegate<T> value, Func<T, bool> filter, IDictionary<T, string> names, string note) where T : Enum, IConvertible
    {
        var tag = Loc.T("(No effect)");
        return builder.Widget(name, label =>
        {
            ImGuiEx.TextWrapped(label);
            ImGui.SameLine();
            ImGuiEx.Text(ImGuiColors.DalamudGrey, tag);
            ImGuiEx.HelpMarker(note);
            ImGui.Indent();
            var copy = value();
            ImGuiEx.SetNextItemFullWidth();
            ImGui.BeginDisabled();
            ImGuiEx.EnumCombo($"##{label}", ref copy, filter, names);
            ImGui.EndDisabled();
            Tooltip(note);
            ImGui.Unindent();
        });
    }

    // ImGuiEx.Tooltip 內部的 hover 判斷不帶 AllowWhenDisabled，停用的控制項永遠不會出現說明。
    private static void Tooltip(string note)
    {
        if(ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGuiEx.SetTooltip(note);
    }
}
