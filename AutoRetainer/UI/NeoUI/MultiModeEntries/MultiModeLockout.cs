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
        // 用 AllRegions() 而不是 Enum.GetValues<ExcelWorldHelper.Region>()——後者只有
        // JP/NA/EU/OC 四個具名列舉值,會漏掉台服(WorldDCGroupType.Region=8,無具名值),
        // 導致台服使用者完全沒有「鎖台服」的按鈕可按。
        foreach(var x in ExcelWorldHelper.AllRegions())
        {
            if(ImGuiEx.IconButtonWithText(FontAwesomeIcon.Lock, string.Format(Loc.T("...do not log into {0} region"), x.GetRegionDisplayName())))
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
