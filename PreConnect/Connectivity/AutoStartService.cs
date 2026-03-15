using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace PreConnect.Connectivity;

/// <summary>
/// 管理 PreConnect 开机自启状态，基于当前用户注册表 Run 键。
/// 无需管理员权限。
/// </summary>
public static class AutoStartService
{
    private const string RunKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "PreConnect";

    /// <summary>当前可执行文件的完整路径。</summary>
    private static string CurrentExePath =>
        Process.GetCurrentProcess().MainModule?.FileName
        ?? Path.Combine(AppContext.BaseDirectory, "PreConnect.exe");

    /// <summary>检查当前应用是否已设置开机自启。</summary>
    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            if (key is null) return false;

            var value = key.GetValue(ValueName) as string;
            return string.Equals(value, CurrentExePath, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 启用开机自启。
    /// 同时扫描 Run 键内所有值，将路径中含 "PreConnect" 的旧条目一并替换，
    /// 避免多个版本同时自启（撞车）。
    /// </summary>
    /// <returns>被替换掉的旧条目数量（0 表示无撞车）。</returns>
    public static int Enable()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key is null) return 0;

            var current = CurrentExePath;
            var replaced = 0;

            // 撞车检查：找出所有指向其他 PreConnect 可执行文件的值
            foreach (var name in key.GetValueNames())
            {
                if (string.Equals(name, ValueName, StringComparison.OrdinalIgnoreCase))
                    continue; // 留给下面统一写

                if (key.GetValue(name) is string path
                    && path.IndexOf("PreConnect", StringComparison.OrdinalIgnoreCase) >= 0
                    && !string.Equals(path, current, StringComparison.OrdinalIgnoreCase))
                {
                    key.DeleteValue(name, throwOnMissingValue: false);
                    replaced++;
                }
            }

            key.SetValue(ValueName, current);
            return replaced;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>禁用开机自启，删除注册表值。</summary>
    public static void Disable()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch
        {
            // 忽略
        }
    }
}
