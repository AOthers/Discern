using System.Diagnostics;
using System.IO;
using System.Text;

namespace Discern.Core;

/// <summary>简单滚动日志，同时写文件和内存环形缓冲供界面展示。</summary>
public static class Log
{
    private const int MaxLines = 500;
    private static readonly Queue<string> Buffer = new();
    private static readonly object Gate = new();
    private static string? _logPath;

    public static event Action<string>? LineWritten;

    public static void Init(string? logPath)
    {
        _logPath = logPath;
        if (logPath == null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);

            // 追加而不是覆盖：重启、以及自启实例与手动实例并存时都不能丢历史
            File.AppendAllText(logPath,
                $"=== Discern {DateTime.Now:yyyy-MM-dd HH:mm:ss} ======\n",
                Encoding.UTF8);
        }
        catch { /* 日志失败不影响主流程 */ }
    }

    public static void Info(string msg) => Write("INFO", msg);
    public static void Warn(string msg) => Write("WARN", msg);
    public static void Error(string msg) => Write("ERROR", msg);

    private static void Write(string level, string msg)
    {
        string line = $"{DateTime.Now:HH:mm:ss} [{level}] {msg}";

        lock (Gate)
        {
            Buffer.Enqueue(line);
            while (Buffer.Count > MaxLines) Buffer.Dequeue();

            if (_logPath != null)
            {
                try { File.AppendAllText(_logPath, line + "\n", Encoding.UTF8); }
                catch { /* ignore */ }
            }
        }

        LineWritten?.Invoke(line);
    }

    public static string[] Snapshot()
    {
        lock (Gate) return Buffer.ToArray();
    }
}
