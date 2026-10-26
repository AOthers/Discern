using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Discern.Core;

public sealed record DriveChange(char Letter, bool Present);

/// <summary>
/// 光驱热插拔监听：用一个隐藏的消息窗口接 WM_DEVICECHANGE，
/// 再对「所有 CD-ROM 盘符是否就绪」做差分，从而得出插入 / 弹出事件。
/// <para>
/// 同时注册两种通知：<c>DBT_DEVTYP_VOLUME</c>（拿盘符最快）与
/// <c>GUID_DEVINTERFACE_CDROM</c>（盘符未挂载也能收到设备到达）。
/// </para>
/// </summary>
public sealed class DriveWatcher : IDisposable
{
    private HwndSource? _source;
    private IntPtr _notifyVolume;
    private IntPtr _notifyInterface;
    private System.Threading.Timer? _debounce;
    private readonly Dictionary<char, bool> _state = new();
    private readonly object _gate = new();
    private bool _disposed;

    /// <summary>插盘 / 弹出通知。</summary>
    public event Action<DriveChange>? Changed;

    /// <summary>插入后等待多久再探测，等文件系统挂载完成。</summary>
    public int SettleDelayMs { get; set; } = 1200;

    public void Start()
    {
        var p = new HwndSourceParameters("DiscernDeviceWatcher")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,                   // 不可见
            ExtendedWindowStyle = 0,
            ParentWindow = new IntPtr(-3),     // HWND_MESSAGE：消息专用窗口
        };

        _source = new HwndSource(p);
        _source.AddHook(WndProc);

        RegisterNotifications(_source.Handle);

        _debounce = new System.Threading.Timer(_ => Rescan(), null,
            Timeout.Infinite, Timeout.Infinite);

        // 建立初始状态，避免把「启动时已在盘」误判成插入
        Rescan(raiseEvents: false);
        Log.Info("设备监听已启动");
    }

    private void RegisterNotifications(IntPtr hwnd)
    {
        // 1) 卷到达 / 移除
        try
        {
            int size = Marshal.SizeOf<NativeMethods.DEV_BROADCAST_VOLUME>();
            IntPtr buf = Marshal.AllocHGlobal(size);
            try
            {
                var v = new NativeMethods.DEV_BROADCAST_VOLUME
                {
                    dbcv_size = size,
                    dbcv_devicetype = NativeMethods.DBT_DEVTYP_VOLUME,
                    dbcv_reserved = 0,
                    dbcv_unitmask = 0,
                    dbcv_flags = 0,
                };
                Marshal.StructureToPtr(v, buf, false);
                _notifyVolume = NativeMethods.RegisterDeviceNotificationW(
                    hwnd, buf, NativeMethods.DEVICE_NOTIFY_WINDOW_HANDLE);
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"注册卷通知失败：{ex.Message}");
        }

        // 2) CD-ROM 设备接口到达
        try
        {
            int size = Marshal.SizeOf<NativeMethods.DEV_BROADCAST_DEVICEINTERFACE>() - 2 + 2;
            IntPtr buf = Marshal.AllocHGlobal(size);
            try
            {
                var d = new NativeMethods.DEV_BROADCAST_DEVICEINTERFACE
                {
                    dbcc_size = size,
                    dbcc_devicetype = NativeMethods.DBT_DEVTYP_DEVICEINTERFACE,
                    dbcc_reserved = 0,
                    dbcc_classguid = NativeMethods.GUID_DEVINTERFACE_CDROM,
                    dbcc_name = 0,
                };
                Marshal.StructureToPtr(d, buf, false);
                _notifyInterface = NativeMethods.RegisterDeviceNotificationW(
                    hwnd, buf, NativeMethods.DEVICE_NOTIFY_WINDOW_HANDLE);
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"注册光驱接口通知失败：{ex.Message}");
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != NativeMethods.WM_DEVICECHANGE) return IntPtr.Zero;

        int evt = wParam.ToInt32();
        if (evt is NativeMethods.DBT_DEVICEARRIVAL
                or NativeMethods.DBT_DEVICEREMOVECOMPLETE
                or NativeMethods.DBT_DEVNODES_CHANGED)
        {
            Signal();
        }

        return IntPtr.Zero;
    }

    private void Signal()
    {
        if (_disposed) return;
        try
        {
            _debounce?.Change(Math.Max(200, SettleDelayMs), Timeout.Infinite);
        }
        catch (ObjectDisposedException) { }
    }

    /// <summary>立即重扫。</summary>
    public void Refresh() => Rescan();

    private void Rescan(bool raiseEvents = true)
    {
        if (_disposed) return;

        var current = new Dictionary<char, bool>();

        foreach (var letter in EnumerateCdRomLetters())
            current[letter] = IsReadyWithRetry(letter);

        List<DriveChange> events = new();

        lock (_gate)
        {
            foreach (var (letter, present) in current)
            {
                bool was = _state.TryGetValue(letter, out bool prev) && prev;
                if (present && !was) events.Add(new DriveChange(letter, true));
            }

            foreach (var (letter, was) in _state)
            {
                bool now = current.TryGetValue(letter, out bool p) && p;
                if (was && !now) events.Add(new DriveChange(letter, false));
            }

            _state.Clear();
            foreach (var kv in current) _state[kv.Key] = kv.Value;
        }

        if (!raiseEvents) return;

        foreach (var e in events)
        {
            try { Changed?.Invoke(e); }
            catch (Exception ex) { Log.Error($"变更处理异常：{ex.Message}"); }
        }
    }

    private static IEnumerable<char> EnumerateCdRomLetters()
    {
        foreach (var d in DriveInfo.GetDrives())
        {
            if (d.DriveType != DriveType.CDRom) continue;
            yield return char.ToUpperInvariant(d.Name[0]);
        }
    }

    private static bool IsReadyWithRetry(char letter, int attempts = 4)
    {
        string root = letter + @":\";
        for (int i = 0; i < attempts; i++)
        {
            try
            {
                var di = new DriveInfo(root);
                if (di.IsReady) return true;
            }
            catch { /* 光驱可能在切换介质 */ }

            if (i < attempts - 1) Thread.Sleep(250);
        }
        return false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { _debounce?.Dispose(); } catch { }
        _debounce = null;

        if (_notifyVolume != IntPtr.Zero)
        {
            NativeMethods.UnregisterDeviceNotification(_notifyVolume);
            _notifyVolume = IntPtr.Zero;
        }

        if (_notifyInterface != IntPtr.Zero)
        {
            NativeMethods.UnregisterDeviceNotification(_notifyInterface);
            _notifyInterface = IntPtr.Zero;
        }

        if (_source != null)
        {
            _source.RemoveHook(WndProc);
            _source.Dispose();
            _source = null;
        }

        Log.Info("设备监听已停止");
    }
}
