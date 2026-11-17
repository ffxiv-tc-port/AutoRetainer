using AutoRetainer.Internal;
using AutoRetainer.Modules.Voyage;
using AutoRetainer.Modules.Voyage.Tasks;
using AutoRetainer.Scheduler.Tasks;
using AutoRetainer.Services;
using AutoRetainer.UI.MainWindow.MultiModeTab;
using AutoRetainerAPI.Configuration;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Config;
using Dalamud.Interface.ImGuiNotification;
using ECommons.CircularBuffers;
using ECommons.Events;
using ECommons.ExcelServices.TerritoryEnumeration;
using ECommons.EzSharedDataManager;
using ECommons.GameHelpers;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using static AutoRetainer.Modules.OfflineDataManager;

namespace AutoRetainer.Modules.Multi;

internal static unsafe partial class MultiMode
{
    internal static IEnumerable<OfflineCharacterData> GetEnabledOfflineData()
    {
        return C.OfflineData.Where(x => x.Enabled).Where(x => !x.IsLockedOut());
    }

    internal static bool AnyRetainersAvailable(int advanceSeconds = 0)
    {
        if(TryGetEnabledCurrentCharacter(out var data) && C.SelectedRetainers.TryGetValue(data.CID, out var enabledRetainers))
        {
            return AnyEnabledRetainerDueWithin(data, enabledRetainers, C.UnsyncCompensation + advanceSeconds);
        }
        return false;
    }

    internal static bool IsAllRetainersHaveMoreThan15Mins()
    {
        foreach(var x in C.OfflineData)
        {
            if(!x.Enabled || x.IsLockedOut()) continue;
            if(!C.SelectedRetainers.TryGetValue(x.CID, out var enabledRetainers)) continue;
            foreach(var z in x.RetainerData)
            {
                if(!IsEnabledRetainer(z, enabledRetainers)) continue;
                if(z.GetVentureSecondsRemaining() < 15 * 60) return false;
            }
        }
        return true;
    }

    // 以下幾支是多角模式決策區塊每幀都會走的判斷（閒置在標題畫面或等下一個角色時也一樣），
    // 直接走訪 List 而不經 LINQ，才不會每幀配置列舉器、閉包與暫存陣列；
    // 篩選條件、走訪順序與短路點必須與各支 summary 寫的 LINQ 形式一致（剩餘秒數與鎖區都讀當下時間）。

    /// <summary>即 <c>C.OfflineData.TryGetFirst(x => x.CID == Svc.PlayerState.ContentId, out data)</c>。</summary>
    private static bool TryGetCurrentCharacter(out OfflineCharacterData data)
    {
        if(C.OfflineData != null)
        {
            foreach(var x in C.OfflineData)
            {
                if(x.CID == Svc.PlayerState.ContentId)
                {
                    data = x;
                    return true;
                }
            }
        }
        data = null;
        return false;
    }

    /// <summary>即 <c>GetEnabledOfflineData().TryGetFirst(x => x.CID == Svc.PlayerState.ContentId, out data)</c>：先看 Enabled、再看鎖區、最後比 CID。</summary>
    private static bool TryGetEnabledCurrentCharacter(out OfflineCharacterData data)
    {
        foreach(var x in C.OfflineData)
        {
            if(x.Enabled && !x.IsLockedOut() && x.CID == Svc.PlayerState.ContentId)
            {
                data = x;
                return true;
            }
        }
        data = null;
        return false;
    }

    /// <summary><see cref="GetEnabledRetainers"/> 的篩選條件。</summary>
    private static bool IsEnabledRetainer(OfflineRetainerData z, HashSet<string> enabledRetainers) => enabledRetainers.Contains(z.Name) && z.HasVenture;

    /// <summary>即 <c>data.GetEnabledRetainers().Any(z => z.GetVentureSecondsRemaining() &lt;= seconds)</c>；<paramref name="enabledRetainers"/> 是該角色在 <c>C.SelectedRetainers</c> 裡的那一份。</summary>
    private static bool AnyEnabledRetainerDueWithin(OfflineCharacterData data, HashSet<string> enabledRetainers, long seconds)
    {
        foreach(var z in data.RetainerData)
        {
            if(IsEnabledRetainer(z, enabledRetainers) && z.GetVentureSecondsRemaining() <= seconds) return true;
        }
        return false;
    }

    /// <summary>
    /// 即 <c>selected.Any() &amp;&amp; selected.All(z => z.GetVentureSecondsRemaining() &lt;= seconds)</c>。
    /// Any() 不讀時間，所以併成一趟：剩餘秒數的呼叫次數與順序和 All 那一趟相同。
    /// </summary>
    private static bool AllEnabledRetainersDueWithin(OfflineCharacterData data, HashSet<string> enabledRetainers, long seconds)
    {
        var any = false;
        foreach(var z in data.RetainerData)
        {
            if(!IsEnabledRetainer(z, enabledRetainers)) continue;
            any = true;
            if(!(z.GetVentureSecondsRemaining() <= seconds)) return false;
        }
        return any;
    }

    internal static OfflineCharacterData GetPreferredCharacter()
    {
        return C.OfflineData.FirstOrDefault(x => x.Preferred && x.CID != Svc.PlayerState.ContentId);
    }

    internal static OfflineRetainerData[] GetEnabledRetainers(this OfflineCharacterData data)
    {
        if(C.SelectedRetainers.TryGetValue(data.CID, out var enabledRetainers))
        {
            return data.RetainerData.Where(z => IsEnabledRetainer(z, enabledRetainers)).ToArray();
        }
        return Array.Empty<OfflineRetainerData>();
    }

    internal static List<OfflineCharacterData> GetRetainerSortedOfflineDatas(bool sort)
    {
        var data = new List<OfflineCharacterData>();
        FillRetainerSortedOfflineDatas(data, sort);
        return data;
    }

    private static long[] GetSortKeyBuffer(int count)
    {
        if(SortKeyBuffer.Length < count) SortKeyBuffer = new long[Math.Max(count, SortKeyBuffer.Length * 2)];
        return SortKeyBuffer;
    }

    /// <summary>
    /// 即 <c>data = [.. data.OrderBy(鍵)]</c>，鍵已依序算好放在 <paramref name="keys"/>。
    /// 插入排序只在前一個鍵「嚴格較大」時才往前移，同鍵維持原順序＝與 OrderBy 相同的穩定排序；角色數只有十位數。
    /// </summary>
    private static void StableSortByKey(List<OfflineCharacterData> data, long[] keys)
    {
        for(var i = 1; i < data.Count; i++)
        {
            var item = data[i];
            var key = keys[i];
            var j = i - 1;
            while(j >= 0 && keys[j] > key)
            {
                data[j + 1] = data[j];
                keys[j + 1] = keys[j];
                j--;
            }
            data[j + 1] = item;
            keys[j + 1] = key;
        }
    }

    /// <summary>把 <paramref name="data"/> 清空後填成 <see cref="GetRetainerSortedOfflineDatas"/> 會回傳的內容與順序。</summary>
    private static void FillRetainerSortedOfflineDatas(List<OfflineCharacterData> data, bool sort)
    {
        data.Clear();
        foreach(var x in C.OfflineData)
        {
            if(!x.IsLockedOut()) data.Add(x);
        }
        if(sort)
        {
            if(C.CharEqualize)
            {
                var keys = GetSortKeyBuffer(data.Count);
                for(var i = 0; i < data.Count; i++) keys[i] = CharaCnt.GetOrDefault(data[i].CID);
                StableSortByKey(data, keys);
            }
            if(C.LongestVentureFirst)
            {
                var keys = GetSortKeyBuffer(data.Count);
                for(var i = 0; i < data.Count; i++) keys[i] = data[i].GetEnabledRetainers().OrderBy(r => r.VentureEndsAt).FirstOrDefault()?.VentureEndsAt ?? long.MaxValue;
                StableSortByKey(data, keys);
            }
            if(C.CappedLevelsLast)
            {
                List<OfflineCharacterData> capped = [];
                List<OfflineCharacterData> levelling = [];
                foreach(var x in data)
                {
                    var e = x.GetEnabledRetainers();
                    foreach(var ret in e)
                    {
                        var cap = ret.Level < Player.MaxLevel && x.GetJobLevel(ret.Job) == ret.Level;
                        if(!cap) goto Getout;
                    }
                    capped.Add(x);
                Getout:
                    continue;
                }
                data.RemoveAll(capped.Contains);
                foreach(var x in data)
                {
                    var e = x.GetEnabledRetainers();
                    foreach(var ret in e)
                    {
                        var canLevel = ret.Level < Player.MaxLevel && x.GetJobLevel(ret.Job) > ret.Level;
                        if(canLevel) goto Add;
                    }
                    continue;
                Add:
                    levelling.Add(x);
                }
                data.RemoveAll(levelling.Contains);
                List<OfflineCharacterData> ordered = [.. levelling, .. data, .. capped];
                data.Clear();
                data.AddRange(ordered);
            }
        }
        if(C.MultiPreferredCharLast)
        {
            var pref = data.FirstOrDefault(x => x.Preferred);
            if(pref != null)
            {
                data.Remove(pref);
                data.Add(pref);
            }
        }
    }

    internal static OfflineCharacterData GetCurrentTargetCharacter()
    {
        if(EnabledSubmarines)
        {
            var data = TargetOrderBuffer;
            FillRetainerSortedOfflineDatas(data, false);
            foreach(var x in data)
            {
                if(x.CID == Player.CID) continue;
                if(x.WorkshopEnabled && x.GetEnabledVesselsData(VoyageType.Airship).Count + x.GetEnabledVesselsData(VoyageType.Submersible).Count > 0)
                {
                    if(x.AreAnyEnabledVesselsReturnInNext(0, C.MultiModeWorkshopConfiguration.MultiWaitForAll))
                    {
                        return x;
                    }
                }
            }
            foreach(var x in data)
            {
                if(x.CID == Player.CID) continue;
                if(x.WorkshopEnabled && x.GetEnabledVesselsData(VoyageType.Airship).Count + x.GetEnabledVesselsData(VoyageType.Submersible).Count > 0)
                {
                    if(x.AreAnyEnabledVesselsReturnInNext(C.MultiModeWorkshopConfiguration.AdvanceTimer, C.MultiModeWorkshopConfiguration.MultiWaitForAll))
                    {
                        return x;
                    }
                }
            }
        }
        if(EnabledRetainers)
        {
            var data = TargetOrderBuffer;
            FillRetainerSortedOfflineDatas(data, true);
            // 「全部都要回來」＝勾選且有探險的僱員非空、而且全部到期；否則任一到期就算。
            foreach(var x in data)
            {
                if(x.CID == Player.CID) continue;
                if(x.Enabled && C.SelectedRetainers.TryGetValue(x.CID, out var enabledRetainers))
                {
                    if(C.MultiModeRetainerConfiguration.MultiWaitForAll
                        ? AllEnabledRetainersDueWithin(x, enabledRetainers, 0)
                        : AnyEnabledRetainerDueWithin(x, enabledRetainers, 0))
                    {
                        return x;
                    }
                }
            }
            foreach(var x in data)
            {
                if(x.CID == Player.CID) continue;
                if(x.Enabled && C.SelectedRetainers.TryGetValue(x.CID, out var enabledRetainers))
                {
                    if(C.MultiModeRetainerConfiguration.MultiWaitForAll
                        ? AllEnabledRetainersDueWithin(x, enabledRetainers, C.MultiModeRetainerConfiguration.AdvanceTimer)
                        : AnyEnabledRetainerDueWithin(x, enabledRetainers, C.MultiModeRetainerConfiguration.AdvanceTimer))
                    {
                        return x;
                    }
                }
            }
        }

        return null;
    }

    internal static bool IsCurrentCharacterDone()
    {
        // 進不了屋就不可能做事：當作做完，換角（或停下）走既有流程。
        return TaskNeoHET.GivenUpEntering || IsCurrentCharacterRetainersDone() && IsCurrentCharacterCaptainDone();
    }

    internal static bool IsCurrentCharacterRetainersDone()
    {
        if(!ProperOnLogin.PlayerPresent) return false;
        if(TryGetCurrentCharacter(out var data))
        {
            if(!EnabledRetainers) return true;
            if(!data.Enabled) return true;
            if(Utils.GetVenturesAmount() < 2 || !Utils.IsInventoryFree()) return true;
            return !IsAnySelectedRetainerFinishesWithin(5 * 60);
        }
        else
        {
            return true;
        }
    }

    internal static bool IsCurrentCharacterCaptainDone()
    {
        if(!EnabledSubmarines) return true;
        if(Data == null) return true;
        if(!Data.WorkshopEnabled) return true;
        return !Data.AreAnyEnabledVesselsReturnInNext(5 * 60, Data.ShouldWaitForAllWhenLoggedIn());
    }

    internal static bool IsAnySelectedRetainerFinishesWithin(int seconds)
    {
        if(!ProperOnLogin.PlayerPresent) return false;
        if(!EnabledRetainers) return false;
        if(TryGetEnabledCurrentCharacter(out var data) && C.SelectedRetainers.TryGetValue(data.CID, out var enabledRetainers))
        {
            return AnyEnabledRetainerDueWithin(data, enabledRetainers, seconds);
        }
        return false;
    }

    internal static bool EnsureCharacterValidity(bool ro = false)
    {
        if(!ProperOnLogin.PlayerPresent) return false;
        if(C.OfflineData.TryGetFirst(x => x.CID == Svc.PlayerState.ContentId, out var data))
        {
            if(Svc.Objects.LocalPlayer.HomeWorld.RowId == Svc.Objects.LocalPlayer.CurrentWorld.RowId && Utils.GetVenturesAmount() >= data.GetNeededVentureAmount() && Utils.IsInventoryFree() && Utils.GetReachableRetainerBell(true) != null)
            {
                return true;
            }
            // 🔴 這是持久化的剔除（Enabled 存進設定檔）。上面兩個判斷都讀背包（GetVenturesAmount 走
            // GetInventoryItemCount、IsInventoryFree 走空格數），兩者在容器讀不到時都會回 0 ——
            // 「探險幣不夠」與「背包滿」會同時假成立。讀不到就先不剔除，下一幀讀得到時照樣會執行。
            if(!ro && Utils.IsInventoryStateReadable())
            {
                data.Enabled = false;
            }
        }
        return false;
    }

    internal static int GetNeededVentureAmount(this OfflineCharacterData data)
    {
        return data.GetEnabledRetainers().Length * 2;
    }
}
