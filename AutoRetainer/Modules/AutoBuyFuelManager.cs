using AutoRetainer.Modules.Voyage;
using AutoRetainer.Scheduler.Tasks;
using ECommons.GameHelpers;
using ECommons.Throttlers;

namespace AutoRetainer.Modules;

// Periodically checks whether the current character is low on Ceruleum
// Tanks while standing in a Company Workshop, and if so, walks up to the
// adventurer doll NPC to buy more from the Free Company Credit Shop.
internal static class AutoBuyFuelManager
{
    internal static void Tick()
    {
        if(!C.AutoBuyFuelEnabled) return;
        if(!Player.Available) return;
        if(!VoyageUtils.Workshops.Contains(Svc.ClientState.TerritoryType)) return;
        if(Data == null || Data.Ceruleum >= C.AutoBuyFuelThreshold) return;
        if(P.TaskManager.IsBusy) return;
        if(DateTimeOffset.Now.ToUnixTimeMilliseconds() - C.AutoBuyFuelCheckTimes.SafeSelect(Player.CID) < 60_000) return;
        if(!EzThrottler.Throttle("AutoBuyFuel.Trigger", 5000)) return;

        C.AutoBuyFuelCheckTimes[Player.CID] = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        TaskAutoBuyFuel.Enqueue();
    }
}
