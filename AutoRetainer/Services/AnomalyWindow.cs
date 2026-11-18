using AutoRetainerAPI.Configuration;
using ECommons.GameHelpers;

namespace AutoRetainer.Services;

/// <summary>
/// 多角色模式跑著（或未登入）時，把「角色被排除、設定被改、流程被停」這類異常集中列在一個視窗，回來一眼看完。
/// 契約：只補充、不取代各處原本的 log／聊天訊息；每筆一律寫一行 Information；
/// 多角色模式關著且已登入時不開窗（原處訊息已經在聊天視窗）。每筆只存字串與時間，不存原生指標。
/// 只在框架執行緒呼叫 <see cref="Add"/>。
/// </summary>
public sealed class AnomalyWindow : Window
{
    /// <summary>一整晚掛著時的上限；超過就丟最舊的。</summary>
    private const int MaxEntries = 200;

    private readonly List<Anomaly> entries = [];

    public AnomalyWindow() : base($"{Loc.T("AutoRetainer has detected anomalies")}###AutoRetainerAnomalyWindow")
    {
        this.SetSizeConstraints(new(300, 200), new(float.MaxValue));
        RespectCloseHotkey = false;
        P.WindowSystem.AddWindow(this);
    }

    public override void Draw()
    {
        ImGuiEx.TextWrapped(Loc.T("Closing this window clears the list."));
        if(ImGuiEx.BeginDefaultTable("Anomalies", [Loc.T("Time"), "~" + Loc.T("Description"), Loc.T("Character")]))
        {
            foreach(var x in entries)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGuiEx.Text($"{x.Date.ToLocalTime():MM-dd HH:mm:ss}");
                ImGui.TableNextColumn();
                ImGuiEx.TextWrapped(x.Description);
                ImGui.TableNextColumn();
                if(string.IsNullOrEmpty(x.Character))
                    ImGuiEx.Text(ImGuiColors.DalamudGrey, "?");
                else
                    ImGuiEx.Text(Censor.Character(x.Character));
            }
            ImGui.EndTable();
        }
    }

    public void Add(string description)
    {
        var character = Player.NameWithWorld;
        PluginLog.Information($"[異常彙報] {Censor.Character(character ?? "")}: {description}");
        if(!MultiMode.Enabled && Svc.ClientState.IsLoggedIn) return;
        if(entries.Count >= MaxEntries) entries.RemoveAt(0);
        entries.Add(new(DateTimeOffset.Now, description, character));
        IsOpen = true;
    }

    public override void OnClose()
    {
        entries.Clear();
    }

    /// <summary>遠航探索三種失敗（燃料、修理材料、背包）照使用者在「應變措施」選的處置，給異常描述用的一句話。</summary>
    internal static string FailActionText(WorkshopFailAction action) => action switch
    {
        WorkshopFailAction.ExcludeChar => Loc.T("This character was excluded from deployables."),
        WorkshopFailAction.ExcludeVessel => Loc.T("This vessel was excluded from deployables."),
        WorkshopFailAction.StopPlugin => Loc.T("Multi Mode and deployables processing were stopped."),
        _ => "",
    };

    private readonly record struct Anomaly(DateTimeOffset Date, string Description, string Character);
}
