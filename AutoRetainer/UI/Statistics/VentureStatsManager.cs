using AutoRetainer.Modules.Statistics;
using ECommons.Configuration;
using Lumina.Excel.Sheets;
using System.IO;

namespace AutoRetainer.UI.Statistics;

public sealed class VentureStatsManager
{
    private VentureStatsManager() { }

    internal Dictionary<string, Dictionary<string, Dictionary<uint, StatisticsData>>> Data = [];
    internal Dictionary<string, uint> CharTotal = [];
    internal Dictionary<string, uint> RetTotal = [];
    internal Dictionary<(string Char, string Ret), HashSet<long>> VentureTimestamps = [];
    private string Filter = "";

    internal void DrawVentures()
    {
        if(Data.Count == 0)
        {
            Load();
        }
        if(ImGui.Button(Loc.T("Reload")))
        {
            Load();
        }
        ImGui.SameLine();
        ImGui.Checkbox(Loc.T("Show HQ and non-HQ together"), ref C.StatsUnifyHQ);
        ImGui.SameLine();
        ImGuiEx.SetNextItemFullWidth();
        ImGui.InputTextWithHint("##search", Loc.T("Filter items..."), ref Filter, 100);
        var cindex = 0;
        foreach(var cData in Data)
        {
            var rindex = 0;
            var display = false;
            if(CharTotal[cData.Key] != 0)
            {
                if(ImGui.CollapsingHeader($"{Censor.Character(cData.Key)}{Loc.T(" | Total Ventures: ")}{CharTotal.GetSafe(cData.Key)}###chara{cData.Key}"))
                {
                    display = true;
                }
            }
            CharTotal[cData.Key] = 0;
            foreach(var x in cData.Value)
            {
                var array = x.Value.Where(c => Filter == string.Empty || $"{Svc.Data.GetExcelSheet<Item>().GetRow(c.Key).Name}".Contains(Filter, StringComparison.OrdinalIgnoreCase));
                var num = (uint)GetVentureCount(cData.Key, x.Key);
                CharTotal[cData.Key] += num;
                if(display && num != 0)
                {
                    ImGui.Dummy(new(10, 1));
                    ImGui.SameLine();
                    if(ImGui.CollapsingHeader($"{Censor.Retainer(x.Key)}{Loc.T(" | Ventures: ")}{num}###{cData.Key}ret{x.Key}"))
                    {
                        foreach(var c in array)
                        {
                            var iName = $"{Svc.Data.GetExcelSheet<Item>().GetRow(c.Key).Name}";
                            ImGuiEx.Text($"             {iName}: {(C.StatsUnifyHQ ? c.Value.Amount + c.Value.AmountHQ : $"{c.Value.Amount}/{c.Value.AmountHQ}")}");
                        }
                    }
                }
            }
        }
    }

    private bool _loading;

    internal void Load()
    {
        // Called from DrawVentures() on the UI thread (first tab open, or the Reload button),
        // and directory-scans + deserializes every *.statistic.json in the config folder, which
        // grows with how long/heavily venture-stats tracking has been used. Build the result in
        // the background using local collections, then publish onto the framework thread so
        // DrawVentures() (which reads these fields every frame without any locking) never sees a
        // partially-updated state.
        if(_loading)
            return;
        _loading = true;

        Task.Run(() =>
        {
            var data = new Dictionary<string, Dictionary<string, Dictionary<uint, StatisticsData>>>();
            var ventureTimestamps = new Dictionary<(string Char, string Ret), HashSet<long>>();
            var charTotal = new Dictionary<string, uint>();
            var retTotal = new Dictionary<string, uint>();

            try
            {
                foreach(var x in Directory.GetFiles(Svc.PluginInterface.GetPluginConfigDirectory()))
                {
                    if(x.EndsWith(".statistic.json"))
                    {
                        var file = EzConfig.LoadConfiguration<StatisticsFile>(x);
                        foreach(var z in file.Records)
                        {
                            AddData(data, ventureTimestamps, file.PlayerName, file.RetainerName, z.ItemId, z.IsHQ, z.Amount, z.Timestamp);
                        }
                    }
                }
                foreach(var x in data)
                {
                    uint ctotal = 0;
                    foreach(var z in x.Value)
                    {
                        uint cnt = 0;
                        foreach(var c in z.Value.Values)
                        {
                            cnt += c.Amount + c.AmountHQ;
                        }
                        retTotal[z.Key] = cnt;
                        ctotal += cnt;
                    }
                    charTotal[x.Key] = ctotal;
                }
            }
            catch(Exception e)
            {
                e.Log();
                Notify.Error($"Error: {e.Message}");
            }

            Svc.Framework.RunOnFrameworkThread(() =>
            {
                Data = data;
                VentureTimestamps = ventureTimestamps;
                CharTotal = charTotal;
                RetTotal = retTotal;
                _loading = false;
            });
        });
    }

    private int GetVentureCount(string character)
    {
        var ret = 0;
        foreach(var x in VentureTimestamps)
        {
            if(x.Key.Char == character)
            {
                ret += x.Value.Count;
            }
        }
        return ret;
    }

    private int GetVentureCount(string character, string retainer)
    {
        if(VentureTimestamps.TryGetValue((character, retainer), out var h))
        {
            return h.Count;
        }
        return 0;
    }

    private static void AddData(Dictionary<string, Dictionary<string, Dictionary<uint, StatisticsData>>> data,
        Dictionary<(string Char, string Ret), HashSet<long>> ventureTimestamps,
        string character, string retainer, uint item, bool hq, uint amount, long timestamp)
    {
        if(!data.TryGetValue(character, out var cData))
        {
            cData = [];
            data.Add(character, cData);
        }
        if(!cData.TryGetValue(retainer, out var rData))
        {
            rData = [];
            cData.Add(retainer, rData);
        }
        if(!rData.TryGetValue(item, out var iData))
        {
            iData = new();
            rData.Add(item, iData);
        }
        if(!ventureTimestamps.ContainsKey((character, retainer)))
        {
            ventureTimestamps[(character, retainer)] = [];
        }
        ventureTimestamps[(character, retainer)].Add(timestamp);
        if(hq)
        {
            iData.AmountHQ += amount;
        }
        else
        {
            iData.Amount += amount;
        }
    }
}
