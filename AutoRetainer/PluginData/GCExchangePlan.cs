using AutoRetainerAPI.Configuration;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AutoRetainer.PluginData;
[Serializable]
public sealed unsafe class GCExchangePlan
{
    public string ID => GUID.ToString();
    public Guid GUID = Guid.NewGuid();
    public string Name = "";
    public List<GCExchangeItem> Items = [];
    public int RemainingSeals = 0;

    /// <summary>依角色的軍票上限另設的保留量(鍵＝上限,例如 50000)。沒有對應的鍵就用 <see cref="RemainingSeals"/>。</summary>
    public Dictionary<int, int> RemainingSealsByCap = [];

    /// <summary>上限讀不到(0)時一律退回 <see cref="RemainingSeals"/>。</summary>
    public int GetRemainingSeals(uint maxSeals)
        => maxSeals > 0 && RemainingSealsByCap.TryGetValue((int)maxSeals, out var v) ? v : RemainingSeals;
    public bool FinalizeByPurchasing = false;

    public bool ShouldSerializeGUID() => GUID != Guid.Empty;
    public bool ShouldSerializeID() => false;
}