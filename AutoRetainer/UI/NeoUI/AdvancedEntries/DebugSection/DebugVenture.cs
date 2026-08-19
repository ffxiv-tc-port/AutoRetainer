using AutoRetainer.Scheduler.Handlers;
using AutoRetainer.Scheduler.Tasks;
using Dalamud.Memory;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace AutoRetainer.UI.NeoUI.AdvancedEntries.DebugSection;

internal unsafe class DebugVenture : DebugSectionBase
{
    internal int VentureID = 0;
    internal string VentureName = "";
    public override void Draw()
    {
        {
            // AgentModule.Instance() 是 CS 手寫的包裝（uiModule == null ? null : uiModule->GetAgentModule()），
            // 合法回 null。原本只判了 GetAgentByInternalId 的回傳值，護不到 Instance() 本身。
            var agentModule = AgentModule.Instance();
            var agent = agentModule == null ? null : agentModule->GetAgentByInternalId((AgentId)140);
            if(agent != null && agent->IsAgentActive())
            {
                ImGuiEx.TextCopy($"{(nint)agent:X16}");
                ImGuiEx.Text($"{*(ushort*)((uint)agent + 456)}");
            }
        }
        if(TryGetAddonByName<AddonRetainerTaskAsk>("RetainerTaskAsk", out var addon) && IsAddonReady(&addon->AtkUnitBase))
        {
            // 按鈕還沒建構完成時畫「?」而不是「False」——後者會讓人以為已經確認過按鈕是停用的。
            ImGuiEx.Text($"Enabled: {Utils.GetButtonEnabled(addon->AssignButton)?.ToString() ?? "?"}");
        }

        foreach(var x in C.OfflineData)
        {
            foreach(var r in x.RetainerData)
            {
                var adata = Utils.GetAdditionalData(x.CID, r.Name);
                ImGuiEx.Text($"{x.Name}@{x.World} - {r.Name} last venture index: {adata.VenturePlanIndex}, next venture: {adata.GetNextPlannedVenture()}/{VentureUtils.GetVentureName(adata.GetNextPlannedVenture())}");
            }
        }
        ImGui.InputInt("Venture id", ref VentureID);
        ImGui.InputText("Venture name", ref VentureName, 100);
        //if (ImGui.Button("SearchVentureByName")) DuoLog.Information(RetainerHandlers.SearchVentureByName(VentureName).ToString());
        if(ImGui.Button("Clear Venture list")) DuoLog.Information(RetainerHandlers.ClearTaskSupplylist().ToString());
        if(ImGui.Button("SelectSpecificVenture Name")) DuoLog.Information(RetainerHandlers.SelectSpecificVentureByName(VentureName).ToString());
        if(ImGui.Button("TaskAssignHuntingVenture"))
        {
            TaskAssignHuntingVenture.Enqueue((uint)VentureID);
        }
        if(ImGui.Button("TaskAssignFieldExploration"))
        {
            TaskAssignFieldExploration.Enqueue((uint)VentureID);
        }
        if(ImGui.Button("Select"))
        {
            RetainerHandlers.SelectSpecificVenture((uint)VentureID);
        }
        if(ImGui.CollapsingHeader(Loc.T("Ventures")))
        {
            // 這個傾印是給 VentureUtils.GetAvailableVentureNames() 當對照用的（就是下面那個
            // CollapsingHeader），兩邊必須讀同一個陣列 = StringArrayType.RetainerTask。
            // 🔴 原本寫死 95：這個字面值是 2023-03-26 那次 commit 留下來的，之後從來沒跟著版本更新過。
            // 它不是 7.2→7.3 那個 +1 位移的受害者 —— 兩種世代底下 95 都不是探險陣列
            // （7.2 = OrchestrionPlayListSelect，7.3 = Orchestrion），是一顆放了兩年多的獨立既有 bug。
            // 🔴 改用具名列舉而不是換一個新的魔術數字：下次陣列再位移時它會自己跟著動。
            // ⚠️ 寫完整命名空間：本檔沒有 using FFXIVClientStructs.FFXIV.Component.GUI，
            // 而補 using 會讓裸寫的 RetainerTask 在別處有撞名風險（VentureUtils 就撞過 Lumina 的同名表格型別）。
            // 🔴 四層裸鏈：Framework（isPointer:true）→ UIModule（裸欄位）→ GetRaptureAtkModule()
            //    → 陣列。任一層 null 都是攔不到的 AVE，逐層判。
            var framework = CSFramework.Instance();
            var atkModule = framework == null || framework->UIModule == null
                ? null
                : framework->UIModule->GetRaptureAtkModule();
            var data = atkModule == null
                ? null
                : atkModule->AtkModule.GetStringArrayData(
                    (int)FFXIVClientStructs.FFXIV.Component.GUI.StringArrayType.RetainerTask);
            if(data != null)
            {
                for(var i = 0; i < data->AtkArrayData.Size; i++)
                {
                    var item = data->StringArray[i];
                    if(item != null)
                    {
                        var str = MemoryHelper.ReadSeStringNullTerminated((nint)(byte*)item);
                        ImGuiEx.Text($"{i}: {str.GetText()}");
                    }
                    else
                    {
                        ImGuiEx.Text($"{i}: null");
                    }
                }
            }
        }

        if(ImGui.CollapsingHeader("GetAvailableVentureNames"))
        {
            foreach(var x in VentureUtils.GetAvailableVentureNames())
            {
                ImGuiEx.Text($"{x}");
            }
        }
    }
}
