using ECommons.Configuration;
using ECommons.Events;
using ECommons.ExcelServices;
using ECommons.MathHelpers;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Component.GUI;
using FXWindows = TerraFX.Interop.Windows.Windows;
using ItemLevel = AutoRetainer.Helpers.ItemLevel;

namespace AutoRetainer.UI.NeoUI.AdvancedEntries.DebugSection;

internal unsafe class DebugMisc : DebugSectionBase
{
    public override void Draw()
    {
        if(ImGui.CollapsingHeader("Title screen / login overlay readiness"))
        {
            ImGuiEx.Text($"CanAutoLogin(): {Utils.CanAutoLogin()}");
            ImGuiEx.Text($"CanAutoLoginFromTaskManager(): {Utils.CanAutoLoginFromTaskManager()}");
            ImGuiEx.Text($"TaskManager.IsBusy: {P.TaskManager.IsBusy}");
            ImGuiEx.Text($"IsLoggedIn: {Svc.ClientState.IsLoggedIn}");
            ImGuiEx.Text($"Condition.Any(): {Svc.Condition.Any()}");
            ImGuiEx.Text($"IsTitleScreenReady(): {Utils.IsTitleScreenReady()}");
            var found = TryGetAddonByName<AtkUnitBase>("_TitleMenu", out var title);
            ImGuiEx.Text($"_TitleMenu found: {found}");
            if(found)
            {
                ImGuiEx.Text($"IsAddonReady: {IsAddonReady(title)}");
                ImGuiEx.Text($"NodeListCount: {title->UldManager.NodeListCount}");
                if(title->UldManager.NodeListCount > 3)
                {
                    ImGuiEx.Text($"NodeList[3].Color.A: {title->UldManager.NodeList[3]->Color.A:X2}");
                }
                if(title->UldManager.NodeListCount > 7)
                {
                    ImGuiEx.Text($"NodeList[7].IsVisible(): {title->UldManager.NodeList[7]->IsVisible()}");
                }
            }
            ImGuiEx.Text($"TitleDCWorldMap found: {TryGetAddonByName<AtkUnitBase>("TitleDCWorldMap", out _)}");
            ImGuiEx.Text($"TitleConnect found: {TryGetAddonByName<AtkUnitBase>("TitleConnect", out _)}");
            ImGuiEx.Text($"LoginOverlay.IsOpen: {P.LoginOverlay.IsOpen}");
            ImGuiEx.Text($"LoginOverlay ms since last Draw(): {(P.LoginOverlay.LastDrawTick == 0 ? "never drawn" : (Environment.TickCount64 - P.LoginOverlay.LastDrawTick).ToString())}");
            ImGuiEx.Text($"LoginOverlay last drawn character count: {P.LoginOverlay.LastDrawnCharaCount}");
            ImGuiEx.Text($"C.OfflineData.Count: {C.OfflineData.Count}");
            ImGuiEx.Text($"C.LoginOverlay: {C.LoginOverlay}");
        }
        if(ImGui.CollapsingHeader("Retainer item stats"))
        {
            var im = InventoryManager.Instance();
            // 除錯顯示。RetainerEquippedItems 只有在雇員視窗開著時才載入，沒開就是 null——常態不是異常。
            var c = im->GetInventoryContainer(InventoryType.RetainerEquippedItems);
            if(c == null)
            {
                ImGuiEx.Text(EColor.RedBright, "Container not loaded (no retainer open)");
            }
            else
            {
                for(var i = 0; i < c->Size; i++)
                {
                    var slot = c->GetInventorySlot(i);
                    if(slot == null)
                    {
                        ImGuiEx.Text(EColor.RedBright, $"{i}: <unreadable>");
                        continue;
                    }
                    ImGuiEx.Text($"{i} ({slot->GetItemId()}): {ExcelItemHelper.GetName(slot->GetItemId() % 1000000)}, gathering: {slot->GetStat(BaseParamEnum.Gathering)} [{slot->GetStatCap(BaseParamEnum.Gathering)}], perception: {slot->GetStat(BaseParamEnum.Perception)} [{slot->GetStatCap(BaseParamEnum.Perception)}]");
                }
            }
        }
        if(ImGui.Button("Test Haseltweaks"))
        {
            Utils.EnsureEnhancedLoginIsOff();
        }
        if(ImGui.Button("Write config via external process"))
        {
            ExternalWriter.PlaceWriteOrder(new(System.IO.Path.Combine(Svc.PluginInterface.ConfigDirectory.FullName, "WriterTest.json"), EzConfig.DefaultSerializationFactory.Serialize(C, true)));
        }
        // 讀不到時顯示 ?,不要畫成 0 —— 0 是合法的部隊點數,把「不知道」渲染成 0 會直接誤導人。
        ImGuiEx.Text($"FC points: {(Utils.TryGetFCPoints(out var fcPoints) ? fcPoints.ToString() : "?")}");
        if(ImGui.CollapsingHeader("Housing"))
        {
            var h = HousingManager.Instance();
            // 讀不到就在列上直說，不要把「不知道」畫成 0/-1 —— 0 是合法的分區與地塊編號。
            if(h == null)
            {
                ImGuiEx.Text($"HousingManager: ? (unavailable)");
            }
            else
            {
                ImGuiEx.Text($"GetCurrentDivision {h->GetCurrentDivision()}");
                ImGuiEx.Text($"GetCurrentHouseId {h->GetCurrentIndoorHouseId()}");
                ImGuiEx.Text($"GetCurrentPlot {h->GetCurrentPlot()}");
                ImGuiEx.Text($"GetCurrentRoom {h->GetCurrentRoom()}");
                ImGuiEx.Text($"GetCurrentWard {h->GetCurrentWard()}");
            }
            if(ImGui.Button("Simulate login"))
            {
                ProperOnLogin.FireArtificially();
            }
            if(h != null && h->OutdoorTerritory != null)
            {
                for(var i = 0; i < 30; i++)
                {
                    ImGuiEx.Text($"IsEstateResident {i}: {P.Memory.OutdoorTerritory_IsEstateResident((nint)h->OutdoorTerritory, (byte)i)}");
                }
            }
        }
        if(ImGui.Button("Install callback hook")) Callback.InstallHook();
        if(ImGui.Button("Disable callback hook")) Callback.UninstallHook();
        ImGuiEx.TextCopy($"{(nint)(&TargetSystem.Instance()->Target):X16}");
        ImGui.Checkbox($"Log opcodes", ref P.LogOpcodes);
        ImGuiEx.Text($"CSFramework.Instance()->FrameCounter: {CSFramework.Instance()->FrameCounter}");
        if(ImGui.Button("Test entrust dup"))
        {
            if(TryGetAddonByName<AtkUnitBase>("RetainerItemTransferList", out var addon))
            {
                Callback.Fire(addon, true, 0, (uint)29);
            }
        }
        ImGuiEx.Text($"Lockon: {*(byte*)((nint)TargetSystem.Instance() + 309)}");
        if(ImGui.Button("Chill frames lock"))
        {
            FPSManager.LockChillFrames();
        }
        if(ImGui.Button("Unlock frames lock"))
        {
            FPSManager.UnlockChillFrames();
        }
        ImGui.Separator();
        ImGuiEx.Text($"CSFramework.Instance()->WindowInactive: {CSFramework.Instance()->WindowInactive}");
        ImGuiEx.Text($"IsKeyPressed(C.TempCollectB): {IsKeyPressed(C.TempCollectB)}");
        ImGuiEx.Text($"Bitmask.IsBitSet(User32.GetKeyState((int)C.TempCollectB), 15): {Bitmask.IsBitSet(FXWindows.GetKeyState((int)C.TempCollectB), 15)}");
        ImGuiEx.Text($"DontReassign: {C.DontReassign}, key {C.TempCollectB}/{(int)C.TempCollectB}");
        foreach(var x in C.OfflineData)
        {
            ImGuiEx.Text($"{x.Name}@{x.World}: {x.Gil + x.RetainerData.Sum(z => z.Gil)}");
        }
        var ocd = Data;
        if(ocd != null)
        {
            ImGuiEx.Text($"Level array:");
            ImGuiEx.Text(ocd.ClassJobLevelArray.Print());
        }

        ImGuiEx.Text($"{Utils.TryGetCurrentRetainer(out var n)}/{n}");
        ImGuiEx.Text($"{ItemLevel.Calculate(out var g, out var p)}/{g}/{p}");
        if(ImGui.Button("Regenerate censor seed"))
        {
            C.CensorSeed = Guid.NewGuid().ToString();
        }
        var inv = Utils.GetActiveRetainerInventoryName();
        ImGuiEx.Text($"Utils.GetActiveRetainerInventoryName(): {inv.Name} {inv.EntrustDuplicatesIndex}");
        ImGuiEx.Text($"ConditionWasEnabled={P.ConditionWasEnabled}");
        if(ImGui.CollapsingHeader("Task debug"))
        {
            ImGuiEx.Text($"Busy: {P.TaskManager.IsBusy}, abort in {P.TaskManager.RemainingTimeMS}");
            if(ImGui.Button($"Generate random numbers 1/500"))
            {
                P.TaskManager.Enqueue(() => { var r = new Random().Next(0, 500); InternalLog.Verbose($"Gen 1/500: {r}"); return r == 0; });
            }
            if(ImGui.Button($"Generate random numbers 1/5000"))
            {
                P.TaskManager.Enqueue(() => { var r = new Random().Next(0, 5000); InternalLog.Verbose($"Gen 1/5000: {r}"); return r == 0; });
            }
            if(ImGui.Button($"Generate random numbers 1/100"))
            {
                P.TaskManager.Enqueue(() => { var r = new Random().Next(0, 100); InternalLog.Verbose($"Gen 1/100: {r}"); return r == 0; });
            }
        }
        ImGuiEx.Text($"QSI status: {P.quickSellItems?.openInventoryContextHook?.IsEnabled}");
        ImGuiEx.Text($"QuickSellItems.IsReadyToUse: {QuickSellItems.IsReadyToUse()}");

        foreach(var x in S.VentureStats.CharTotal)
        {
            ImGuiEx.Text($"{x.Key} : {x.Value}");
        }
        foreach(var x in S.VentureStats.RetTotal)
        {
            ImGuiEx.Text($"{x.Key} : {x.Value}");
        }

        ImGui.Separator();
        {
            if(ImGui.Button("Fire") && TryGetAddonByName<AtkUnitBase>("GrandCompanySupplyList", out var addon) && IsAddonReady(addon) && addon->UldManager.NodeList[5]->IsVisible())
            {
                AutoGCHandin.InvokeHandin(addon, 0);
            }
        }

        {
            if(TryGetAddonByName<AtkUnitBase>("GrandCompanySupplyList", out var addon) && IsAddonReady(addon))
            {
                ImGuiEx.Text($"IsSelectedFilterValid: {AutoGCHandin.IsSelectedFilterValid(addon)}");
            }
        }

    }
}
