using System.IO;
using System.Runtime.InteropServices;

namespace Discern.Core;

/// <summary>
/// 配置里可以写相对路径（例如 <c>Emulators\pcsx2\pcsx2x64.exe</c>），
/// 这里负责把它解析成实际存在的绝对路径。
///
/// 查找顺序：应用所在目录 → 逐级父目录（最多 <see cref="MaxUpLevels"/> 级）。
/// 这样同一份 library.json 在
/// <c>Discern\bin\Release\net10.0-windows\</c>、发布目录、
/// 或者任意层级的子目录里都能找到同级/上层的 Emulators 文件夹。
/// </summary>
public static class PathSearch
{
    /// <summary>向上回溯的最大层数。</summary>
    public const int MaxUpLevels = 8;

    /// <summary>
    /// 解析一个可能是相对路径的配置项。
    /// 空字符串、找不到、或指向目录而非文件时返回 <c>null</c>。
    /// </summary>
    public static string? ResolveFile(string? configured) => Resolve(configured, requireDirectory: false);

    /// <summary>解析一个目录配置项。</summary>
    public static string? ResolveDirectory(string? configured) => Resolve(configured, requireDirectory: true);

    private static string? Resolve(string? configured, bool requireDirectory)
    {
        if (string.IsNullOrWhiteSpace(configured)) return null;

        string raw = configured.Trim().Trim('"');
        if (raw.Length == 0) return null;

        raw = Environment.ExpandEnvironmentVariables(raw);

        bool Exists(string p) => requireDirectory ? Directory.Exists(p) : File.Exists(p);

        // 绝对路径：直接判定，不做任何猜测
        if (Path.IsPathRooted(raw))
        {
            string full;
            try { full = Path.GetFullPath(raw); }
            catch { return null; }
            return Exists(full) ? full : null;
        }

        foreach (string root in CandidateRoots())
        {
            string full;
            try { full = Path.GetFullPath(Path.Combine(root, raw)); }
            catch { continue; }

            if (Exists(full)) return full;
        }

        return null;
    }

    /// <summary>解析用的搜索根：应用目录及其各级父目录。</summary>
    public static IEnumerable<string> CandidateRoots()
    {
        DirectoryInfo? dir;
        try { dir = new DirectoryInfo(AppContext.BaseDirectory); }
        catch { dir = null; }

        for (int level = 0; level <= MaxUpLevels && dir != null; level++)
        {
            yield return dir.FullName;
            dir = dir.Parent;
        }
    }

    /// <summary>把相对路径展开成便于报错的描述。</summary>
    public static string Describe(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured)) return "(未配置)";

        string raw = configured.Trim().Trim('"');
        if (Path.IsPathRooted(raw)) return raw;

        return $"{raw}  (从 {AppContext.BaseDirectory} 向上查 {MaxUpLevels} 级)";
    }

    // ------------------------------------------------------------------ 真实路径

    private const uint FILE_SHARE_ALL = 0x00000007;      // READ | WRITE | DELETE
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    private static readonly IntPtr INVALID_HANDLE = new(-1);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFileW(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition,
        uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(
        IntPtr hFile, [Out] char[] lpszFilePath, uint cchFilePath, uint dwFlags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    /// <summary>
    /// 把路径规整成「真实路径」：解析目录联接、符号链接、8.3 短名和大小写。
    ///
    /// 必要性：程序从 <c>E:\Game\光盘启动器\app\Discern.exe</c> 这类联接路径启动时，
    /// <see cref="Environment.ProcessPath"/> 返回的是联接路径本身。
    /// 如果拿它写进开机自启，删掉联接的那天自启就失效了。
    /// </summary>
    public static string GetRealPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return path ?? string.Empty;

        IntPtr handle = IntPtr.Zero;
        try
        {
            // 访问权限传 0 就够：只想问「你是谁」，不想读内容
            handle = CreateFileW(path, 0, FILE_SHARE_ALL, IntPtr.Zero,
                OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);

            if (handle == IntPtr.Zero || handle == INVALID_HANDLE) return path;

            var buffer = new char[1024];
            uint len = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, 0);
            if (len == 0 || len >= buffer.Length) return path;

            string result = new string(buffer, 0, (int)len);

            // 去掉 \\?\ 前缀；UNC 路径是 \\?\UNC\server\share 形式
            if (result.StartsWith(@"\\?\UNC\", StringComparison.Ordinal))
                return @"\\" + result[8..];
            if (result.StartsWith(@"\\?\", StringComparison.Ordinal))
                return result[4..];

            return result;
        }
        catch
        {
            return path;
        }
        finally
        {
            if (handle != IntPtr.Zero && handle != INVALID_HANDLE) CloseHandle(handle);
        }
    }
}
