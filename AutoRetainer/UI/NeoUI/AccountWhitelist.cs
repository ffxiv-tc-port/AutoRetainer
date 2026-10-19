using ECommons.GameHelpers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AutoRetainer.UI.NeoUI;
public sealed unsafe class AccountWhitelist : NeoUIEntry
{
    public override void Draw()
    {
        ImGuiEx.TextWrapped(Loc.T("You may setup account whitelist. In the event you will log in using non-whitelisted account, AutoRetainer will not record any characters, retainers, or submarines."));
        if(C.WhitelistedAccounts.Count == 0)
        {
            ImGuiEx.TextWrapped(EColor.GreenBright, Loc.T("Current whitelist status: Disabled. To enable, add some account to it."));
        }
        else
        {
            ImGuiEx.TextWrapped(EColor.YellowBright, Loc.T("Current whitelist status: Enabled. To disable, remove all accounts from it."));
        }

        if(ImGuiEx.IconButtonWithText(FontAwesomeIcon.UserPlus, Loc.T("Add current account"), enabled: Player.Available))
        {
            // 不知道帳號時不加：把 0 加進白名單會被當成一個真的帳號。
            if(P.Memory.TryGetMyAccountId(out var accountId)) C.WhitelistedAccounts.Add(accountId);
            else Notify.Error("讀不到目前的帳號，沒有加入白名單。");
        }

        foreach(var x in C.WhitelistedAccounts)
        {
            ImGui.PushID(x.ToString());
            if(ImGuiEx.IconButton(FontAwesomeIcon.Trash))
            {
                new TickScheduler(() => C.WhitelistedAccounts.Remove(x));
            }
            ImGui.SameLine();
            ImGuiEx.TextV($"{Loc.T("Account ")}{x}");
            ImGui.PopID();
        }
    }
}