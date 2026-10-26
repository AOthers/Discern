using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Discern.Core;

/// <summary>单条 TOC 音轨信息。</summary>
public sealed class TocTrack
{
    public int Number { get; init; }

    /// <summary>TOC 控制位低 4 位。bit2 (0x04) 置位表示这是数据轨，清零表示音频轨。</summary>
    public int Control { get; init; }

    public int Adr { get; init; }

    /// <summary>起始 LBA（24 位大端解析后的值）。</summary>
    public int Address { get; init; }

    /// <summary>数据轨：CD-ROM / CD-ROM XA / Mode 1 / Mode 2。音频轨返回 false。</summary>
    public bool IsData => (Control & 0x04) != 0;

    /// <summary>音频轨判定。</summary>
    public bool IsAudio => !IsData;

    public override string ToString() =>
        $"Track{Number:00} {(IsAudio ? "AUDIO" : "DATA")} ctrl=0x{Control:X} lba={Address}";
}

/// <summary>TOC 读取结果。</summary>
public sealed class TocResult
{
    public int FirstTrack { get; init; }
    public int LastTrack { get; init; }
    public IReadOnlyList<TocTrack> Tracks { get; init; } = Array.Empty<TocTrack>();

    /// <summary>所有曲目都是音频轨 → 音乐 CD。</summary>
    public bool IsAudioCd => Tracks.Count > 0 && Tracks.All(t => t.IsAudio);

    /// <summary>存在至少一条数据轨 → 数据光盘（游戏盘 / 软件盘 / 视频盘）。</summary>
    public bool HasDataTrack => Tracks.Any(t => t.IsData);
}

/// <summary>
/// 一个逻辑光驱。封装裸设备访问：TOC 读取、扇区读取、介质存在性检测。
/// <para>
/// 裸设备访问（<c>\\.\D:</c>）在 Windows Vista 及以上需要管理员权限。
/// 构造时传 <c>allowRaw=false</c> 或运行时权限不足，所有裸操作会返回失败，
/// 由上层 <see cref="DiscProbe"/> 降级到文件系统启发式。
/// </para>
/// </summary>
public sealed class OpticalDrive : IDisposable
{
    /// <summary>ISO9660 / CD-ROM 单扇区字节数。</summary>
    public const int SectorSize = 2048;

    /// <summary>ISO9660 主卷描述符（PVD）固定位于第 16 扇区。</summary>
    public const int PvdLba = 16;

    private IntPtr _handle = IntPtr.Zero;
    private bool _opened;
    private bool _rawAvailable;
    private int _lastError;

    public char Letter { get; }
    public string RootPath { get; }
    public string DevicePath { get; }

    /// <summary>裸设备通道是否可用（打开成功过）。</summary>
    public bool RawAvailable => _rawAvailable;

    public int LastError => _lastError;

    public OpticalDrive(char letter, bool openRaw = true)
    {
        Letter = char.ToUpperInvariant(letter);
        RootPath = Letter + @":\";
        DevicePath = @"\\.\" + Letter + ":";

        if (openRaw)
            OpenRaw();
    }

    // ---------------------------------------------------------------- 设备

    private void OpenRaw()
    {
        _handle = NativeMethods.CreateFileW(
            DevicePath,
            NativeMethods.GENERIC_READ,
            NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
            IntPtr.Zero,
            NativeMethods.OPEN_EXISTING,
            NativeMethods.FILE_ATTRIBUTE_NORMAL,
            IntPtr.Zero);

        if (_handle == IntPtr.Zero || _handle == new IntPtr(-1))
        {
            _lastError = Marshal.GetLastWin32Error();
            _handle = IntPtr.Zero;
            _opened = false;
            _rawAvailable = false;
            return;
        }

        _opened = true;
        _rawAvailable = true;
    }

    /// <summary>介质是否就绪。优先用 IOCTL_STORAGE_CHECK_VERIFY，失败再退回 DriveInfo。</summary>
    public bool IsMediaPresent()
    {
        if (_opened)
        {
            // 复用已打开的句柄探测介质
            if (NativeMethods.DeviceIoControl(
                    _handle, NativeMethods.IOCTL_STORAGE_CHECK_VERIFY,
                    IntPtr.Zero, 0, IntPtr.Zero, 0,
                    out _, IntPtr.Zero))
                return true;

            int err = Marshal.GetLastWin32Error();
            // 21 = ERROR_NOT_READY（无盘），1167 = ERROR_DEVICE_NOT_CONNECTED
            if (err == 21 || err == 1167 || err == 1117 /*ERROR_IO_DEVICE*/)
                return false;
            // 其它错误继续走 DriveInfo 兜底
        }

        try
        {
            var di = new DriveInfo(RootPath);
            return di.IsReady;
        }
        catch
        {
            return false;
        }
    }

    // ---------------------------------------------------------------- TOC

    /// <summary>
    /// 读取光盘目录区（TOC）。这是区分「音乐 CD」与「数据盘」最权威的方式：
    /// 控制位 bit2 为 0 的是音频轨，为 1 的是数据轨。
    /// </summary>
    public TocResult? ReadToc()
    {
        if (!_opened) return null;

        // CDROM_TOC: Length(2) + FirstTrack(1) + LastTrack(1) + TrackData(100*8)
        const int bufferSize = 4 + 100 * 8;
        IntPtr buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            // Length 字段需要预先填充期望大小（部分驱动依赖此约定）
            Marshal.WriteInt16(buffer, 0, (short)(bufferSize - 2));

            if (!NativeMethods.DeviceIoControl(
                    _handle, NativeMethods.IOCTL_CDROM_READ_TOC,
                    IntPtr.Zero, 0, buffer, (uint)bufferSize,
                    out uint returned, IntPtr.Zero) || returned < 4)
            {
                _lastError = Marshal.GetLastWin32Error();
                return null;
            }

            byte[] data = new byte[returned];
            Marshal.Copy(buffer, data, 0, (int)returned);
            return ParseToc(data, (int)returned);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static TocResult ParseToc(byte[] data, int length)
    {
        // Length 为大端 16 位，表示其后有效字节数
        int declared = (data[0] << 8) | data[1];
        int firstTrack = data[2];
        int lastTrack = data[3];

        var tracks = new List<TocTrack>();
        int p = 4;

        // 100 个 TOC 条目；首条通常是 Track 0x01，末条是 0xAA（Lead-Out）
        while (p + 8 <= length && tracks.Count < 100)
        {
            int control = data[p + 1] & 0x0F;
            int adr = (data[p + 1] >> 4) & 0x0F;
            int trackNo = data[p + 2];
            int address = (data[p + 4] << 24) | (data[p + 5] << 16) | (data[p + 6] << 8) | data[p + 7];

            // 0xAA 是 Lead-Out，不是曲目
            if (trackNo != 0xAA && trackNo >= 1)
            {
                tracks.Add(new TocTrack
                {
                    Number = trackNo,
                    Control = control,
                    Adr = adr,
                    Address = address
                });
            }

            p += 8;

            if (trackNo == 0xAA) break;
        }

        if (firstTrack == 0) firstTrack = tracks.Count > 0 ? tracks[0].Number : 0;
        if (lastTrack == 0) lastTrack = tracks.Count > 0 ? tracks[^1].Number : 0;

        return new TocResult
        {
            FirstTrack = firstTrack,
            LastTrack = lastTrack,
            Tracks = tracks
        };
    }

    // ---------------------------------------------------------------- 扇区

    /// <summary>
    /// 读取裸扇区。失败（无盘 / 无权限 / 超出范围）返回 null。
    /// </summary>
    public byte[]? ReadSectors(int lba, int count = 1)
    {
        if (!_opened || lba < 0 || count <= 0) return null;

        long offset = (long)lba * SectorSize;
        if (!NativeMethods.SetFilePointerEx(_handle, offset, out _, NativeMethods.FILE_BEGIN))
        {
            _lastError = Marshal.GetLastWin32Error();
            return null;
        }

        int total = count * SectorSize;
        byte[] buffer = new byte[total];

        if (!NativeMethods.ReadFile(_handle, buffer, (uint)total, out uint read, IntPtr.Zero))
        {
            _lastError = Marshal.GetLastWin32Error();
            return null;
        }

        if (read == 0) return null;
        if (read < total) Array.Resize(ref buffer, (int)read);
        return buffer;
    }

    /// <summary>读取 ISO9660 主卷描述符所在扇区（第 16 扇区）。</summary>
    public byte[]? ReadPrimaryVolumeDescriptor() => ReadSectors(PvdLba, 1);

    // ---------------------------------------------------------------- 卷信息

    /// <summary>卷标（用于给用户显示，也参与指纹）。</summary>
    public string GetVolumeLabel()
    {
        var sb = new StringBuilder(261);
        if (NativeMethods.GetVolumeInformationW(
                RootPath, sb, sb.Capacity, out _, out _, out _, null, 0))
            return sb.ToString();
        return string.Empty;
    }

    /// <summary>文件系统名称：CDFS / UDF / ISO9660 / 空（音频 CD 通常为空）。</summary>
    public string GetFileSystemName()
    {
        var sb = new StringBuilder(261);
        if (NativeMethods.GetVolumeInformationW(
                RootPath, null, 0, out _, out _, out _, sb, sb.Capacity))
            return sb.ToString();
        return string.Empty;
    }

    /// <summary>卷总容量（字节），失败返回 -1。</summary>
    public long GetTotalSize()
    {
        try
        {
            var di = new DriveInfo(RootPath);
            return di.IsReady ? di.TotalSize : -1;
        }
        catch
        {
            return -1;
        }
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero)
        {
            NativeMethods.CloseHandle(_handle);
            _handle = IntPtr.Zero;
        }
        _opened = false;
        _rawAvailable = false;
    }
}
