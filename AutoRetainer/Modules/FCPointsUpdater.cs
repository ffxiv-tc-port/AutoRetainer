using ECommons.Automation;
using ECommons.Automation.NeoTaskManager;
using ECommons.Events;
using ECommons.EzEventManager;
using ECommons.GameHelpers;
using ECommons.UIHelpers;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace AutoRetainer.Modules;
public sealed unsafe class FCPointsUpdater
{
    private readonly TaskManager TaskManager = new(new(timeLimitMS: 15000, abortOnTimeout: true, showDebug: false));
    private int OldFCPoints;

    private FCPointsUpdater()
    {
        ProperOnLogin.RegisterInteractable(() => ScheduleUpdateIfNeeded(), true);
        new EzLogout(() => TaskManager.Abort());
        new EzTerritoryChanged((x) => ScheduleUpdateIfNeeded());
    }

    public bool IsFCChestReady()
    {
        if(TryGetAddonByName<AtkUnitBase>("FreeCompanyChest", out var addon) && IsAddonReady(addon))
        {
            var reader = new ReaderFreeCompanyChest(addon);
            return reader.Ready;
        }
        return false;
    }

    public class ReaderFreeCompanyChest(AtkUnitBase* UnitBase, int BeginOffset = 0) : AtkReader(UnitBase, BeginOffset)
    {
        public bool Ready => ReadUInt(10) == 0;
    }

    public void ScheduleUpdateIfNeeded(bool force = false)
    {
        if(!Player.Available) return;
        if(!C.UpdateStaleFCData) return;
        if(!Player.IsInHomeWorld) return;
        if(Data != null && Data.FCID != 0 && C.FCData.TryGetValue(Data.FCID, out var fcdata))
        {
            if(force || DateTimeOffset.Now.ToUnixTimeMilliseconds() > fcdata.FCPointsLastUpdate + 30 * 60 * 60 * 1000)
            {
                OldFCPoints = Utils.FCPoints;
                TaskManager.Abort();
                TaskManager.Enqueue(() => !IsOccupied());
                TaskManager.Enqueue(() => IsScreenReady() && Player.Interactable);
                TaskManager.Enqueue(() =>
                {
                    // Close(true) 對關閉中的窗再叫一次同樣未證安全:同一扇 FreeCompany 只關一次。
                    // 🔴 這一顆必須有回傳值(區塊 lambda),不能是 Action:NeoTaskManager 的
                    // TaskManagerTask(Action …) 建構子把它包成 () => { action(); return true; }
                    // (ECommons/Automation/NeoTaskManager/TaskManagerTask.cs:29-39 與 57-67),
                    // 任務內部回傳的 false 一律被吞掉。原本這個 block 從頭到尾沒有任何 return,綁到的就是 Action ——
                    // 被守衛擋下時等於「這一步整個跳過」:那扇 FreeCompany 沒被關掉,整條鏈
                    // 「/freecompanycmd 開出一扇新窗」的前提就不成立。
                    // ⚠️ 之後的分歧離線證不完 —— 台服 TextCommand #161 對 /freecompanycmd 只寫「打開公會視窗」,
                    // 沒說是不是 toggle(所以下面兩條哪一條成立,要實機才知道):
                    //   ・若是 toggle:那道指令會把窗關掉而不是打開 → CloseAfter 永遠等不到窗、回 false 到 15 秒逾時,
                    //     而這個 TaskManager 是 abortOnTimeout: true(:12)⇒ 整條佇列(含最後寫回離線資料)被清掉。
                    //   ・若只是單純開啟:同一扇窗留在同一個位址,CloseAfter 撞到同一把守衛 key
                    //     (兩顆都不帶 paramKey ⇒ 共用「FreeCompany」這把),要等 60 幀逃生口才放行,代價約一秒。
                    //   兩種情形這個改法都對,而且都比原本安全。
                    // 🔑 為什麼同檔 CloseAfter 那顆是對的、只有這顆錯:兩顆的上游原形本來就不一樣。
                    // 上游(git show e707e02^)的 CloseBefore 就是 Enqueue(Action) —— 語意是「窗剛好開著就順手關掉」,
                    // 那個形狀下「條件不成立」只有一種意思(沒事可做),用 Action 完全正確;
                    // 而上游的 CloseAfter 本來就有回傳值,因為它的語意是「等到窗出現再關」。
                    // 守衛 commit 把 TryPressOnce **AND 進這裡原本的 if 條件**、把 return false **插進 CloseAfter 原本的區塊**,
                    // 於是同一個守衛落進兩種結構:那邊接得住「這一輪先別按」,這邊卻讓「沒事可做」與
                    // 「守衛說現在不行」塌成同一個分支。⇒ 不是漏改,是把守衛掛到一個表達不了「稍後再試」的既有形狀上。
                    // 🔴 絕不回 null:NeoTaskManager 的 bool? 三態裡 null 是 Abort(),會清掉整條佇列
                    // (寫成 bool 而不是 bool? 就從語言層面排除了這件事,與 CloseAfter 那顆一致)。
                    if(TryGetAddonByName<AtkUnitBase>("FreeCompany", out var addon))
                    {
                        if(!DialogGuards.TryPressOnce("FreeCompany", (nint)addon, "FCPoints.CloseBefore")) return false;
                        addon->Close(true);
                        TaskManager.InsertDelay(10, true);
                        return true;
                    }
                    // 窗本來就沒開著(常態):這一步已經達成,鏈往下走。
                    // ⚠️ 與 CloseAfter 那顆的 false 刻意相反 —— 那顆要等 /freecompanycmd 把窗開出來,這顆是「有開著才關」。
                    return true;
                }, "FC 點數:先關掉已經開著的部隊視窗");
                TaskManager.Enqueue(() => Chat.ExecuteCommand("/freecompanycmd"));
                /*TaskManager.Enqueue(() =>
								{
										if(TryGetAddonByName<AtkUnitBase>("FreeCompany", out var addon))
										{
												if (addon->IsVisible())
												{
														addon->IsVisible() = false;
														return true;
												}
										}
										return false;
								});*/
                TaskManager.Enqueue(() =>
                {
                    if(TryGetAddonByName<AtkUnitBase>("FreeCompany", out var addon))
                    {
                        // 上一步關掉的那扇若還在關閉中(10 幀延遲與危險窗口同量級),這裡看到的是同一位址:不再關第二次。
                        if(!DialogGuards.TryPressOnce("FreeCompany", (nint)addon, "FCPoints.CloseAfter")) return false;
                        addon->Close(true);
                        return true;
                    }
                    return false;
                }, "FC 點數:讀完點數之後把部隊視窗關掉");
                TaskManager.Enqueue(() => Utils.FCPoints != OldFCPoints, new(abortOnTimeout: false));
                TaskManager.Enqueue(() => OfflineDataManager.WriteOfflineData(false, true));
            }
        }
    }
}
