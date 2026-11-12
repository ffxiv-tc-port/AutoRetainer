namespace AutoRetainer.Internal;

/// <summary>改版後版面／特徵碼對不上而停用的功能被走到時，給使用者看得見的說明。只在框架執行緒呼叫。</summary>
internal static class UnavailableNotice
{
    private static readonly HashSet<string> Shown = [];

    internal static string SupplyListMessage
        => string.Format(Loc.T("Automatic expert delivery is unavailable: after a game update AutoRetainer could not verify the layout of the expert delivery list, so it will not read it. Other features are unaffected. Details: {0}"), NativeLayout.SupplyListFailure);

    internal static string RetainerItemCommandMessage
        => string.Format(Loc.T("Entrusting items to retainers, retrieving them and having retainers sell them are unavailable: after a game update AutoRetainer could not verify the game's retainer item command, so these steps are skipped. Other features are unaffected. Details: {0}"), P.Memory?.RetainerItemCommandFailure);

    internal static void ReportSupplyList(string where) => Report("SupplyList", where, SupplyListMessage);

    internal static void ReportRetainerItemCommand(string where) => Report("RetainerItemCommand", where, RetainerItemCommandMessage);

    /// <summary>同一項功能每次載入只在聊天視窗說一次，之後每次略過只記 Debug。</summary>
    private static void Report(string key, string where, string message)
    {
        if(Shown.Add(key)) DuoLog.Warning(message);
        PluginLog.Debug($"[UnavailableNotice] {key} skipped at {where}");
    }
}
