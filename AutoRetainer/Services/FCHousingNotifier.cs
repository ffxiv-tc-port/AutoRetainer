using AutoRetainerAPI.Configuration;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;
using ECommons.ChatMethods;
using ECommons.Configuration;
using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;

namespace AutoRetainer.Services;

/// <summary>
/// Records each free company's founding date and estate ownership, and announces once when an FC
/// without an estate has existed for <see cref="Config.FCHousingEligibleDays"/> days.
/// </summary>
/// <remarks>
/// 本檔假設與契約（偏移對台服執行檔反組譯驗過，證據在 commit body）：
/// - 公會情報代理：+0x28 int 狀態（2＝已填入）、+0x38 公會 ID、+0x50 房屋 ID（-1＝沒有房產）、+0x5C 成立時間（Unix 秒）；只有打開「公會情報」視窗時遊戲才會填。
/// - GetOwnedHouseId(FreeCompanyEstate) 回登入時伺服器送來的值：-1＝沒有公會房屋；0＝還沒收到，當作不知道。
/// - 只在框架執行緒當下讀值；不保存原生指標、不寫遊戲記憶體、不開視窗、不送封包。
/// </remarks>
public sealed unsafe class FCHousingNotifier : IDisposable
{
    private const int ProfileStateOffset = 0x28;
    private const int ProfileFcIdOffset = 0x38;
    private const int ProfileEstateOffset = 0x50;
    private const int ProfileFoundedOffset = 0x5C;
    private const int ProfileLoadedState = 2;

    /// <summary>Launch day of A Realm Reborn, Unix seconds. Anything earlier is not a founding time.</summary>
    private const long EarliestFounding = 1377561600;
    // 登入後先等一段時間：房屋 ID 是全域值，換角色時要等新角色的那一份送到才可信。
    private const long LoginSettleMs = 15000;
    private const long TickIntervalMs = 1000;
    private const long EstateRefreshMs = 60L * 60 * 1000;

    private long NextTick;
    private long PresentSince;
    private bool OwnedHouseIdBroken;
    private bool LoggedTickError;
    private bool LoggedIdMismatch;

    private FCHousingNotifier()
    {
        Svc.Framework.Update += OnUpdate;
    }

    public void Dispose()
    {
        Svc.Framework.Update -= OnUpdate;
    }

    private void OnUpdate(IFramework framework)
    {
        var now = Environment.TickCount64;
        if(now < NextTick) return;
        NextTick = now + TickIntervalMs;
        try
        {
            Tick(now);
        }
        catch(Exception e)
        {
            if(LoggedTickError) return;
            LoggedTickError = true;
            PluginLog.Warning($"[FCHousing] tick failed, further errors suppressed: {e}");
        }
    }

    private void Tick(long now)
    {
        if(!Player.Available)
        {
            PresentSince = 0;
            return;
        }
        if(PresentSince == 0) PresentSince = now;
        var changed = ReadProfile();
        if(now - PresentSince >= LoginSettleMs)
        {
            if(Player.Interactable && Player.IsInHomeWorld) changed |= ReadOwnEstate();
            changed |= AnnounceEligible();
        }
        if(changed) EzConfig.Save();
    }

    private bool ReadProfile()
    {
        var module = AgentModule.Instance();
        if(module == null) return false;
        var agent = module->GetAgentByInternalId(AgentId.FreeCompanyProfile);
        if(agent == null || !agent->IsAgentActive()) return false;
        var raw = (byte*)agent;
        if(*(int*)(raw + ProfileStateOffset) != ProfileLoadedState) return false;
        var fcId = *(ulong*)(raw + ProfileFcIdOffset);
        if(fcId == 0) return false;
        if(!C.FCData.TryGetValue(fcId, out var fc))
        {
            LogIdMismatchOnce(fcId, agent);
            return false;
        }

        var changed = false;
        long founded = *(uint*)(raw + ProfileFoundedOffset);
        if(IsPlausibleFounding(founded) && fc.FoundedAt != founded)
        {
            PluginLog.Information($"[FCHousing] FC {fcId:X16}: founding time {founded} ({DateTimeOffset.FromUnixTimeSeconds(founded):yyyy-MM-dd} UTC) read from the FC profile window (was {fc.FoundedAt})");
            fc.FoundedAt = founded;
            changed = true;
        }
        var house = *(ulong*)(raw + ProfileEstateOffset);
        changed |= SetEstate(fcId, fc, ToEstateState(house), "FC profile window");
        return changed;
    }

    private bool ReadOwnEstate()
    {
        if(OwnedHouseIdBroken) return false;
        var info = InfoModule.Instance();
        var proxy = info == null ? null : info->GetInfoProxyFreeCompany();
        if(proxy == null || proxy->Id == 0) return false;
        if(!C.FCData.TryGetValue(proxy->Id, out var fc)) return false;
        ulong house;
        try
        {
            house = HousingManager.GetOwnedHouseId(EstateType.FreeCompanyEstate).Id;
        }
        catch(Exception e)
        {
            OwnedHouseIdBroken = true;
            PluginLog.Warning($"[FCHousing] GetOwnedHouseId unavailable, estate check from login data disabled for this session: {e.Message}");
            return false;
        }
        return SetEstate(proxy->Id, fc, ToEstateState(house), "login data");
    }

    /// <remarks>0 is never trusted as "has estate": it is what the value reads before the server has sent anything.</remarks>
    private static FCEstateState ToEstateState(ulong houseId) => houseId switch
    {
        ulong.MaxValue => FCEstateState.NoEstate,
        0 => FCEstateState.Unknown,
        _ => FCEstateState.HasEstate,
    };

    private static bool SetEstate(ulong fcId, FCData fc, FCEstateState state, string source)
    {
        if(state == FCEstateState.Unknown) return false;
        var nowMs = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        if(fc.EstateState == state && nowMs - fc.EstateStateUpdate < EstateRefreshMs) return false;
        if(fc.EstateState != state)
        {
            PluginLog.Information($"[FCHousing] FC {fcId:X16}: estate {fc.EstateState} -> {state} (from {source})");
        }
        fc.EstateState = state;
        fc.EstateStateUpdate = nowMs;
        // 再看到它有房就清掉已通知旗標：之後若房子沒了（放棄、自動拆除）會再通知一次。
        if(state == FCEstateState.HasEstate) fc.HousingEligibleNotified = 0;
        return true;
    }

    private static bool AnnounceEligible()
    {
        if(!C.NotifyFCHousingEligible) return false;
        var changed = false;
        foreach(var x in C.FCData)
        {
            var fc = x.Value;
            if(x.Key == 0 || fc == null || fc.HousingEligibleNotified != 0) continue;
            if(GetDaysUntilEligible(fc) is not int days || days > 0) continue;
            fc.HousingEligibleNotified = DateTimeOffset.Now.ToUnixTimeMilliseconds();
            changed = true;
            var name = C.NoNames || string.IsNullOrEmpty(fc.Name) ? Loc.T("Your free company") : string.Format(Loc.T("Free company \"{0}\""), fc.Name);
            var message = string.Format(Loc.T("{0} has existed for {1} days and has no estate - it can now buy free company land."), name, GetDaysSinceFounding(fc));
            PluginLog.Information($"[FCHousing] FC {x.Key:X16}: announced housing eligibility (founded {fc.FoundedAt}, threshold {C.FCHousingEligibleDays} days)");
            Notify.Info(message);
            Svc.Chat.Print(new()
            {
                Message = new SeStringBuilder().AddUiForeground($"[AutoRetainer] {message}", (ushort)UIColor.Green).Build()
            });
        }
        return changed;
    }

    private void LogIdMismatchOnce(ulong profileFcId, AgentInterface* agent)
    {
        if(LoggedIdMismatch) return;
        var name = ((AgentFreeCompanyProfile*)agent)->Name.ToString();
        if(string.IsNullOrEmpty(name)) return;
        foreach(var x in C.FCData)
        {
            if(x.Value?.Name != name) continue;
            LoggedIdMismatch = true;
            PluginLog.Information($"[FCHousing] FC profile window shows \"{name}\" with id {profileFcId:X16}, but the stored FC of that name has id {x.Key:X16}; founding date not recorded");
            return;
        }
    }

    public static bool IsPlausibleFounding(long unixSeconds)
    {
        return unixSeconds >= EarliestFounding && unixSeconds <= DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 86400;
    }

    public static int? GetDaysSinceFounding(FCData fc)
    {
        if(!IsPlausibleFounding(fc.FoundedAt)) return null;
        return (int)((DateTimeOffset.UtcNow.ToUnixTimeSeconds() - fc.FoundedAt) / 86400);
    }

    /// <returns>Days still to wait (0 or less = eligible now); null when the founding date or the estate state is not known, or the FC has an estate.</returns>
    public static int? GetDaysUntilEligible(FCData fc)
    {
        if(fc.EstateState != FCEstateState.NoEstate) return null;
        if(!IsPlausibleFounding(fc.FoundedAt)) return null;
        var eligibleAt = fc.FoundedAt + Math.Clamp(C.FCHousingEligibleDays, 1, 3650) * 86400L;
        var remaining = eligibleAt - DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return remaining <= 0 ? 0 : (int)((remaining + 86399) / 86400);
    }
}
