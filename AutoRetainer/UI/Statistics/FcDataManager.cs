using AutoRetainerAPI.Configuration;
using AutoRetainer.Services;
using ECommons.GameHelpers;

namespace AutoRetainer.UI.Statistics;
public sealed class FcDataManager
{
    private FcDataManager() { }

    public void Draw()
    {
        ImGui.Checkbox(Loc.T("Update every 30 hours"), ref C.UpdateStaleFCData);
        ImGui.SameLine();
        if(ImGuiEx.Button(Loc.T("Update"), Player.Interactable))
        {
            S.FCPointsUpdater.ScheduleUpdateIfNeeded(true);
        }
        ImGui.SameLine();
        ImGui.Checkbox(Loc.T("Show only wallet FC"), ref C.DisplayOnlyWalletFC);
        if(ImGui.BeginTable("FCData", 7, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn(Loc.T("Name"), ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn(Loc.T("Characters"));
            ImGui.TableSetupColumn(Loc.T("Gil"));
            ImGui.TableSetupColumn(Loc.T("FC points"));
            ImGui.TableSetupColumn(Loc.T("Founded"));
            ImGui.TableSetupColumn(Loc.T("FC estate"));
            ImGui.TableSetupColumn($"##control");
            ImGui.TableHeadersRow();

            var totalGil = 0L;
            var totalPoint = 0L;

            var i = 0;
            foreach(var x in C.FCData)
            {
                if(x.Key == 0) continue;
                if(!x.Value.GilCountsTowardsChara && C.DisplayOnlyWalletFC) continue;
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGuiEx.TextV(C.NoNames ? string.Format(Loc.T("Free company {0}"), ++i) : x.Value.Name);

                ImGui.TableNextColumn();
                foreach(var c in C.OfflineData.Where(z => z.FCID == x.Key))
                {
                    ImGuiEx.Text(x.Value.HolderChara == c.CID && x.Value.GilCountsTowardsChara ? EColor.GreenBright : null, Censor.Character(c.Name, c.World));
                    if(ImGuiEx.HoveredAndClicked(Loc.T("Left click - Relog to this character")))
                    {
                        Svc.Commands.ProcessCommand($"/ays relog {c.Name}@{c.World}");
                    }
                    if(x.Value.GilCountsTowardsChara)
                    {
                        if(ImGuiEx.HoveredAndClicked(Loc.T("Right click - set as gil holder"), ImGuiMouseButton.Right))
                        {
                            x.Value.HolderChara = c.CID;
                        }
                    }
                }

                ImGui.TableNextColumn();
                if(x.Value.LastGilUpdate != -1 && x.Value.LastGilUpdate != 0)
                {
                    ImGuiEx.Text($"{x.Value.Gil:N0}");
                    totalGil += x.Value.Gil;
                    ImGuiEx.Tooltip(string.Format(Loc.T("Last updated {0}. Ctrl + click to reset"), UpdatedWhen(x.Value.LastGilUpdate)));
                    if(ImGuiEx.HoveredAndClicked() && ImGuiEx.Ctrl)
                    {
                        x.Value.LastGilUpdate = -1;
                        x.Value.Gil = 0;
                    }
                }
                else
                {
                    ImGuiEx.Text(Loc.T("Unknown"));
                }

                ImGui.TableNextColumn();
                if(x.Value.FCPointsLastUpdate != 0)
                {
                    ImGuiEx.Text($"{x.Value.FCPoints:N0}");
                    totalPoint += x.Value.FCPoints;
                    ImGuiEx.Tooltip(string.Format(Loc.T("Last updated {0}"), UpdatedWhen(x.Value.FCPointsLastUpdate)));
                }
                else
                {
                    ImGuiEx.Text(Loc.T("Unknown"));
                }

                ImGui.TableNextColumn();
                DrawFounded(x.Value);
                ImGui.TableNextColumn();
                DrawEstate(x.Value);

                ImGui.TableNextColumn();
                ImGui.PushFont(UiBuilder.IconFont);
                ImGuiEx.ButtonCheckbox($"\uf555##FC{x.Key}", ref x.Value.GilCountsTowardsChara, EColor.Green);
                ImGui.PopFont();
                ImGuiEx.Tooltip(Loc.T("Mark this free company as Wallet FC. Gil Display tab will include money of this FC."));
                ImGui.SameLine();
                if(ImGuiEx.IconButton(FontAwesomeIcon.Trash, $"{x.Key}Dele", enabled: ImGuiEx.Ctrl))
                {
                    new TickScheduler(() => C.FCData.Remove(x));
                }

                ImGuiEx.Tooltip(Loc.T("Hold CTRL and click to delete this FC. Note that if you will relog to that FC, it will appear again."));
            }

            ImGui.TableNextRow();
            ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, EColor.GreenDark.ToUint());
            ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg1, EColor.GreenDark.ToUint());
            ImGui.TableNextColumn();
            ImGuiEx.Text(Loc.T("TOTAL"));
            ImGui.TableNextColumn();
            ImGui.TableNextColumn();
            ImGuiEx.Text($"{totalGil:N0}");
            ImGui.TableNextColumn();
            ImGuiEx.Text($"{totalPoint:N0}");

            ImGui.EndTable();
        }


        string UpdatedWhen(long time)
        {
            var diff = DateTimeOffset.Now.ToUnixTimeMilliseconds() - time;
            if(diff < 1000L * 60) return Loc.T("just now");
            if(diff < 1000L * 60 * 60) return string.Format(Loc.T("{0} minute(s) ago"), (int)(diff / 1000 / 60));
            if(diff < 1000L * 60 * 60 * 60) return string.Format(Loc.T("{0} hour(s) ago"), (int)(diff / 1000 / 60 / 60));
            return string.Format(Loc.T("{0} day(s) ago"), (int)(diff / 1000 / 60 / 60 / 24));
        }
    }

    private static void DrawFounded(FCData fc)
    {
        if(FCHousingNotifier.IsPlausibleFounding(fc.FoundedAt))
        {
            ImGuiEx.Text(DateTimeOffset.FromUnixTimeSeconds(fc.FoundedAt).ToLocalTime().ToString("yyyy/MM/dd"));
            return;
        }
        ImGuiEx.Text(ImGuiColors.DalamudGrey, "?");
        ImGuiEx.Tooltip(Loc.T("Founding date not known yet. Open it once and it will be remembered: the Status tab of the Free Company window on a character of this free company, or right-click one of its members and choose \"View Company Profile\"."));
    }

    private static void DrawEstate(FCData fc)
    {
        var checkedWhen = fc.EstateStateUpdate > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(fc.EstateStateUpdate).ToLocalTime().ToString("yyyy/MM/dd HH:mm") : "";
        switch(fc.EstateState)
        {
            case FCEstateState.HasEstate:
                ImGuiEx.Text(Loc.T("Owned"));
                ImGuiEx.Tooltip(string.Format(Loc.T("Last confirmed {0}"), checkedWhen));
                return;
            case FCEstateState.NoEstate:
                var days = FCHousingNotifier.GetDaysUntilEligible(fc);
                if(days == null)
                {
                    ImGuiEx.Text(ImGuiColors.DalamudYellow, Loc.T("None (?)"));
                    ImGuiEx.Tooltip(string.Format(Loc.T("No estate as of {0}. Whether it can buy land yet is unknown until the founding date is known."), checkedWhen));
                }
                else if(days <= 0)
                {
                    ImGuiEx.Text(EColor.GreenBright, Loc.T("Can buy land"));
                    ImGuiEx.Tooltip(string.Format(Loc.T("No estate as of {0}, and founded at least {1} days ago. The game also requires FC rank and member count conditions."), checkedWhen, C.FCHousingEligibleDays));
                }
                else
                {
                    ImGuiEx.Text(string.Format(Loc.T("{0} more day(s)"), days));
                    ImGuiEx.Tooltip(string.Format(Loc.T("No estate as of {0}. Reaches {1} days since founding in {2} day(s)."), checkedWhen, C.FCHousingEligibleDays, days));
                }
                return;
            default:
                ImGuiEx.Text(ImGuiColors.DalamudGrey, "?");
                ImGuiEx.Tooltip(Loc.T("Not checked yet. Log in once with a character of this free company, or open its profile."));
                return;
        }
    }

    // 金幣分頁開著時每幀對每個角色×每個部隊各呼叫一次；用索引走清單，不配置閉包、委派與列舉器。結果與 TryGetFirst 相同（取第一個符合的）。
    public OfflineCharacterData GetHolderChara(ulong fcid, FCData data)
    {
        var list = C.OfflineData;
        if(list == null) return null;
        for(var i = 0; i < list.Count; i++)
        {
            var chara = list[i];
            if(chara.FCID == fcid && chara.CID == data.HolderChara) return chara;
        }
        for(var i = 0; i < list.Count; i++)
        {
            var fchara = list[i];
            if(fchara.FCID == fcid)
            {
                data.HolderChara = fchara.CID;
                return fchara;
            }
        }
        return null;
    }
}
