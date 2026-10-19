using AutoRetainerAPI.Configuration;
using Dalamud.Interface.Components;
using ECommons.ExcelServices;
using System.Globalization;

namespace AutoRetainer.UI.Statistics;

public sealed class GilDisplayManager
{
    private GilDisplayManager() { }

    // 統計分頁開著時每幀都畫；輸入（顯示設定、篩選字、角色／僱員／部隊資料）沒變就沿用上一次排好的各行，變了才重排。
    // 只在框架執行緒（Draw）讀寫。翻譯只在載入時讀一次，不列入輸入。
    private readonly List<Line> Lines = [];
    private readonly InputKey Inputs = new();

    public void Draw()
    {
        ImGuiEx.SetNextItemWidthScaled(200f);
        ImGui.InputInt(Loc.T("Ignore characters/retainers with gil less than"), ref C.MinGilDisplay.ValidateRange(0, int.MaxValue));
        ImGuiComponents.HelpMarker(Loc.T("Ignored retainer gil still contributes to character/DC total. Character is ignored if their gil AND all retainers' gil is less than this value. Ignored characters do not contribute to DC total."));
        // 不給 key 的多載每幀都用 StackTrace 算 key（整條呼叫堆疊都要配置）；這裡只有一處，給固定 key。
        ref var filter = ref Ref<string>.Get("GilDisplayManager.Filter");
        ImGui.Checkbox(Loc.T("Only display character total"), ref C.GilOnlyChars);
        ImGui.SameLine();
        ImGuiEx.SetNextItemFullWidth();
        ImGui.InputTextWithHint("##fltr", Loc.T("Filter..."), ref filter, 50);
        Inputs.Begin(record: false);
        FeedInputs(filter);
        if(!Inputs.End())
        {
            BuildLines(filter);
            // BuildLines 會把找不到持有者的部隊寫回 HolderChara，所以排完才記下輸入。
            Inputs.Begin(record: true);
            FeedInputs(filter);
            Inputs.End();
        }
        for(var i = 0; i < Lines.Count; i++)
        {
            var line = Lines[i];
            if(line.Kind == LineKind.Separator)
            {
                ImGui.Separator();
            }
            else if(line.Color is { } color)
            {
                ImGuiEx.Text(color, line.Text);
            }
            else
            {
                ImGuiEx.Text(line.Text);
            }
            if(line.Kind == LineKind.CharacterTotal && ImGuiEx.HoveredAndClicked(Loc.T("Click to relog")))
            {
                if(!MultiMode.Relog(line.Chara, out var error, Internal.RelogReason.Command))
                {
                    Notify.Error(error);
                }
            }
        }
    }

    private void BuildLines(string filter)
    {
        Lines.Clear();
        Dictionary<ExcelWorldHelper.Region, List<OfflineCharacterData>> data = [];
        foreach(var x in C.OfflineData)
        {
            if(ExcelWorldHelper.TryGet(x.World, out var world))
            {
                // 台服(陸行鳥 DC=151)的 WorldDCGroupType.Region 是 8,ExcelWorldHelper.Region
                // 列舉沒有這個具名值,但轉型本身沒問題——只是顯示時要用 GetRegionDisplayName()
                // 而不是直接 ToString(),否則裸數字「8」會取代地區名稱。
                var region = world.GetRegion();
                if(!data.ContainsKey(region))
                {
                    data[region] = [];
                }
                data[region].Add(x);
            }
        }
        var globalTotal = 0L;
        foreach(var x in data)
        {
            AddText(null, $"{x.Key.GetRegionDisplayName()}:");
            var dcTotal = 0L;
            foreach(var c in x.Value)
            {
                if(c.NoGilTrack) continue;
                if(filter != "" && !$"{c.Name}@{c.World}".Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
                FCData fcdata = null;
                var charTotal = c.Gil + SumRetainerGil(c);
                foreach(var fc in C.FCData)
                {
                    if(S.FCData.GetHolderChara(fc.Key, fc.Value) == c && fc.Value.GilCountsTowardsChara)
                    {
                        fcdata = fc.Value;
                        charTotal += fcdata.Gil;
                        break;
                    }
                }
                if(charTotal > C.MinGilDisplay)
                {
                    if(!C.GilOnlyChars)
                    {
                        AddText(null, $"    {Censor.Character(c.Name, c.World)}: {c.Gil:N0}");
                        foreach(var r in c.RetainerData)
                        {
                            if(r.Gil > C.MinGilDisplay)
                            {
                                AddText(null, $"        {Censor.Retainer(r.Name)}: {r.Gil:N0}");
                            }
                        }
                        if(fcdata != null && fcdata.Gil > 0)
                        {
                            AddText(ImGuiColors.DalamudYellow, $"        {Loc.T("Free Company ")}{fcdata.Name}: {fcdata.Gil:N0}");
                        }
                    }
                    Lines.Add(new(LineKind.CharacterTotal, ImGuiColors.DalamudViolet, $"    {Censor.Character(c.Name, c.World)}{(fcdata != null && fcdata.Gil > 0 ? "+FC" : "")} total: {charTotal:N0}", c));
                    dcTotal += charTotal;
                    AddSeparator();
                }
            }
            AddText(ImGuiColors.DalamudOrange, $"{Loc.T("Data center total (")}{x.Key.GetRegionDisplayName()}): {dcTotal:N0}");
            globalTotal += dcTotal;
            AddSeparator();
            AddSeparator();
        }
        AddText(ImGuiColors.DalamudOrange, $"{Loc.T("Overall total: ")}{globalTotal:N0}");
    }

    private void AddText(Vector4? color, string text) => Lines.Add(new(LineKind.Text, color, text, null));

    private void AddSeparator() => Lines.Add(new(LineKind.Separator, null, null, null));

    // 畫面上每一行用到的值，依固定順序餵進 Inputs；順序或內容任何一處不同就重排。
    private void FeedInputs(string filter)
    {
        Inputs.Num(C.MinGilDisplay);
        Inputs.Num(C.GilOnlyChars ? 1 : 0);
        Inputs.Num(C.NoNames ? 1 : 0);
        Inputs.Str(C.CensorSeed);
        Inputs.Str(filter);
        Inputs.Obj(CultureInfo.CurrentCulture);
        var chars = C.OfflineData;
        Inputs.Num(chars.Count);
        foreach(var c in chars)
        {
            Inputs.Num((long)c.CID);
            Inputs.Num((long)c.FCID);
            Inputs.Num(c.Gil);
            Inputs.Num(c.NoGilTrack ? 1 : 0);
            Inputs.Str(c.Name);
            Inputs.Str(c.World);
            var retainers = c.RetainerData;
            Inputs.Num(retainers.Count);
            foreach(var r in retainers)
            {
                Inputs.Num(r.Gil);
                Inputs.Str(r.Name);
            }
        }
        Inputs.Num(C.FCData.Count);
        foreach(var fc in C.FCData)
        {
            Inputs.Num((long)fc.Key);
            Inputs.Num((long)fc.Value.HolderChara);
            Inputs.Num(fc.Value.Gil);
            Inputs.Num(fc.Value.GilCountsTowardsChara ? 1 : 0);
            Inputs.Str(fc.Value.Name);
        }
    }

    // 等同 RetainerData.Sum(s => s.Gil)；Sum 走 IEnumerable 會裝箱列舉器，這裡每幀每個角色一次。
    private static long SumRetainerGil(OfflineCharacterData c)
    {
        var sum = 0L;
        foreach(var r in c.RetainerData) sum += r.Gil;
        return sum;
    }

    private enum LineKind : byte
    {
        Text,
        Separator,
        CharacterTotal,
    }

    private readonly record struct Line(LineKind Kind, Vector4? Color, string Text, OfflineCharacterData Chara);

    // 記錄模式把餵進來的值依序存下；比對模式逐一比上一次記下的值。比對模式不配置。
    private sealed class InputKey
    {
        private long[] Nums = new long[256];
        private string[] Strs = new string[64];
        private object[] Objs = new object[4];
        private int NumCount, StrCount, ObjCount;
        private int NumPos, StrPos, ObjPos;
        private bool Recording, Recorded, Differs;

        public void Begin(bool record)
        {
            Recording = record;
            Differs = false;
            NumPos = StrPos = ObjPos = 0;
        }

        public void Num(long value)
        {
            if(Recording)
            {
                if(NumPos == Nums.Length) Array.Resize(ref Nums, Nums.Length * 2);
                Nums[NumPos++] = value;
            }
            else if(!Differs)
            {
                if(NumPos < NumCount && Nums[NumPos] == value) NumPos++;
                else Differs = true;
            }
        }

        public void Str(string value)
        {
            if(Recording)
            {
                if(StrPos == Strs.Length) Array.Resize(ref Strs, Strs.Length * 2);
                Strs[StrPos++] = value;
            }
            else if(!Differs)
            {
                if(StrPos < StrCount && string.Equals(Strs[StrPos], value, StringComparison.Ordinal)) StrPos++;
                else Differs = true;
            }
        }

        public void Obj(object value)
        {
            if(Recording)
            {
                if(ObjPos == Objs.Length) Array.Resize(ref Objs, Objs.Length * 2);
                Objs[ObjPos++] = value;
            }
            else if(!Differs)
            {
                if(ObjPos < ObjCount && ReferenceEquals(Objs[ObjPos], value)) ObjPos++;
                else Differs = true;
            }
        }

        /// <summary>記錄模式回 true；比對模式在曾經記錄過、且這次餵的值與上次逐一相同時回 true。</summary>
        public bool End()
        {
            if(Recording)
            {
                Array.Clear(Strs, StrPos, StrCount > StrPos ? StrCount - StrPos : 0);
                Array.Clear(Objs, ObjPos, ObjCount > ObjPos ? ObjCount - ObjPos : 0);
                NumCount = NumPos;
                StrCount = StrPos;
                ObjCount = ObjPos;
                Recorded = true;
                return true;
            }
            return Recorded && !Differs && NumPos == NumCount && StrPos == StrCount && ObjPos == ObjCount;
        }
    }
}
