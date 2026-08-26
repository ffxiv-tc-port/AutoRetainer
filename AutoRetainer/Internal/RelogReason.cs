namespace AutoRetainer.Internal;

public enum RelogReason
{
    Overlay, Command, ConfigGUI, MultiMode,

    /// <summary>稀有品繳交循環的多角色連跑。
    /// <para>🔴 刻意**不重用** MultiMode 這個值。MultiMode 在 Relog 裡有兩個副作用是這條流程不要的:
    /// 它會無條件排入角色後處理 IPC(讓別的外掛在換角前先做事,長度不可預期),
    /// 而且它是多開排程自己的身分,拿它冒名會讓「誰在換角色」變得查不出來。
    /// 用自己的值之後,後處理要不要跑就跟其他手動換角一樣,由「啟用手動重新登入後的角色後處理」決定。</para>
    /// <para>📌 這個值也刻意不落在 Overlay/Command/ConfigGUI 那一組:那一組會重置首選角色、
    /// 並且在「手動登入時停用多角色模式」開著時把多開排程關掉 —— 那是使用者對**多開排程**的設定,
    /// 不該被這條獨立的流程動到。</para></summary>
    ExpertDeliveryLoop,
}
