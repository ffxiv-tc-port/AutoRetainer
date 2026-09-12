using ECommons.Automation;
using ECommons.GameHelpers;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Client.Game.Control;

namespace AutoRetainer.Modules;

/// <summary>
/// Single owner for every "/automove" this plugin issues.
/// Why this exists: <see cref="AutoRetainer.TaskManager"/> is created with abortOnTimeout:true and <c>TaskManager.Abort()</c> clears the ENTIRE queue, not just the step that failed.
/// <see cref="Tick"/> runs from the framework update - outside the task system entirely - and issues the "off" as soon as autorun is still engaged while no task queue is left to turn it off.
/// Note this is deliberately NOT a longer timeout on the "off" step: a longer timeout only lowers the probability of the runaway.
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
