using Dalamud.Game;
using Lumina.Excel.Sheets;

namespace AutoRetainer.Modules.Voyage.VoyageCalculator;

public static class SubmarineSheetUtils
{
    public static Vector3 Position(this SubmarineExploration Row)
    {
        return new(Row.X, Row.Y, Row.Z);
    }

    public static uint GetSurveyTime(this SubmarineExploration Row, float speed)
    {
        if(speed < 1)
            speed = 1;
        return (uint)Math.Floor(Row.SurveyDurationmin * 7000 / (speed * 100) * 60);
    }

    public static uint GetVoyageTime(this SubmarineExploration Row, SubmarineExploration other, float speed)
    {
        if(speed < 1)
            speed = 1;
        return (uint)Math.Floor(Vector3.Distance(Row.Position(), other.Position()) * 3990 / (speed * 100) * 60);
    }

    public static uint GetDistance(this SubmarineExploration Row, SubmarineExploration other)
    {
        return (uint)Math.Floor(Vector3.Distance(Row.Position(), other.Position()) * 0.035);
    }

    public static string ConvertDestination(this SubmarineExploration Row)
    {
        return Utils.UpperCaseStr(Row.Destination);
    }

    public static string FancyDestination(this SubmarineExploration Row)
    {
        // Location 欄是扇區代號字母(A/B/.../AC),各語言版本一致;台服實測 exd-tc/7.20/
        // SubmarineExploration.csv 全部 160 列,非空的 Location 全數符合 [A-Z]{1,2}。
        // 這裡原本會對取表指定日文語言,但本艦隊的 Lumina
        // fork 在 ExcelModule.GetRawSheetCore() 開頭無條件執行 language = Language,語言參數是
        // 死參數(對所有客戶端皆然)——留著只會讓讀碼的人誤以為真的取到了日文表。移除後行為等價。
        return $"[{Svc.Data.GetExcelSheet<SubmarineExploration>().GetRow(Row.RowId).Location}] " + Utils.UpperCaseStr(Row.Destination);
    }
}
