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
        _entry.Tooltip = "AutoRetainer — 多角色模式\n左鍵：開啟／關閉多角色模式\n右鍵：開啟／關閉主視窗";
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

    private static void OnClick(DtrInteractionEvent evt)
    {
        // 右鍵開關主視窗（再按一次關閉）。原本沒有分辨按鍵，右鍵也會切換多角色模式，
        // 等於跟左鍵重複，所以接管它不會損失既有功能。
        if (evt.ClickType == MouseClickType.Right)
        {
            P.AutoRetainerWindow.IsOpen ^= true;
            return;
        }

        MultiMode.Enabled = !MultiMode.Enabled;
        _lastState = null;
    }

    internal static void Dispose()
    {
        _entry?.Remove();
        _entry = null;
    }
}
