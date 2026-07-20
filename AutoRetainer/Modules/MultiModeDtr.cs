using AutoRetainer.Modules.Multi;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Game.Text;
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

        var icon = MultiMode.Enabled ? SeIconChar.Circle : SeIconChar.Cross;
        _entry.Text = $"{icon.ToIconString()} 多角色模式";
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
