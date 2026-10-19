namespace AutoRetainer.Modules.Multi;

/// <summary>多角色模式這一次開／關是誰造成的。</summary>
/// <remarks>數值是 IPC <c>AutoRetainer.GetMultiModeLastChangeV1</c> 與 <c>AutoRetainer.GetLastManualRelogV1</c> 的契約：只可新增、不可改號。</remarks>
internal enum MultiModeChangeCause
{
    /// <summary>載入後還沒變過。</summary>
    None = 0,

    /// <summary>使用者在介面上按的、打的指令，或因使用者手動換角而關。</summary>
    User = 1,

    /// <summary>AutoRetainer 自己決定的（跑完、條件不符停下、錯誤、自動開啟、重載後恢復）。</summary>
    AutoRetainer = 2,

    /// <summary>別的插件經 IPC 要求的。</summary>
    OtherPlugin = 3,
}
