using System.Diagnostics;
using System.IO;
using System.Text;

namespace Discern.Core;

public sealed record LaunchOutcome(bool Ok, string Message, string? CommandLine = null);

/// <summary>
/// 根据识别结果组装命令行并启动外部程序（模拟器 / 播放器 / 游戏本体）。
/// </summary>
public static class Launcher
{
    /// <summary>
    /// 解析可执行文件路径：先看显式配置，再按 <see cref="EmulatorProfile.SearchPaths"/> 探测。
    /// 相对路径从应用目录逐级向上查找，所以配置里可以直接写 <c>Emulators\...</c>。
    /// </summary>
    public static string? ResolveExecutable(EmulatorProfile profile) =>
        ResolveFirst(profile.Executable, profile.SearchPaths);

    /// <summary>解析 RetroArch 之类的核心文件，未配置则返回 null。</summary>
    public static string? ResolveCore(EmulatorProfile profile) =>
        PathSearch.ResolveFile(profile.Core);

    /// <summary>可执行文件未配置时返回 true（表示交给系统默认处理）。</summary>
    public static bool PrefersShellDefault(EmulatorProfile profile) =>
        string.IsNullOrWhiteSpace(profile.Executable) && profile.SearchPaths.Count == 0;

    private static string? ResolveFirst(string primary, IEnumerable<string> candidates)
    {
        var first = PathSearch.ResolveFile(primary);
        if (first != null) return first;

        foreach (var candidate in candidates)
        {
            var hit = PathSearch.ResolveFile(candidate);
            if (hit != null) return hit;
        }

        return null;
    }

    /// <summary>套用占位符模板。</summary>
    public static string ApplyTemplate(string template, DiscInfo disc, string? imagePath = null, string? corePath = null)
    {
        string drive = disc.DriveLetter.ToString();
        return template
            .Replace("{disc}", disc.RootPath)
            .Replace("{device}", @"\\.\" + drive + ":")
            .Replace("{drive}", drive)
            .Replace("{core}", corePath ?? string.Empty)
            .Replace("{serial}", disc.Serial ?? string.Empty)
            .Replace("{title}", disc.Title)
            .Replace("{image}", imagePath ?? string.Empty);
    }

    /// <summary>为这张盘选出该用的启动配置。</summary>
    public static (EmulatorProfile? profile, string reason) SelectProfile(DiscInfo disc, AppConfig cfg)
    {
        // 1) 已入库的游戏可以指定专用模拟器
        var entry = cfg.MatchGame(disc);
        if (entry?.Emulator is { Length: > 0 } emuKey &&
            cfg.ResolveProfile(emuKey) is { } ov)
        {
            var cloned = new EmulatorProfile
            {
                DisplayName = ov.DisplayName,
                Executable = ov.Executable,
                Core = ov.Core,
                Arguments = entry.Arguments ?? ov.Arguments,
                UseShellExecute = ov.UseShellExecute,
                SearchPaths = ov.SearchPaths,
            };
            return (cloned, $"游戏库命中 {entry.Title ?? entry.Serial}，使用专用配置");
        }

        // 2) 音乐 CD
        if (disc.Kind == DiscKind.AudioCd)
            return (cfg.Audio, "音乐 CD 使用播放器配置");

        // 3) PC 游戏 / 软件
        if (disc.Kind is DiscKind.PcGame or DiscKind.PcSoftware)
            return (cfg.PcGame, "PC 盘使用系统默认处理");

        // 4) 平台模拟器
        if (disc.Platform != null && cfg.ResolveProfile(disc.Platform) is { } p)
            return (p, $"平台 {disc.Platform} 使用 {p.DisplayName}");

        return (null, disc.Platform != null
            ? $"平台 {disc.Platform} 未在配置中定义模拟器"
            : "无法确定启动方式");
    }

    /// <summary>只描述启动计划，不真正启动（用于自检 / 诊断）。</summary>
    public static string DescribePlan(DiscInfo disc, AppConfig cfg, string? imagePath = null)
    {
        var (profile, reason) = SelectProfile(disc, cfg);
        if (profile == null)
            return $"无启动方案（{reason}）";

        if (PrefersShellDefault(profile))
            return $"系统默认处理：{profile.DisplayName}  [{reason}]";

        string? exe = ResolveExecutable(profile);
        string? core = ResolveCore(profile);
        string args = ApplyTemplate(profile.Arguments, disc, imagePath, core);

        if (string.IsNullOrEmpty(exe))
            return $"{profile.DisplayName}：未找到可执行文件 → 退回系统默认处理  [{reason}]";

        string coreNote = "";
        if (!string.IsNullOrWhiteSpace(profile.Core) && core == null)
            coreNote = "  ⚠ 核心未找到";

        return $"\"{exe}\" {args}{coreNote}  [{reason}]";
    }

    /// <summary>逐个检查配置里的模拟器能否解析到文件，供 <c>--emulators</c> 输出。</summary>
    public static IEnumerable<string> Diagnose(AppConfig cfg)
    {
        yield return "== 模拟器 / 播放器配置 ==";
        yield return $"配置文件 : {cfg.ConfigPath ?? "(内存)"}";
        yield return $"应用目录 : {AppContext.BaseDirectory}";
        yield return "";

        var keys = new List<string>(cfg.Emulators.Keys);
        keys.Sort(StringComparer.OrdinalIgnoreCase);

        foreach (string key in keys)
        {
            var p = cfg.Emulators[key];
            yield return $"[{key}] {p.DisplayName}";

            if (PrefersShellDefault(p))
            {
                yield return "    exe   -  交给系统默认处理";
            }
            else
            {
                string? exe = ResolveExecutable(p);
                yield return exe != null
                    ? $"    exe   OK  {exe}"
                    : $"    exe   NG  未找到 → {PathSearch.Describe(p.Executable)}";
            }

            if (!string.IsNullOrWhiteSpace(p.Core))
            {
                string? core = ResolveCore(p);
                yield return core != null
                    ? $"    core  OK  {core}"
                    : $"    core  NG  未找到 → {PathSearch.Describe(p.Core)}";
            }

            yield return $"    args  {p.Arguments}";
            yield return "";
        }

        yield return $"[audio] {cfg.Audio.DisplayName}";
        yield return $"    exe   {(PrefersShellDefault(cfg.Audio) ? "-  交给系统默认处理" : PathSearch.Describe(cfg.Audio.Executable))}";
        yield return "";
        yield return $"[pcgame] {cfg.PcGame.DisplayName}";
        yield return $"    exe   {(PrefersShellDefault(cfg.PcGame) ? "-  交给系统默认处理" : PathSearch.Describe(cfg.PcGame.Executable))}";
    }

    /// <summary>执行启动。</summary>
    public static LaunchOutcome Launch(DiscInfo disc, AppConfig cfg, string? imagePath = null)
    {
        var (profile, reason) = SelectProfile(disc, cfg);
        if (profile == null)
            return new LaunchOutcome(false, reason);

        Log.Info($"[选择] {reason}");

        if (PrefersShellDefault(profile))
            return ShellDefault(disc, profile);

        string? exe = ResolveExecutable(profile);
        string? core = ResolveCore(profile);
        string args = ApplyTemplate(profile.Arguments, disc, imagePath, core);

        // 没有配置可执行文件 → 交给系统默认处理（ShellExecute）
        if (string.IsNullOrEmpty(exe))
        {
            Log.Warn($"[回退] 未找到 {profile.DisplayName} 的可执行文件");
            return ShellDefault(disc, profile);
        }

        if (!string.IsNullOrWhiteSpace(profile.Core) && core == null)
        {
            string msg = $"核心文件不存在：{profile.Core}";
            Log.Error($"[启动失败] {msg}");
            return new LaunchOutcome(false, msg);
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                UseShellExecute = profile.UseShellExecute,
                // WorkingDirectory 指向模拟器目录：PCSX2 的 portable.ini、
                // RetroArch 的 retroarch.cfg / cores / system 都依赖这个
                WorkingDirectory = Path.GetDirectoryName(exe) ?? Environment.CurrentDirectory,
            };

            string cmd = $"\"{exe}\" {args}";
            Log.Info($"[启动] {cmd}");

            using var proc = Process.Start(psi);
            if (proc == null)
                return new LaunchOutcome(false, "进程未能启动", cmd);

            return new LaunchOutcome(true, $"已启动 {profile.DisplayName}（PID {proc.Id}）", cmd);
        }
        catch (Exception ex)
        {
            Log.Error($"[启动失败] {ex.Message}");
            return new LaunchOutcome(false, $"启动失败：{ex.Message}", $"\"{exe}\" {args}");
        }
    }

    private static LaunchOutcome ShellDefault(DiscInfo disc, EmulatorProfile profile)
    {
        try
        {
            string target = disc.RootPath;

            var psi = new ProcessStartInfo
            {
                FileName = target,
                UseShellExecute = true,
            };

            // 音频 CD 与视频盘优先用「播放」动作，其余交给系统「打开」
            if (disc.Kind is DiscKind.AudioCd or DiscKind.DvdVideo or DiscKind.BluRayVideo)
                psi.Verb = "play";

            Log.Info($"[启动] shell:{(psi.Verb ?? "open")} {target} → {profile.DisplayName}");

            using var proc = Process.Start(psi);
            return new LaunchOutcome(true,
                proc != null
                    ? $"已交给系统默认程序处理（{profile.DisplayName}）"
                    : $"已请求系统处理（{profile.DisplayName}）",
                $"shell:{psi.Verb ?? "open"} \"{target}\"");
        }
        catch (Exception ex)
        {
            Log.Warn($"[回退] 系统默认处理失败：{ex.Message}，改为打开盘符");
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"\"{disc.RootPath}\"",
                    UseShellExecute = true,
                });
                return new LaunchOutcome(true, "已用资源管理器打开光盘", $"explorer.exe \"{disc.RootPath}\"");
            }
            catch (Exception ex2)
            {
                return new LaunchOutcome(false, $"打开失败：{ex2.Message}");
            }
        }
    }
}
