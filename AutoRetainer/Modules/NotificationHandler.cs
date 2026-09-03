using AutoRetainer.Services;
using Dalamud.Game.Text.SeStringHandling;
using ECommons.ChatMethods;

namespace AutoRetainer.Modules;

internal static class NotificationHandler
{
    internal static bool CurrentState = false;
    internal static bool IsNotified = false;
    internal static bool IsHidden = false;
    internal static void Tick()
    {
        var currentState = GetNotifyState();
        if(currentState != CurrentState)
        {
            CurrentState = currentState;
            if(currentState)
            {
                if(C.NotifyDisplayInChatX) Svc.Chat.Print(new()
                {
                    Message = new SeStringBuilder().AddUiForeground("[AutoRetainer] Some of the retainers have completed their ventures!", (ushort)UIColor.Green).Build()
                });
                IsHidden = false;
                IsNotified = true;
                // 🔴 這裡是<b>狀態邊緣</b>（currentState 剛從 false 翻成 true），
                //    不是輪詢路徑——放進 GetNotifyState 或 Tick 開頭的話會變成每幀一顆氣球。
                RetainerTrayNotify.OnRetainersBecameAvailable();
            }
            else
            {
                IsNotified = false;
                IsHidden = false;
            }
        }
    }

    internal static bool GetNotifyState()
    {
        if(C.NotifyIncludeAllChara)
        {
            foreach(var x in C.OfflineData)
            {
                if(!C.NotifyIgnoreNoMultiMode || x.Enabled)
                {
                    foreach(var r in x.RetainerData)
                    {
                        if(r.HasVenture && r.GetVentureSecondsRemaining() <= 0)
                        {
                            return true;
                        }
                    }
                }
            }
            return false;
        }
        else
        {
            if(SvcEx.PlayerState.ContentId != 0 && C.OfflineData.TryGetFirst(x => x.CID == SvcEx.PlayerState.ContentId, out var x))
            {
                foreach(var r in x.RetainerData)
                {
                    if(r.HasVenture && r.GetVentureSecondsRemaining() <= 0)
                    {
                        return true;
                    }
                }
            }
            return false;
        }
    }
}
