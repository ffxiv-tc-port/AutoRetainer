namespace AutoRetainer.PluginData;
[Serializable]
public class EntrustPlan
{
    public Guid Guid = Guid.NewGuid();
    public string Name = "";
    public bool Duplicates = false;
    public bool DuplicatesMultiStack = false;
    public List<EntrustCategoryConfiguration> EntrustCategories = [];
    public List<uint> EntrustItems = [];
    public Dictionary<uint, int> EntrustItemsAmountToKeep = [];
    public bool AllowEntrustFromArmory = false;
    public bool ManualPlan = false;
    public bool ExcludeProtected = false;

    /// <summary>裝備類別只存放「稀有品繳交循環」拿得走的裝備,其餘(時裝——白色稀有度、
    /// 不可分解的裝備)留在身上。
    ///
    /// <para>📌 預設 false ＝ 既有行為完全不變。AutoRetainer 的設定不是 EzConfig 的
    /// <c>DefaultValueHandling.Include</c> 那套,JSON 裡沒有這個鍵就吃 C# 初值,
    /// 所以既有使用者也一樣是關著的。</para>
    ///
    /// <para>⚠️ 只作用在裝備類別(<see cref="Helpers.Utils.GearUICategories"/>),
    /// 其他類別完全不受影響;逐件點名在 <see cref="EntrustItems"/> 裡的道具也不受影響。</para></summary>
    public bool EntrustOnlyDeliverableGear = false;
}
