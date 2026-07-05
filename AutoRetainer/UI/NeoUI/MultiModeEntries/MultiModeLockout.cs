using ECommons.ExcelServices;

namespace AutoRetainer.UI.NeoUI.MultiModeEntries;
public class MultiModeLockout : NeoUIEntry
{
    public override string Path => Loc.T("Multi Mode/Region Lock");

    private int Num = 12;

    public override void Draw()
    {
        ImGuiEx.TextV("For");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(150f);
        ImGui.InputInt(Loc.T("hours..."), ref Num.ValidateRange(1, 10000));
        foreach(var x in Enum.GetValues<ExcelWorldHelper.Region>())
        {
            if(ImGuiEx.IconButtonWithText(FontAwesomeIcon.Lock, $"...do not log into {x} region"))
            {
                C.LockoutTime[x] = DateTimeOffset.Now.ToUnixTimeSeconds() + Num * 60 * 60;
            }
        }
        if(ImGuiEx.IconButtonWithText(FontAwesomeIcon.Unlock, Loc.T("Remove all locks")))
        {
            C.LockoutTime.Clear();
        }
    }
}
