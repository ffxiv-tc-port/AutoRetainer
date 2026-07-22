using ECommons.ExcelServices;

namespace AutoRetainer.UI.NeoUI.MultiModeEntries;
public class MultiModeLockout : NeoUIEntry
{
    public override string Path => Loc.T("Multi Mode/Region Lock");

    private int Num = 12;

    public override void Draw()
    {
        ImGuiEx.TextV(Loc.T("For"));
        ImGui.SameLine();
        ImGui.SetNextItemWidth(150f);
        ImGui.InputInt(Loc.T("hours..."), ref Num.ValidateRange(1, 10000));
        foreach(var x in Enum.GetValues<ExcelWorldHelper.Region>())
        {
            if(ImGuiEx.IconButtonWithText(FontAwesomeIcon.Lock, string.Format(Loc.T("...do not log into {0} region"), x)))
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
