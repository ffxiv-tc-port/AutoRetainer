using AutoRetainerAPI.Configuration;
using Dalamud.Game.ClientState.Objects.SubKinds;
using ECommons;
using ECommons.Automation.NeoTaskManager;
using ECommons.Automation.NeoTaskManager.Tasks;
using ECommons.Automation.UIInput;
using ECommons.ExcelServices;
using ECommons.ExcelServices.Sheets;
using ECommons.GameFunctions;
using ECommons.GameHelpers;
using ECommons.MathHelpers;
using ECommons.Throttlers;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using System.Reflection.Metadata.Ecma335;

namespace AutoRetainer.Modules.GcHandin;

internal static unsafe class GCContinuation
{
    public static readonly GCInfo Maelstrom = new(1002387, 1002388, new(92.751045f, 40.27537f, 75.468185f));
    public static readonly GCInfo ImmortalFlames = new(1002390, 1002391, new(-141.44354f, 4.109951f, -106.125496f));
    public static readonly GCInfo TwinAdder = new(1002393, 1002394, new(-67.464386f, -0.5018193f, -8.161054f));

    public static readonly uint VentureItem = 21072;

    public static bool DebugMode = false;
    public static bool DebugConf = false;

    /// <summary>
    /// Per-step configuration for the continuation chains, deliberately mirroring the one
    /// <see cref="ContinuePurchase"/> already uses.
    ///
    /// P.TaskManager defaults to abortOnTimeout:true and Abort() clears the ENTIRE queue, so under
    /// the default configuration ANY step of this chain timing out also discarded
    /// <see cref="EnableDeliveringIfPossible"/> - the one and only place that sets
    /// <see cref="AutoGCHandin.Operation"/> back to true. The user-visible result was "it stopped
    /// handing in after spending my seals", reported nowhere except a PluginLog.Warning.
    ///
    /// Skipping a step instead of killing the queue is safe here because every step is idempotent
    /// and self-checking: they each look for their own addon and return false until it is present,
    /// so a step that is skipped simply leaves its window unopened and the following steps also fall
    /// through without acting. Nothing in this chain commits an irreversible action - the purchases
    /// themselves are gated behind ContinuePurchase, which already used exactly this configuration.
    /// </summary>
    private static readonly TaskManagerConfiguration ContinuationConf = new(abortOnTimeout: false, timeLimitMS: 20000);

    public static void EnqueueInitiation(bool redeliver)
    {
        P.TaskManager.Enqueue(GCContinuation.WaitUntilNotOccupied, ContinuationConf);
        P.TaskManager.Enqueue(GCContinuation.InteractWithShop, ContinuationConf);
        P.TaskManager.Enqueue(BeginNewPurchase, ContinuationConf);
        P.TaskManager.Enqueue(GCContinuation.WaitUntilNotOccupied, ContinuationConf);
        if(redeliver)
        {
            P.TaskManager.Enqueue(GCContinuation.InteractWithExchange, ContinuationConf);
            P.TaskManager.Enqueue(GCContinuation.SelectProvisioningMission, ContinuationConf);
            P.TaskManager.Enqueue(() => GCContinuation.SelectSupplyListTab(2), "SelectSupplyListTab(2)", ContinuationConf);
            P.TaskManager.Enqueue(GCContinuation.EnableDeliveringIfPossible, ContinuationConf);
            // Without this the failure stays invisible: if the chain gets as far as here but the
            // supply list never becomes operable, Operation is left false and automatic delivery
            // just never resumes, with nothing said in chat. Runs as its own step so it reports the
            // outcome of the whole chain rather than of any single addon check.
            P.TaskManager.Enqueue(ReportIfDeliveringDidNotResume, "ReportIfDeliveringDidNotResume", ContinuationConf);
        }
    }

    private static void ReportIfDeliveringDidNotResume()
    {
        if(AutoGCHandin.Operation) return;
        DuoLog.Warning(Loc.T("Could not resume automatic expert delivery after spending seals - reopen the supply list and enable it again if you want to continue."));
    }

    public static void EnqueueDeliveryClose()
    {
        // Same reasoning as above: CloseSupplyList failing used to take CloseSelectString down with
        // it, leaving the retainer-style selection window sitting on screen with no explanation.
        P.TaskManager.Enqueue(GCContinuation.CloseSupplyList, ContinuationConf);
        P.TaskManager.Enqueue(GCContinuation.CloseSelectString, ContinuationConf);
        P.TaskManager.Enqueue(GCContinuation.WaitUntilNotOccupied, ContinuationConf);
    }

    internal static bool SetVenturesExchangeAmount(int amount)
    {
        if(TryGetAddonByName<AtkUnitBase>("ShopExchangeCurrencyDialog", out var addon) && IsAddonReady(addon))
        {
            if(EzThrottler.Throttle("GC SetMaxVenturesExchange"))
            {
                // 🔴 NodeList[8] 原本上界與元素都沒驗,GetComponent() 的回值也沒判空就直接
                //    numeric->SetValue()。三層任一取不到就回 false —— 呼叫端把 false 當成
                //    「這次還沒設定成功」而在下一個節流窗重試,與既有的「addon 尚未 ready」同語意。
                //    🔑 這裡絕不能回 true:回 true 等於謊報「數量已設好」,後面的確認步驟會照
                //    版面上的預設值(1 個)成交,使用者拿到的委託票數量會靜默地不對。
                var numericNode = Utils.GetNodeSafe(&addon->UldManager, 8);
                var numeric = numericNode == null ? null : (AtkComponentNumericInput*)numericNode->GetComponent();
                if(numeric == null)
                {
                    PluginLog.Information("ShopExchangeCurrencyDialog NodeList[8] 的數量輸入元件取不到(版面未建好或已拆除),這一輪不設定數量");
                    return false;
                }
                var sealsPer = Utils.GetCurrentlyAvailableSharedExchangeListings().SafeSelect(VentureItem)?.Seals ?? 200u;
                var maxBySeals = sealsPer > 0 ? (int)(GetAdjustedSeals() / sealsPer) : amount;
                var set = Math.Min(amount, maxBySeals);
                if(set < 1) throw new Exception($"Venture amount is too low, is {set}, expected 1 or more");
                PluginLog.Debug($"Setting {set} ventures");
                numeric->SetValue((int)set);
                return true;
            }
        }
        return false;
    }

    internal static bool? SelectExchange()
    {
        if(TryGetAddonByName<AtkUnitBase>("ShopExchangeCurrencyDialog", out var addon) && IsAddonReady(addon) && EzThrottler.Throttle("GC SelectExchange"))
        {
            var button = addon->GetComponentButtonById(17);
            if(Utils.IsButtonEnabled(button))
            {
                (*button).ClickAddonButton(addon);
            }
            return true;
        }
        return false;
    }

    internal static bool? ConfirmExchange()
    {
        {
            var x = Utils.GetSpecificYesno(x => x.RemoveWhitespaces().EqualsIgnoreCaseAny(Svc.Data.GetExcelSheet<Addon>().GetRow(2436).Text.GetText().RemoveWhitespaces(), Svc.Data.GetExcelSheet<Addon>().GetRow(11502).Text.GetText().RemoveWhitespaces()));
            if(x != null && FrameThrottler.Throttle("ConfirmCannotEquip", 4))
            {
                new AddonMaster.SelectYesno((nint)x).Yes();
                return false;
            }
        }
        {
            var x = Utils.GetSpecificYesno(x => x.ContainsAny(StringComparison.OrdinalIgnoreCase, Lang.GCSealExchangeConfirm));
            if(x != null && EzThrottler.Throttle("GC ConfirmExchange"))
            {
                new AddonMaster.SelectYesno((nint)x).Yes();
                return true;
            }
        }
        return false;
    }

    internal static bool? SelectGCExchangeVerticalTab(int which)
    {
        if(!which.InRange(0, 3, false)) throw new ArgumentOutOfRangeException(nameof(which));
        if(TryGetAddonByName<AtkUnitBase>("GrandCompanyExchange", out var addon) && IsAddonReady(addon) && EzThrottler.Throttle("GC SelectGCExchangeVerticalTab"))
        {
            // 🔴 fail-closed：取不到按鈕就回 false（＝這一輪沒做成，下一幀重試），
            //    不能回 true（會讓流程以為分頁已經切好而繼續往下走），
            //    更不能回 null —— NeoTaskManager 的 bool? 是三態，null 是 Abort，會把整條佇列清掉。
            //    這與外層 addon 還沒就緒時走的那條 return false 是同一個語意。
            if(!Utils.TryGetRadioButtonById(addon, (uint)(37 + which), out var button)) return false;
            button->ClickRadioButton(addon);
            return true;
        }
        return false;
    }

    internal static bool? SelectGCExchangeHorizontalTab(int which)
    {
        if(!which.InRange(0, 4, false)) throw new ArgumentOutOfRangeException(nameof(which));
        if(TryGetAddonByName<AtkUnitBase>("GrandCompanyExchange", out var addon) && IsAddonReady(addon) && EzThrottler.Throttle("GC SelectGCExchangeHorizontalTab"))
        {
            // fail-closed 同上：取不到就當「這一輪沒切成」重試，不是 Abort。
            if(!Utils.TryGetRadioButtonById(addon, (uint)(44 + which), out var button)) return false;
            button->ClickRadioButton(addon);
            return true;
        }
        return false;
    }

    internal static GCInfo? GetGCInfo()
    {
        if(PlayerState.Instance()->GrandCompany == 1) return Maelstrom;
        if(PlayerState.Instance()->GrandCompany == 2) return TwinAdder;
        if(PlayerState.Instance()->GrandCompany == 3) return ImmortalFlames;
        return null;
    }

    internal static bool? InteractWithExchange()
    {
        return InteractWithDataID(GetGCInfo().Value.ExchangeDataID);
    }

    internal static bool? InteractWithShop()
    {
        return InteractWithDataID(GetGCInfo().Value.ShopDataID);
    }

    private static bool? InteractWithDataID(uint dataID)
    {
        if(Svc.Targets.Target != null)
        {
            if(Player.IsAnimationLocked) return false;
            var t = Svc.Targets.Target;
            if(t.IsTargetable && t.BaseId == dataID && Vector3.Distance(Player.Object.Position, t.Position) < 10f && !IsOccupied() && EzThrottler.Throttle("GCInteract"))
            {
                TargetSystem.Instance()->InteractWithObject(Svc.Targets.Target.Struct(), false);
                return true;
            }
        }
        else
        {
            foreach(var t in Svc.Objects)
            {
                if(t.IsTargetable && t.BaseId == dataID && Vector3.Distance(Player.Object.Position, t.Position) < 10f && !IsOccupied() && EzThrottler.Throttle("GCSetTarget"))
                {
                    Svc.Targets.Target = t;
                    return false;
                }
            }
        }
        return false;
    }

    internal static bool? WaitUntilNotOccupied()
    {
        return !IsOccupied();
    }

    internal static bool? SelectProvisioningMission()
    {
        if(TryGetAddonByName<AddonSelectString>("SelectString", out var addon) && IsAddonReady(&addon->AtkUnitBase))
        {
            if(EzThrottler.Throttle("SelectProvisioningMission") && Utils.TrySelectSpecificEntry(Svc.Data.GetExcelSheet<QuestDialogueText>(name: "custom/000/ComDefGrandCompanyOfficer_00073").GetRow(69).Value.GetText()))
            {
                return true;
            }
        }
        return false;
    }

    internal static bool? SelectSupplyListTab(int which)
    {
        if(!which.InRange(0, 3, false)) throw new ArgumentOutOfRangeException(nameof(which));
        if(TryGetAddonByName<AtkUnitBase>("GrandCompanySupplyList", out var addon) && IsAddonReady(addon) && EzThrottler.Throttle("GC SelectGCExpertDelivery"))
        {
            // fail-closed 同上：取不到就當「這一輪沒切成」重試，不是 Abort。
            if(!Utils.TryGetRadioButtonById(addon, (uint)(11 + which), out var button)) return false;
            button->ClickRadioButton(addon);
            return true;
        }
        return false;
    }

    internal static bool? EnableDeliveringIfPossible()
    {
        if(TryGetAddonByName<AtkUnitBase>("GrandCompanySupplyList", out var addon) && IsAddonReady(addon) && EzThrottler.Throttle("GC EnableDeliveringIfPossible"))
        {
            if(AutoGCHandin.Overlay.DrawConditions() && AutoGCHandin.Overlay.Allowed)
            {
                AutoGCHandin.Operation = true;
                return true;
            }
        }
        return false;
    }

    public static int GetTab(this GCExchangeCategoryTab cat)
    {
        return cat switch
        {
            GCExchangeCategoryTab.Materiel => 2,
            GCExchangeCategoryTab.Weapons => 0,
            GCExchangeCategoryTab.Armor => 1,
            GCExchangeCategoryTab.Materials => 3,
            _ => throw new ArgumentOutOfRangeException()
        };
    }

    internal static bool? CloseSupplyList()
    {
        if(TryGetAddonByName<AtkUnitBase>("GrandCompanySupplyList", out var addon) && IsAddonReady(addon) && EzThrottler.Throttle("GC CloseSupplyList"))
        {
            Callback.Fire(addon, true, -1);
            return true;
        }
        return false;
    }

    internal static bool? CloseSelectString()
    {
        if(TryGetAddonByName<AtkUnitBase>("SelectString", out var addon) && IsAddonReady(addon) && EzThrottler.Throttle("GC CloseSelectString"))
        {
            Callback.Fire(addon, true, -1);
            return true;
        }
        return false;
    }

    internal static bool? CloseExchange()
    {
        if(TryGetAddonByName<AtkUnitBase>("GrandCompanyExchange", out var addon) && IsAddonReady(addon) && EzThrottler.Throttle("GC GrandCompanyExchange"))
        {
            Callback.Fire(addon, true, -1);
            return true;
        }
        return false;
    }

    public static uint GetAdjustedSeals()
    {
        var plan = Utils.GetGCExchangePlanWithOverrides();
        return (uint)Math.Max(0, AutoGCHandin.GetSeals() - Math.Min(plan.RemainingSeals, AutoGCHandin.GetMaxSeals() - 20000));
    }

    public static uint GetAdjustedMaxSeals()
    {
        return (uint)(AutoGCHandin.GetMaxSeals() - Utils.GetGCExchangePlanWithOverrides().RemainingSeals);
    }

    internal static bool? OpenSeals()
    {

        if(TryGetAddonByName<AtkUnitBase>("GrandCompanyExchange", out var addon) && IsAddonReady(addon) && AutoGCHandin.IsValidGCTerritory())
        {
            var reader = new ReaderGrandCompanyExchange(addon);
            for(var i = 0; i < reader.Items.Count; i++)
            {
                var itemInfo = reader.Items[i];
                if(itemInfo.ItemID == 21072)
                {
                    var currentRank = AutoGCHandin.GetRank();
                    if(currentRank >= itemInfo.RankReq && GetAdjustedSeals() >= itemInfo.Seals)
                    {
                        if(FrameThrottler.Throttle("GCCont.OpenItem", 20))
                        {
                            Callback.Fire(addon, true, 0, i, 1, Callback.ZeroAtkValue, currentRank >= itemInfo.RankReq, itemInfo.OpenCurrencyExchange, itemInfo.ItemID, itemInfo.IconID, itemInfo.Seals);
                            return true;
                        }
                    }
                }
            }
        }
        return false;
    }

    public static uint GetAmountThatCanBePurchased(this GCExchangeItem item, bool potential = false)
    {
        var meta = Utils.GetCurrentlyAvailableSharedExchangeListings().SafeSelect(item.ItemID);
        if(meta == null) return 0;
        if(AutoGCHandin.GetRank() < meta.MinPurchaseRank) return 0;
        var potentialSeals = GetAdjustedMaxSeals() - 5000;
        if(GetAdjustedSeals() < meta.Seals)
        {
            if(potential)
            {
                var maxSeals = potentialSeals; //buffer
                if(maxSeals < meta.Seals) return 0;
            }
            else
            {
                return 0;
            }
        }

        var cnt = InventoryManager.Instance()->GetInventoryItemCount(meta.ItemID);

        var targetQuantity = item.QuantitySingleTime == 0 ? item.Quantity - cnt : item.QuantitySingleTime;
        if(targetQuantity <= 0) return 0;
        if(meta.ItemID == VentureItem)
        {
            var canBuy = (uint)(65000 - InventoryManager.Instance()->GetInventoryItemCount(VentureItem));
            canBuy = Math.Min(canBuy, (potential ? potentialSeals : GetAdjustedSeals()) / meta.Seals);
            return (uint)Math.Min(canBuy, targetQuantity);
        }

        var canFit = Utils.GetAmountThatCanFit(Utils.PlayerInvetories, meta.ItemID, false);
        if(canFit == 0)
        {
            if(!potential)
            {
                return 0;
            }
            else
            {
                if(!DoesInventoryHaveDeliverableItem())
                {
                    return 0;
                }
            }
        }
        canFit = Math.Min(canFit, (uint)targetQuantity);
        canFit = Math.Min(canFit, 99);
        canFit = Math.Min(canFit, meta.Data.StackSize);
        canFit = Math.Min(canFit, (potential ? potentialSeals : GetAdjustedSeals()) / meta.Seals);
        if(meta.Data.IsUnique)
        {
            canFit = Math.Min(canFit, 1);
            if(cnt > 0) return 0;
        }
        return canFit;
    }

    /// <remarks>
    /// 讀不到的容器／格位一律跳過，也就是**只可能少報不可能多報**。唯一的呼叫端拿 <c>false</c>
    /// 去走 <c>return 0</c>（＝這次不兌換），少報一律讓判斷倒向「不做事」。
    /// ⚠️ 解參考 null 在 .NET Core 是 corrupted-state exception，try/catch 攔不到，只能靠事前檢查。
    /// </remarks>
    public static bool DoesInventoryHaveDeliverableItem()
    {
        foreach(var x in Utils.PlayerInvetories)
        {
            var inv = InventoryManager.Instance()->GetInventoryContainer(x);
            if(inv == null) continue;
            for(var i = 0; i < inv->GetSize(); i++)
            {
                var item = inv->GetInventorySlot(i);
                if(item == null) continue;
                if(item->ItemId == 0 || Data.GetIMSettings().IMProtectList.Contains(item->ItemId)) continue;
                // ⚠️ data 必須在 ItemId == 0 的早退之後才取值：ExcelItemHelper.Get 對未知 ID 回 null，
                // 而原本的寫法先 Get 再讀 data.Value，未知 ID 會丟 InvalidOperationException。
                var data = ExcelItemHelper.Get(item->ItemId);
                if(data == null) continue;
                if(!data.Value.ItemUICategory.RowId.EqualsAny([.. Utils.ArmorsUICategories, .. Utils.WeaponsUICategories])) continue;
                if(!data.Value.GetRarity().EqualsAny(ItemRarity.Green, ItemRarity.Pink, ItemRarity.Blue)) continue;
                if(data.Value.Desynth == 0) continue;
                return true;
            }
        }
        return false;
    }

    public static bool PurchaseItem(this GCExchangeItem item)
    {
        var meta = Utils.GetCurrentlyAvailableSharedExchangeListings()[item.ItemID];
        var amount = item.GetAmountThatCanBePurchased();
        if(TryGetAddonByName<AtkUnitBase>("GrandCompanyExchange", out var addon) && IsAddonReady(addon) && AutoGCHandin.IsValidGCTerritory())
        {
            var reader = new ReaderGrandCompanyExchange(addon);
            if(reader.RankTab != meta.Rank)
            {
                if(Utils.GenericThrottle)
                {
                    if(CleanupUI()) return false;
                    SelectGCExchangeVerticalTab((int)meta.Rank);
                }
                return false;
            }
            else
            {
                for(var i = 0; i < reader.Items.Count; i++)
                {
                    var itemInfo = reader.Items[i];
                    if(itemInfo.ItemID == meta.ItemID)
                    {
                        var canPurchase = AutoGCHandin.GetRank() >= itemInfo.RankReq;
                        var adjustedAmount = itemInfo.Stackable ? amount : 1;
                        var currentSealsCount = AutoGCHandin.GetSeals();
                        if(itemInfo.ItemID == VentureItem)
                        {
                            if(Utils.GenericThrottle && EzThrottler.Throttle("GCBuy"))
                            {
                                if(CleanupUI()) return false;
                                if(!DebugConf)
                                {
                                    Callback.Fire(addon, true, 0, i, 1, Callback.ZeroAtkValue, canPurchase, itemInfo.OpenCurrencyExchange, itemInfo.ItemID, itemInfo.IconID, itemInfo.Seals);
                                }
                                else
                                {
                                    DuoLog.Information($"Purchasing {i}'th item {itemInfo.Name} (venture)");
                                }
                                ContinuePurchase(meta, amount, currentSealsCount, item);
                                return true;
                            }
                        }
                        else
                        {
                            if(Utils.GenericThrottle && EzThrottler.Throttle("GCBuy"))
                            {
                                if(CleanupUI()) return false;
                                if(!DebugConf)
                                {
                                    Callback.Fire(addon, true, 0, i, adjustedAmount, Callback.ZeroAtkValue, canPurchase, itemInfo.OpenCurrencyExchange, Callback.ZeroAtkValue, Callback.ZeroAtkValue, Callback.ZeroAtkValue);
                                }
                                else
                                {
                                    DuoLog.Information($"Purchasing {i}'th item {itemInfo.Name}");
                                }
                                ContinuePurchase(meta, amount, currentSealsCount, item);
                                return true;
                            }
                        }
                        return false;
                    }
                }
                if(Utils.GenericThrottle)
                {
                    if(CleanupUI()) return false;
                    SelectGCExchangeHorizontalTab(meta.Category.GetTab());
                }
            }
        }
        return false;
    }

    public static void ContinuePurchase(this GCExchangeListingMetadata listing, uint itemCount, uint sealsCount, GCExchangeItem exchangeItem)
    {
        TaskManagerConfiguration conf = new(abortOnTimeout: false, timeLimitMS: 5000);
        List<TaskManagerTask> tasks = [];
        if(listing.ItemID == VentureItem)
        {
            tasks.Add(new(() => SetVenturesExchangeAmount((int)itemCount), conf));
            tasks.Add(new(SelectExchange, conf));
        }
        tasks.Add(new(ConfirmExchange, conf));
        tasks.Add(new(() => AutoGCHandin.GetSeals() < sealsCount, conf));
        tasks.Add(new(() =>
        {
            var newSeals = AutoGCHandin.GetSeals();
            var spent = sealsCount > newSeals ? sealsCount - newSeals : 0u;
            var purchased = listing.Seals > 0 ? spent / listing.Seals : 0u;
            exchangeItem.QuantitySingleTime = (int)Math.Max(0, exchangeItem.QuantitySingleTime - purchased);
        }, conf));
        tasks.Add(new FrameDelayTask(4));
        tasks.Add(new(BeginNewPurchase));
        P.TaskManager.InsertMulti([.. tasks]);
    }

    public static void BeginNewPurchase()
    {
        var next = GetNextPurchaseListing();
        if(next != null)
        {
            P.TaskManager.Insert(next.PurchaseItem);
        }
        else
        {
            P.TaskManager.Insert(CloseExchange);
        }
    }

    public static GCExchangeItem GetNextPurchaseListing()
    {
        List<GCExchangeItem> items = [.. Utils.GetGCExchangePlanWithOverrides().Items, new(VentureItem, 65000)];
        foreach(var l in items)
        {
            var amt = l.GetAmountThatCanBePurchased();
            if(amt > 0)
            {
                return l;
            }
            else if(l.GetAmountThatCanBePurchased(true) > 0)
            {
                return null;
            }
        }
        return null;
    }

    /// <summary>
    /// 「這扇窗已經按過取消」的記號,兩扇窗各記各的。
    /// </summary>
    /// <remarks>
    /// 🔴 <see cref="Addon"/> 存的是 <see cref="AtkUnitBase"/> 的位址,但<b>只拿來做等值比較,永遠不解參</b>。
    /// 跨幀持有原生指標再解參是崩潰級的錯誤;這裡要的只是「下次看到的是不是同一扇窗」這個身分判斷。
    /// </remarks>
    private struct CancelGuard
    {
        public nint Addon;
        public long Frame;
    }

    private static CancelGuard SelectYesnoCancelGuard;
    private static CancelGuard ShopExchangeDialogCancelGuard;

    /// <summary>
    /// 已經按過取消、那扇窗卻還沒消失時,最多再等這麼多幀才允許補按一次。
    /// </summary>
    /// <remarks>
    /// 🔑 這不是節流 —— 真正的防護是「同一扇窗只按一次」,這個值只是防死鎖的逃生口:
    /// 永久封鎖會讓呼叫端的任務一路卡到逾時,而 NeoTaskManager 預設的逾時是清掉整條佇列。
    /// 取 60 幀(約 0.5~1 秒)是為了遠遠大於「關閉中的那幾幀」,補按永遠不會落在危險窗口內。
    /// </remarks>
    private const int ReCancelEscapeFrames = 60;

    /// <remarks>
    /// 🔴 SelectYesno 被按下之後有「正在關閉中」的幾幀:<c>GetAddonByName</c> 仍然拿得到實例,
    /// <c>IsVisible</c> 與 <c>UldManager.LoadedState == Loaded</c> 也都還成立(=<c>IsReady()</c> 三關全過),
    /// 此時再送一次 callback 會踩到原生 AccessViolation(C0000005)。AVE 在 .NET Core 是
    /// corrupted-state exception,<c>try</c>/<c>catch</c> 與任何例外隔離都攔不住 ——
    /// 唯一的防護是「不要送第二次」,不是「送了再接住」。
    ///
    /// 呼叫端的 <see cref="Utils.GenericThrottle"/> <b>不是</b>這個防護:它是全外掛共用一把 key 的幀節流,
    /// 記的是「上一次任何地方動作是哪一幀」,不是「這扇窗已經按過」。而且它的幀數是
    /// <c>10 + C.ExtraFrameDelay</c>,設定裡 <c>ExtraFrameDelay</c> 的合法範圍是 <c>-10..100</c> ——
    /// 設成 -10 時延遲為 0 幀,節流<b>每一幀都放行</b>,正在關閉的那扇窗會被連續幀重按。
    /// </remarks>
    public static bool CleanupUI()
    {
        if(TryCancelDialogOnce("SelectYesno", ref SelectYesnoCancelGuard)) return true;
        if(TryCancelDialogOnce("ShopExchangeCurrencyDialog", ref ShopExchangeDialogCancelGuard)) return true;
        return false;
    }

    /// <returns>
    /// <see langword="true"/> 代表「這一輪呼叫端不要再往下走」—— 涵蓋「剛按了取消」與
    /// 「按過了、窗還在關閉中」兩種情形,兩者對呼叫端的意義相同(畫面上還有擋路的窗)。
    /// </returns>
    private static bool TryCancelDialogOnce(string addonName, ref CancelGuard guard)
    {
        if(!TryGetAddonByName<AtkUnitBase>(addonName, out var addon) || addon == null)
        {
            // 窗真的從 addon 清單消失了 —— 這是唯一能確定「上一次按下的那扇已經收乾淨」的證據。
            // 只有在這裡解除封鎖,下一扇同名窗才會被當成新的窗來處理。
            guard = default;
            return false;
        }
        var current = (nint)addon;
        var frame = (long)Svc.PluginInterface.UiBuilder.FrameCount;
        if(guard.Addon == current)
        {
            // 這一扇已經按過取消。窗還在 = 可能正在關閉中,此時再 FireCallback 就是上面說的 AVE。
            if(frame - guard.Frame < ReCancelEscapeFrames) return true;
            // 逃生口:等了遠超過關閉所需的時間,窗仍在。視為那次取消沒生效(或這是另一扇重用了
            // 同一塊記憶體的新窗),放行補按一次。
            PluginLog.Information($"{addonName} 按下取消後 {frame - guard.Frame} 幀仍未關閉,補按一次");
            guard = default;
        }
        if(!addon->IsReady()) return false;
        guard = new() { Addon = current, Frame = frame };
        Callback.Fire(addon, true, -1);
        return true;
    }
}
