using System.IO;
using System.Security.Principal;
using System.Text;
using System.Windows;
using Discern.Core;

namespace Discern;

public partial class App : Application
{
    public static string BaseDir { get; } =
        AppDomain.CurrentDomain.BaseDirectory;

    public static string ConfigPath { get; } =
        Path.Combine(BaseDir, "Data", "library.json");

    public static string LogPath { get; } =
        Path.Combine(BaseDir, "logs", $"discern-{DateTime.Now:yyyyMMdd}.log");

    public static bool IsElevated { get; private set; }

    /// <summary>保证只有一个实例在监听插盘（进程退出自动释放）。</summary>
    private Mutex? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        Log.Init(LogPath);
        IsElevated = CheckElevated();

        Log.Info($"启动 Discern · 管理员={(IsElevated ? "是" : "否")}");

        // 命令行：--probe / --probe-dir  → 打印识别结果后退出（调试 / 脚本化 / 自动化测试）
        if (e.Args.Any(a => a.Equals("--probe", StringComparison.OrdinalIgnoreCase))
            || e.Args.Any(a => a.Equals("--probe-dir", StringComparison.OrdinalIgnoreCase)))
        {
            RunProbeMode(e.Args);
            Shutdown(0);
            return;
        }

        // 命令行：--emulators → 逐个检查模拟器 / 核心能否解析到，打印后退出
        if (e.Args.Any(a => a.Equals("--emulators", StringComparison.OrdinalIgnoreCase)))
        {
            RunEmulatorCheck(e.Args);
            Shutdown(0);
            return;
        }

        // 命令行：--autostart=on|off|status → 直接读写开机自启项后退出
        string? autoArg = e.Args.FirstOrDefault(a =>
            a.StartsWith("--autostart", StringComparison.OrdinalIgnoreCase));
        if (autoArg != null)
        {
            RunAutoStartMode(autoArg, e.Args);
            Shutdown(0);
            return;
        }

        // 单实例：开机自启的常驻进程 + 用户手动双击，不能各起一个去抢同一张盘
        _singleInstance = new Mutex(true, @"Local\Discern.SingleInstance", out bool isFirst);
        if (!isFirst)
        {
            Log.Info("已有 Discern 实例在运行，本次启动直接退出");
            Shutdown(0);
            return;
        }

        // 自启时带 --background：只进托盘，不弹窗口
        bool background = e.Args.Any(a =>
            a.Equals(AutoStart.BackgroundArgument, StringComparison.OrdinalIgnoreCase));

        var win = new MainWindow(startHidden: background);
        MainWindow = win;

        if (!background) win.Show();
    }

    private static bool CheckElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    // ------------------------------------------------------------------ probe 模式

    private static void RunProbeMode(string[] args)
    {
        AttachParentConsole();
        var sb = new StringBuilder();

        // 输出目标（--out=FILE）：避免 WinExe 的控制台附着在不同宿主下失效
        string? outFile = null;
        foreach (var a in args)
            if (a.StartsWith("--out=", StringComparison.OrdinalIgnoreCase))
                outFile = a[6..];

        // 目录级探测（--probe-dir PATH）
        string? probeDir = null;
        for (int i = 0; i < args.Length; i++)
            if (args[i].Equals("--probe-dir", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                probeDir = args[i + 1];

        // 可选：注入一个引导签名，用于验证裸扇区分支（无管理员权限时也能测）
        string? bootMagic = null;
        foreach (var a in args)
            if (a.StartsWith("--boot-magic=", StringComparison.OrdinalIgnoreCase))
                bootMagic = a[13..].ToLowerInvariant();

        if (probeDir != null)
        {
            try
            {
                var info = DiscProbe.ProbePath(probeDir, bootMagic: bootMagic);
                Describe(sb, info, AppConfig.LoadOrCreate(ConfigPath));
            }
            catch (Exception ex)
            {
                sb.AppendLine($"探测失败: {ex.Message}");
            }

            Emit(sb, outFile);
            return;
        }

        char? only = null;
        foreach (var a in args)
        {
            if (a.StartsWith("--drive=", StringComparison.OrdinalIgnoreCase))
                only = char.ToUpperInvariant(a[8]);
        }

        var drives = DriveInfo.GetDrives()
            .Where(d => d.DriveType == DriveType.CDRom)
            .Select(d => char.ToUpperInvariant(d.Name[0]))
            .ToList();

        if (only.HasValue) drives = drives.Where(l => l == only.Value).ToList();

        if (drives.Count == 0)
        {
            sb.AppendLine("未发现 CD-ROM 设备。");
            Emit(sb, outFile);
            return;
        }

        foreach (var letter in drives)
        {
            using var drive = new OpticalDrive(letter);
            var info = DiscProbe.Probe(drive);
            Describe(sb, info, AppConfig.LoadOrCreate(ConfigPath));
        }

        Emit(sb, outFile);
    }

    // ------------------------------------------------------------------ emulators 自检

    private static void RunEmulatorCheck(string[] args)
    {
        AttachParentConsole();

        string? outFile = null;
        foreach (var a in args)
            if (a.StartsWith("--out=", StringComparison.OrdinalIgnoreCase))
                outFile = a[6..];

        var sb = new StringBuilder();
        var cfg = AppConfig.LoadOrCreate(ConfigPath);
        foreach (var line in Launcher.Diagnose(cfg))
            sb.AppendLine(line);

        Emit(sb, outFile);
    }

    // ------------------------------------------------------------------ autostart 模式

    private static void RunAutoStartMode(string arg, string[] args)
    {
        AttachParentConsole();

        string? outFile = null;
        foreach (var a in args)
            if (a.StartsWith("--out=", StringComparison.OrdinalIgnoreCase))
                outFile = a[6..];

        int eq = arg.IndexOf('=');
        string value = eq >= 0 ? arg[(eq + 1)..].Trim() : "status";

        var sb = new StringBuilder();

        if (value.Equals("on", StringComparison.OrdinalIgnoreCase))
        {
            bool ok = AutoStart.Set(true, out string msg);
            Record(ok, msg);
            sb.AppendLine(msg);
        }
        else if (value.Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            bool ok = AutoStart.Set(false, out string msg);
            Record(ok, msg);
            sb.AppendLine(msg);
        }
        else
        {
            sb.AppendLine($"状态    : {(AutoStart.IsEnabled() ? "已开启" : "未开启")}");
            sb.AppendLine($"当前记录: {AutoStart.RecordedCommand() ?? "(无)"}");
            sb.AppendLine($"将写入  : {AutoStart.Command}");
        }

        Emit(sb, outFile);

        // 命令行动作也写进日志文件，和界面操作保持一致的留痕
        static void Record(bool ok, string msg)
        {
            if (ok) Log.Info($"[自启] {msg}");
            else Log.Warn($"[自启] {msg}");
        }
    }

    private static void Emit(StringBuilder sb, string? outFile)
    {
        string text = sb.ToString();
        Console.Write(text);
        Console.Out.Flush();

        if (outFile != null)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outFile))!);
                File.WriteAllText(outFile, text, Encoding.UTF8);
            }
            catch { /* 写文件失败时控制台输出仍然有效 */ }
        }
    }

    private static void Describe(StringBuilder sb, DiscInfo info, AppConfig cfg)
    {
        sb.AppendLine($"===== {info.DriveLetter}: =====  ({info.RootPath})");
        sb.AppendLine($"KIND      : {info.Kind}");
        sb.AppendLine($"类别      : {info.KindDisplay}");
        sb.AppendLine($"平台      : {info.Platform ?? "-"}");
        sb.AppendLine($"标题      : {info.Title}");
        sb.AppendLine($"序列号    : {info.Serial ?? "-"}");
        sb.AppendLine($"卷标      : {info.VolumeLabel}");
        sb.AppendLine($"文件系统  : {info.FileSystem}");
        sb.AppendLine($"容量      : {(info.TotalSize > 0 ? $"{info.TotalSize / 1024.0 / 1024.0:F1} MB" : "-")}");
        sb.AppendLine($"可自动启动: {info.IsActionable}");
        sb.AppendLine($"裸扇区读取: {info.RawAccessAvailable}");
        sb.AppendLine($"启动计划  : {Launcher.DescribePlan(info, cfg)}");

        if (info.Tracks.Count > 0)
        {
            sb.AppendLine($"音轨      : {info.Tracks.Count}");
            foreach (var t in info.Tracks.Take(20)) sb.AppendLine($"            {t}");
        }

        sb.AppendLine("证据:");
        foreach (var ev in info.Evidence)
            sb.AppendLine($"  [{ev.Weight,3}] {ev.Rule,-22} {ev.Detail}");

        if (info.RootEntries.Count > 0)
        {
            sb.AppendLine($"根目录({info.RootEntries.Count}):");
            foreach (var entry in info.RootEntries.Take(25))
                sb.AppendLine($"  {Path.GetFileName(entry)}");
        }

        sb.AppendLine();
    }

    private static void AttachParentConsole()
    {
        try
        {
            NativeMethods.AttachConsole(NativeMethods.ATTACH_PARENT_PROCESS);
        }
        catch { /* 无父控制台 */ }

        try
        {
            var stdout = new StreamWriter(Console.OpenStandardOutput(), Encoding.UTF8)
            {
                AutoFlush = true
            };
            Console.SetOut(stdout);
        }
        catch { /* ignore */ }
    }
}
