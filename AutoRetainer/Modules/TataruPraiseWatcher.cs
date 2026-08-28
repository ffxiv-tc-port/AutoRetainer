using AutoRetainer.Internal;
using AutoRetainer.Modules.Voyage;
using AutoRetainerAPI.Configuration;
using ECommons.GameHelpers;
using ECommons.Throttlers;

namespace AutoRetainer.Modules;

/// <summary>
/// 監看「回來的時間到了」這條邊，到點就請 TataruPraise 念一句。
/// </summary>
/// <remarks>
/// 🔴 <b>接的是時間，不是流程。</b>上一版接在工房收尾與僱員收尾的流程收斂點上，
/// 結果是「實際去收的時候才響」——使用者要的是「返航時間到就響」，
/// 那兩件事之間可以差好幾個小時，而且人不在遊戲前面時根本不會發生。
/// <para>
/// 判斷完全建立在 <c>C.OfflineData</c> 已經記下來的絕對時間戳上
/// （潛水艇／飛空艇＝<see cref="OfflineVesselData.ReturnTime"/>，
/// 僱員＝<see cref="OfflineRetainerData.VentureEndsAt"/>，兩者都是 Unix 秒），
/// 所以<b>不限當前角色</b>：那些時間戳離線後依然有效，不需要重新整理就會自己到期。
/// </para>
/// <para>
/// 🔴 <b>每 tick 只比較 long。</b>不掃 ObjectTable、不解任何原生指標、不讀遊戲記憶體，
/// 而且外面還包一層 1 秒節流。
/// </para>
/// <para>
/// ⚠️ 這是單向通知，<b>不參與 AutoRetainer 的任何流程判斷</b>；
/// 讀錯的最壞後果是少念一句或多念一句話。
/// </para>
/// </remarks>
internal static class TataruPraiseWatcher
{
    internal enum DeployableKind
    {
        Submarine,
        Airship,
        Retainer,
    }

    /// <summary>「看過這個目標了，但還沒有宣告過任何一次到期」。
    ///
    /// <para>🔴 用 0 當哨兵是安全的：<see cref="Observe"/> 一開頭就把 <c>endsAt &lt;= 0</c>
    /// （潛艇停在船塢／僱員沒有探險）擋掉了，所以 0 永遠不可能是一個真的被宣告過的時間戳。</para></summary>
    private const long NotAnnounced = 0;

    /// <summary>合理的 Unix 時間下限（2020-09）。
    ///
    /// <para>🔴 這道閘門是必要的，不是防禦性裝飾：<c>P.Time</c> 在
    /// <c>C.UseServerTime</c> 開著時走 <c>CSFramework.GetServerTime()</c>，
    /// 而那個值在還沒連上伺服器時可能是 0。如果放它進來，
    /// 每一艘船都會被算成「還沒到期」而被種成未宣告，等真正的時間一進來
    /// <b>整批同時到期＝開機響一輪</b>——正好是這次要避免的那個行為。
    /// 時間不合理就整個 tick 不做事：不宣告，也不種任何東西。</para></summary>
    private const long SaneUnixTimeFloor = 1_600_000_000;

    /// <summary>每個目標最後一次「已經宣告過」的到期時間戳。
    ///
    /// <para>只存在記憶體裡，刻意不持久化：重開遊戲之後所有已經過期的目標會在第一次看到時
    /// 被靜默種成已宣告（見 <see cref="Observe"/>），所以開機不會補響。</para>
    ///
    /// <para>⚠️ 鍵裡放 <see cref="DeployableKind"/> 是因為僱員跟船在理論上可以同名——
    /// 只用 (CID, 名字) 當鍵的話，撞名會讓其中一邊<b>靜默永遠不響</b>。</para></summary>
    private static readonly Dictionary<(ulong CID, DeployableKind Kind, string Name), long> Announced = [];

    /// <summary>兩種船。提到欄位上是為了不要每秒配一次陣列。</summary>
    private static readonly VoyageType[] VesselTypes = [VoyageType.Submersible, VoyageType.Airship];

    internal static void Tick()
    {
        if(!C.TataruPraiseOnCompletion)
        {
            // 關掉的時候把記錄丟掉。這樣中途再打開時會重新走一次「第一次看到就種起來」，
            // 不會把關閉期間累積的到期一次補念出來。
            if(Announced.Count > 0) Announced.Clear();
            return;
        }
        // 沒登入就不看：這時 P.Time 可能還不可信，而且也沒有人在聽。
        if(!Player.Available) return;
        if(!EzThrottler.Throttle("TataruPraiseWatcher.Scan", 1000)) return;

        var now = P.Time;
        if(now < SaneUnixTimeFloor) return;

        var voyageCount = 0;
        string voyageFirst = null;
        var retainerCount = 0;
        string retainerFirst = null;

        foreach(var chara in C.OfflineData)
        {
            foreach(var type in VesselTypes)
            {
                // 🔑 刻意走 GetVesselData/GetEnabledVesselsData 而不是直接讀欄位：
                //    這兩個 helper 由同一個 VoyageType 決定，配對不會錯邊
                //    （直接讀欄位很容易把 OfflineSubmarineData 配到 EnabledAirships 上，而且是靜默的）。
                var enabled = chara.GetEnabledVesselsData(type);
                foreach(var vessel in chara.GetVesselData(type))
                {
                    if(!enabled.Contains(vessel.Name)) continue;
                    var kind = type == VoyageType.Submersible ? DeployableKind.Submarine : DeployableKind.Airship;
                    var who = Observe(chara, kind, vessel.Name, vessel.ReturnTime, now);
                    if(who != null)
                    {
                        voyageCount++;
                        voyageFirst ??= who;
                    }
                }
            }

            // GetEnabledRetainers 已經同時濾掉「沒被勾選」與「身上沒有探險」的僱員。
            foreach(var retainer in chara.GetEnabledRetainers())
            {
                var who = Observe(chara, DeployableKind.Retainer, retainer.Name, retainer.VentureEndsAt, now);
                if(who != null)
                {
                    retainerCount++;
                    retainerFirst ??= who;
                }
            }
        }

        // 同一秒有好幾個一起到期時只念一次：使用者要的是一個提示音，不是連珠炮。
        // ⚠️ 但每一個目標都已經在 Observe 裡各自記過帳了，所以不會有人被漏掉、之後也不會重念。
        if(voyageCount > 0) TataruPraiseIPC.TryPraise(TataruPraiseIPC.CategoryVoyage, Describe(voyageFirst, voyageCount));
        if(retainerCount > 0) TataruPraiseIPC.TryPraise(TataruPraiseIPC.CategoryRetainer, Describe(retainerFirst, retainerCount));
    }

    private static string Describe(string first, int count) => count == 1 ? first : $"{first} 等 {count} 個";

    /// <summary>看一個目標一眼，回報它是不是<b>剛好</b>在這一瞬間跨過到期線。</summary>
    /// <returns>跨過去了就回一段人看得懂的描述（只在真的要念的時候才組字串）；其餘一律 null。</returns>
    private static string Observe(OfflineCharacterData chara, DeployableKind kind, string name, long endsAt, long now)
    {
        // 潛艇停在船塢（ReturnTime == 0）／僱員身上沒有探險：沒有「到期」這件事可談。
        if(endsAt <= 0) return null;

        var key = (chara.CID, kind, name);
        var expired = endsAt <= now;

        if(!Announced.TryGetValue(key, out var announced))
        {
            // 🔴 第一次看到這個目標。已經過期的就直接記成「宣告過了」但不出聲——
            //    這就是「剛登入/剛載入不要補響一輪」的實作點。
            //    還沒到期的種成 NotAnnounced，之後真的跨過去時才會念。
            Announced[key] = expired ? endsAt : NotAnnounced;
            return null;
        }

        // 還沒到期就沒事；到期時間跟上次宣告的是同一個，代表這一趟已經念過了。
        // （重新派出之後 endsAt 會變成新的未來值，下次到期時自然就對不上而重新成立。）
        if(!expired || announced == endsAt) return null;

        Announced[key] = endsAt;
        return $"{chara} 的{Label(kind)}「{name}」";
    }

    private static string Label(DeployableKind kind) => kind switch
    {
        DeployableKind.Submarine => "潛水艇",
        DeployableKind.Airship => "飛空艇",
        DeployableKind.Retainer => "僱員",
        _ => kind.ToString(),
    };
}
