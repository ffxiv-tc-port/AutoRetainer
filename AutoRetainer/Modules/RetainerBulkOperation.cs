using AutoRetainer.Scheduler.Handlers;
using ECommons.Automation.NeoTaskManager;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace AutoRetainer.Modules;

/// <summary>
/// Failure containment for the "do this for every retainer" buttons on the retainer list overlay
/// (Quick Entrust, Quick Withdraw Gil, and the custom-task button other plugins add through IPC).
///
/// Those buttons queue the FULL chain for EVERY available retainer up front -
/// <c>SelectRetainerByName -> work -> SelectQuit</c>, nine times over for a typical character. Since
/// P.TaskManager is created with abortOnTimeout:true and <c>Abort()</c> clears the ENTIRE queue, one
/// retainer wedging for 20 seconds discards not only the rest of that retainer's chain but every
/// remaining retainer as well - including the trailing SelectQuit / ConfirmCantBuyback steps whose
/// only job is to put the UI back. The batch stops dead with a retainer window still open, and the
/// only trace is a PluginLog.Warning that a user's log level will usually filter out.
///
/// This class does not change how the batch runs. It appends a sentinel step that can only execute
/// if the whole batch survived, so "sentinel never ran and the queue is empty" is a reliable signal
/// that the batch was aborted - by a timeout, a thrown exception, a task returning null, or an
/// external Abort(). On that signal it says so in chat and, if the bailout module is enabled, queues
/// a short best-effort chain to put the UI back where the batch would have left it.
///
/// Deliberately NOT done here: relaxing abortOnTimeout on the batch steps themselves. Skipping a
/// failed <c>SelectRetainerByName</c> would let the NEXT retainer's work run against whichever
/// retainer happens to still be open, which is a far worse outcome than stopping.
/// </summary>
internal static unsafe class RetainerBulkOperation
{
    private static bool Active = false;
    private static string Description = "";

    /// <summary><c>Environment.TickCount64</c> of the moment the queue was first observed empty while
    /// a batch was still marked in flight. <c>long.MaxValue</c> means we are not in that state.</summary>
    private static long StaleSince = long.MaxValue;

    /// <summary>
    /// Runs <paramref name="enqueueBatch"/> and tracks the resulting batch.
    /// </summary>
    internal static void Enqueue(string description, Action enqueueBatch)
    {
        Active = false;
        StaleSince = long.MaxValue;
        Description = description;
        enqueueBatch();
        // The overlay only draws these buttons when the queue is idle, so anything queued now came
        // from the batch. If the loop matched no retainers there is nothing to watch.
        if(!P.TaskManager.IsBusy) return;
        P.TaskManager.Enqueue(Finish, "RetainerBulkOperation.Finish");
        Active = true;
    }

    private static void Finish()
    {
        Active = false;
        StaleSince = long.MaxValue;
    }

    internal static void Tick()
    {
        if(!Active) return;

        if(P.TaskManager.IsBusy)
        {
            StaleSince = long.MaxValue;
            return;
        }

        if(StaleSince == long.MaxValue)
        {
            StaleSince = Environment.TickCount64;
            return;
        }

        // Same floor as the voyage panel watchdog: a user who set BailoutTimeout very low must not be
        // able to make this fire while they are legitimately clicking around themselves.
        if(Environment.TickCount64 - StaleSince < Math.Max(C.BailoutTimeout, 5) * 1000) return;

        Active = false;
        StaleSince = long.MaxValue;

        DuoLog.Warning(string.Format(Loc.T("\"{0}\" stopped before it finished - any remaining retainers were skipped."), Description));

        if(!C.EnableBailout) return;

        // Best effort, in the order the windows stack: dismiss the modal first, then unwind out of
        // the retainer back to the retainer list. Every step is a no-op returning true when its
        // window is not present, so an already-clean screen costs a handful of frames rather than
        // one timeout per step.
        TaskManagerConfiguration conf = new(abortOnTimeout: false, timeLimitMS: 5000);
        P.TaskManager.Enqueue(DismissBuybackIfPresent, "BulkRecovery.DismissBuyback", conf);
        P.TaskManager.Enqueue(CancelBankIfOpen, "BulkRecovery.CancelBank", conf);
        P.TaskManager.Enqueue(CloseRetainerAgentIfOpen, "BulkRecovery.CloseRetainerAgent", conf);
        P.TaskManager.Enqueue(QuitRetainerIfOpen, "BulkRecovery.QuitRetainer", conf);
        P.TaskManager.Enqueue(DismissBuybackIfPresent, "BulkRecovery.DismissBuybackAfterQuit", conf);
    }

    private static bool? DismissBuybackIfPresent()
    {
        if(Utils.GetSpecificYesno(Lang.WillBeUnableToProcessBuyback) == null) return true;
        return RetainerHandlers.ConfirmCantBuyback();
    }

    private static bool? CancelBankIfOpen()
    {
        if(!TryGetAddonByName<AtkUnitBase>("Bank", out var addon) || !IsAddonReady(addon)) return true;
        // forceCancel: this is a recovery path, never complete a half-configured gil transfer.
        return RetainerHandlers.ProcessBankOrCancel(true);
    }

    private static bool? CloseRetainerAgentIfOpen()
    {
        var agentModule = Framework.Instance()->UIModule->GetAgentModule();
        if(agentModule == null) return true;
        var agent = agentModule->GetAgentByInternalId(AgentId.Retainer);
        if(agent == null || !agent->IsAgentActive()) return true;
        return RetainerHandlers.CloseAgentRetainer();
    }

    private static bool? QuitRetainerIfOpen()
    {
        // Back at the retainer list already - nothing left to unwind.
        if(TryGetAddonByName<AtkUnitBase>("RetainerList", out var list) && IsAddonReady(list)) return true;
        var hasSupply = TryGetAddonByName<AtkUnitBase>("RetainerTaskSupply", out _);
        var hasMenu = TryGetAddonByName<AtkUnitBase>("SelectString", out var menu) && IsAddonReady(menu);
        if(!hasSupply && !hasMenu) return true;
        return RetainerHandlers.SelectQuit();
    }
}
