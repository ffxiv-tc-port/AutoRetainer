using AutoRetainer.Modules.Voyage;
using AutoRetainer.Services.Lifestream;
using ECommons.ExcelServices;
using ECommons.ExcelServices.TerritoryEnumeration;
using ECommons.GameHelpers;
using ECommons.IPC;

namespace AutoRetainer.Scheduler.Tasks;
public static class TaskTeleportToProperty
{
    public static uint[] Apartments = [Houses.Ingleside_Apartment, Houses.Kobai_Goten_Apartment, Houses.Lily_Hills_Apartment, Houses.Sultanas_Breath_Apartment, Houses.Topmast_Apartment];
    public static bool EnqueueIfNeededAndPossible(bool isSubmersibleOperation)
    {
        if(Player.Territory.EqualsAny(VoyageUtils.Workshops)) return false;
        if(!isSubmersibleOperation && C.NoTeleportHetWhenNextToBell && Utils.GetReachableRetainerBell(false) != null) return false;
        var fcTeleportEnabled = (Data.GetAllowFcTeleportForRetainers() && !isSubmersibleOperation) || (Data.GetAllowFcTeleportForSubs() && isSubmersibleOperation);
        var data = LifestreamHousePath.Get(Player.CID);
        var info = ECommonsIPC.Lifestream.GetCurrentPlotInfo();
        {
            var canPrivate = Data.GetAllowPrivateTeleportForRetainers() && data.Private != null && data.Private.PathToEntrance.Count > 0;
            var canFc = (fcTeleportEnabled && data.FC != null && data.FC.PathToEntrance.Count > 0);
            if((isSubmersibleOperation || !canPrivate) && canFc)
            {
                return Process(true);
            }
            if(!isSubmersibleOperation && canPrivate)
            {
                return Process(false);
            }
        }

        if(C.AllowSimpleTeleport)
        {
            var canFc = fcTeleportEnabled && ECommonsIPC.Lifestream.HasFreeCompanyHouse() != false;
            var canPrivate = Data.GetAllowPrivateTeleportForRetainers() && ECommonsIPC.Lifestream.HasPrivateHouse() != false;
            if((isSubmersibleOperation || !canPrivate) && canFc)
            {
                return ProcessSimple(true);
            }
            if(!isSubmersibleOperation && canPrivate)
            {
                return ProcessSimple(false);
            }
        }

        if(!isSubmersibleOperation && Data.GetIsTeleportEnabledForRetainers())
        {
            //apartment logic
            if(Data.GetAllowApartmentTeleportForRetainers())
            {
                if(ECommonsIPC.Lifestream.HasApartment() == true && Apartments.Contains(Player.Territory)) return false;
                if(ECommonsIPC.Lifestream.HasApartment() != false)
                {
                    P.TaskManager.Enqueue(() => ECommonsIPC.Lifestream.EnterApartment(true));
                    P.TaskManager.Enqueue(() =>
                    {
                        if(!Svc.ClientState.IsLoggedIn)
                        {
                            PluginLog.Warning($"Logout while waiting to return to home; expecting DC travel. Aborting and waiting for relogging.");
                            return null;
                        }
                        if(Player.Interactable && ECommonsIPC.Lifestream.HasApartment() == false)
                        {
                            PluginLog.Warning("Upon returning home, apartment not found. Aborting and retrying.");
                            return null;
                        }
                        return IsScreenReady() && Player.Interactable && Apartments.Contains(Player.Territory) && !ECommonsIPC.Lifestream.IsBusy();
                    }, new(timeLimitMS: 5 * 60 * 1000));
                    return true;
                }
            }
            //inn logic
            if(!Inns.List.Contains((ushort)Player.Territory))
            {
                P.TaskManager.Enqueue(() => ECommonsIPC.Lifestream.EnqueueInnShortcut(1));
                P.TaskManager.Enqueue(() =>
                {
                    if(!Svc.ClientState.IsLoggedIn)
                    {
                        PluginLog.Warning($"Logout while waiting to return to home; expecting DC travel. Aborting and waiting for relogging.");
                        return null;
                    }
                    return IsScreenReady() && Player.Interactable && Inns.List.Contains((ushort)Player.Territory) && !ECommonsIPC.Lifestream.IsBusy();
                }, new(timeLimitMS: 5 * 60 * 1000));
                return true;
            }
        }

        //if at this point no decision was made, just invoke HET if needed, enter any house and don't care about it

        if(ExcelTerritoryHelper.Get(Player.Territory)?.TerritoryIntendedUse.RowId == (uint)TerritoryIntendedUseEnum.Residential_Area)
        {
            if(TaskNeoHET.IsInMarkerHousingPlot([.. TaskNeoHET.PrivateMarkers, .. TaskNeoHET.FcMarkers, .. (C.SharedHET ? TaskNeoHET.SharedMarkers : [])]))
            {
                TaskNeoHET.Enqueue(null);
                return true;
            }
            else if(TaskNeoHET.GetApartmentEntrance() != null && Player.DistanceTo(TaskNeoHET.GetApartmentEntrance()) < 40f)
            {
                TaskNeoHET.Enqueue(null);
                return true;
            }
        }

        return false;

        bool Process(bool fc)
        {
            var pathData = fc ? data.FC : data.Private;
            if(info != null
                && info.Value.Plot == pathData.Plot
                && info.Value.Ward == pathData.Ward
                && info.Value.Kind == pathData.ResidentialDistrict)
            {
                if(Player.Territory.EqualsAny([.. Houses.List]))
                {
                    return false;
                }
                else
                {
                    TaskNeoHET.Enqueue(null);
                    return true; //already here
                }
            }
            P.TaskManager.Enqueue(() => S.LifestreamExtra.EnqueuePropertyShortcut(fc ? 2 : 1, 1));
            P.TaskManager.Enqueue(() =>
            {
                if(!Svc.ClientState.IsLoggedIn)
                {
                    PluginLog.Warning($"Logout while waiting to return to home; expecting DC travel. Aborting and waiting for relogging.");
                    return null;
                }
                return Player.Interactable
                && ECommonsIPC.Lifestream.GetCurrentPlotInfo()?.Plot == pathData.Plot
                && ECommonsIPC.Lifestream.GetCurrentPlotInfo()?.Ward == pathData.Ward
                && ECommonsIPC.Lifestream.GetCurrentPlotInfo()?.Kind == pathData.ResidentialDistrict
                && !ECommonsIPC.Lifestream.IsBusy();
            }, new(timeLimitMS: 5 * 60 * 1000));
            TaskNeoHET.Enqueue(null);
            return true;
        }

        bool ProcessSimple(bool fc)
        {
            var isHere = TaskNeoHET.IsInMarkerHousingPlot(fc ? TaskNeoHET.FcMarkers : TaskNeoHET.PrivateMarkers);
            var noProperty = !(fc ? ECommonsIPC.Lifestream.HasFreeCompanyHouse() : ECommonsIPC.Lifestream.HasPrivateHouse());
            if(noProperty == true)
            {
                return false;
            }
            if(Player.Territory.EqualsAny([.. Houses.List]) && (!fc || TaskNeoHET.GetWorkshopEntrance() != null))
            {
                return false;
            }
            else if(isHere)
            {
                TaskNeoHET.Enqueue(null);
                return true; //already here
            }
            P.TaskManager.Enqueue(() => S.LifestreamExtra.EnqueuePropertyShortcut(fc ? 2 : 1, 1));
            P.TaskManager.Enqueue(() =>
            {
                if(!Svc.ClientState.IsLoggedIn)
                {
                    PluginLog.Warning($"Logout while waiting to return to home; expecting DC travel. Aborting and waiting for relogging.");
                    return null;
                }
                return Player.Interactable
                && Player.Territory.EqualsAny([.. ResidentalAreas.List])
                && !ECommonsIPC.Lifestream.IsBusy();
            }, new(timeLimitMS: 5 * 60 * 1000));
            TaskNeoHET.Enqueue(null);
            return true;
        }
    }

    public static bool ShouldVoidHET()
    {
        if(!Player.Available) return false;
        if(Data == null) return true;
        var subsSoon = Data.WorkshopEnabled && Data.AnyEnabledVesselsAvailable() && MultiMode.EnabledSubmarines && (!Data.ShouldWaitForAllWhenLoggedIn() || Data.AreAnyEnabledVesselsReturnInNext(1, true));
        var retainersSoon = MultiMode.AnyRetainersAvailable(0) && MultiMode.EnabledRetainers;
        var blockHet = subsSoon || retainersSoon;
        if(C.AllowSimpleTeleport && (Data.GetAllowFcTeleportForRetainers() || Data.GetAllowPrivateTeleportForRetainers())) return blockHet;
        var data = LifestreamHousePath.Get(Player.CID);
        if(Data.GetAllowFcTeleportForRetainers() && data.FC != null && data.FC.PathToEntrance.Count > 0) return blockHet;
        if(Data.GetAllowPrivateTeleportForRetainers() && data.Private != null && data.Private.PathToEntrance.Count > 0) return blockHet;
        return false;
    }
}
