using AutoRetainer.Modules.Multi;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using ECommons.DalamudServices;

namespace AutoRetainer.Modules;

internal static class MultiModeDtr
{
    private static IDtrBarEntry _entry;
    private static bool? _lastState;

    internal static void Init()
    {
        _entry = Svc.DtrBar.Get("AutoRetainer.MultiMode");
        _entry.OnClick = OnClick;
        _entry.Tooltip = "點擊切換多角色模式 (Multi Mode)";
        _lastState = null;
    }

    internal static void Tick()
    {
        if(_entry == null) return;
        if(_lastState == MultiMode.Enabled) return;
        _lastState = MultiMode.Enabled;

        // bell icon identifies the entry, dot icon shows on/off state - game icon font has no
        // bell glyph, so this uses IconPayload/BitmapFontIcon (bitmap icons) instead of
        // SeIconChar (PUA glyph text), same technique as LazyLoot/WrathCombo's DTR entries
        _entry.Text = new SeString(
            new IconPayload(BitmapFontIcon.Alarm),
            new IconPayload(MultiMode.Enabled ? BitmapFontIcon.GreenDot : BitmapFontIcon.NoCircle));
    }

    private static void OnClick()
    {
        MultiMode.Enabled = !MultiMode.Enabled;
        _lastState = null;
    }

    internal static void Dispose()
    {
        _entry?.Remove();
        _entry = null;
    }
}
