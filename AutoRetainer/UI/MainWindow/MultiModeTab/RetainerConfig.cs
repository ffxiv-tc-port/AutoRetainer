using AutoRetainerAPI;
using AutoRetainerAPI.Configuration;

namespace AutoRetainer.UI.MainWindow.MultiModeTab;
public static unsafe class RetainerConfig
{
    public static void Draw(OfflineRetainerData ret, OfflineCharacterData data, AdditionalRetainerData adata)
    {
        ImGui.CollapsingHeader($"{Censor.Retainer(ret.Name)} - {Censor.Character(data.Name)} {Loc.T("Configuration")}  ##conf", ImGuiTreeNodeFlags.DefaultOpen | ImGuiTreeNodeFlags.Bullet | ImGuiTreeNodeFlags.OpenOnArrow);
        ImGuiEx.Text(Loc.T("Additional Post-venture Tasks:"));
        //ImGui.Checkbox($"Entrust Duplicates", ref adata.EntrustDuplicates);
        var selectedPlan = C.EntrustPlans.FirstOrDefault(x => x.Guid == adata.EntrustPlan);
        ImGuiEx.TextV(Loc.T("Entrust Items:"));
        if(!C.EnableEntrustManager) ImGuiEx.HelpMarker(Loc.T("Globally disabled in settings"), EColor.RedBright, FontAwesomeIcon.ExclamationTriangle.ToIconString());
        ImGui.SameLine();
        ImGui.SetNextItemWidth(150f);
        if(ImGui.BeginCombo($"##select", selectedPlan?.Name ?? Loc.T("Disabled"), ImGuiComboFlags.HeightLarge))
        {
            if(ImGui.Selectable(Loc.T("Disabled"))) adata.EntrustPlan = Guid.Empty;
            for(var i = 0; i < C.EntrustPlans.Count; i++)
            {
                var plan = C.EntrustPlans[i];
                ImGui.PushID(plan.Guid.ToString());
                if(ImGui.Selectable(plan.Name, plan == selectedPlan))
                {
                    adata.EntrustPlan = plan.Guid;
                }
                ImGui.PopID();
            }
            ImGui.EndCombo();
        }
        if(ImGuiEx.IconButtonWithText(FontAwesomeIcon.Copy, Loc.T("Copy entrust plan to...")))
        {
            ImGui.OpenPopup($"CopyEntrustPlanTo");
        }
        if(ImGui.BeginPopup("CopyEntrustPlanTo"))
        {
            if(ImGui.Selectable(Loc.T("To all other retainers of this character")))
            {
                var cnt = 0;
                foreach(var x in data.RetainerData)
                {
                    cnt++;
                    Utils.GetAdditionalData(data.CID, x.Name).EntrustPlan = adata.EntrustPlan;
                }
                Notify.Info(string.Format(Loc.T("Changed {0} retainers"), cnt));
            }
            if(ImGui.Selectable(Loc.T("To all other retainers without entrust plan of this character")))
            {
                // 🔴 計數器與通知都要在迴圈外：放在迴圈內的話 cnt 每圈重設，
                // 而且每個僱員各彈一次「已變更 1 位僱員」。
                var cnt = 0;
                foreach(var x in data.RetainerData)
                {
                    // 🔴 「沒有存入計畫」判斷的是**目標**僱員，不是來源僱員：
                    // 原本比的是 adata.EntrustPlan（來源自己的計畫），那個值在整個迴圈裡是常數，
                    // 條件因此退化成「全部套用」或「一個都不套用」。
                    // 這裡改成與下面「所有角色」那一段同一種寫法：先取目標的 additional data，
                    // 再看它指到的計畫還存不存在（Guid.Empty 或指向已刪除的計畫都算「沒有計畫」）。
                    var a = Utils.GetAdditionalData(data.CID, x.Name);
                    if(!C.EntrustPlans.Any(s => s.Guid == a.EntrustPlan))
                    {
                        a.EntrustPlan = adata.EntrustPlan;
                        cnt++;
                    }
                }
                Notify.Info(string.Format(Loc.T("Changed {0} retainers"), cnt));
            }
            if(ImGui.Selectable(Loc.T("To all other retainers of ALL characters")))
            {
                var cnt = 0;
                foreach(var offlineData in C.OfflineData)
                {
                    foreach(var x in offlineData.RetainerData)
                    {
                        Utils.GetAdditionalData(offlineData.CID, x.Name).EntrustPlan = adata.EntrustPlan;
                        cnt++;
                    }
                }
                Notify.Info(string.Format(Loc.T("Changed {0} retainers"), cnt));
            }
            if(ImGui.Selectable(Loc.T("To all other retainers without entrust plan of ALL characters")))
            {
                var cnt = 0;
                foreach(var offlineData in C.OfflineData)
                {
                    foreach(var x in offlineData.RetainerData)
                    {
                        // 🔴 這裡的 CID 必須跟著外圈的 offlineData 走：原本寫成 data.CID（目前這個角色），
                        // 等於拿「別的角色的僱員名字」去查／建目前角色的 additional data，
                        // 判斷與寫入都落在錯的角色上——上面「所有角色」那一段用的就是 offlineData.CID。
                        var a = Utils.GetAdditionalData(offlineData.CID, x.Name);
                        if(!C.EntrustPlans.Any(s => s.Guid == a.EntrustPlan))
                        {
                            a.EntrustPlan = adata.EntrustPlan;
                            cnt++;
                        }
                    }
                }
                Notify.Info(string.Format(Loc.T("Changed {0} retainers"), cnt));
            }
            ImGui.EndPopup();
        }
        ImGui.Checkbox(Loc.T("Withdraw/Deposit Gil"), ref adata.WithdrawGil);
        if(adata.WithdrawGil)
        {
            if(ImGui.RadioButton(Loc.T("Withdraw"), !adata.Deposit)) adata.Deposit = false;
            if(ImGui.RadioButton(Loc.T("Deposit"), adata.Deposit)) adata.Deposit = true;
            ImGuiEx.SetNextItemWidthScaled(200f);
            ImGui.InputInt(Loc.T("Amount, %"), ref adata.WithdrawGilPercent.ValidateRange(1, 100), 1, 10);
        }
        ImGui.Separator();
        Svc.PluginInterface.GetIpcProvider<ulong, string, object>(ApiConsts.OnRetainerSettingsDraw).SendMessage(data.CID, ret.Name);
        if(C.Verbose)
        {
            if(ImGui.Button(Loc.T("Fake ready")))
            {
                ret.VentureEndsAt = 1;
            }
            if(ImGui.Button(Loc.T("Fake unready")))
            {
                ret.VentureEndsAt = P.Time + 60 * 60;
            }
        }
    }
}
