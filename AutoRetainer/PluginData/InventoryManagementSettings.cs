using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AutoRetainer.PluginData;
public sealed unsafe class InventoryManagementSettings
{
    public Guid GUID = Guid.NewGuid();
    internal string ID => GUID.ToString();

    public string Name = "";

    public bool IMEnableCofferAutoOpen = false;
    public bool IMEnableAutoVendor = false;
    public bool IMEnableContextMenu = false;
    public bool IMSkipVendorIfRetainer = false;
    public List<uint> IMAutoVendorHard = [];
    public List<uint> IMAutoVendorHardIgnoreStack = [];
    public List<uint> IMAutoVendorSoft = [];
    public List<uint> IMProtectList = [];
    public int IMAutoVendorHardStackLimit = 20;
    public bool IMDry = false;
    public bool IMEnableItemDesynthesis = false;
    public bool IMEnableNpcSell = false;
    public bool AllowSellFromArmory = false;

    // 🔴 丟棄是永久損失，所以刻意**不**共用上面任何一份賣出清單：
    // 既有使用者的賣出清單往往已經很長，若讓它同時當丟棄清單，開啟功能的那一刻
    // 就會把「本來要賣掉換錢」的東西全部丟掉，而且是靜默的。專屬清單預設為空，
    // 代表就算使用者打開開關、也要自己一件一件加進來才會有任何東西被丟棄。
    public bool IMEnableItemDiscard = false;
    public List<uint> IMAutoDiscardList = [];

    public bool AdditionModeProtectList = true;
    public bool AdditionModeSoftSellList = false;
    public bool AdditionModeHardSellList = false;
}