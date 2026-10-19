using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AutoRetainer.UI.NeoUI.InventoryManagementEntries.GCDeliveryEntries;
public sealed unsafe class GeneralSettings : InventoryManagemenrBase
{
    public override string Name { get; } = Loc.T("Grand Company Delivery/General Settings");

    public override void Draw()
    {
        ImGui.Checkbox(Loc.T("Enable Expert Delivery continuation"), ref C.AutoGCContinuation);
        ImGui.Indent();
        ImGuiEx.TextWrapped(Loc.T("""
            When Expert Delivery Continuation is enabled:
            - The plugin will automatically spend available Grand Company Seals to purchase items from the configured Exchange List.
            - If the Exchange List is empty, only Ventures will be purchased.

            After seals have been spent:
            - Expert Delivery will resume automatically.
            - The process will repeat until there are no eligible items left to deliver or no seals remaining.
            """));
        ImGui.Unindent();

        ImGui.Separator();
        ImGui.Checkbox(Loc.T("Multi Mode: hand in expert delivery items when ventures run low"), ref C.FullAutoGCDeliveryDeliverOnVentureExhaust);
        ImGuiEx.HelpMarker(Loc.T("During Multi Mode, when a character has this many ventures or fewer, it first goes to its Grand Company, hands in expert delivery items and spends seals by the exchange list (whatever the list leaves goes to ventures), and only then carries on with its retainers.\n\nA character is only sent when all of these hold: its retainers are enabled for Multi Mode; \"Teleport for retainers...\" is on for it, so it can get back to a summoning bell (a character with deployables also needs teleport to the free company house for deployables); its delivery mode is not Disabled; its Grand Company rank allows expert delivery; Lifestream is loaded; and it has something to hand in.\n\nIf the trip bought no ventures, one more purchase round is run before leaving. If that still buys none, the character is not sent again until its ventures go up or a pause runs out: 2 hours at first, doubling each time up to 24 hours."));
        ImGui.Indent();
        ImGuiEx.SetNextItemWidthScaled(100);
        ImGui.InputInt(Loc.T("Go when ventures are at or below"), ref C.FullAutoGCDeliveryDeliverOnVentureLessThan.ValidateRange(0, 65000), 0, 0);
        ImGui.Unindent();
        ImGui.Checkbox(Loc.T("Multi Mode: hand in expert delivery items when free inventory slots run low"), ref C.FullAutoGCDeliveryDeliverOnInventory);
        ImGuiEx.HelpMarker(Loc.T("During Multi Mode, when a character's bags (not the armoury chest) have this many free slots or fewer and hold something to hand in, it first goes to its Grand Company, hands in expert delivery items and spends seals by the exchange list, and only then carries on. This works for characters in Multi Mode for retainers or for deployables; the other conditions are the same as for the ventures option above (teleport to get back, delivery mode, Grand Company rank, Lifestream, home world).\n\nSet this well above what one round of retainers and deployables can bring back, otherwise the bags can fill up before it gets a chance to go.\n\nIf a trip ends with no more free slots than before and nothing handed in from the bags, this option is paused for that character: 2 hours at first, doubling each time up to 24 hours. The two options are judged and paused separately."));
        ImGui.Indent();
        ImGuiEx.SetNextItemWidthScaled(100);
        ImGui.InputInt(Loc.T("Go when free inventory slots are at or below"), ref C.FullAutoGCDeliveryInventory.ValidateRange(0, 140), 0, 0);
        ImGui.Unindent();
        ImGui.Checkbox(Loc.T("Teleport back home after handing in"), ref C.FullAutoGCDeliveryTpBackToHome);
        ImGuiEx.HelpMarker(Loc.T("As soon as the trip is over, teleport back with the same Multi Mode teleport settings used for retainers (private house, free company house, apartment or inn; the free company house when deployables are waiting). Without this, the character stays at the Grand Company until it next has retainers or deployables to process, and may log out there."));
        DrawVentureDeliveryPauses();
    }

    private static void DrawVentureDeliveryPauses()
    {
        var now = DateTime.Now;
        var paused = MultiModeVentureDelivery.Pauses.Where(x => x.UntilLocal > now).ToArray();
        if(paused.Length == 0) return;
        // 暫停是「這個角色為什麼沒去」的答案，列上要看得見；細節放 tooltip。
        DrawPauseLine(paused, MultiModeVentureDelivery.Trigger.Ventures, Loc.T("Paused after a trip that bought no ventures"),
            Loc.T("These characters are not sent to the Grand Company for ventures before the time shown, unless their ventures go up. The pause is kept only until the plugin is reloaded."));
        DrawPauseLine(paused, MultiModeVentureDelivery.Trigger.Inventory, Loc.T("Paused after a trip that freed no inventory space"),
            Loc.T("These characters are not sent to the Grand Company to free inventory space before the time shown. The pause is kept only until the plugin is reloaded."));
    }

    private static void DrawPauseLine(MultiModeVentureDelivery.PauseInfo[] paused, MultiModeVentureDelivery.Trigger kind, string label, string tooltip)
    {
        var names = paused.Where(x => x.Kind == kind).Select(x => $"{Censor.Character(x.Name, x.World)} ({x.UntilLocal:MM/dd HH:mm})").ToArray();
        if(names.Length == 0) return;
        ImGuiEx.TextWrapped(ImGuiColors.DalamudGrey, $"{label}: {string.Join(", ", names)}");
        ImGuiEx.Tooltip(tooltip);
    }
}