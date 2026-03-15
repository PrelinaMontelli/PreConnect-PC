using System;
using System.Threading;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using WinRT;
using Windows.Globalization;

namespace PreConnect;

public static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ComWrappersSupport.InitializeComWrappers();
        StartupDiagnostics.InstallGlobalExceptionHooks();
        TryConfigureLanguageOverride();
        StartupDiagnostics.WriteStartupReport("Program.Main");

        Application.Start(_ =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            new App();
        });
    }

    private static void TryConfigureLanguageOverride()
    {
        try
        {
            ApplicationLanguages.PrimaryLanguageOverride = "zh-CN";
        }
        catch (Exception ex)
        {
            StartupDiagnostics.WriteException(
                "Program.TryConfigureLanguageOverride",
                ex,
                "LanguageOverride=zh-CN");
        }
    }
}