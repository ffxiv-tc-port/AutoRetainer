using AutoRetainer.Internal;
using AutoRetainer.Scheduler.Tasks;
using AutoRetainerAPI.Configuration;
using Dalamud.Memory;
using Dalamud.Utility;
using ECommons.ExcelServices;
using Lumina.Excel.Sheets;

namespace AutoRetainer.Helpers;

internal static unsafe class VentureUtils
{
    internal const uint QuickExplorationID = 395;

    private static bool IsNullOrEmpty(this string s)
    {
        return GenericHelpers.IsNullOrEmpty(s);
    }

    internal static void BuildUnwrappedList(AdditionalRetainerData adata, OfflineCharacterData data, OfflineRetainerData ret)
    {
        try
        {
            if(adata.VenturePlan.ListUnwrapped.Count > 500)
            {
                ImGuiEx.Text($"The venture list is too large to show preview.");
                ImGuiEx.Text($"Progress: {adata.VenturePlanIndex}/{adata.VenturePlan.ListUnwrapped.Count}");
                return;
            }
            List<(Vector4? col, string str)> strings = [];
            var focus = 0;
            for(var j = 0; j < adata.VenturePlan.ListUnwrapped.Count; j++)
            {
                var v = adata.VenturePlan.ListUnwrapped[j];
                if(j == adata.VenturePlanIndex - 1)
                {
                    focus = j;
                    strings.Add((ImGuiColors.ParsedGreen, $"{VentureUtils.GetFancyVentureName(v, data, ret, out _)}"));
                }
                else if(j == adata.VenturePlanIndex || (j == 0 && adata.VenturePlan.PlanCompleteBehavior == PlanCompleteBehavior.Restart_plan && adata.VenturePlanIndex >= adata.VenturePlan.ListUnwrapped.Count))
                {
                    strings.Add((ImGuiColors.DalamudYellow, $"{VentureUtils.GetFancyVentureName(v, data, ret, out _)}"));
                }
                else
                {
                    strings.Add((null, $"{VentureUtils.GetFancyVentureName(v, data, ret, out _)}"));
                }
            }
            var min = Math.Max(focus - 8, 0);
            var max = Math.Min(focus + 10, strings.Count);
            if(min != 0) ImGuiEx.Text($"... {min} more ...");
            for(var i = min; i < max; i++)
            {
                var s = strings[i];
                ImGuiEx.Text(s.col, s.str);
            }
            if(max != strings.Count) ImGuiEx.Text($"... {strings.Count - max} more ...");
        }
        catch(Exception e)
        {
            PluginLog.Error($"{e}");
        }
    }

    internal static void ProcessVenturePlanner(this GameRetainerManager.Retainer ret, uint next)
    {
        if(next != 0)
        {
            var adj = VentureUtils.GetAdjustedRetainerTask(next, (Job)ret.ClassJob);
            if(adj != next)
            {
                PluginLog.Debug($"Adjusted venture ID {next}->{adj}");
                next = adj;
            }
        }
        // next 的兩個來源都不可信：①委託計畫存在設定檔裡(可跨版本殘留、可從剪貼簿貼入)
        // ②IPC 的 SetVenture 讓任何外掛塞進任意 uint。底下四處 GetVentureById(next) 都是裸
        // GetRow，查無此列時 Lumina 擲 ArgumentOutOfRangeException，而本函式位在
        // Svc.Framework.Update -> SchedulerMain.Tick 的每幀路徑上 => 會每幀洗版並把排程打斷。
        // 在這裡一次擋掉，比在四個呼叫點各補一次可靠。
        if(!Svc.Data.GetExcelSheet<RetainerTask>().TryGetRow(next, out var nextTask))
        {
            PluginLog.Information($"[AutoRetainer] 委託 ID {next} 不存在於本地 RetainerTask 資料表，略過本次委託指派。請檢查委託計畫是否來自其他服務版本。");
            return;
        }
        DebugLog($"Not completed or restarting");
        if(ret.VentureID != 0)
        {
            DebugLog($"Venture id is not zero, next={next}, ventureID={ret.VentureID}");
            if(next == ret.VentureID)
            {
                DebugLog($"Reassigning");
                TaskReassignVenture.Enqueue();
            }
            else
            {
                DebugLog($"Collecting");
                TaskCollectVenture.Enqueue();
                if(nextTask.IsFieldExploration())
                {
                    DebugLog($"Assigning field exploration: {next}");
                    TaskAssignFieldExploration.Enqueue(next);
                }
                else if(nextTask.IsQuickExploration())
                {
                    DebugLog($"Assigning quick: {next}");
                    TaskAssignQuickVenture.Enqueue();
                }
                else
                {
                    DebugLog($"Assigning hunt: {next}");
                    TaskAssignHuntingVenture.Enqueue(next);
                }
            }
        }
        else
        {
            DebugLog($"Venture not assigned");
            if(nextTask.IsFieldExploration())
            {
                DebugLog($"Assigning field exploration: {next}");
                TaskAssignFieldExploration.Enqueue(next);
            }
            else if(nextTask.IsQuickExploration())
            {
                DebugLog($"Assigning quick: {next}");
                TaskAssignQuickVenture.Enqueue();
            }
            else
            {
                DebugLog($"Assigning hunt: {next}");
                TaskAssignHuntingVenture.Enqueue(next);
            }
        }
    }

    internal static int GetVentureItemAmount(uint Task, OfflineCharacterData data, OfflineRetainerData retainer, out int index)
    {
        var task = GetVentureById(Task);
        if(task == null)
        {
            // 委託 ID 不存在於本地資料表：回 0 而不是讓 Lumina 擲例外。
            index = 0;
            return 0;
        }
        return task.Value.GetVentureItemAmount(data, retainer, out index);
    }

    internal static int GetVentureItemAmount(this RetainerTask task, OfflineCharacterData data, OfflineRetainerData retainer, out int index)
    {
        index = 0;
        if(task.IsRandom)
        {
            return 0;
        }
        var adata = Utils.GetAdditionalData(data.CID, retainer.Name);

        var param = task.RetainerTaskParameter.ValueNullable;
        if(param == null) return 0;
        var normal = Svc.Data.GetExcelSheet<RetainerTaskNormal>().GetRowOrDefault(task.Task.RowId);
        if(task.Task.RowId == 0 || normal == null) return 0;
        if(retainer.Job == (uint)Job.FSH)
        {
            for(var i = 0; i < param?.PerceptionFSH.Count; i++)
            {
                if(adata.Perception >= param?.PerceptionFSH[i])
                {
                    index = i + 1;
                }
            }
        }
        else if(IsDoL(retainer.Job))
        {
            for(var i = 0; i < param?.PerceptionDoL.Count; i++)
            {
                if(adata.Perception >= param?.PerceptionDoL[i])
                {
                    index = i + 1;
                }
            }
        }
        else
        {
            for(var i = 0; i < param?.ItemLevelDoW.Count; i++)
            {
                if(adata.Ilvl >= param?.ItemLevelDoW[i])
                {
                    index = i + 1;
                }
            }
        }
        if(index >= normal?.Quantity.Count) return 0;
        return normal?.Quantity[index] ?? 0;
    }

    internal static int GetVentureRequitement(this RetainerTask task)
    {
        if(IsDoL(task.ClassJobCategory.RowId))
        {
            return task.RequiredGathering;
        }
        else
        {
            return task.RequiredItemLevel;
        }
    }

    internal static (int[] Stat, int[] Amount) GetVentureAmounts(this RetainerTask task, OfflineRetainerData retainer)
    {
        var param = task.RetainerTaskParameter.Value;
        var normal = Svc.Data.GetExcelSheet<RetainerTaskNormal>().GetRowOrDefault(task.Task.RowId);
        List<int> stat =
        [
            0
        ];
        List<int> amount =
        [
            normal?.Quantity[0] ?? 0
        ];
        if(retainer.Job == (uint)Job.FSH)
        {
            for(var i = 0; i < param.PerceptionFSH.Count; i++)
            {
                amount.Add(normal?.Quantity[i + 1] ?? 0);
                stat.Add(param.PerceptionFSH[i]);
            }
        }
        else if(IsDoL(retainer.Job))
        {
            for(var i = 0; i < param.PerceptionDoL.Count; i++)
            {
                amount.Add(normal?.Quantity[i + 1] ?? 0);
                stat.Add(param.PerceptionDoL[i]);
            }
        }
        else
        {
            for(var i = 0; i < param.ItemLevelDoW.Count; i++)
            {
                amount.Add(normal?.Quantity[i + 1] ?? 0);
                stat.Add(param.ItemLevelDoW[i]);
            }
        }
        return (stat.ToArray(), amount.ToArray());
    }

    internal static string GetFancyVentureName(uint Task, OfflineCharacterData data, OfflineRetainerData retainer, out bool Available)
    {
        var task = GetVentureById(Task);
        if(task == null)
        {
            // 委託計畫存在設定檔裡，可能跨版本殘留或從剪貼簿貼入無效 ID。
            // 這裡位在 Draw 路徑上，擲例外會讓整個外掛視窗消失，改成把不明 ID 直接顯示出來。
            Available = false;
            return $"?{Task}";
        }
        return task.Value.GetFancyVentureName(data, retainer, out Available);
    }

    internal static string GetFancyVentureName(this RetainerTask Task, OfflineCharacterData data, OfflineRetainerData retainer, out bool Available)
    {
        return GetFancyVentureName(Task, data, retainer, out Available, out _, out _);
    }

    private static Dictionary<string, FancyVentureCacheEntry> FancyVentureNameCache = [];
    internal static string GetFancyVentureName(this RetainerTask Task, OfflineCharacterData data, OfflineRetainerData retainer, out bool Available, out string left, out string right)
    {
        var signature = $"{Task.RowId}/{data.Identity}/{retainer.Identity}";
        if(FancyVentureNameCache.TryGetValue(signature, out var cached) && cached.IsValid)
        {
            left = cached.Left;
            right = cached.Right;
            Available = cached.Avail;
            return cached.Entry;
        }
        var r = Task.GetFancyVentureNameParts(data, retainer, out Available);
        left = Available ? "" : Lang.CharDeny + r.UnavailabilitySymbols + " ";
        var lvls = r.Level == 0 ? "" : $"{Lang.CharLevel}{r.Level} ";
        right = r.Yield == 0 ? "" : $"x{r.Yield} {r.YieldStars}";
        left = $"{left}{lvls}{r.Name}";
        var ret = (C.Verbose ? $"#{Task.RowId}->{Task.GetAdjustedRetainerTask((Job)retainer.Job)?.RowId}/{Task.ClassJobCategory.Value.GetShortName()} " : "") + left + " " + right;
        FancyVentureNameCache[signature] = new(ret, Available, left, right);
        return ret;
    }

    internal static string GetShortName(this ClassJobCategory cat)
    {
        if(cat.RowId == GetCategory((uint)Job.BRD)) return "DoW";
        return cat.Name.ToString();
    }

    internal static (string UnavailabilitySymbols, int Level, string Name, int Yield, int YieldRate, string YieldStars) GetFancyVentureNameParts(this RetainerTask Task, OfflineCharacterData data, OfflineRetainerData retainer, out bool Available)
    {
        (string UnavailabilitySymbols, int Level, string Name, int Yield, int YieldRate, string YieldStars) retp = ("", 0, "", 0, 0, "");
        var adata = Utils.GetAdditionalData(data.CID, retainer.Name);
        var UnavailabilitySymbol = "";
        var canNotGather = Task.RequiredGathering > 0 && adata.Gathering < Task.RequiredGathering && adata.Gathering > -1;
        if(!Task.IsFieldExploration() && IsDoL(Task.ClassJobCategory.RowId))
        {
            var gathered = data.UnlockedGatheringItems.Count == 0 || data.UnlockedGatheringItems.Contains(VentureUtils.GetGatheringItemByItemID(Task.GetVentureItemId()));
            if(gathered)
            {
                if(canNotGather)
                {
                    Available = false;
                    UnavailabilitySymbol = Lang.CharPlant;
                }
                else
                {
                    Available = true;
                }
            }
            else
            {
                Available = false;
                if(canNotGather)
                {
                    UnavailabilitySymbol = Lang.CharQuestion + Lang.CharPlant;
                }
                else
                {
                    UnavailabilitySymbol = Lang.CharQuestion;
                }
            }
        }
        else
        {
            //PluginLog.Information($"{Task.GetVentureName()}, {Task.RequiredItemLevel} > {adata.Ilvl}, {Task.RequiredGathering} > {adata.Gathering}");
            if(Task.RequiredItemLevel > 0 && adata.Ilvl > -1)
            {
                Available = Task.RequiredItemLevel <= adata.Ilvl;
                if(!Available) UnavailabilitySymbol = Lang.CharItemLevel;
            }
            else if(Task.RequiredGathering > 0 && adata.Gathering > -1)
            {
                Available = !canNotGather;
                if(!Available) UnavailabilitySymbol = Lang.CharPlant;
            }
            else
            {
                Available = true;
            }
        }
        retp.Name = Task.GetVentureName();
        if(Task.RetainerLevel == 0)
        {
            //
        }
        else
        {
            retp.Level = Task.RetainerLevel;
        }
        if(retainer.Level < Task.RetainerLevel)
        {
            Available = false;
            UnavailabilitySymbol += Lang.CharLevelSync;
        }
        if(!Available)
        {
            retp.UnavailabilitySymbols = UnavailabilitySymbol;

        }
        if(!Task.IsRandom)
        {
            var amount = Task.GetVentureItemAmount(data, retainer, out retp.YieldRate);
            retp.Yield = amount;
            retp.YieldStars = $"{"★".Repeat(retp.YieldRate)}{"☆".Repeat(4 - retp.YieldRate)}";
        }
        return retp;
    }

    internal static uint GetAdjustedRetainerTask(uint task, Job job)
    {
        return GetAdjustedRetainerTask(Svc.Data.GetExcelSheet<RetainerTask>().GetRowOrDefault(task), job)?.RowId ?? 0;
    }

    internal static RetainerTask? GetAdjustedRetainerTask(this RetainerTask task, Job job)
    {
        return GetAdjustedRetainerTask((RetainerTask?)task, job);
    }

    internal static RetainerTask? GetAdjustedRetainerTask(this RetainerTask? task, Job job)
    {
        if(task.GetVentureItemId() == 0) return task;
        var n = Svc.Data.GetExcelSheet<RetainerTask>().Cast<RetainerTask?>().FirstOrDefault(x => x?.GetVentureItemId() == task.GetVentureItemId() && x?.ClassJobCategory.Value.RowId == GetCategory((uint)job));
        return n ?? task;
    }

    internal static int GetCategory(uint ClassJob)
    {
        if(ClassJob == (int)Job.BTN) return 18;
        if(ClassJob == (int)Job.MIN) return 17;
        if(ClassJob == (int)Job.FSH) return 19;
        return 34;
    }

    // 這兩個是 VenturePlanner 的摺疊標題。索引 0~3 以前恆為英文字面值,所以直接切掉最後一個字元
    // 用來去掉英文的句點;現在 0~3 改成讀遊戲表(客戶端語言),無條件切尾會把台服的「）」吃掉。
    // 改成只去掉句點:英文的結果與原本完全相同,其他語言不再被截斷。
    internal static string GetHuntingVentureName(uint ClassJob)
    {
        if(ClassJob == (int)Job.BTN) return Lang.HuntingVentureNames[2].TrimEnd('.');
        if(ClassJob == (int)Job.MIN) return Lang.HuntingVentureNames[1].TrimEnd('.');
        if(ClassJob == (int)Job.FSH) return Lang.HuntingVentureNames[3].TrimEnd('.');
        return Lang.HuntingVentureNames[0].TrimEnd('.');
    }

    internal static string GetFieldExVentureName(uint ClassJob)
    {
        if(ClassJob == (int)Job.BTN) return Lang.FieldExplorationNames[2].TrimEnd('.');
        if(ClassJob == (int)Job.MIN) return Lang.FieldExplorationNames[1].TrimEnd('.');
        if(ClassJob == (int)Job.FSH) return Lang.FieldExplorationNames[3].TrimEnd('.');
        return Lang.FieldExplorationNames[0].TrimEnd('.');
    }

    internal static bool IsDoL(uint ClassJob)
    {
        if(ClassJob == (int)Job.BTN) return true;
        if(ClassJob == (int)Job.MIN) return true;
        if(ClassJob == (int)Job.FSH) return true;
        return false;
    }

    internal static uint GetGatheringItemByItemID(uint itemID)
    {
        return Svc.Data.GetExcelSheet<GatheringItem>().AsNullable().FirstOrDefault(x => x?.Item.RowId == itemID)?.RowId ?? 0;
    }

    /// <summary>
    /// 委託 ID 的來源(設定檔內的委託計畫、剪貼簿貼入、IPC)都不保證存在於本地資料表，
    /// 因此一律走 GetRowOrDefault：查無此列時回 null，由呼叫端決定顯示什麼，
    /// 不要讓 Lumina 在 Draw / 每幀路徑上擲例外。
    /// </summary>
    internal static RetainerTask? GetVentureById(uint id)
    {
        return Svc.Data.GetExcelSheet<RetainerTask>().GetRowOrDefault(id);
    }

    internal static IEnumerable<RetainerTask> GetFieldExplorations(uint ClassJob)
    {
        var cat = GetCategory(ClassJob);
        return Svc.Data.GetExcelSheet<RetainerTask>().Where(x => x.ClassJobCategory.Value.RowId == cat).Where(x => x.MaxTimemin == 1080 && !x.GetVentureName().IsNullOrEmpty()).OrderBy(x => x.RetainerLevel);
    }

    internal static IEnumerable<RetainerTask> GetHunts(uint ClassJob)
    {
        var cat = GetCategory(ClassJob);
        return Svc.Data.GetExcelSheet<RetainerTask>().Where(x => x.ClassJobCategory.Value.RowId == cat).Where(x => x.MaxTimemin == 60 && !x.GetVentureName().IsNullOrEmpty()).OrderBy(x => x.RetainerLevel);
    }

    internal static RetainerTask QuickExploration => Svc.Data.GetExcelSheet<RetainerTask>().GetRow(QuickExplorationID);

    internal static bool IsFieldExploration(this RetainerTask task)
    {
        return task.MaxTimemin == 1080;
    }

    internal static bool IsQuickExploration(this RetainerTask task)
    {
        return task.RowId == QuickExplorationID;
    }

    internal static IEnumerable<RetainerTask> GetAvailableVentures(this IEnumerable<RetainerTask> tasks, OfflineRetainerData data)
    {
        return tasks.Where(x => x.RetainerLevel <= data.Level);
    }

    internal static string GetVentureName(uint id)
    {
        // GetRowOrDefault 而非 GetRow：這個多載的呼叫端包含 IPC.SetVenture(任意 uint)，
        // 裸 GetRow 會在別的外掛呼叫我們的 IPC 時把例外丟回對方。下面的 RetainerTask? 多載
        // 本來就處理 null(回 null),所以這裡改法零漣漪。
        return GetVentureName(Svc.Data.GetExcelSheet<RetainerTask>().GetRowOrDefault(id));
    }

    internal static string GetVentureName(this RetainerTask task)
    {
        return GetVentureName((RetainerTask?)task);
    }

    internal static string GetVentureName(this RetainerTask? task)
    {
        if(task == null) return null;
        if(task.Value.IsRandom)
        {
            return $"{Svc.Data.GetExcelSheet<RetainerTaskRandom>().GetRowOrDefault(task.Value.Task.RowId)?.Name.ToDalamudString().GetText()}";
        }
        else
        {
            return $"{Svc.Data.GetExcelSheet<RetainerTaskNormal>().GetRowOrDefault(task.Value.Task.RowId)?.Item.ValueNullable?.Name.ToDalamudString().GetText()}";
        }
    }

    internal static uint GetVentureItemId(this RetainerTask task)
    {
        return GetVentureItemId((RetainerTask?)task);
    }

    internal static uint GetVentureItemId(this RetainerTask? task)
    {
        return Svc.Data.GetExcelSheet<RetainerTaskNormal>().GetRowOrDefault(task.Value.Task.RowId)?.Item.Value.RowId ?? 0;
    }

    internal static Item? GetVentureItem(this RetainerTask task)
    {
        return GetVentureItem((RetainerTask?)task);
    }

    internal static Item? GetVentureItem(this RetainerTask? task)
    {
        return Svc.Data.GetExcelSheet<RetainerTaskNormal>().GetRowOrDefault(task.Value.Task.RowId)?.Item.Value;
    }

    internal static List<string> GetAvailableVentureNames()
    {
        List<string> ret = [];
        // 7.2 → 7.3 在 CastBarEnemy 處插入了一項，之後每個陣列索引都 +1，所以上游寫死的 97
        // 在台服 7.20（7.3 世代）指到的是一個沒有名字的空位，不是探險清單。後果是靜默的：
        // 取不到名字 → 指定探險永遠比對不到 → 走「Can not find venture id」那條路徑。
        // 🔴 改成引用出貨 CS 的列舉而不是換一個新的魔術數字：下次版本再位移時它會自己跟著動。
        // ⚠️ 這裡一定要完整命名空間 —— 本檔的 RetainerTask 是 Lumina 的表格型別，會撞名。
        // 🔴 這是四層裸鏈：Framework（isPointer:true，可能 null）→ UIModule（裸欄位）
        //    → GetRaptureAtkModule()（可能 null）→ 陣列。任一層 null 就是攔不到的 AVE。
        //    讀不到就回空清單 —— 呼叫端本來就要處理「找不到探險名字」那條路徑。
        var framework = CSFramework.Instance();
        if(framework == null || framework->UIModule == null) return ret;
        var atkModule = framework->UIModule->GetRaptureAtkModule();
        if(atkModule == null) return ret;
        var data = atkModule->AtkModule.GetStringArrayData(
            (int)FFXIVClientStructs.FFXIV.Component.GUI.StringArrayType.RetainerTask);
        if(data != null)
        {
            for(var i = 0; i < data->AtkArrayData.Size; i++)
            {
                if(data->StringArray[i] == null) break;
                if(i % 4 != 1) continue;
                var item = data->StringArray[i];
                if(item != null)
                {
                    var str = MemoryHelper.ReadSeStringNullTerminated((nint)(byte*)item);
                    ret.Add(str.GetText());
                }
            }
        }
        return ret;
    }

    internal static string[] GetVentureLevelCategory(uint id)
    {
        return Svc.Data.GetExcelSheet<RetainerTask>().GetRow(id).GetVentureLevelCategory();
    }

    internal static string[] GetVentureLevelCategory(this RetainerTask Task)
    {
        foreach(var x in Svc.Data.GetExcelSheet<RetainerTaskLvRange>())
        {
            if(Task.RetainerLevel >= x.Min && Task.RetainerLevel <= x.Max)
            {
                return [$" {x.Min}-{x.Max}.", $"  {x.Min}～{x.Max}", $" {x.Min} - {x.Max}", $" {x.Min} à {x.Max}"];
            }
        }
        return null;
    }
}
