using ECommons.UIHelpers;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace AutoRetainer.Modules.GcHandin;
public unsafe class ReaderGrandCompanyExchange(AtkUnitBase* UnitBase, int BeginOffset = 0) : AtkReader(UnitBase, BeginOffset)
{
    public uint ItemCount => ReadUInt(1) ?? 0;
    public GCExchangeRankTab RankTab => (GCExchangeRankTab)(ReadUInt(2) ?? 0);
    /// <summary>與 <c>Loop&lt;ItemInfo&gt;(17, 1, (int)ItemCount)</c> 逐筆相同（起點、步距、遇到空的那筆就停）。</summary>
    /// <remarks>不走 <c>Loop</c>：它每一筆都用 <c>Activator.CreateInstance</c> 建 reader（反射找建構子、綁參數），
    /// 兌換時這份清單每幀被讀好幾次。</remarks>
    public List<ItemInfo> Items
    {
        get
        {
            var unitBase = AtkReaderParams.UnitBase;
            var max = (int)ItemCount;
            var ret = new List<ItemInfo>();
            for(var i = 0; i < max; i++)
            {
                var r = new ItemInfo(unitBase, 17 + i);
                if(r.IsNull) break;
                ret.Add(r);
            }
            return ret;
        }
    }

    public unsafe class ItemInfo(nint UnitBasePtr, int BeginOffset = 0) : AtkReader(UnitBasePtr, BeginOffset)
    {
        public string Name => ReadSeString(0)?.GetText();
        public uint Seals => ReadUInt(50) ?? 0;
        public uint Bag => ReadUInt(100) ?? 0;
        public uint IconID => ReadUInt(150) ?? 0;
        public bool Stackable => ReadBool(250) ?? false;
        public uint ItemID => ReadUInt(300) ?? 0;
        public uint Unk350 => ReadUInt(350) ?? 0;
        public uint RankReq => ReadUInt(400) ?? 0;
        public bool OpenCurrencyExchange => ReadBool(450) ?? false;
    }
}
