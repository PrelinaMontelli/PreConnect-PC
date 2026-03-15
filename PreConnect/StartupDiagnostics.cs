using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;

namespace PreConnect;

internal static class StartupDiagnostics
{
    private const int AppModelErrorNoPackage = 15700;
    private static readonly object SyncRoot = new();
    private static string? _logFilePath;

    public static void InstallGlobalExceptionHooks()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            var exception = args.ExceptionObject as Exception;
            WriteException("AppDomain.CurrentDomain.UnhandledException", exception, $"IsTerminating={args.IsTerminating}");
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            WriteException("TaskScheduler.UnobservedTaskException", args.Exception, "Observed=false");
        };
    }

    public static void WriteStartupReport(string stage)
    {
        var logLines = new List<string>
        {
            "================ Startup Diagnostics ================",
            $"TimestampUtc={DateTimeOffset.UtcNow:O}",
            $"Stage={stage}",
            $"ProcessPath={Environment.ProcessPath ?? string.Empty}",
            $"BaseDirectory={AppContext.BaseDirectory}",
            $"FrameworkDescription={RuntimeInformation.FrameworkDescription}",
            $"RuntimeIdentifier={RuntimeInformation.RuntimeIdentifier}",
            $"ProcessArchitecture={RuntimeInformation.ProcessArchitecture}",
            $"OSArchitecture={RuntimeInformation.OSArchitecture}",
            $"OSDescription={RuntimeInformation.OSDescription}",
            $"Is64BitProcess={Environment.Is64BitProcess}",
            $"IsElevatedAdministrator={IsRunningAsAdministrator()}",
            $"HasPackageIdentity={TryGetPackageFullName(out var packageFullName)}",
            $"PackageFullName={packageFullName}",
            $"ConfiguredWindowsAppSdkSelfContained={IsConfiguredWindowsAppSdkSelfContained()}",
            $"WindowsAppSdkRuntimeDllPresent={File.Exists(Path.Combine(AppContext.BaseDirectory, "Microsoft.WindowsAppRuntime.dll"))}",
            $"WindowsAppSdkBootstrapDllPresent={File.Exists(Path.Combine(AppContext.BaseDirectory, "Microsoft.WindowsAppRuntime.Bootstrap.dll"))}",
            $"WindowsUiDllPresent={File.Exists(Path.Combine(AppContext.BaseDirectory, "Microsoft.UI.Xaml.dll"))}",
            $"WindowsAppSdkRuntimeVersion={TryGetFileVersion(Path.Combine(AppContext.BaseDirectory, "Microsoft.WindowsAppRuntime.dll"))}",
            $"WindowsUiXamlVersion={TryGetFileVersion(Path.Combine(AppContext.BaseDirectory, "Microsoft.UI.Xaml.dll"))}"
        };

        WriteLines(logLines);
    }

    public static void WriteException(string source, Exception? exception, string? extraDetail = null)
    {
        var logLines = new List<string>
        {
            "---------------- Exception ----------------",
            $"TimestampUtc={DateTimeOffset.UtcNow:O}",
            $"Source={source}"
        };

        if (!string.IsNullOrWhiteSpace(extraDetail))
        {
            logLines.Add(extraDetail);
        }

        if (exception is null)
        {
            logLines.Add("Exception=<null>");
        }
        else
        {
            logLines.Add($"ExceptionType={exception.GetType().FullName}");
            logLines.Add($"Message={exception.Message}");
            logLines.Add($"HResult=0x{exception.HResult:X8}");
            logLines.Add("StackTrace=");
            logLines.Add(exception.ToString());
        }

        WriteLines(logLines);
    }

    private static void WriteLines(IEnumerable<string> lines)
    {
        lock (SyncRoot)
        {
            var logFilePath = GetLogFilePath();
            Directory.CreateDirectory(Path.GetDirectoryName(logFilePath)!);
            File.AppendAllLines(logFilePath, lines, Encoding.UTF8);
        }
    }

    private static string GetLogFilePath()
    {
        if (!string.IsNullOrWhiteSpace(_logFilePath))
        {
            return _logFilePath;
        }

        var basePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PreConnect",
            "Logs");

        _logFilePath = Path.Combine(basePath, $"startup-{DateTime.Now:yyyyMMdd}.log");
        return _logFilePath;
    }

    private static bool IsRunningAsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static bool IsConfiguredWindowsAppSdkSelfContained()
    {
#if PRECONNECT_WINDOWSAPPSDK_SELF_CONTAINED
        return true;
#else
        return false;
#endif
    }

    private static string TryGetFileVersion(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return "missing";
        }

        try
        {
            var versionInfo = FileVersionInfo.GetVersionInfo(filePath);
            return versionInfo.FileVersion ?? versionInfo.ProductVersion ?? "unknown";
        }
        catch (Exception ex)
        {
            return $"error:{ex.GetType().Name}";
        }
    }

    private static bool TryGetPackageFullName(out string packageFullName)
    {
        var length = 0;
        var result = GetCurrentPackageFullName(ref length, null);

        if (result == AppModelErrorNoPackage)
        {
            packageFullName = string.Empty;
            return false;
        }

        if (result != 0 || length <= 0)
        {
            throw new Win32Exception(result);
        }

        var builder = new StringBuilder(length);
        result = GetCurrentPackageFullName(ref length, builder);
        if (result != 0)
        {
            throw new Win32Exception(result);
        }

        packageFullName = builder.ToString();
        return true;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetCurrentPackageFullName(ref int packageFullNameLength, StringBuilder? packageFullName);
}