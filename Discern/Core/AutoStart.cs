using System.IO;
using Microsoft.Win32;

namespace Discern.Core;

/// <summary>
/// 开机自启，写在 <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c>。
///
/// 用注册表而不是「启动」文件夹：可枚举、可编程开关、不需要额外快捷方式。
/// 每个用户各自一份，不碰 HKLM，因此不需要管理员权限。
/// </summary>
public static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Discern";

    /// <summary>Discern 需要常驻才能收到插盘通知，自启时带 --background 直接进托盘。</summary>
    public const string BackgroundArgument = "--background";

    /// <summary>
    /// 本程序当前的实际可执行文件路径。
    ///
    /// 必须走 <see cref="PathSearch.GetRealPath"/>：从目录联接或符号链接路径
    /// 启动时 <see cref="Environment.ProcessPath"/> 返回的是链接路径，
    /// 写进自启项后一旦链接被删，开机自启就指向了一个不存在的文件。
    /// </summary>
    public static string ExecutablePath
    {
        get
        {
            try
            {
                if (Environment.ProcessPath is { Length: > 0 } p &&
                    p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    return PathSearch.GetRealPath(p);
            }
            catch { /* ignore */ }

            return PathSearch.GetRealPath(Path.Combine(AppContext.BaseDirectory, "Discern.exe"));
        }
    }

    /// <summary>将要写入注册表的启动命令。</summary>
    public static string Command => $"\"{ExecutablePath}\" {BackgroundArgument}";

    /// <summary>写入失败时写进日志的手动配置说明（单行，便于 grep）。</summary>
    public static string ManualHint =>
        $@"手动添加：HKCU\Software\Microsoft\Windows\CurrentVersion\Run 下新建字符串 {ValueName} = {Command}";

    /// <summary>注册表里是否已有 Discern 的自启项。</summary>
    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string s && s.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>读回注册表里实际记录的命令，没有则返回 null。</summary>
    public static string? RecordedCommand()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) as string;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>开启 / 关闭自启。</summary>
    public static bool Set(bool enabled, out string message)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (key == null)
            {
                message = "无法打开注册表 Run 项";
                return false;
            }

            if (enabled)
            {
                key.SetValue(ValueName, Command, RegistryValueKind.String);

                // 回读确认：注册表写入可能被策略静默忽略
                string? readBack = key.GetValue(ValueName) as string;
                if (readBack != Command)
                {
                    message = $"自启写入未生效（回读：{readBack ?? "(无)"}）";
                    return false;
                }

                message = $"已开启开机自启：{Command}";
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                message = "已关闭开机自启";
            }

            return true;
        }
        catch (UnauthorizedAccessException)
        {
            // 最常见的情况：安全软件或组策略禁止写启动项。
            // 不直接把异常原文抛给用户看，那串完整注册表路径又长又没有信息量。
            message = "没有写入启动项的权限（被安全软件或系统策略拒绝）";
            return false;
        }
        catch (System.Security.SecurityException)
        {
            message = "没有写入启动项的权限（被安全软件或系统策略拒绝）";
            return false;
        }
        catch (Exception ex)
        {
            message = $"设置自启失败：{ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// 已开启自启、但记录的是旧路径时（比如换了目录或重新编译过），静默修正。
    /// 返回是否发生了修正。
    ///
    /// 只比较命令里的可执行文件部分：注册表里记的是
    /// <c>"…\Discern.exe" --background</c>，直接拿整串比永远不相等，
    /// 会导致每次启动都误判为「路径过期」而去重写注册表。
    /// </summary>
    public static bool RepairIfStale()
    {
        string? recorded = RecordedCommand();
        if (recorded == null) return false;

        string? recordedExe = ExtractExecutable(recorded);

        // 两边都规整成真实路径再比，避免「同一个文件、两种写法」被误判为过期
        if (recordedExe != null &&
            PathSearch.GetRealPath(recordedExe)
                .Equals(ExecutablePath, StringComparison.OrdinalIgnoreCase))
            return false;

        if (Set(true, out string message))
        {
            Log.Info($"[自启] 路径已更新：{recorded} → {Command}");
            return true;
        }

        // Set 本身不写日志，由调用方决定记什么，避免同一动作被记两遍
        Log.Warn($"[自启] {message}");
        return false;
    }

    /// <summary>从一条启动命令里取出可执行文件路径（支持带引号与不带引号两种写法）。</summary>
    internal static string? ExtractExecutable(string command)
    {
        string s = command.Trim();
        if (s.Length == 0) return null;

        if (s[0] == '"')
        {
            int end = s.IndexOf('"', 1);
            return end > 1 ? s[1..end] : null;
        }

        int space = s.IndexOf(' ');
        return space > 0 ? s[..space] : s;
    }
}
