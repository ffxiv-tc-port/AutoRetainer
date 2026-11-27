using AutoRetainerAPI;
using AutoRetainerAPI.Configuration;

namespace AutoRetainer.UI.MainWindow.MultiModeTab;
public static unsafe class RetainerConfig
{
    /// <summary>角色層級的設定，每位僱員的設定頁都畫同一份。選單只列稀有品繳交循環會拜訪的僱員。</summary>
    private static void DrawTradeableGearHub(OfflineCharacterData data)
    {
        C.TradeableGearHub.TryGetValue(data.CID, out var hubId);
        var hub = hubId == 0 ? null : data.RetainerData.FirstOrDefault(x => x.RetainerID == hubId);
        var label = hubId == 0 ? Loc.T("Not selected") : hub == null ? Loc.T("(retainer not found)") : Censor.Retainer(hub.Name);
        ImGuiEx.TextV(Loc.T("Tradeable gear hub:"));
        ImGui.SameLine();
        ImGuiEx.SetNextItemWidthScaled(150f);
        if(ImGui.BeginCombo("##tradeableGearHub", label, ImGuiComboFlags.HeightLarge))
        {
            if(ImGui.Selectable(Loc.T("Not selected"), hubId == 0)) C.TradeableGearHub.Remove(data.CID);
            foreach(var name in GCExpertDeliveryLoop.ResolveRetainers(data))
            {
                var r = data.RetainerData.FirstOrDefault(x => x.Name == name);
                if(r == null || r.RetainerID == 0) continue;
                ImGui.PushID(r.RetainerID.ToString());
                if(ImGui.Selectable(Censor.Retainer(r.Name), r.RetainerID == hubId)) C.TradeableGearHub[data.CID] = r.RetainerID;
                ImGui.PopID();
            }
            ImGui.EndCombo();
        }
        ImGuiEx.HelpMarker(Loc.T("Tradeable gear (gear that is not untradable) is entrusted only to this retainer; the other retainers no longer take tradeable gear. When this retainer has no room, tradeable gear stays in your inventory. While your inventory has fewer free slots than the multi mode minimum, gear is entrusted the usual way. Only retainers visited by the expert delivery loop can be chosen. Untradable gear and items listed one by one in the entrust plan are not affected; with \"entrust only deliverable rare items\" on, gear that cannot be delivered still stays on you. If this retainer is not opened in a round, it is not opened just for this; the gear waits for its next turn."));
        if(hubId != 0 && !TradeableGearHub.TryGetHubName(data, out _))
        {
            ImGuiEx.Text(EColor.RedBright, Loc.T("The tradeable gear hub is no longer visited by the expert delivery loop (or was not found); gear is entrusted the usual way until you pick another one."));
        }
    }

    public static void Draw(OfflineRetainerData ret, OfflineCharacterData data, AdditionalRetainerData adata)
    {
        ImGui.CollapsingHeader($"{Censor.Retainer(ret.Name)} - {Censor.Character(data.Name)} {Loc.T("Configuration")}  ##conf", ImGuiTreeNodeFlags.DefaultOpen | ImGuiTreeNodeFlags.Bullet | ImGuiTreeNodeFlags.OpenOnArrow);
        ImGuiEx.Text(Loc.T("Additional Post-venture Tasks:"));
        //ImGui.Checkbox($"Entrust Duplicates", ref adata.EntrustDuplicates);
        var selectedPlan = C.EntrustPlans.FirstOrDefault(x => x.Guid == adata.EntrustPlan);
        ImGuiEx.TextV(Loc.T("Entrust Items:"));
        if(!C.EnableEntrustManager) ImGuiEx.HelpMarker(Loc.T("Globally disabled in settings"), EColor.RedBright, FontAwesomeIcon.ExclamationTriangle.ToIconString());
        ImGui.SameLine();
        ImGuiEx.SetNextItemWidthScaled(150f);
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
        DrawTradeableGearHub(data);
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
                    // 🔴 「沒有存入計畫」判斷的是**目標**僱員，不是來源僱員（adata.EntrustPlan 在整個迴圈裡是常數，
                    // 比它會退化成「全部套用」或「一個都不套用」）：先取目標的 additional data，
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
                        // 🔴 這裡的 CID 必須跟著外圈的 offlineData 走：用 data.CID（目前這個角色）的話，
                        // 等於拿「別的角色的僱員名字」去查／建目前角色的 additional data，
                        // 判斷與寫入都落在錯的角色上。
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
        // 📌 批量套用存取 Gil：照上面「複製委託計畫到...」的先例，把**目前這位**的三個欄位（開關／提取或存入／百分比）
        //    複製到本角色或所有角色的全部僱員。要「全開」＝先把這一位設成想要的樣子再按套用。
        if(ImGuiEx.IconButtonWithText(FontAwesomeIcon.Copy, Loc.T("Copy Gil settings to...")))
        {
            ImGui.OpenPopup($"CopyGilSettingsTo");
        }
        if(ImGui.BeginPopup("CopyGilSettingsTo"))
        {
            if(ImGui.Selectable(Loc.T("To all other retainers of this character")))
            {
                var cnt = 0;
                foreach(var x in data.RetainerData)
                {
                    var a = Utils.GetAdditionalData(data.CID, x.Name);
                    a.WithdrawGil = adata.WithdrawGil;
                    a.Deposit = adata.Deposit;
                    a.WithdrawGilPercent = adata.WithdrawGilPercent;
                    cnt++;
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
                        // 🔴 CID 跟著外圈 offlineData 走（理由同上面委託計畫那一段）。
                        var a = Utils.GetAdditionalData(offlineData.CID, x.Name);
                        a.WithdrawGil = adata.WithdrawGil;
                        a.Deposit = adata.Deposit;
                        a.WithdrawGilPercent = adata.WithdrawGilPercent;
                        cnt++;
                    }
                }
                Notify.Info(string.Format(Loc.T("Changed {0} retainers"), cnt));
            }
            ImGui.EndPopup();
        }
        ImGui.Separator();
        DrawEventProviders.RetainerSettingsDraw.GetIfSubscribed()?.SendMessage(data.CID, ret.Name);
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
