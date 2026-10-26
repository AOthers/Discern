using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Discern.Core;

/// <summary>
/// 光盘识别器。判定顺序从「物理层证据」到「文件层证据」，证据越硬优先级越高：
/// <list type="number">
///   <item>TOC 音轨控制位 — 唯一可靠区分音乐 CD 与数据盘的方式</item>
///   <item>扇区 0 引导签名 — Sega CD / Saturn / Dreamcast / PC Engine 的盘面魔数</item>
///   <item>ISO9660 SYSTEM.CNF — PS1（BOOT）与 PS2（BOOT2）的硬指纹</item>
///   <item>目录结构 — VIDEO_TS / BDMV / autorun.inf / *.exe</item>
/// </list>
/// </summary>
public static class DiscProbe
{
    /// <summary>PS1/PS2 序列号：SLUS_200.62 / SLES_123.45 / SCUS_941.63 …</summary>
    private static readonly Regex SerialRegex =
        new(@"(?<pre>[A-Z]{2,4})[_\-\s]?(?<mid>\d{3})[.\-](?<rev>\d{2})",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex Boot2Regex =
        new(@"BOOT2\s*=\s*(?<path>[^\r\n;]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex BootRegex =
        new(@"^\s*BOOT\s*=\s*(?<path>[^\r\n;]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline);

    // ================================================================== 入口

    /// <summary>探测一个真实光驱。</summary>
    public static DiscInfo Probe(OpticalDrive drive)
    {
        var info = new DiscInfo
        {
            DriveLetter = drive.Letter,
            RootPath = drive.RootPath,
            VolumeLabel = SafeLabel(drive),
            FileSystem = SafeFileSystem(drive),
            TotalSize = drive.GetTotalSize(),
            RawAccessAvailable = drive.RawAvailable,
        };

        if (!drive.IsMediaPresent())
        {
            info.Kind = DiscKind.Empty;
            info.Title = "（无盘）";
            info.Evidence.Add(new DiscEvidence("media-present", "光驱内无介质", 100));
            return info;
        }

        TocResult? toc = drive.ReadToc();
        info.Tracks = toc?.Tracks ?? Array.Empty<TocTrack>();

        string? bootMagic = SniffBootMagic(drive.ReadSectors(0, 1));

        byte[]? pvd = drive.ReadPrimaryVolumeDescriptor();
        string? isoLabel = ParsePvdLabel(pvd);

        return Finish(info, drive.RootPath, toc, bootMagic, isoLabel);
    }

    /// <summary>
    /// 目录级识别：只依赖文件系统视图，不需要裸设备权限。
    /// 用于没有物理光驱的环境下验证识别规则，或手动指向一个已解包的光盘目录。
    /// </summary>
    public static DiscInfo ProbePath(
        string rootPath,
        char? letter = null,
        string? volumeLabel = null,
        string? bootMagic = null,
        TocResult? toc = null)
    {
        if (!Directory.Exists(rootPath))
            throw new DirectoryNotFoundException(rootPath);

        char effective = letter ?? InferLetter(rootPath);

        var info = new DiscInfo
        {
            DriveLetter = effective,
            RootPath = rootPath,
            VolumeLabel = volumeLabel ?? Path.GetFileName(rootPath.TrimEnd('\\', '/')),
            FileSystem = "（目录）",
            TotalSize = DirectorySize(rootPath),
            RawAccessAvailable = false,
        };

        info.Tracks = toc?.Tracks ?? Array.Empty<TocTrack>();
        return Finish(info, rootPath, toc, bootMagic, null);
    }

    // ================================================================== 核心

    private static DiscInfo Finish(
        DiscInfo info,
        string rootPath,
        TocResult? toc,
        string? bootMagic,
        string? isoLabel)
    {
        if (toc is { Tracks.Count: > 0 })
        {
            info.Evidence.Add(new DiscEvidence(
                "toc",
                $"{toc.Tracks.Count} 条音轨：{string.Join(", ", toc.Tracks.Select(t => t.IsAudio ? "A" : "D"))}",
                90));
        }

        if (bootMagic != null)
        {
            info.Evidence.Add(new DiscEvidence("boot-sector", $"扇区 0 引导签名命中 {bootMagic}", 95));
        }

        if (isoLabel != null)
        {
            info.Evidence.Add(new DiscEvidence("iso9660-pvd", $"ISO9660 卷标识：{isoLabel}", 80));
        }

        info.RootEntries = ListRoot(rootPath);

        Classify(info, toc, bootMagic, info.RootEntries, rootPath);

        if (string.IsNullOrWhiteSpace(info.Title))
        {
            info.Title = !string.IsNullOrWhiteSpace(info.VolumeLabel) ? info.VolumeLabel
                       : !string.IsNullOrWhiteSpace(isoLabel) ? isoLabel!
                       : $"{info.DriveLetter}: 光盘";
        }

        return info;
    }

    private static void Classify(
        DiscInfo info,
        TocResult? toc,
        string? bootMagic,
        IReadOnlyList<string> root,
        string rootPath)
    {
        // 1) 纯音乐 CD —— 最高优先级
        if (toc is { IsAudioCd: true })
        {
            info.Kind = DiscKind.AudioCd;
            info.Title = $"音乐 CD（{toc.Tracks.Count} 首）";
            info.Evidence.Add(new DiscEvidence("toc-audio-only", "全部曲目均为音频轨 CDDA", 100));
            return;
        }

        // 2) 无裸设备时的音乐 CD 降级识别：根目录全是 .cda
        if (toc == null && root.Count > 0 && root.All(IsCdaFile))
        {
            info.Kind = DiscKind.AudioCd;
            info.Title = $"音乐 CD（{root.Count} 首）";
            info.Evidence.Add(new DiscEvidence("cda-fallback", "根目录仅含 TrackXX.cda", 70));
            return;
        }

        // 3) 引导扇区签名（世嘉系 / NEC 系）
        switch (bootMagic)
        {
            case "segacd":
                Set(info, DiscKind.SegaCdGame, "segacd", "引导扇区签名 SEGADISCSYSTEM", 95);
                return;
            case "saturn":
                Set(info, DiscKind.SaturnGame, "saturn", "引导扇区签名 SEGA SEGASATURN", 95);
                return;
            case "dreamcast":
                Set(info, DiscKind.DreamcastGame, "dreamcast", "引导扇区签名 SEGA SEGAKATANA", 95);
                return;
            case "pcengine":
                Set(info, DiscKind.PcEngineCdGame, "pcengine", "引导扇区签名 PC Engine CD-ROM SYSTEM", 95);
                return;
        }

        // 4) SYSTEM.CNF —— PS1 / PS2 的分水岭
        string? sysCnfPath = FindInRoot(rootPath, "SYSTEM.CNF", root);
        if (sysCnfPath != null)
        {
            string text = ReadText(sysCnfPath) ?? string.Empty;

            var m2 = Boot2Regex.Match(text);
            if (m2.Success)
            {
                string target = m2.Groups["path"].Value.Trim();
                info.Serial = ExtractSerial(target) ?? ExtractSerial(text);
                info.Title = CleanBootTarget(target);
                Set(info, DiscKind.Ps2Game, "ps2", $"SYSTEM.CNF 含 BOOT2 = {target}", 98);
                return;
            }

            var m1 = BootRegex.Match(text);
            if (m1.Success)
            {
                string target = m1.Groups["path"].Value.Trim();
                info.Serial = ExtractSerial(target) ?? ExtractSerial(text);
                info.Title = CleanBootTarget(target);
                Set(info, DiscKind.Ps1Game, "ps1", $"SYSTEM.CNF 含 BOOT = {target}", 98);
                return;
            }

            info.Evidence.Add(new DiscEvidence("system-cnf", "存在 SYSTEM.CNF 但无 BOOT/BOOT2 行", 40));
        }

        // 5) 视频盘
        //    DVD 的正常结构是 VIDEO_TS\VIDEO_TS.IFO（IFO 在目录里面，不在根上）；
        //    少数重制版把 IFO 直接放在根目录，两种都接受。
        string? videoTs = root.FirstOrDefault(e => EqName(e, "VIDEO_TS"));
        if (videoTs != null)
        {
            bool hasIfo = false;
            try
            {
                hasIfo = File.Exists(Path.Combine(videoTs, "VIDEO_TS.IFO"))
                      || File.Exists(Path.Combine(rootPath, "VIDEO_TS.IFO"));
            }
            catch { /* 光驱已弹出 */ }

            if (hasIfo)
            {
                Set(info, DiscKind.DvdVideo, "dvdvideo", "存在 VIDEO_TS\\VIDEO_TS.IFO", 90);
                return;
            }

            info.Evidence.Add(new DiscEvidence("video-ts", "存在 VIDEO_TS 目录但缺少 VIDEO_TS.IFO", 45));
        }

        string? bdmv = root.FirstOrDefault(e => EqName(e, "BDMV"));
        if (bdmv != null)
        {
            Set(info, DiscKind.BluRayVideo, "bluray", "存在 BDMV 目录", 90);
            return;
        }

        // 6) Dreamcast 次级指纹
        if (root.Any(e => EqName(e, "1ST_READ.BIN")))
        {
            Set(info, DiscKind.DreamcastGame, "dreamcast", "根目录含 1ST_READ.BIN", 85);
            return;
        }

        // 7) PC 盘
        string? autorun = FindInRoot(rootPath, "autorun.inf", root);

        if (autorun != null)
        {
            string text = ReadText(autorun) ?? string.Empty;
            bool autoLaunch = text.Contains("open=", StringComparison.OrdinalIgnoreCase)
                           || text.Contains("shellexecute=", StringComparison.OrdinalIgnoreCase);

            info.Evidence.Add(new DiscEvidence("autorun-inf",
                autoLaunch ? "autorun.inf 含自动运行指令" : "autorun.inf 存在但无自动运行指令",
                autoLaunch ? 85 : 50));

            if (autoLaunch)
            {
                string? target = ParseAutorunTarget(text);
                if (target != null) info.Title = Path.GetFileNameWithoutExtension(target);
                Set(info, DiscKind.PcGame, "pc", "autorun.inf 自动运行入口", 85);
                return;
            }
        }

        var exeNames = root
            .Where(e => e.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetFileNameWithoutExtension)
            .ToList();

        if (exeNames.Count > 0)
        {
            bool installer = exeNames.Any(n =>
                n!.Equals("setup", StringComparison.OrdinalIgnoreCase) ||
                n.Equals("install", StringComparison.OrdinalIgnoreCase));

            info.Title = exeNames[0]!;
            Set(info, installer ? DiscKind.PcSoftware : DiscKind.PcGame, "pc",
                $"根目录含可执行文件：{string.Join(", ", exeNames.Take(3))}", 75);
            return;
        }

        // 8) 混合模式音乐 CD（有数据轨但绝大多数是音轨）
        if (toc != null && toc.Tracks.Count(t => t.IsAudio) >= 2)
        {
            info.Kind = DiscKind.AudioCd;
            info.Title = $"增强型音乐 CD（{toc.Tracks.Count(t => t.IsAudio)} 首）";
            info.Evidence.Add(new DiscEvidence("enhanced-cd", "含数据轨但音频轨占多数", 60));
            return;
        }

        // 9) 兜底
        info.Kind = DiscKind.DataDisc;
        info.Evidence.Add(new DiscEvidence("fallback", "未命中任何平台特征", 10));
    }

    private static void Set(DiscInfo info, DiscKind kind, string platform, string detail, int weight)
    {
        info.Kind = kind;
        info.Platform = platform;
        info.Evidence.Add(new DiscEvidence(platform, detail, weight));
    }

    // ================================================================== 裸设备解析

    /// <summary>识别扇区 0 的引导签名。</summary>
    public static string? SniffBootMagic(byte[]? boot)
    {
        if (boot == null) return null;
        if (ContainsAscii(boot, "SEGADISCSYSTEM")) return "segacd";
        if (ContainsAscii(boot, "SEGA SEGASATURN")) return "saturn";
        if (ContainsAscii(boot, "SEGA SEGAKATANA")) return "dreamcast";
        if (ContainsAscii(boot, "PC Engine CD-ROM SYSTEM")) return "pcengine";
        return null;
    }

    /// <summary>从 ISO9660 主卷描述符里取出卷标识。</summary>
    public static string? ParsePvdLabel(byte[]? pvd)
    {
        if (pvd is not { Length: >= 128 }) return null;
        if (pvd[0] != 0x01 || !AsciiAt(pvd, 1, "CD001")) return null;

        string raw = Encoding.Latin1.GetString(pvd, 40, 32).TrimEnd(' ', '\0');
        return raw.Length > 0 ? raw : null;
    }

    // ================================================================== 工具

    private static bool IsCdaFile(string name) =>
        name.EndsWith(".cda", StringComparison.OrdinalIgnoreCase);

    private static bool EqName(string entry, string name) =>
        Path.GetFileName(entry).Equals(name, StringComparison.OrdinalIgnoreCase);

    private static string? FindInRoot(string rootPath, string fileName, IReadOnlyList<string> entries)
    {
        var hit = entries.FirstOrDefault(e => EqName(e, fileName));
        if (hit != null) return hit;

        try
        {
            string direct = Path.Combine(rootPath, fileName);
            if (File.Exists(direct)) return direct;
        }
        catch { /* 光驱可能已弹出 */ }

        return null;
    }

    private static List<string> ListRoot(string rootPath)
    {
        var list = new List<string>(256);
        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(rootPath))
            {
                list.Add(entry);
                if (list.Count >= 200) break;
            }
        }
        catch { /* 音频 CD / 未就绪 / 访问被拒 */ }
        return list;
    }

    private static string? ReadText(string path, int maxBytes = 8192)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            int len = (int)Math.Min(maxBytes, fs.Length);
            if (len <= 0) return string.Empty;
            byte[] buf = new byte[len];
            int read = fs.Read(buf, 0, len);
            return Encoding.Latin1.GetString(buf, 0, read);
        }
        catch
        {
            return null;
        }
    }

    private static string? ExtractSerial(string text)
    {
        var m = SerialRegex.Match(text);
        if (!m.Success) return null;
        return $"{m.Groups["pre"].Value}_{m.Groups["mid"].Value}.{m.Groups["rev"].Value}";
    }

    /// <summary>把 <c>cdrom0:\SLUS_200.62;1</c> 整理成可读标题。</summary>
    private static string CleanBootTarget(string target)
    {
        string t = target.Trim();
        int colon = t.IndexOf(':');
        if (colon >= 0) t = t[(colon + 1)..];
        t = t.TrimStart('\\', '/');
        int semi = t.IndexOf(';');
        if (semi >= 0) t = t[..semi];
        return t;
    }

    private static string? ParseAutorunTarget(string autorun)
    {
        foreach (var line in autorun.Split('\n'))
        {
            int eq = line.IndexOf('=');
            if (eq < 0) continue;
            string key = line[..eq].Trim();
            string val = line[(eq + 1)..].Trim();
            if (key.Equals("open", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("shellexecute", StringComparison.OrdinalIgnoreCase))
            {
                if (val.Length > 0) return val;
            }
        }
        return null;
    }

    private static bool ContainsAscii(byte[] buffer, string needle)
    {
        int n = needle.Length;
        if (n == 0 || buffer.Length < n) return false;
        for (int i = 0; i <= buffer.Length - n; i++)
        {
            int j = 0;
            while (j < n && buffer[i + j] == (byte)needle[j]) j++;
            if (j == n) return true;
        }
        return false;
    }

    private static bool AsciiAt(byte[] buffer, int offset, string needle)
    {
        if (buffer.Length < offset + needle.Length) return false;
        for (int i = 0; i < needle.Length; i++)
            if (buffer[offset + i] != (byte)needle[i]) return false;
        return true;
    }

    private static char InferLetter(string path)
    {
        string full = Path.GetFullPath(path);
        return full.Length >= 2 && full[1] == ':' ? char.ToUpperInvariant(full[0]) : '?';
    }

    private static long DirectorySize(string path)
    {
        try
        {
            return Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                .Sum(f => new FileInfo(f).Length);
        }
        catch { return -1; }
    }

    private static string SafeLabel(OpticalDrive drive)
    {
        try { return drive.GetVolumeLabel(); } catch { return string.Empty; }
    }

    private static string SafeFileSystem(OpticalDrive drive)
    {
        try { return drive.GetFileSystemName(); } catch { return string.Empty; }
    }
}
