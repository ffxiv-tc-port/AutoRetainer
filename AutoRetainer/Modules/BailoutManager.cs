using AutoRetainer.Modules.Voyage;
using ECommons.Automation.UIInput;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace AutoRetainer.Modules;

internal static unsafe class BailoutManager
{
    internal static bool SimulateStuckOnQuit = false;
    internal static bool SimulateStuckOnVoyagePanel = false;
    internal static long NoSelectString = long.MaxValue;
    internal static long CharaSelectStuck = long.MaxValue;
    internal static bool IsLogOnTitleEnabled = false;

    internal static void Tick()
    {
        if(C.EnableBailout)
        {
            if(SchedulerMain.PluginEnabled || (MultiMode.Enabled && VoyageUtils.IsInVoyagePanel()))
            {
                if(!Utils.IsBusy && !VoyageScheduler.Enabled && TryGetAddonByName<AtkUnitBase>("SelectString", out var addon) && IsAddonReady(addon))
                {
                    if(Environment.TickCount64 - NoSelectString > C.BailoutTimeout * 1000)
                    {
                        // 🔴 BailoutTimeout 沒有下界(設 0 時每個 GenericThrottle 窗都可重按關閉中的窗):同一扇只送一次,
                        //    與 TrySelectSpecificEntry 共用 SelectString 那把 key。
                        if(Utils.GenericThrottle && DialogGuards.TryPressOnce("SelectString", (nint)addon, "Bailout.CloseSelectString"))
                        {
                            DuoLog.Warning($"[Bailout] Closing stuck SelectString window");
                            Callback.Fire(addon, true, -1);
                            NoSelectString = Environment.TickCount64;
                        }
                    }
                }
                else
                {
                    NoSelectString = Environment.TickCount64;
                }
            }

            if(!Svc.ClientState.IsLoggedIn && C.EnableCharaSelectBailout)
            {
                if(MultiMode.Enabled)
                {
                    // AgentLobby 取得器合法回 null；下面會讀 lobby->AgentInterface / TemporaryLocked。
                    // 拿不到就整段條件不成立 ⇒ 走 else 分支重設卡住計時，等於「這一輪不脫困」，
                    // 不會拿 null 去解參考。
                    var lobby = AgentLobby.Instance();
                    if(lobby != null && !Utils.IsBusy && !TryGetAddonByName<AtkUnitBase>("SelectOk", out _) && TryGetAddonByName<AtkUnitBase>("_CharaSelectReturn", out var addon) && IsAddonReady(addon) && (!lobby->AgentInterface.IsAgentActive() || !lobby->TemporaryLocked))
                    {
                        if(Environment.TickCount64 - CharaSelectStuck > 10 * 1000)
                        {
                            if(Utils.GenericThrottle)
                            {
                                DuoLog.Warning($"[Bailout] Backing out of CharaSelect");
                                addon->GetComponentButtonById(4)->ClickAddonButton(addon);
                                CharaSelectStuck = Environment.TickCount64;
                                EzThrottler.Throttle("MultiModeAfkOnTitleLogin", 60000, true);
                                IsLogOnTitleEnabled = true;
                            }
                        }
                    }
                    else
                    {
                        CharaSelectStuck = Environment.TickCount64;
                    }
                }
                else
                {
                    IsLogOnTitleEnabled = false;
                    CharaSelectStuck = long.MaxValue;
                }
            }

            if(!Svc.ClientState.IsLoggedIn && C.ResolveConnectionErrors && Utils.GetRemainingSessionMiliSeconds() > 10 * 60 * 1000 && MultiMode.Enabled)
            {
                if(TryGetAddonByName<AtkUnitBase>("Dialogue", out var addon) && IsAddonReady(addon))
                {
                    if(EzThrottler.Throttle("ClickDialogueOk", 10000))
                    {
                        addon->GetComponentButtonById(4)->ClickAddonButton(addon);
                        EzThrottler.Throttle("MultiModeAfkOnTitleLogin", 60000, true);
                        IsLogOnTitleEnabled = true;
                    }
                }
            }
        }
    }
}
