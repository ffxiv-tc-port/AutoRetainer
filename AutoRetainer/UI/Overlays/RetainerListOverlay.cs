using AutoRetainer.Internal;
using AutoRetainer.Scheduler.Handlers;
using AutoRetainer.Scheduler.Tasks;
using AutoRetainerAPI;
using AutoRetainerAPI.Configuration;
using Dalamud.Interface.Components;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace AutoRetainer.UI.Overlays;

internal unsafe class RetainerListOverlay : Window
{
    private float height;
    internal volatile string PluginToProcess = null;

    public RetainerListOverlay() : base("AutoRetainer retainerlist overlay", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoFocusOnAppearing, true)
    {
        P.WindowSystem.AddWindow(this);
        RespectCloseHotkey = false;
        IsOpen = true;
    }

    public override bool DrawConditions()
    {
        if(!C.UIBar) return false;
        if(Svc.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.OccupiedSummoningBell] && TryGetAddonByName<AtkUnitBase>("RetainerList", out var addon) && IsAddonReady(addon))
        {
            Position = new(addon->X, addon->Y - height);
            return true;
        }
        return false;
    }

    public override void PreDraw()
    {
        // Dalamud 的 Window 基底類別在 PreDraw() 裡推每視窗不透明度(標題列右鍵選單的
        // 「不透明度」滑桿)。沒有呼叫 base 會讓那個內建功能對本視窗靜默半失效。
        base.PreDraw();
        //ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
    }

    public override void Draw()
    {
        var e = SchedulerMain.PluginEnabled;

        // 這一列就在僱員列表正上方，是「整理包包時想先讓 AutoRetainer 住手」最會被點到的地方。
        // 多角模式執行中原本要按住 CTRL 才點得動，使用者裁定改成永遠可介入;
        // 介入之後排程器不會卡住的理由見 SchedulerMain.SetEnabledByUser 的註解。
        if(ImGui.Checkbox(Loc.T("Enable AutoRetainer"), ref e))
        {
            P.WasEnabled = false;
            SchedulerMain.SetEnabledByUser(e, PluginEnableReason.Manual);
        }
        if(MultiMode.Active)
        {
            ImGuiComponents.HelpMarker(Loc.T(SharedText.MultiModeOverridesThisOption));
        }
        if(P.WasEnabled)
        {
            ImGui.SameLine();
            ImGuiEx.Text(GradientColor.Get(ImGuiColors.DalamudGrey, ImGuiColors.DalamudGrey3, 500), Loc.T("Paused"));
        }
        if(C.MultiModeUIBar)
        {
            ImGui.SameLine();
            if(ImGui.Checkbox(Loc.T("MultiMode"), ref MultiMode.Enabled))
            {
                MultiMode.OnMultiModeEnabled();
                if(MultiMode.Active)
                {
                    SchedulerMain.EnablePlugin(PluginEnableReason.MultiMode);
                }
            }
        }

        Svc.PluginInterface.GetIpcProvider<object>(ApiConsts.OnMainControlsDraw).SendMessage();

        ImGui.SameLine();

        if(ImGuiEx.IconButton($"{Lang.IconSettings}##Open plugin interface"))
        {
            Svc.Commands.ProcessCommand("/ays");
        }
        ImGuiEx.Tooltip(Loc.T("Open Plugin Settings"));
        if(!P.TaskManager.IsBusy)
        {
            ImGui.SameLine();
            if(ImGuiEx.IconButton($"{Lang.IconDuplicate}##Entrust all duplicates"))
            {
                // Wrapped so an abort part way through the batch is reported and the retainer window
                // it was left sitting in gets closed - see RetainerBulkOperation.
                RetainerBulkOperation.Enqueue(Loc.T("Quick Entrust"), () =>
                {
                    for(var i = 0; i < GameRetainerManager.Count; i++)
                    {
                        var ret = GameRetainerManager.Retainers[i];
                        if(ret.Available)
                        {
                            var adata = Utils.GetAdditionalData(Data.CID, ret.Name);
                            var selectedPlan = C.EntrustPlans.FirstOrDefault(x => x.Guid == adata.EntrustPlan);
                            if(selectedPlan != null)
                            {
                                P.TaskManager.Enqueue(() => RetainerListHandlers.SelectRetainerByName(ret.Name.ToString()));
                                TaskEntrustDuplicates.EnqueueNew(selectedPlan);
                                if(C.RetainerMenuDelay > 0)
                                {
                                    TaskWaitSelectString.Enqueue(C.RetainerMenuDelay);
                                }
                                P.TaskManager.Enqueue(RetainerHandlers.SelectQuit);
                            }
                            else
                            {
                                //Notify.Error($"No entrust plan found for retainer {ret.Name}");
                            }

                        }
                    }
                });
            }
            ImGuiEx.Tooltip(Loc.T("Quick Entrust"));

            ImGui.SameLine();
            if(ImGuiEx.IconButton($"{Lang.IconGil}##WithdrawGil"))
            {
                RetainerBulkOperation.Enqueue(Loc.T("Quick Withdraw Gil"), () =>
                {
                    for(var i = 0; i < GameRetainerManager.Count; i++)
                    {
                        var ret = GameRetainerManager.Retainers[i];
                        if(ret.Available)
                        {
                            P.TaskManager.Enqueue(() => RetainerListHandlers.SelectRetainerByName(ret.Name.ToString()));
                            TaskWithdrawGil.Enqueue(100);

                            if(C.RetainerMenuDelay > 0)
                            {
                                TaskWaitSelectString.Enqueue(C.RetainerMenuDelay);
                            }
                            P.TaskManager.Enqueue(RetainerHandlers.SelectQuit);
                        }
                    }
                });
            }
            ImGuiEx.Tooltip(Loc.T("Quick Withdraw Gil"));

            {
                ImGui.SameLine();
                if(ImGuiEx.IconButton($"{Lang.IconFire}##vendoritems"))
                {
                    // Only the manual button is wrapped: EnqueueVendorItemsByRetainer is also called
                    // from inside an already-running chain (the "itemsell" command), where the queue
                    // is busy by definition and the sentinel would belong to the wrong batch.
                    RetainerBulkOperation.Enqueue(Loc.T("Quick Vendor Items"), Utils.EnqueueVendorItemsByRetainer);
                }
                if(ImGui.IsItemClicked(ImGuiMouseButton.Right))
                {
                    ImGui.OpenPopup(Loc.T("QuickVendorPopup"));
                }
                ImGuiEx.Tooltip(Loc.T("Quick Vendor Items"));
                if(ImGui.BeginPopup(Loc.T("QuickVendorPopup")))
                {
                    if(ImGui.Selectable(Loc.T("Sell items from Quick Venture List")))
                    {
                        RetainerBulkOperation.Enqueue(Loc.T("Sell items from Quick Venture List"), () =>
                        {
                            for(var i = 0; i < GameRetainerManager.Count; i++)
                            {
                                var ret = GameRetainerManager.Retainers[i];
                                if(ret.Available)
                                {
                                    P.TaskManager.Enqueue(() => RetainerListHandlers.SelectRetainerByName(ret.Name.ToString()));
                                    TaskVendorItems.Enqueue(true);

                                    if(C.RetainerMenuDelay > 0)
                                    {
                                        TaskWaitSelectString.Enqueue(C.RetainerMenuDelay);
                                    }
                                    P.TaskManager.Enqueue(RetainerHandlers.SelectQuit);
                                    P.TaskManager.Enqueue(RetainerHandlers.ConfirmCantBuyback);
                                    break;
                                }
                            }
                        });
                    }
                    ImGui.EndPopup();
                }
            }

            PluginToProcess = null;
            Svc.PluginInterface.GetIpcProvider<object>(ApiConsts.OnRetainerListTaskButtonsDraw).SendMessage();
            if(PluginToProcess != null)
            {
                // Same unbounded shape as the two buttons above, and additionally driven by another
                // plugin's IPC task, so it is the least predictable of the three.
                var plugin = PluginToProcess;
                RetainerBulkOperation.Enqueue(plugin, () =>
                {
                    for(var i = 0; i < GameRetainerManager.Count; i++)
                    {
                        var ret = GameRetainerManager.Retainers[i];
                        if(ret.Available)
                        {
                            P.TaskManager.Enqueue(() => RetainerListHandlers.SelectRetainerByName(ret.Name.ToString()));
                            TaskPostprocessRetainerIPC.Enqueue(ret.Name.ToString(), plugin);

                            if(C.RetainerMenuDelay > 0)
                            {
                                TaskWaitSelectString.Enqueue(C.RetainerMenuDelay);
                            }
                            P.TaskManager.Enqueue(RetainerHandlers.SelectQuit);
                            P.TaskManager.Enqueue(RetainerHandlers.ConfirmCantBuyback);
                        }
                    }
                });
            }
        }
        height = ImGui.GetWindowSize().Y;
    }

    public override void PostDraw()
    {
        //ImGui.PopStyleVar();
        // 與 base.PreDraw() 成對:base 自己決定要不要 pop，所以無條件呼叫是安全的。
        base.PostDraw();
    }
}
