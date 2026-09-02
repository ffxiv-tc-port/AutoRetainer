using Dalamud.Utility;
using ECommons.Automation;
using ECommons.ExcelServices;
using ECommons.GameHelpers;
using ECommons.Throttlers;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace AutoRetainer.Scheduler.Tasks;
public static unsafe class TaskChangeCharacter
{
    public static void Enqueue(string currentWorld, string charaName, string charaWorld, int account)
    {
        if(Svc.ClientState.IsLoggedIn)
        {
            EnqueueLogout();
        }
        EnqueueLogin(currentWorld, charaName, charaWorld, account);
    }

    public static void EnqueueLogout()
    {
        P.TaskManager.Enqueue(Logout);
        P.TaskManager.Enqueue(SelectYesLogout, new(timeLimitMS: 100000));
    }

    public static void EnqueueLogin(string currentWorld, string charaName, string charaWorld, int account)
    {
        BailoutManager.IsLogOnTitleEnabled = false;
        if((int)Svc.Data.Language < 4)
        {
            var dc = (int)ExcelWorldHelper.Get(currentWorld).Value.DataCenter.RowId;
            PluginLog.Information($"Enqueue login: world={currentWorld}, charaName: {charaName}, charaWorld={charaWorld}, acc={account}, dc={dc}");
            if(dc == 0)
            {
                DuoLog.Warning($"Invalid data for {charaName}@{charaWorld}. Attempting to auto-fix...");
                currentWorld = charaWorld;
                dc = (int)ExcelWorldHelper.Get(currentWorld).Value.DataCenter.RowId;
                if(dc == 0)
                {
                    DuoLog.Error("Failed to fix world data. Log in manually.");
                    return;
                }
            }
            P.TaskManager.Enqueue(ClickSelectDataCenter, new(timeLimitMS: 1000000));
            P.TaskManager.Enqueue(() => SelectDataCenter(dc), $"Connect to DC {dc}");
            P.TaskManager.Enqueue(() => SelectServiceAccount(account), $"SelectServiceAccount {account}");
        }
        else
        {
            P.TaskManager.Enqueue(ClickStart);
        }
        P.TaskManager.Enqueue(() => SelectCharacter(charaName, charaWorld), $"Select chara {charaName}@{charaWorld}", new(timeLimitMS: 1000000));
        P.TaskManager.Enqueue(ConfirmLogin);
        if(C.PostLoginSceneSettleDelay > 0)
        {
            PluginLog.Information($"Waiting {C.PostLoginSceneSettleDelay}s for scene to settle after login before continuing");
            P.TaskManager.EnqueueDelay(C.PostLoginSceneSettleDelay * 1000);
        }
    }

    public static bool? SelectYesLogout()
    {
        if(!Svc.ClientState.IsLoggedIn) return true;
        var addon = Utils.GetSpecificYesno(Svc.Data.GetExcelSheet<Addon>()?.GetRow(115).Text.ToDalamudString().GetText());
        if(addon == null || !IsAddonReady(addon)) return false;
        // 🔴 這支按完 return false 持續輪詢到登出:登出確認框關閉中三關全過,同一扇只按一次。
        if(Utils.GenericThrottle && EzThrottler.Throttle("ConfirmLogout") && DialogGuards.TryPressOnce("SelectYesno", (nint)addon, "ConfirmLogout"))
        {
            new AddonMaster.SelectYesno((nint)addon).Yes();
            return false;
        }
        return false;
    }

    public static bool? Logout()
    {
        if(C.DontLogout) return null;
        var addon = Utils.GetSpecificYesno(Svc.Data.GetExcelSheet<Addon>()?.GetRow(115).Text.ToDalamudString().GetText());
        if(addon != null) return true;
        var isLoggedIn = Svc.Condition.Any();
        if(!isLoggedIn) return true;

        if(Player.Interactable && !Player.IsAnimationLocked && Utils.GenericThrottle && EzThrottler.Throttle("InitiateLogout"))
        {
            Chat.ExecuteCommand("/logout");
            return false;
        }
        return false;
    }

    public static bool? SelectServiceAccount(int account)
    {
        if(TryGetAddonByName<AtkUnitBase>("_CharaSelectWorldServer", out _))
        {
            return true;
        }
        if(TryGetAddonMaster<AddonMaster.SelectString>(out var m) && m.IsAddonReady)
        {
            var compareTo = Svc.Data.GetExcelSheet<Lobby>()?.GetRow(11).Text.GetText();
            if(m.Text == compareTo)
            {
                // 原本完全沒有節流;選項按下即關窗,同一扇 SelectString 只按一次。
                if(!DialogGuards.TryPressOnce("SelectString", (nint)m.Base, "SelectServiceAccount")) return false;
                m.Entries[account].Select();
                return true;
            }
        }
        else
        {
            Utils.RethrottleGeneric();
        }
        return false;
    }

    public static bool? ClickSelectDataCenter()
    {
        if(TryGetAddonByName<AtkUnitBase>("TitleDCWorldMap", out var addon) && addon->IsVisible)
        {
            PluginLog.Information($"Visible");
            Utils.RethrottleGeneric();
            return true;
        }
        if(TryGetAddonMaster<AddonMaster._TitleMenu>(out var m) && m.IsReady)
        {
            // 標題選單按下後是隱藏不是拆除,位址會留著:走 15 幀的例行逃生口,補按仍受 500ms 節流。
            if(Utils.GenericThrottle && EzThrottler.Throttle("ClickTitleMenuStart") && DialogGuards.TryPressOnce("_TitleMenu", (nint)m.Base, "ClickSelectDataCenter", escapeIsRoutine: true))
            {
                m.DataCenter();
                return false;
            }
        }
        else
        {
            Utils.RethrottleGeneric();
        }
        return false;
    }

    public static bool? ClickStart()
    {
        if(TryGetAddonByName<AtkUnitBase>("_CharaSelectListMenu", out var addon) && addon->IsVisible)
        {
            PluginLog.Information($"Visible");
            Utils.RethrottleGeneric();
            return true;
        }
        if(TryGetAddonMaster<AddonMaster._TitleMenu>(out var m) && m.IsReady)
        {
            if(Utils.GenericThrottle && EzThrottler.Throttle("ClickTitleMenuStart") && DialogGuards.TryPressOnce("_TitleMenu", (nint)m.Base, "ClickStart", escapeIsRoutine: true))
            {
                m.Start();
                return false;
            }
        }
        else
        {
            Utils.RethrottleGeneric();
        }
        return false;
    }

    public static bool? SelectDataCenter(int dc)
    {
        if(TryGetAddonMaster<AddonMaster.TitleDCWorldMap>(out var m) && m.IsAddonReady)
        {
            if(Utils.GenericThrottle && EzThrottler.Throttle("ClickDCSelect") && DialogGuards.TryPressOnce("TitleDCWorldMap", (nint)m.Base, "ClickDCSelect"))
            {
                m.Select(dc);
                return true;
            }
        }
        else
        {
            Utils.RethrottleGeneric();
        }
        return false;
    }

    public static (string Name, string World)? Expected = null;

    public static bool? SelectCharacter(string name, string world)
    {
        Expected = (name, world);
        if(TryGetAddonByName<AtkUnitBase>("SelectYesno", out _))
        {
            Utils.RethrottleGeneric();
            return true;
        }
        if(TryGetAddonByName<AtkUnitBase>("SelectOk", out _))
        {
            Utils.RethrottleGeneric();
            return true;
        }
        if(TryGetAddonMaster<AddonMaster._CharaSelectListMenu>(out var m) && m.IsAddonReady && TryGetAddonMaster<AddonMaster._CharaSelectWorldServer>(out var mw))
        {
            if(m.TemporarilyLocked) return false;
            if(mw.Worlds.Length == 0) return false;
            foreach(var c in m.Characters)
            {
                if(c.Name == name && ExcelWorldHelper.GetName(c.HomeWorld) == world)
                {
                    // 角色清單在登入前不會關:帶參數組(哪一格),同一格 15 幀內不重送。
                    if(Utils.GenericThrottle && EzThrottler.Throttle("SelectChara") && DialogGuards.TryPressOnce("_CharaSelectListMenu", (nint)m.Base, "SelectChara", $"Login{c.Index}", escapeIsRoutine: true))
                    {
                        /*if (!c.IsSelected)
                        {
                            c.Select();
                        }
                        else
                        {
                            c.Login();
                        }*/
                        c.Login();
                    }
                    return false;
                }
            }
            foreach(var w in mw.Worlds)
            {
                if(w.Name == world)
                {
                    if(Utils.GenericThrottle && EzThrottler.Throttle("SelectWorld") && DialogGuards.TryPressOnce("_CharaSelectWorldServer", (nint)mw.Base, "SelectWorld", $"World{w.Index}", escapeIsRoutine: true))
                    {
                        w.Select();
                    }
                    return false;
                }
            }
        }
        else
        {
            Utils.RethrottleGeneric();
        }
        return false;
    }

    public static bool? ConfirmLogin()
    {
        if(TryGetAddonByName<AtkUnitBase>("SelectOk", out _))
        {
            return true;
        }
        if(TryGetAddonMaster<AddonMaster.SelectYesno>(out var m) && m.IsAddonReady)
        {
            var text = m.Text;
            if(!DialogGuards.TextIsUnstable(text) && text.ContainsAny(StringComparison.OrdinalIgnoreCase, Lang.LogInPartialText))
            {
                if(Utils.GenericThrottle && EzThrottler.Throttle("ConfirmLogin") && DialogGuards.TryPressOnce("SelectYesno", (nint)m.Base, "ConfirmLogin"))
                {
                    m.Yes();
                    return true;
                }
            }
        }
        else
        {
            Utils.RethrottleGeneric();
        }
        return false;
    }
}
