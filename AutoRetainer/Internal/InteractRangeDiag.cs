using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using GameObjectStruct = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace AutoRetainer.Internal;

/// <summary>
/// 傳喚鈴互動距離的對照診斷：手寫常數（<see cref="Utils.GetValidInteractionDistance"/>）對遊戲 <c>EventFramework.CheckInteractRange</c>。
/// </summary>
/// <remarks>
/// 🔴 只記錄，不影響要不要互動；只在框架執行緒呼叫（任務管理器的步驟），傳進來的物件是同一幀取的。
/// 每次載入第一次問得到遊戲答案時記一行（一致也記，當校準錨）；之後只記不一致，同一種 30 秒一行。
/// </remarks>
internal static unsafe class InteractRangeDiag
{
    private const long MismatchIntervalMs = 30_000;
    private static bool AnchorLogged;
    private static bool UnavailableLogged;
    private static int Mismatches;
    private static (bool Constant, bool Game, byte Kind, ushort Territory) LastMismatchKey;
    private static long LastMismatchAt;

    internal static void CompareBell(IGameObject bell, float distance, float limit)
    {
        var player = Svc.Objects.LocalPlayer;
        if(player == null) return;
        var game = AskGame(player, bell);
        if(game == null)
        {
            if(!UnavailableLogged)
            {
                UnavailableLogged = true;
                PluginLog.Information("[BellRange] 問不到遊戲的 CheckInteractRange（函式沒解析到或 EventFramework 還沒建立），這次不對照；之後問得到會再記");
            }
            return;
        }

        var constant = distance < limit && bell.IsTargetable;
        var agree = constant == game.Value;
        if(agree && AnchorLogged) return;

        var now = Environment.TickCount64;
        var territory = Svc.ClientState.TerritoryType;
        var key = (constant, game.Value, (byte)bell.ObjectKind, territory);
        if(!agree)
        {
            Mismatches++;
            if(AnchorLogged && key == LastMismatchKey && now - LastMismatchAt < MismatchIntervalMs) return;
            LastMismatchKey = key;
            LastMismatchAt = now;
        }

        var head = AnchorLogged ? "傳喚鈴互動距離判定不一致" : (agree ? "首次對照（一致）" : "首次對照（不一致）");
        AnchorLogged = true;
        var dy = bell.Position.Y - player.Position.Y;
        PluginLog.Information($"[BellRange] {head}：常數={constant}（距離 {distance:F2} m，門檻 {limit:F2} m，可選取 {bell.IsTargetable}），遊戲 CheckInteractRange={game.Value}；鈴 {bell.ObjectKind} 0x{bell.BaseId:X}，區域 {territory}，高低差 {dy:+0.00;-0.00} m，本次載入不一致判定累計 {Mismatches} 次（每幀算一次）");
    }

    /// <returns><see langword="null"/>＝問不到（函式沒解析到，或單例還沒建立）。</returns>
    private static bool? AskGame(IGameObject player, IGameObject target)
    {
        if(EventFramework.MemberFunctionPointers.CheckInteractRange is null) return null;
        try
        {
            var framework = EventFramework.Instance();
            if(framework == null) return null;
            return framework->CheckInteractRange((GameObjectStruct*)player.Address, (GameObjectStruct*)target.Address, 1, false);
        }
        catch(InvalidOperationException)
        {
            return null;
        }
    }
}
