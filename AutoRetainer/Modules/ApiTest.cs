using ECommons.Automation.LegacyTaskManager;
using ECommons.WindowsFormsReflector;

namespace AutoRetainer.Modules;

internal static class ApiTest
{
    internal static bool Enabled = false;
    internal static TaskManager TaskManager;

    internal static void Init()
    {
        P.API.OnRetainerPostprocessStep += API_OnRetainerPostprocessTask;
        P.API.OnRetainerReadyToPostprocess += API_OnRetainerReadyToPostprocess;
    }

    private static void API_OnRetainerPostprocessTask(string retainerName)
    {
        if(!Enabled) return;
        PluginLog.Information($"Now requesting postprocess for {retainerName}");
        P.API.RequestRetainerPostprocess();
    }

    private static void API_OnRetainerReadyToPostprocess(string retainerName)
    {
        PluginLog.Information($"Now postprocessing {retainerName}");
        // 用到才建：建構即訂閱 Framework.Update，只開過除錯勾選框的人才需要它。
        TaskManager ??= new();
        TaskManager.Enqueue(() =>
        {
            if(GenericHelpers.IsKeyPressed(Keys.Back))
            {
                return true;
            }
            return false;
        }, int.MaxValue);
        TaskManager.Enqueue(P.API.FinishRetainerPostProcess);
    }
}
