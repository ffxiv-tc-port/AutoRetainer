namespace AutoRetainer.PluginData;

/// <summary>
/// 稀有品繳交循環的**每角色**設定。
///
/// <para>🔴 這裡的每一個欄位都有「這個角色沒有自己設定過」的表示法,而那種狀態一律退回設定檔裡原本
/// 那份**跨角色共用**的值(<c>Config.ExpertDeliveryLoopRetainerNames</c> 等)。這個功能第一版只做單角,
/// 設定本來就是全域的 —— 既有使用者的設定必須原封不動繼續生效,所以這個類別是**覆寫**,不是取代。
/// 舊欄位一個都不刪、也不再被寫入,降級回舊版照樣讀得到。</para>
/// </summary>
[Serializable]
public class ExpertDeliveryLoopCharacterConfig
{
    /// <summary>手動選取的僱員名。
    /// <para>🔴 <c>null</c> 與空清單是**兩件事**:<c>null</c> ＝ 這個角色從來沒被設定過,沿用全域那份;
    /// 空清單 ＝ 使用者明確把這個角色的僱員全部取消勾選。把兩者混為一談會讓「取消全部勾選」
    /// 靜默變回「沿用別的角色的名單」。</para></summary>
    public List<string> RetainerNames = null;

    /// <summary>這個角色專用的傳喚鈴目的地(Lifestream 我的最愛)。0 ＝ 沒設,用全域那個。</summary>
    public uint BellFavoriteId = 0;
    public byte BellFavoriteSub = 0;
    /// <summary>選取當下的顯示名稱。Lifestream 沒載入、或這個項目已經被取消收藏時,UI 還講得出它是誰。</summary>
    public string BellFavoriteName = "";

    /// <summary>這個角色專用的繳交點目的地。0 ＝ 沒設,用全域那個。
    /// <para>📌 這個欄位存在的理由不是「多一個選項」:不同角色可以隸屬不同的大國防聯軍,
    /// 而三個聯軍在三個不同的城市。共用一個繳交點會讓其中一些角色被送到沒有自己聯軍的城市,
    /// 接著在那裡找不到繳交 NPC —— 表現出來是整整一個逾時的空轉,不是一句錯誤訊息。</para></summary>
    public uint GCFavoriteId = 0;
    public byte GCFavoriteSub = 0;
    public string GCFavoriteName = "";
}
