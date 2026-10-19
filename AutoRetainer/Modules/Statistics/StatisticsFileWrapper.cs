using AutoRetainer.Services;
using ECommons.Configuration;

namespace AutoRetainer.Modules.Statistics;

internal class StatisticsFileWrapper
{
    internal ulong CID;
    internal string RetainerName;
    internal string FileName => $"{CID:X16}_{RetainerName}.statistic.json";
    internal StatisticsFile File;

    internal StatisticsFileWrapper(ulong CID, string RetainerName)
    {
        this.CID = CID;
        this.RetainerName = RetainerName;
        File = EzConfig.LoadConfiguration<StatisticsFile>(FileName);
        if(CID == Svc.PlayerState.ContentId)
        {
            File.PlayerName = Svc.Objects.LocalPlayer.Name.ToString() + "@" + Svc.Objects.LocalPlayer.HomeWorld.ValueNullable?.Name.ToString();
        }
        File.RetainerName = RetainerName;
    }

    internal void Add(StatisticsRecord record)
    {
        File.Records.Add(record);
        Save();
    }

    internal void Save()
    {
        File.SaveConfiguration(FileName);
    }
}
