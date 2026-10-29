using AutoRetainerAPI.Configuration;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Memory;
using Dalamud.Plugin.Services;
using ECommons.ChatMethods;
using ECommons.Configuration;
using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System.Globalization;
using System.Text.RegularExpressions;

namespace AutoRetainer.Services;

/// <summary>
/// Records each free company's founding date and estate ownership, and announces once when an FC
/// without an estate has existed for <see cref="Config.FCHousingEligibleDays"/> days.
/// </summary>
/// <remarks>
/// 本檔假設與契約（偏移對台服執行檔反組譯驗過，證據在 commit body）：
/// - 公會情報代理：+0x28 狀態（1＝請求中、2＝剛填入，下一幀畫完就歸 0）、+0x38 公會 ID、+0x50 房屋 ID（-1＝沒有房產）、+0x5C 成立時間（Unix 秒）；欄位畫完後保留到下一次填入。
/// - 公會「情報」畫面：自己公會的資料封包把成立日期以「年/月/日」字串寫進字串陣列 49 第 6 格；換角色不會清掉，所以只認登入後變過的值。
/// - GetOwnedHouseId(FreeCompanyEstate) 回登入時伺服器送來的值：-1＝沒有公會房屋；0＝還沒收到，當作不知道。
/// - 只在框架執行緒當下讀值；不保存原生指標、不寫遊戲記憶體、不開視窗、不送封包。
/// </remarks>
public sealed unsafe class FCHousingNotifier : IDisposable
{
    private const int ProfileStateOffset = 0x28;
    private const int ProfileFcIdOffset = 0x38;
    private const int ProfileEstateOffset = 0x50;
    private const int ProfileFoundedOffset = 0x5C;
    private const int ProfileRequestPending = 1;
    private const int StatusStringArray = 49;
    private const int StatusFoundedIndex = 6;

    /// <summary>Launch day of A Realm Reborn, Unix seconds. Anything earlier is not a founding time.</summary>
    private const long EarliestFounding = 1377561600;
    // 登入後先等一段時間：房屋 ID 是全域值，換角色時要等新角色的那一份送到才可信。
    private const long LoginSettleMs = 15000;
    private const long TickIntervalMs = 1000;
    private const long EstateRefreshMs = 60L * 60 * 1000;

    private static readonly Regex DatePattern = new(@"(\d{4})\s*/\s*(\d{1,2})\s*/\s*(\d{1,2})", RegexOptions.CultureInvariant);

    private long NextTick;
    private long PresentSince;
    private bool OwnedHouseIdBroken;
    private bool LoggedTickError;
    private bool LoggedIdMismatch;
    private string LastProfileDiag;
    private string StatusBaseline;
    private string LastStatusDiag;
    private readonly Dictionary<string, ulong> StatusAccepted = [];

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
            StatusBaseline = null;
            return;
        }
        if(PresentSince == 0)
        {
            PresentSince = now;
            StatusBaseline = ReadStatusFounded() ?? "";
        }
        var changed = ReadProfile();
        changed |= ReadStatusTab();
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
        if(agent == null) return false;
        if(!agent->IsAgentActive())
        {
            if(TryGetAddonByName<AtkUnitBase>("FreeCompanyProfile", out var addon) && addon->IsVisible)
            {
                ProfileDiag("FreeCompanyProfile addon is visible but agent 81 is not active");
            }
            else
            {
                LastProfileDiag = null;
            }
            return false;
        }
        var raw = (byte*)agent;
        var state = *(int*)(raw + ProfileStateOffset);
        var fcId = *(ulong*)(raw + ProfileFcIdOffset);
        long founded = *(uint*)(raw + ProfileFoundedOffset);
        var house = *(ulong*)(raw + ProfileEstateOffset);
        var values = $"state={state} fcId={fcId:X16} founded={founded} house={house:X16}";
        // 請求中時欄位還是上一次的資料，等回應填入。
        if(state == ProfileRequestPending)
        {
            ProfileDiag($"waiting for profile data ({values})");
            return false;
        }
        if(fcId == 0)
        {
            ProfileDiag($"no profile data yet ({values})");
            return false;
        }
        if(!C.FCData.TryGetValue(fcId, out var fc))
        {
            ProfileDiag($"profile is not one of the stored free companies ({values})");
            LogIdMismatchOnce(fcId, agent);
            return false;
        }

        var changed = false;
        if(!IsPlausibleFounding(founded))
        {
            ProfileDiag($"implausible founding time ({values})");
        }
        else if(fc.FoundedAt != founded)
        {
            PluginLog.Information($"[FCHousing] FC {fcId:X16}: founding time {founded} ({DateTimeOffset.FromUnixTimeSeconds(founded):yyyy-MM-dd} UTC) read from the FC profile window (was {fc.FoundedAt})");
            fc.FoundedAt = founded;
            changed = true;
        }
        changed |= SetEstate(fcId, fc, ToEstateState(house), "FC profile window");
        return changed;
    }

    /// <summary>Same reason as last time is not logged again until the profile window closes.</summary>
    private void ProfileDiag(string reason)
    {
        if(reason == LastProfileDiag) return;
        LastProfileDiag = reason;
        PluginLog.Information($"[FCHousing] profile: {reason}");
    }

    private void StatusDiag(string reason)
    {
        if(reason == LastStatusDiag) return;
        LastStatusDiag = reason;
        PluginLog.Information($"[FCHousing] FC status tab: {reason}");
    }

    /// <returns>The founding date text, "" when the slot is empty, null when the array is not available.</returns>
    private static string ReadStatusFounded()
    {
        var ui = UIModule.Instance();
        var atk = ui == null ? null : ui->GetRaptureAtkModule();
        if(atk == null) return null;
        var arr = atk->AtkModule.GetStringArrayData(StatusStringArray);
        if(arr == null || arr->StringArray == null || arr->AtkArrayData.Size <= StatusFoundedIndex) return null;
        var ptr = arr->StringArray[StatusFoundedIndex].Value;
        if(ptr == null) return "";
        return MemoryHelper.ReadSeStringNullTerminated((nint)ptr).TextValue.Trim();
    }

    private bool ReadStatusTab()
    {
        var text = ReadStatusFounded();
        if(string.IsNullOrEmpty(text)) return false;
        var info = InfoModule.Instance();
        var proxy = info == null ? null : info->GetInfoProxyFreeCompany();
        if(proxy == null || proxy->Id == 0) return false;
        var fcId = proxy->Id;
        if(!C.FCData.TryGetValue(fcId, out var fc)) return false;
        if(text == StatusBaseline && !(StatusAccepted.TryGetValue(text, out var owner) && owner == fcId))
        {
            StatusDiag($"\"{text}\" unchanged since login, may belong to another character's free company; not recorded for {fcId:X16}");
            return false;
        }
        var m = DatePattern.Match(text);
        if(!m.Success
            || !int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var y)
            || !int.TryParse(m.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var mo)
            || !int.TryParse(m.Groups[3].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var d)
            || mo is < 1 or > 12 || d < 1 || y < 2013 || d > DateTime.DaysInMonth(y, mo))
        {
            StatusDiag($"could not parse a date from \"{text}\"");
            return false;
        }
        StatusAccepted[text] = fcId;
        var date = new DateTime(y, mo, d);
        // 已有精確時間（公會情報視窗）且只差時區造成的一天內：不覆寫，免得兩個來源互覆。
        if(IsPlausibleFounding(fc.FoundedAt) && Math.Abs((DateTimeOffset.FromUnixTimeSeconds(fc.FoundedAt).ToLocalTime().Date - date).TotalDays) <= 1) return false;
        // 只有日期：記成當天最後一秒，門檻寧可晚一天到、不提早。
        var founded = new DateTimeOffset(date.AddDays(1).AddSeconds(-1), TimeZoneInfo.Local.GetUtcOffset(date)).ToUnixTimeSeconds();
        if(!IsPlausibleFounding(founded))
        {
            StatusDiag($"implausible date \"{text}\"");
            return false;
        }
        PluginLog.Information($"[FCHousing] FC {fcId:X16}: founding date {date:yyyy-MM-dd} read from the FC status tab \"{text}\" (was {fc.FoundedAt})");
        fc.FoundedAt = founded;
        return true;
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
