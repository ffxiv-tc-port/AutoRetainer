using AutoRetainer.Services;
using AutoRetainerAPI.Configuration;
using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace AutoRetainer.UI.Overlays;

internal unsafe class AutoGCHandinOverlay : Window
{
    internal float height;
    internal bool Allowed = false;
    public AutoGCHandinOverlay() : base("AutoRetainer GC Handin overlay", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.AlwaysUseWindowPadding | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoSavedSettings, true)
    {
        RespectCloseHotkey = false;
        IsOpen = true;
    }

    public override void Draw()
    {
        if(Allowed)
        {
            ImGui.Checkbox(Loc.T("Enable Automatic Expert Delivery"), ref AutoGCHandin.Operation);
        }
        if(C.OfflineData.TryGetFirst(x => x.CID == SvcEx.PlayerState.ContentId, out var d) && !AutoGCHandin.Operation)
        {
            ImGui.SameLine();
            ImGuiEx.SetNextItemWidthScaled(200);
            ImGuiEx.EnumCombo("##mode", ref d.GCDeliveryType, Loc.EnumNames<GCDeliveryType>());
            if(d.GCDeliveryType == GCDeliveryType.Hide_Gear_Set_Items)
            {
                ImGui.SameLine();
                ImGui.PushFont(UiBuilder.IconFont);
                ImGuiEx.Text(Lang.IconWarning);
                ImGui.PopFont();
            }
            if(d.GCDeliveryType == GCDeliveryType.Show_All_Items)
            {
                ImGui.SameLine();
                ImGui.PushFont(UiBuilder.IconFont);
                ImGuiEx.Text($"{Lang.IconWarning}{Lang.IconWarning}{Lang.IconWarning}");
                ImGui.PopFont();
            }
        }
        //1078	Priority Seal Allowance	Company seals earned are increased.	ui/icon/016000/016518.tex	0	0	All Classes	1	dk05th_stup0t		False	False	False	False	False	False	False	False	False	0	1	False	False	15	0	False	0	False	0	False	0	0	0	False
        if(!Svc.Objects.LocalPlayer.StatusList.Any(x => x.StatusId == 1078) && InventoryManager.Instance()->GetInventoryItemCount(14946) > 0)
        {
            ImGui.SameLine();
            ImGuiEx.Text(GradientColor.Get(ImGuiColors.DalamudRed, ImGuiColors.DalamudYellow), Loc.T("You can use Priority Seal Allowance"));
        }
        if(!Player.IsInHomeWorld)
        {
            ImGui.SameLine();
            ImGuiEx.Text(GradientColor.Get(ImGuiColors.DalamudRed, ImGuiColors.DalamudYellow), Loc.T("Foreign world. No FC points will be granted."));
        }
        height = ImGui.GetWindowSize().Y;
    }

    public override bool DrawConditions()
    {
        return Svc.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.OccupiedInQuestEvent] && (Allowed || (TryGetAddonByName<AtkUnitBase>("GrandCompanySupplyList", out var addon)
                // 🔴 NodeListCount > 20 只驗了上界、沒判 NodeList[5] 本身為 null —— 半套邊界檢查。
                //    這是每幀跑的疊加層開關條件,取不到時視為「不顯示」(＝原本節點不可見的行為)。
                //    ⚠️ NodeListCount > 20 保留不動:它擋的是「這不是完整版面」,與 IsNodeVisible
                //    內部的索引上界檢查不是同一件事,拿掉等於順手放寬既有條件。
                && addon->UldManager.NodeListCount > 20
                && Utils.IsNodeVisible(&addon->UldManager, 5)));
    }
}
