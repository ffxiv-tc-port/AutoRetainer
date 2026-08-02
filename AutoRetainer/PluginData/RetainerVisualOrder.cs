using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AutoRetainer.PluginData;
public enum RetainersVisualOrder
{
    Ventures, Inventory_Slots, Region_JP, Region_NA, Region_EU, Region_OC, World, DataCenter, Name,
    // 台服(陸行鳥)桶。刻意接在尾端而不是插進既有成員之間——這個列舉用 Newtonsoft.Json 預設
    // 設定序列化,沒有 StringEnumConverter,存檔存的是序數(int)。插在中間會讓既有使用者存檔
    // 裡的數字重新對應到不同的成員(靜默錯位),接在尾端則完全不影響既有序數。
    Region_TW
}