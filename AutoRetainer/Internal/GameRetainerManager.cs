using FFXIVClientStructs.FFXIV.Client.Game;

namespace AutoRetainer.Internal;
public static unsafe class GameRetainerManager
{
    public static bool Ready => RetainerManager.Instance()->IsReady != false;
    public static List<Retainer> Retainers
    {
        get
        {
            var ret = new List<Retainer>();
            var m = RetainerManager.Instance();
            Span<int> order = stackalloc int[m->Retainers.Length];
            var n = GetListedIndices(order);
            for(var k = 0; k < n; k++)
            {
                var i = order[k];
                ret.Add(new(m->Retainers[i])
                {
                    DisplayOrder = m->DisplayOrder.IndexOf((byte)i)
                });
            }
            return ret;
        }
    }

    /// <summary>
    /// <see cref="Retainers"/> 的格子索引：同一個篩選、同一個順序，但不配置。回傳筆數。
    /// <paramref name="indices"/> 至少要有 <c>RetainerManager.Retainers.Length</c> 格。
    /// </summary>
    /// <remarks>OfflineDataManager 靠它判斷離線僱員清單需不需要重建，所以 <see cref="Retainers"/> 的順序只能從這裡來。</remarks>
    public static int GetListedIndices(Span<int> indices)
    {
        var m = RetainerManager.Instance();
        var n = 0;
        var allInDisplayOrder = true;
        for(var i = 0; i < m->Retainers.Length; i++)
        {
            if(!IsListed(ref m->Retainers[i])) continue;
            indices[n++] = i;
            if(m->DisplayOrder.IndexOf((byte)i) == -1) allInDisplayOrder = false;
        }
        // 有一格不在 DisplayOrder 裡就維持格子順序；否則依它第一次出現的位置穩定排序。
        if(!allInDisplayOrder) return n;
        for(var k = 1; k < n; k++)
        {
            var idx = indices[k];
            var key = m->DisplayOrder.IndexOf((byte)idx);
            var j = k - 1;
            while(j >= 0 && m->DisplayOrder.IndexOf((byte)indices[j]) > key)
            {
                indices[j + 1] = indices[j];
                j--;
            }
            indices[j + 1] = idx;
        }
        return n;
    }

    // 與 Retainers.Count 相同（同一個篩選），但不建清單、不解碼名字。
    public static int Count
    {
        get
        {
            var m = RetainerManager.Instance();
            var n = 0;
            for(var i = 0; i < m->Retainers.Length; i++)
            {
                if(IsListed(ref m->Retainers[i])) n++;
            }
            return n;
        }
    }

    private static bool IsListed(ref RetainerManager.Retainer x) => x.RetainerId != 0 && x.Name[0] != 0;

    /// <summary>
    /// 與 <c>Utils.TryGetRetainerByName(name, out var r)</c> 再讀 <c>r.VentureID</c> 相同（同一個就緒判斷、同一個順序、取第一個同名的），
    /// 但不建清單、不解碼每個名字。
    /// </summary>
    public static bool TryGetVentureIdByName(string name, out uint ventureId)
    {
        ventureId = 0;
        if(!Ready) return false;
        var m = RetainerManager.Instance();
        Span<int> order = stackalloc int[m->Retainers.Length];
        var n = GetListedIndices(order);
        for(var k = 0; k < n; k++)
        {
            ref var x = ref m->Retainers[order[k]];
            if(!Modules.OfflineDataManager.NameMatches(name, x.Name)) continue;
            ventureId = x.VentureId;
            return true;
        }
        return false;
    }

    /// <summary><see cref="Retainer.Available"/> 的判斷本體，給不建 <see cref="Retainer"/> 的呼叫端用。</summary>
    public static bool IsAvailable(ref RetainerManager.Retainer x) => x.ClassJob != 0 && x.Available;

    public class Retainer
    {
        public RetainerManager.Retainer Handle;
        public string Name;
        public uint VentureID => Handle.VentureId;
        public bool Available => IsAvailable(ref Handle);
        public DateTime VentureComplete => Utils.DateFromTimeStamp(Handle.VentureComplete);
        public ulong RetainerID => Handle.RetainerId;
        public uint Gil => Handle.Gil;
        public uint VentureCompleteTimeStamp => Handle.VentureComplete;
        public int MarkerItemCount => Handle.MarketItemCount;
        /// <summary>僱員背包裡佔用的格數（0x2B）。不含寄售中的道具，那是 <see cref="MarkerItemCount"/>（0x31）。</summary>
        public int ItemCount => Handle.ItemCount;
        public uint MarketExpire => Handle.MarketExpire;
        public int Level => Handle.Level;
        public uint ClassJob => Handle.ClassJob;
        public RetainerManager.RetainerTown Town => Handle.Town;
        public int DisplayOrder = 0;


        public Retainer(RetainerManager.Retainer handle)
        {
            Handle = handle;
            Name = handle.Name.Read();
        }
    }
}
