namespace AutoRetainer.UI.NeoUI.InventoryManagementEntries;
public abstract class InventoryManagemenrBase : NeoUIEntry
{
    public abstract string Name { get; }
    // 設定視窗每幀讀一次（分頁外框的標題）；只在 Loc.T 回傳的那一份（換語系就換）或 Name 換掉時重組。只在主執行緒讀。
    private string PathPrefix, PathName, PathText;

    public sealed override string Path
    {
        get
        {
            var prefix = Loc.T("Inventory Management");
            var name = Name;
            if(PathText == null || !ReferenceEquals(prefix, PathPrefix) || !ReferenceEquals(name, PathName))
            {
                PathText = $"{prefix}/{name}";
                PathPrefix = prefix;
                PathName = name;
            }
            return PathText;
        }
    }
}
