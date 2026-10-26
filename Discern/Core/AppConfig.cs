using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Discern.Core;

/// <summary>一个模拟器 / 播放器的启动配置。</summary>
public sealed class EmulatorProfile
{
    public string DisplayName { get; set; } = "";

    /// <summary>
    /// 可执行文件路径。留空表示「用系统默认方式处理」。
    /// 支持绝对路径、<c>%ENV%</c> 变量，以及相对路径（从应用目录逐级向上查找，
    /// 见 <see cref="PathSearch"/>）。
    /// </summary>
    public string Executable { get; set; } = "";

    /// <summary>
    /// RetroArch 这类「主程序 + 核心」架构用的核心文件（.dll）。
    /// 留空表示不需要。同样支持相对路径，在参数模板里用 <c>{core}</c> 引用。
    /// </summary>
    public string Core { get; set; } = "";

    /// <summary>
    /// 参数模板。可用占位符：
    /// <c>{disc}</c> 盘符根路径（D:\）、<c>{device}</c> 裸设备（\\.\D:）、
    /// <c>{drive}</c> 盘符字母（D）、<c>{core}</c> 已解析的核心文件绝对路径、
    /// <c>{serial}</c> 序列号、<c>{title}</c> 标题、<c>{image}</c> 预抓取的镜像路径。
    /// </summary>
    public string Arguments { get; set; } = "\"{disc}\"";

    /// <summary>是否使用 ShellExecute（需要提权或需要系统关联时打开）。</summary>
    public bool UseShellExecute { get; set; } = false;

    /// <summary>可执行文件候选路径，Executable 解析不到时按序探测。同样支持相对路径。</summary>
    public List<string> SearchPaths { get; set; } = new();
}

/// <summary>游戏库中的单条已知游戏。命中后可以覆盖平台或指定专用启动方式。</summary>
public sealed class GameEntry
{
    public string? Serial { get; set; }
    public string? Title { get; set; }
    public string? Platform { get; set; }

    /// <summary>该游戏专用的模拟器键，覆盖平台默认。</summary>
    public string? Emulator { get; set; }

    /// <summary>专用参数模板。</summary>
    public string? Arguments { get; set; }
}

/// <summary>整体配置 + 游戏库。对应 Data\library.json。</summary>
public sealed class AppConfig
{
    /// <summary>当前配置结构版本。旧文件（无此字段 → 0）会被备份后重建。</summary>
    public const int CurrentVersion = 3;

    /// <summary>配置结构版本。0 表示旧版文件，用于触发迁移。</summary>
    public int Version { get; set; }

    /// <summary>平台键 → 启动配置。</summary>
    public Dictionary<string, EmulatorProfile> Emulators { get; set; } = new();

    /// <summary>音乐 CD 的处理方式。</summary>
    public EmulatorProfile Audio { get; set; } = new() { DisplayName = "默认播放器" };

    /// <summary>PC 游戏没有 autorun 时的处理方式。</summary>
    public EmulatorProfile PcGame { get; set; } = new() { DisplayName = "系统默认" };

    public List<GameEntry> Games { get; set; } = new();

    /// <summary>识别完成后是否自动启动（false 则只在界面提示）。</summary>
    public bool AutoLaunch { get; set; } = true;

    /// <summary>插入后等待多少毫秒再探测，给驱动挂载文件系统留时间。</summary>
    public int SettleDelayMs { get; set; } = 1200;

    [JsonIgnore]
    public string? ConfigPath { get; set; }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static AppConfig LoadOrCreate(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), JsonOpts);
                if (cfg != null)
                {
                    if (cfg.Version < CurrentVersion)
                    {
                        Log.Warn($"配置版本 {cfg.Version} 低于当前 {CurrentVersion}，备份旧文件后重建默认配置");
                        BackupOld(path, cfg.Version);

                        var upgraded = CreateDefault();
                        upgraded.ConfigPath = path;
                        upgraded.Save();
                        return upgraded;
                    }

                    cfg.ConfigPath = path;
                    return cfg;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"配置读取失败，使用默认配置：{ex.Message}");
        }

        var def = CreateDefault();
        def.ConfigPath = path;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(def, JsonOpts));
        }
        catch (Exception ex)
        {
            Log.Warn($"默认配置写入失败：{ex.Message}");
        }
        return def;
    }

    private static void BackupOld(string path, int oldVersion)
    {
        try
        {
            string bak = $"{path}.v{oldVersion}.bak";
            if (File.Exists(bak)) File.Delete(bak);
            File.Move(path, bak);
            Log.Info($"旧配置已备份到 {bak}");
        }
        catch (Exception ex)
        {
            Log.Warn($"旧配置备份失败：{ex.Message}");
        }
    }

    public void Save()
    {
        if (string.IsNullOrEmpty(ConfigPath)) return;
        try
        {
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, JsonOpts));
        }
        catch (Exception ex)
        {
            Log.Warn($"配置保存失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 默认配置：指向项目自带的 <c>Emulators</c> 目录
    /// （PCSX2 单机负责 PS2，RetroArch + 各 core 负责其余光盘机种）。
    /// </summary>
    public static AppConfig CreateDefault()
    {
        // RetroArch 的统一形态：主程序 -L <core> <盘符>
        static EmulatorProfile RetroArch(string name, string coreFile) => new()
        {
            DisplayName = name,
            Executable = @"Emulators\RetroArch\retroarch.exe",
            Core = @"Emulators\RetroArch\cores\" + coreFile,
            Arguments = "-L \"{core}\" \"{disc}\"",
            UseShellExecute = false,
        };

        return new AppConfig
        {
            Version = CurrentVersion,
            AutoLaunch = true,
            SettleDelayMs = 1200,
            Emulators = new Dictionary<string, EmulatorProfile>(StringComparer.OrdinalIgnoreCase)
            {
                // ---- PS1：SwanStation（DuckStation 的 libretro 版）----
                ["ps1"] = RetroArch("RetroArch · SwanStation（PS1）", "swanstation_libretro.dll"),

                // ---- PS2：用单机 PCSX2，比 libretro 核心更完整 ----
                ["ps2"] = new()
                {
                    DisplayName = "PCSX2 1.7（PS2）",
                    Executable = @"Emulators\pcsx2\pcsx2x64.exe",
                    // -disc 让 PCSX2 直接读物理光驱；-nogui 隐含 batch，退出游戏即关闭
                    Arguments = "-disc \"{disc}\" -fullscreen -nogui",
                    SearchPaths =
                    {
                        @"Emulators\pcsx2\pcsx2x64-avx2.exe",
                        @"C:\Program Files\PCSX2\pcsx2-qt.exe",
                    }
                },

                // ---- 其余光盘机种：全部走 RetroArch ----
                ["dreamcast"] = RetroArch("RetroArch · Flycast（DC）", "flycast_libretro.dll"),
                ["saturn"] = RetroArch("RetroArch · Beetle Saturn", "mednafen_saturn_libretro.dll"),
                ["segacd"] = RetroArch("RetroArch · Genesis Plus GX（Sega CD）", "genesis_plus_gx_libretro.dll"),
                ["pcengine"] = RetroArch("RetroArch · Beetle PCE（PCE-CD）", "mednafen_pce_libretro.dll"),

                // ---- 视频盘：走系统里实际装着的 MPC-HC（K-Lite 自带）----
                // MPC-HC 直接吃目录路径：含 VIDEO_TS 的目录会被当 DVD 打开
                ["dvdvideo"] = new()
                {
                    DisplayName = "MPC-HC（DVD 视频）",
                    Arguments = "\"{disc}\"",
                    SearchPaths =
                    {
                        @"C:\Program Files (x86)\K-Lite Codec Pack\MPC-HC64\mpc-hc64.exe",
                        @"C:\Program Files\K-Lite Codec Pack\MPC-HC64\mpc-hc64.exe",
                        @"C:\Program Files\MPC-HC\mpc-hc64.exe",
                        @"C:\Program Files (x86)\MPC-HC\mpc-hc64.exe",
                    }
                },
                ["bluray"] = new()
                {
                    DisplayName = "MPC-HC（Blu-ray）",
                    Arguments = "\"{disc}\"",
                    SearchPaths =
                    {
                        @"C:\Program Files (x86)\K-Lite Codec Pack\MPC-HC64\mpc-hc64.exe",
                        @"C:\Program Files\K-Lite Codec Pack\MPC-HC64\mpc-hc64.exe",
                        @"C:\Program Files\MPC-HC\mpc-hc64.exe",
                        @"C:\Program Files (x86)\MPC-HC\mpc-hc64.exe",
                    }
                },
            },
            Audio = new EmulatorProfile
            {
                DisplayName = "系统默认播放器",
                Executable = "",
                Arguments = "{disc}",
            },
            PcGame = new EmulatorProfile
            {
                DisplayName = "系统默认（autorun / 资源管理器）",
                Executable = "",
                Arguments = "{disc}",
            },
            Games = new List<GameEntry>(),
        };
    }

    /// <summary>按序列号或标题匹配已知游戏，命中则返回覆盖项。</summary>
    public GameEntry? MatchGame(DiscInfo disc)
    {
        foreach (var g in Games)
        {
            if (!string.IsNullOrEmpty(g.Serial) && !string.IsNullOrEmpty(disc.Serial) &&
                g.Serial.Equals(disc.Serial, StringComparison.OrdinalIgnoreCase))
                return g;
        }

        foreach (var g in Games)
        {
            if (!string.IsNullOrEmpty(g.Title) && !string.IsNullOrEmpty(disc.Title) &&
                g.Title.Equals(disc.Title, StringComparison.OrdinalIgnoreCase))
                return g;
        }

        return null;
    }

    /// <summary>解析平台对应的启动配置，找不到返回 null。</summary>
    public EmulatorProfile? ResolveProfile(string platform) =>
        Emulators.TryGetValue(platform, out var p) ? p : null;
}
