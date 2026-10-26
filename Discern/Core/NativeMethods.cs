using System.Runtime.InteropServices;
using System.Text;

namespace Discern.Core;

/// <summary>
/// Win32 互操作声明。只包含光盘探测与热插拔监听需要的最小集合。
/// </summary>
internal static class NativeMethods
{
    // ---------------- 设备打开 ----------------

    public const uint GENERIC_READ = 0x80000000;
    public const uint GENERIC_WRITE = 0x40000000;
    public const uint FILE_SHARE_READ = 0x00000001;
    public const uint FILE_SHARE_WRITE = 0x00000002;
    public const uint OPEN_EXISTING = 3;
    public const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;
    public const uint FILE_FLAG_NO_BUFFERING = 0x20000000;
    public const uint FILE_FLAG_RANDOM_ACCESS = 0x10000000;
    public const uint FILE_BEGIN = 0;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeviceIoControl(
        IntPtr hDevice,
        uint dwIoControlCode,
        IntPtr lpInBuffer,
        uint nInBufferSize,
        IntPtr lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint SetFilePointer(
        IntPtr hFile,
        int lDistanceToMove,
        IntPtr lpDistanceToMoveHigh,
        uint dwMoveMethod);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetFilePointerEx(
        IntPtr hFile,
        long liDistanceToMove,
        out long lpNewFilePointer,
        uint dwMoveMethod);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ReadFile(
        IntPtr hFile,
        byte[] lpBuffer,
        uint nNumberOfBytesToRead,
        out uint lpNumberOfBytesRead,
        IntPtr lpOverlapped);

    // ---------------- 卷 / 驱动器 ----------------

    public const uint DRIVE_UNKNOWN = 0;
    public const uint DRIVE_NO_ROOT_DIR = 1;
    public const uint DRIVE_REMOVABLE = 2;
    public const uint DRIVE_FIXED = 3;
    public const uint DRIVE_REMOTE = 4;
    public const uint DRIVE_CDROM = 5;
    public const uint DRIVE_RAMDISK = 6;

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint GetDriveTypeW(string lpRootPathName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetVolumeInformationW(
        string lpRootPathName,
        StringBuilder? lpVolumeNameBuffer,
        int nVolumeNameSize,
        out uint lpVolumeSerialNumber,
        out uint lpMaximumComponentLength,
        out uint lpFileSystemFlags,
        StringBuilder? lpFileSystemNameBuffer,
        int nFileSystemNameSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint GetLogicalDrives();

    // ---------------- IOCTL 码 ----------------

    /// <summary>IOCTL_CDROM_READ_TOC — 读取目录区（TOC），判定音轨/数据轨。</summary>
    public const uint IOCTL_CDROM_READ_TOC = 0x00024000;

    /// <summary>IOCTL_CDROM_GET_CONFIGURATION</summary>
    public const uint IOCTL_CDROM_GET_CONFIGURATION = 0x00024048;

    /// <summary>IOCTL_STORAGE_CHECK_VERIFY — 检查介质是否存在。</summary>
    public const uint IOCTL_STORAGE_CHECK_VERIFY = 0x0002D480;

    /// <summary>IOCTL_STORAGE_MEDIA_REMOVAL</summary>
    public const uint IOCTL_STORAGE_MEDIA_REMOVAL = 0x0002D404;

    /// <summary>IOCTL_DISK_GET_DRIVE_GEOMETRY</summary>
    public const uint IOCTL_DISK_GET_DRIVE_GEOMETRY = 0x00070000;

    // ---------------- 设备通知 ----------------

    public const int WM_DEVICECHANGE = 0x0219;
    public const int DBT_DEVICEARRIVAL = 0x8000;
    public const int DBT_DEVICEREMOVECOMPLETE = 0x8004;
    public const int DBT_DEVTYP_OEM = 0x00000000;
    public const int DBT_DEVTYP_VOLUME = 0x00000002;
    public const int DBT_DEVTYP_PORT = 0x00000003;
    public const int DBT_DEVTYP_DEVICEINTERFACE = 0x00000005;

    public const int DBTF_MEDIA = 0x0001;
    public const int DBT_DEVNODES_CHANGED = 0x0007;

    /// <summary>GUID_DEVINTERFACE_CDROM</summary>
    public static readonly Guid GUID_DEVINTERFACE_CDROM =
        new("53F56308-B6BF-11D0-94F2-00A0C91EFB8B");

    [StructLayout(LayoutKind.Sequential)]
    public struct DEV_BROADCAST_HDR
    {
        public int dbch_size;
        public int dbch_devicetype;
        public int dbch_reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DEV_BROADCAST_VOLUME
    {
        public int dbcv_size;
        public int dbcv_devicetype;
        public int dbcv_reserved;
        public int dbcv_unitmask;
        public short dbcv_flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DEV_BROADCAST_DEVICEINTERFACE
    {
        public int dbcc_size;
        public int dbcc_devicetype;
        public int dbcc_reserved;
        public Guid dbcc_classguid;
        public short dbcc_name; // 变长 UTF-16 字符串的起点
    }

    public const int DEVICE_NOTIFY_WINDOW_HANDLE = 0x00000000;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr RegisterDeviceNotificationW(
        IntPtr hRecipient,
        IntPtr notificationFilter,
        int flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnregisterDeviceNotification(IntPtr handle);

    // ---------------- 控制台 ----------------

    [DllImport("kernel32.dll")]
    public static extern bool AttachConsole(int dwProcessId);

    public const int ATTACH_PARENT_PROCESS = -1;
}
