using System.Text;
using System.Text.Json;

namespace IsoForge;

/// <summary>
/// 极简 ISO9660（Level 1，无 Joliet）镜像生成器，仅用于生成测试光盘镜像。
///
/// 用法： IsoForge spec.json out.iso
///
/// spec.json:
/// {
///   "label": "PS2TEST",
///   "bootMagic": "SEGA SEGAKATANA",   // 可选：写入扇区 0 起始处的 ASCII 魔数
///   "entries": [
///     { "path": "SYSTEM.CNF", "content": "BOOT2 = cdrom0:\\SLUS_200.62;1\r\n" },
///     { "path": "VIDEO_TS/VIDEO_TS.IFO", "content": "dummy" }
///   ]
/// }
/// </summary>
internal static class Program
{
    private const int SectorSize = 2048;

    private sealed class Spec
    {
        public string Label { get; set; } = "TESTDISC";
        public string? BootMagic { get; set; }
        public List<SpecEntry> Entries { get; set; } = new();
    }

    private sealed class SpecEntry
    {
        public string Path { get; set; } = "";
        public string Content { get; set; } = "";
    }

    private sealed class DirNode
    {
        public string Name = "";
        public string FullPath = "";
        public DirNode? Parent;
        public readonly List<DirNode> Dirs = new();
        public readonly List<FileNode> Files = new();
        public int Lba;
        public int Number;     // path table 序号，根为 1
        public int ParentNumber = 1;

        public string IsoName => Name.ToUpperInvariant();
    }

    private sealed class FileNode
    {
        public string Name = "";
        public byte[] Data = Array.Empty<byte>();
        public int Lba;
    }

    private static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("用法: IsoForge <spec.json> <out.iso>         生成镜像");
            Console.Error.WriteLine("      IsoForge <spec.json> <outdir> --tree  展开为目录树");
            return 2;
        }

        var json = JsonSerializer.Deserialize<Spec>(
            File.ReadAllText(args[0]),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        if (json == null)
        {
            Console.Error.WriteLine("spec 解析失败");
            return 2;
        }

        if (args.Any(a => a.Equals("--tree", StringComparison.OrdinalIgnoreCase)))
        {
            MaterializeTree(json, args[1]);
            Console.WriteLine($"已展开目录树 {args[1]}  (label={json.Label})");
            return 0;
        }

        byte[] image = Build(json);
        File.WriteAllBytes(args[1], image);

        Console.WriteLine($"已生成 {args[1]}  ({image.Length / SectorSize} 扇区, {image.Length / 1024.0:F1} KB)");
        return 0;
    }

    /// <summary>把 spec 展开成真实目录，便于在无光驱环境下验证文件层识别规则。</summary>
    private static void MaterializeTree(Spec spec, string outDir)
    {
        Directory.CreateDirectory(outDir);

        foreach (var e in spec.Entries)
        {
            string rel = e.Path.Replace('\\', '/');
            string full = Path.Combine(outDir, rel.Replace('/', '\\'));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, Encoding.Latin1.GetBytes(e.Content));
        }
    }

    // ------------------------------------------------------------------ 构建

    private static byte[] Build(Spec spec)
    {
        // 1) 建树
        var root = new DirNode { Name = "", FullPath = "" };

        foreach (var e in spec.Entries)
        {
            var parts = e.Path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) continue;

            var cur = root;
            for (int i = 0; i < parts.Length - 1; i++)
            {
                var child = cur.Dirs.FirstOrDefault(d =>
                    d.Name.Equals(parts[i], StringComparison.OrdinalIgnoreCase));
                if (child == null)
                {
                    child = new DirNode
                    {
                        Name = parts[i],
                        FullPath = cur.FullPath.Length == 0 ? parts[i] : cur.FullPath + "/" + parts[i],
                        Parent = cur,
                    };
                    cur.Dirs.Add(child);
                }
                cur = child;
            }

            cur.Files.Add(new FileNode
            {
                Name = parts[^1].ToUpperInvariant(),
                Data = Encoding.Latin1.GetBytes(e.Content),
            });
        }

        // 2) 目录编号（BFS，根为 1）
        var allDirs = new List<DirNode>();
        var queue = new Queue<DirNode>();
        queue.Enqueue(root);
        int number = 0;
        while (queue.Count > 0)
        {
            var d = queue.Dequeue();
            d.Number = ++number;
            d.ParentNumber = d.Parent?.Number ?? 1;
            allDirs.Add(d);
            foreach (var sub in d.Dirs.OrderBy(x => x.IsoName, StringComparer.Ordinal))
                queue.Enqueue(sub);
        }

        // 3) 布局：16 PVD / 17 终止符 / 18 类型L路径表 / 19 类型M路径表 / 20+ 目录 / 之后文件
        const int pvdSector = 16;
        const int termSector = 17;
        const int lPathSector = 18;
        const int mPathSector = 19;
        int next = 20;

        foreach (var d in allDirs) d.Lba = next++;

        foreach (var d in allDirs)
        {
            foreach (var f in d.Files.OrderBy(x => x.Name, StringComparer.Ordinal))
            {
                f.Lba = next;
                next += Math.Max(1, (f.Data.Length + SectorSize - 1) / SectorSize);
            }
        }

        int totalSectors = next;
        byte[] image = new byte[totalSectors * SectorSize];

        // 4) 系统区（含可选引导魔数）
        if (!string.IsNullOrEmpty(spec.BootMagic))
        {
            byte[] magic = Encoding.ASCII.GetBytes(spec.BootMagic);
            Array.Copy(magic, 0, image, 0, Math.Min(magic.Length, SectorSize));
        }

        // 5) 路径表
        byte[] lPath = BuildPathTable(allDirs, littleEndian: true, out int pathSize);
        byte[] mPath = BuildPathTable(allDirs, littleEndian: false, out _);
        Array.Copy(lPath, 0, image, lPathSector * SectorSize, lPath.Length);
        Array.Copy(mPath, 0, image, mPathSector * SectorSize, mPath.Length);

        // 6) 目录数据
        foreach (var d in allDirs)
        {
            byte[] data = BuildDirectory(d);
            if (data.Length > SectorSize)
                throw new InvalidOperationException($"目录 {d.FullPath} 记录超过一个扇区，测试镜像请减少条目");
            Array.Copy(data, 0, image, d.Lba * SectorSize, data.Length);
        }

        // 7) 文件数据
        foreach (var d in allDirs)
        {
            foreach (var f in d.Files)
            {
                if (f.Data.Length > 0)
                    Array.Copy(f.Data, 0, image, f.Lba * SectorSize, f.Data.Length);
            }
        }

        // 8) PVD
        byte[] pvd = new byte[SectorSize];
        pvd[0] = 0x01;
        Encoding.ASCII.GetBytes("CD001").CopyTo(pvd, 1);
        pvd[6] = 0x01;

        WritePadded(pvd, 8, 32, "DISCERN TEST");           // system identifier
        WritePadded(pvd, 40, 32, spec.Label.ToUpperInvariant()); // volume identifier

        WriteBothEndian32(pvd, 80, (uint)totalSectors);         // volume space size
        WriteBothEndian16(pvd, 120, 1);                         // volume set size
        WriteBothEndian16(pvd, 124, 1);                         // volume sequence number
        WriteBothEndian16(pvd, 128, SectorSize);                // logical block size
        WriteBothEndian32(pvd, 132, (uint)pathSize);            // path table size
        WriteLe32(pvd, 140, (uint)lPathSector);                 // L path table
        WriteLe32(pvd, 144, 0);
        WriteBe32(pvd, 148, (uint)mPathSector);                 // M path table
        WriteBe32(pvd, 152, 0);

        // 根目录记录
        byte[] rootRec = DirectoryRecord(root, isRoot: true, isDir: true);
        Array.Copy(rootRec, 0, pvd, 156, rootRec.Length);

        WritePadded(pvd, 190, 128, "DISCERN");
        WritePadded(pvd, 318, 128, "DISCERN");
        WritePadded(pvd, 446, 128, "");
        WritePadded(pvd, 574, 128, "ISO FORGE TEST");

        WriteRecordingDate(pvd, 813);
        WriteRecordingDate(pvd, 830);
        WritePadded(pvd, 847, 17, "");
        WritePadded(pvd, 864, 17, "");
        pvd[881] = 0x01;

        Array.Copy(pvd, 0, image, pvdSector * SectorSize, SectorSize);

        // 9) 终止符
        byte[] term = new byte[SectorSize];
        term[0] = 0xFF;
        Encoding.ASCII.GetBytes("CD001").CopyTo(term, 1);
        term[6] = 0x01;
        Array.Copy(term, 0, image, termSector * SectorSize, SectorSize);

        return image;
    }

    // ------------------------------------------------------------------ 目录

    private static byte[] BuildDirectory(DirNode dir)
    {
        var buf = new MemoryStream();
        var writer = new BinaryWriter(buf);

        // "." 与 ".."
        writer.Write(DirectoryRecord(dir, isRoot: true, isDir: true));
        var parent = dir.Parent ?? dir;
        writer.Write(DirectoryRecord(parent, isRoot: true, isDir: true, forceDotDot: true));

        // 子目录 + 文件，按名字排序（ISO9660 要求）
        var items = new List<(string Name, byte[] Rec)>();
        foreach (var sub in dir.Dirs)
            items.Add((sub.IsoName, DirectoryRecord(sub, isRoot: false, isDir: true)));
        foreach (var f in dir.Files)
            items.Add((f.Name + ";1", DirectoryRecord(null, isRoot: false, isDir: false, file: f)));

        foreach (var (_, rec) in items.OrderBy(x => x.Name, StringComparer.Ordinal))
            writer.Write(rec);

        writer.Flush();
        byte[] data = buf.ToArray();

        // 记录不得跨越扇区边界：测试镜像条目少，直接放到下一个扇区
        if (data.Length > SectorSize)
            throw new InvalidOperationException($"目录 {dir.FullPath} 过大");

        return data;
    }

    private static byte[] DirectoryRecord(
        DirNode? dir, bool isRoot, bool isDir, FileNode? file = null, bool forceDotDot = false)
    {
        string identifier;
        if (isDir)
        {
            identifier = isRoot ? (forceDotDot ? "\u0001" : "\u0000") : dir!.IsoName;
        }
        else
        {
            identifier = file!.Name + ";1";
        }

        byte[] idBytes = isDir || isRoot
            ? Encoding.Latin1.GetBytes(identifier)
            : Encoding.ASCII.GetBytes(identifier);

        int lba = isDir ? dir!.Lba : file!.Lba;
        int length = isDir ? SectorSize : file!.Data.Length;

        int recLen = 33 + idBytes.Length;
        if (recLen % 2 != 0) recLen++;

        byte[] rec = new byte[recLen];
        rec[0] = (byte)recLen;
        rec[1] = 0;

        WriteBothEndian32(rec, 2, (uint)lba);
        WriteBothEndian32(rec, 10, (uint)length);

        WriteRecordingDate(rec, 18);

        rec[25] = (byte)(isDir ? 0x02 : 0x00);
        rec[26] = 0;
        rec[27] = 0;
        WriteBothEndian16(rec, 28, 1);

        rec[32] = (byte)idBytes.Length;
        Array.Copy(idBytes, 0, rec, 33, idBytes.Length);

        return rec;
    }

    // ------------------------------------------------------------------ 路径表

    private static byte[] BuildPathTable(List<DirNode> dirs, bool littleEndian, out int size)
    {
        using var ms = new MemoryStream();

        foreach (var d in dirs)
        {
            string id = d.Parent == null ? "\u0000" : d.IsoName;
            byte[] idBytes = Encoding.Latin1.GetBytes(id);

            ms.WriteByte((byte)idBytes.Length);
            ms.WriteByte(0);

            if (littleEndian)
            {
                ms.Write(BitConverter.GetBytes((uint)d.Lba), 0, 4);
                ms.Write(BitConverter.GetBytes((ushort)d.ParentNumber), 0, 2);
            }
            else
            {
                ms.Write(new[] { (byte)(d.Lba >> 24), (byte)(d.Lba >> 16), (byte)(d.Lba >> 8), (byte)d.Lba }, 0, 4);
                ms.Write(new[] { (byte)(d.ParentNumber >> 8), (byte)d.ParentNumber }, 0, 2);
            }

            ms.Write(idBytes, 0, idBytes.Length);

            if ((8 + idBytes.Length) % 2 != 0) ms.WriteByte(0);
        }

        size = (int)ms.Length;
        return ms.ToArray();
    }

    // ------------------------------------------------------------------ 编码工具

    private static void WritePadded(byte[] buf, int offset, int length, string value)
    {
        for (int i = 0; i < length; i++)
            buf[offset + i] = 0x20;
        byte[] b = Encoding.ASCII.GetBytes(value);
        Array.Copy(b, 0, buf, offset, Math.Min(b.Length, length));
    }

    private static void WriteBothEndian16(byte[] buf, int offset, ushort value)
    {
        buf[offset] = (byte)value;
        buf[offset + 1] = (byte)(value >> 8);
        buf[offset + 2] = (byte)(value >> 8);
        buf[offset + 3] = (byte)value;
    }

    private static void WriteBothEndian32(byte[] buf, int offset, uint value)
    {
        WriteLe32(buf, offset, value);
        WriteBe32(buf, offset + 4, value);
    }

    private static void WriteLe32(byte[] buf, int offset, uint value)
    {
        buf[offset] = (byte)value;
        buf[offset + 1] = (byte)(value >> 8);
        buf[offset + 2] = (byte)(value >> 16);
        buf[offset + 3] = (byte)(value >> 24);
    }

    private static void WriteBe32(byte[] buf, int offset, uint value)
    {
        buf[offset] = (byte)(value >> 24);
        buf[offset + 1] = (byte)(value >> 16);
        buf[offset + 2] = (byte)(value >> 8);
        buf[offset + 3] = (byte)value;
    }

    private static void WriteRecordingDate(byte[] buf, int offset)
    {
        var now = DateTime.Now;
        buf[offset] = (byte)(now.Year - 1900);
        buf[offset + 1] = (byte)now.Month;
        buf[offset + 2] = (byte)now.Day;
        buf[offset + 3] = (byte)now.Hour;
        buf[offset + 4] = (byte)now.Minute;
        buf[offset + 5] = (byte)now.Second;
        buf[offset + 6] = 0;
    }
}
