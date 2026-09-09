namespace AutoRetainer.Modules.GcHandin;

internal class GCHandinInterruptedException : Exception
{
    /// <summary>
    /// true 代表這次中斷是流程自己會收拾掉的正常狀況：只寫進記錄檔，不印到使用者的聊天視窗。
    /// <para/>
    /// 🔴 ECommons 的 <c>DuoLog</c> 在**每一個等級**都無條件 <c>Svc.Chat.Print</c>（六個等級的實作
    /// 完全對稱、沒有任何等級閘門），所以「把它降一級」不會讓訊息離開聊天視窗 —— 要不印到聊天，
    /// 唯一的辦法是換成 <c>PluginLog</c>／<c>Svc.Log</c>。
    /// </summary>
    public bool QuietInChat { get; }

    public GCHandinInterruptedException(string message, bool quietInChat = false) : base(message)
    {
        QuietInChat = quietInChat;
    }
}
