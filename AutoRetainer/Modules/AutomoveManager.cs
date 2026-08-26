using ECommons.Automation;
using ECommons.GameHelpers;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Client.Game.Control;

namespace AutoRetainer.Modules;

/// <summary>
/// Single owner for every "/automove" this plugin issues.
///
/// Why this exists: <see cref="AutoRetainer.TaskManager"/> is created with abortOnTimeout:true and
/// <c>TaskManager.Abort()</c> clears the ENTIRE queue, not just the step that failed. Every approach
/// flow in the plugin is built as two separate queued steps - one that turns autorun ON, and several
/// steps later one that turns it OFF once the target is in range. The "off" step is therefore only
/// reached if the queue survives, and it happens to be the single step most likely to kill the queue:
/// it returns false until the player is within roughly 4 yalms, so a player wedged just outside that
/// radius (stuck on workshop furniture, another character, a mount) times out after 20 seconds,
/// Abort() runs, and the "/automove off" is discarded along with everything else that was queued.
/// The character then keeps running in a straight line until the user notices and stops it by hand.
///
/// The fix is to stop making the "off" depend on the queue at all. <see cref="On"/> records that WE
/// engaged autorun; <see cref="Tick"/> runs from the framework update - outside the task system
/// entirely - and issues the "off" as soon as autorun is still engaged while no task queue is left
/// to turn it off. That covers every way a chain can die (timeout, thrown exception, a task
/// returning null, or an external Abort()), not just the timeout case.
///
/// Note this is deliberately NOT a longer timeout on the "off" step: a longer timeout only lowers
/// the probability of the runaway, it does not remove the dependency on the queue surviving.
/// </summary>
internal static class AutomoveManager
{
    /// <summary>
    /// Set when this plugin turned autorun on, cleared once autorun is observed to actually be off.
    /// Deliberately NOT cleared when we send "/automove off" - if that command does not land we want
    /// <see cref="Tick"/> to keep retrying until the game itself reports autorun as stopped.
    /// </summary>
    private static bool EngagedByUs = false;

    /// <summary>
    /// <c>Environment.TickCount64</c> of the moment "autorun is on but nothing is queued to turn it
    /// off" first became true. <c>long.MaxValue</c> means we are not currently in that state.
    /// </summary>
    private static long StaleSince = long.MaxValue;

    /// <summary>
    /// Grace period so an ordinary gap between two enqueues - or a chain that is just about to queue
    /// its own "off" step - is never mistaken for an aborted queue.
    /// </summary>
    private const int StaleGraceMS = 1000;

    internal static void On()
    {
        EngagedByUs = true;
        StaleSince = long.MaxValue;
        // The previous inline callers re-issued "/automove on" every single frame while approaching,
        // so skipping the send when autorun is already engaged is a pure reduction in chat traffic.
        // It also means a send that did not land is retried on the next frame rather than assumed
        // to have worked.
        if(IsAutoRunning()) return;
        Chat.ExecuteCommand("/automove on");
    }

    internal static void Off()
    {
        EngagedByUs = false;
        StaleSince = long.MaxValue;
        Chat.ExecuteCommand("/automove off");
    }

    /// <summary>
    /// Reads the game's own autorun state. Verified against the TC 7.20 executable: the signature has
    /// exactly one match and resolves to a three-instruction leaf function (compare a global byte
    /// against 3, return the flag) that takes no arguments and dereferences no pointer, so the call
    /// itself cannot fault. The try/catch only covers the signature failing to resolve after a future
    /// patch, in which case we assume autorun IS engaged so the rescue below still fires - failing
    /// towards "send a redundant /automove off" rather than towards "let the character run away".
    /// </summary>
    private static bool IsAutoRunning()
    {
        try
        {
            return InputManager.IsAutoRunning();
        }
        catch(Exception e)
        {
            if(EzThrottler.Throttle("AutomoveManager.SigFailure", 600000))
            {
                PluginLog.Warning($"[Automove] Could not read autorun state ({e.Message}); assuming it is engaged.");
            }
            return true;
        }
    }

    internal static void Tick()
    {
        if(!EngagedByUs) return;

        if(!Player.Available)
        {
            // Zoning, logging out, or sitting at character select. The game drops autorun by itself
            // and there is nothing here that could receive a chat command anyway.
            EngagedByUs = false;
            StaleSince = long.MaxValue;
            return;
        }

        if(!IsAutoRunning())
        {
            // Either our own "off" landed, or the player / an interaction stopped it. Nothing to undo.
            EngagedByUs = false;
            StaleSince = long.MaxValue;
            return;
        }

        if(P.TaskManager?.IsBusy == true || P.ODMTaskManager?.IsBusy == true)
        {
            // A chain is still running - let it reach its own "off" step.
            StaleSince = long.MaxValue;
            return;
        }

        if(StaleSince == long.MaxValue)
        {
            StaleSince = Environment.TickCount64;
            return;
        }

        if(Environment.TickCount64 - StaleSince < StaleGraceMS) return;
        // Throttled rather than one-shot: EngagedByUs stays set until the game reports autorun as
        // stopped, so a command that does not land is retried once per second instead of silently
        // giving up.
        if(!EzThrottler.Throttle("AutomoveManager.Rescue", 1000)) return;

        PluginLog.Information("[Automove] Autorun is still engaged but the task queue is empty (the step that should have stopped it was discarded) - sending /automove off.");
        Chat.ExecuteCommand("/automove off");
    }
}
