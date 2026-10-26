using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Discern.Core;
using WinForms = System.Windows.Forms;

namespace Discern;

/// <summary>左侧光驱列表的一行。</summary>
public sealed class DriveRow
{
    public char Letter { get; init; }
    public DiscInfo? Info { get; init; }
    public string Headline { get; init; } = "";
    public string Subline { get; init; } = "";
    public Brush Accent { get; init; } = Brushes.Gray;

    public override string ToString() => $"{Letter}: {Headline}";
}

public partial class MainWindow : Window
{
    private readonly ObservableCollection<DriveRow> _rows = new();
    private readonly DriveWatcher _watcher = new();
    private AppConfig _config;

    /// <summary>防止同一次插入被重复启动（盘符 → 内容指纹 + 时间）。</summary>
    private readonly Dictionary<char, (string Key, DateTime At)> _lastLaunch = new();

    private DiscInfo? _current;

    private WinForms.NotifyIcon? _tray;

    /// <summary>true 表示走的是「真正退出」，否则关窗只是隐藏到托盘。</summary>
    private bool _exiting;

    /// <summary>程序化修改 ChkAutoStart 时置位，避免事件重入。</summary>
    private bool _autoStartUpdating;

    /// <summary>重扫重入保护：定时器、托盘菜单、按钮都会触发。</summary>
    private bool _refreshing;

    public MainWindow(bool startHidden = false)
    {
        InitializeComponent();

        _config = AppConfig.LoadOrCreate(App.ConfigPath);
        ChkAuto.IsChecked = _config.AutoLaunch;

        // 自启项记的是旧路径时（换了目录或重新编译过）静默修正
        AutoStart.RepairIfStale();
        ChkAutoStart.IsChecked = AutoStart.IsEnabled();

        DriveList.ItemsSource = _rows;

        TxtElevation.Text = App.IsElevated
            ? "管理员模式 · 可读裸扇区"
            : "普通权限 · 识别为降级模式";

        Log.LineWritten += OnLogLine;
        foreach (var line in Log.Snapshot()) AppendLog(line);

        _watcher.SettleDelayMs = _config.SettleDelayMs;
        _watcher.Changed += OnDriveChanged;
        _watcher.Start();

        SetupTray();

        // 不依赖窗口 Loaded：常驻托盘时窗口从不显示，也要先扫一遍
        Dispatcher.BeginInvoke(new Action(() =>
        {
            RefreshDrives(autoLaunch: false);

            if (startHidden)
                Log.Info("[托盘] 已在托盘中常驻，插入光盘会自动识别");
        }));

        Closing += OnClosing;
    }

    // ------------------------------------------------------------------ 事件

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_exiting)
        {
            _tray?.Dispose();
            _watcher.Dispose();
            return;
        }

        // 默认不退出：隐藏到托盘，继续监听插盘
        e.Cancel = true;
        Hide();
        _tray?.ShowBalloonTip(1500, "Discern 仍在运行",
            "窗口已隐藏到托盘，插入光盘会自动识别。右键托盘图标可退出。",
            WinForms.ToolTipIcon.Info);
    }

    private void OnDriveChanged(DriveChange change)
    {
        Dispatcher.Invoke(() =>
        {
            Log.Info(change.Present
                ? $"{change.Letter}: 检测到光盘插入"
                : $"{change.Letter}: 光盘已弹出");

            RefreshDrives(autoLaunch: change.Present);
        });
    }

    private void OnLogLine(string line) =>
        Dispatcher.Invoke(() => AppendLog(line));

    private void AppendLog(string line)
    {
        LogBox.AppendText(line + Environment.NewLine);
        LogBox.ScrollToEnd();
    }

    // ------------------------------------------------------------------ 扫描

    /// <summary>
    /// 重扫光驱。探测本身（裸扇区、TOC、目录枚举）是阻塞 I/O，
    /// 一张划伤或转速不稳的盘能让单次探测拖到几秒，
    /// 所以丢到线程池里跑，别把 UI 卡住。
    /// </summary>
    private async void RefreshDrives(bool autoLaunch)
    {
        // 定时器、托盘菜单、按钮都可能触发，重叠执行会互相覆盖 _rows
        if (_refreshing) return;
        _refreshing = true;

        try
        {
            var letters = DriveInfo.GetDrives()
                .Where(d => d.DriveType == DriveType.CDRom)
                .Select(d => char.ToUpperInvariant(d.Name[0]))
                .OrderBy(c => c)
                .ToList();

            if (letters.Count == 0)
            {
                _rows.Clear();
                TxtStatus.Text = "未检测到光驱设备";
                _current = null;
                ShowEmptyDetail("本机没有检测到 CD/DVD 光驱。");
                return;
            }

            // 弹出后清理
            for (int i = _rows.Count - 1; i >= 0; i--)
            {
                if (!letters.Contains(_rows[i].Letter)) _rows.RemoveAt(i);
            }

            TxtStatus.Text = "正在读取光驱…";
            bool elevated = App.IsElevated;

            var probed = await Task.Run(() => letters
                .Select(letter =>
                {
                    using var drive = new OpticalDrive(letter, openRaw: elevated);
                    return DiscProbe.Probe(drive);
                })
                .ToList());

            // await 之后已回到 UI 线程，可以安全改 _rows
            foreach (var info in probed)
            {
                char letter = info.DriveLetter;
                var row = BuildRow(letter, info);

                int existing = IndexOfLetter(letter);
                if (existing >= 0) _rows[existing] = row;
                else _rows.Add(row);

                if (autoLaunch && _config.AutoLaunch && info.IsActionable && !IsDuplicated(letter, info))
                {
                    Log.Info($"{letter}: 识别为 {info.KindDisplay}，准备自动启动");
                    Launch(info, fromAuto: true);
                }
            }

            // 保持选中项
            if (_current != null)
            {
                int idx = IndexOfLetter(_current.DriveLetter);
                if (idx >= 0) DriveList.SelectedIndex = idx;
            }
            else if (_rows.Count > 0)
            {
                DriveList.SelectedIndex = 0;
            }

            int ready = _rows.Count(r => r.Info is { Kind: not DiscKind.Empty });
            TxtStatus.Text = ready == 0
                ? $"{letters.Count} 个光驱，均无盘"
                : $"{letters.Count} 个光驱 · {ready} 张光盘已就绪";
        }
        catch (Exception ex)
        {
            Log.Error($"[扫描失败] {ex.Message}");
            TxtStatus.Text = $"扫描失败：{ex.Message}";
        }
        finally
        {
            _refreshing = false;
        }
    }

    private int IndexOfLetter(char letter)
    {
        for (int i = 0; i < _rows.Count; i++)
            if (_rows[i].Letter == letter) return i;
        return -1;
    }

    private static DriveRow BuildRow(char letter, DiscInfo info)
    {
        string headline, subline;
        Brush accent;

        if (info.Kind == DiscKind.Empty)
        {
            headline = "（无盘）";
            subline = "光驱空闲";
            accent = new SolidColorBrush(Color.FromRgb(0x60, 0x6C, 0x76));
        }
        else
        {
            headline = $"{letter}: · {info.KindDisplay}";
            subline = string.IsNullOrWhiteSpace(info.Serial)
                ? info.Title
                : $"{info.Title}  ·  {info.Serial}";
            accent = KindColor(info.Kind);
        }

        return new DriveRow
        {
            Letter = letter,
            Info = info,
            Headline = headline,
            Subline = subline,
            Accent = accent,
        };
    }

    private static Brush KindColor(DiscKind kind) => kind switch
    {
        DiscKind.AudioCd => new SolidColorBrush(Color.FromRgb(0x4F, 0xB3, 0xFF)),
        DiscKind.Ps1Game => new SolidColorBrush(Color.FromRgb(0xC0, 0x8C, 0xFF)),
        DiscKind.Ps2Game => new SolidColorBrush(Color.FromRgb(0x8B, 0xA9, 0xFF)),
        DiscKind.DreamcastGame => new SolidColorBrush(Color.FromRgb(0xFF, 0x9F, 0x5A)),
        DiscKind.SaturnGame => new SolidColorBrush(Color.FromRgb(0xFF, 0x7A, 0x7A)),
        DiscKind.SegaCdGame => new SolidColorBrush(Color.FromRgb(0xFF, 0xB0, 0x60)),
        DiscKind.PcEngineCdGame => new SolidColorBrush(Color.FromRgb(0xF0, 0x9A, 0xC0)),
        DiscKind.PcGame => new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80)),
        DiscKind.PcSoftware => new SolidColorBrush(Color.FromRgb(0x8C, 0xC1, 0x8C)),
        DiscKind.DvdVideo or DiscKind.BluRayVideo => new SolidColorBrush(Color.FromRgb(0xF5, 0xC5, 0x6B)),
        _ => new SolidColorBrush(Color.FromRgb(0x9B, 0xA9, 0xB4)),
    };

    // ------------------------------------------------------------------ 详情

    private void DriveList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DriveList.SelectedItem is DriveRow row)
            ShowDetail(row.Info, row.Letter);
    }

    private void ShowDetail(DiscInfo? info, char letter)
    {
        _current = info;

        if (info == null)
        {
            ShowEmptyDetail("无法读取该光驱。");
            return;
        }

        TxtKind.Text = $"{letter}: · {info.KindDisplay}";
        TxtTitle.Text = string.IsNullOrWhiteSpace(info.Title) ? "（无标题）" : info.Title;

        PropsGrid.Children.Clear();
        AddProp("平台", info.Platform ?? "—");
        AddProp("序列号", info.Serial ?? "—");
        AddProp("卷标", string.IsNullOrWhiteSpace(info.VolumeLabel) ? "—" : info.VolumeLabel);
        AddProp("文件系统", string.IsNullOrWhiteSpace(info.FileSystem) ? "—" : info.FileSystem);
        AddProp("容量", info.TotalSize > 0
            ? $"{info.TotalSize / 1024.0 / 1024.0:F1} MB"
            : "—");
        AddProp("音轨", info.Tracks.Count > 0
            ? $"{info.Tracks.Count} 条（{info.Tracks.Count(t => t.IsAudio)} 音频）"
            : "—");
        AddProp("裸扇区读取", info.RawAccessAvailable ? "可用" : "不可用（降级识别）");
        AddProp("可自动启动", info.IsActionable ? "是" : "否");

        EvidencePanel.Children.Clear();
        if (info.Evidence.Count == 0)
        {
            EvidencePanel.Children.Add(new TextBlock
            {
                Text = "无",
                Foreground = (Brush)FindResource("Fg1"),
                FontSize = 12
            });
        }
        else
        {
            foreach (var ev in info.Evidence.OrderByDescending(x => x.Weight))
            {
                var tb = new TextBlock { FontSize = 12, Margin = new Thickness(0, 1, 0, 1) };
                tb.Inlines.Add(new Run($"[{ev.Weight,3}] ")
                {
                    Foreground = (Brush)FindResource("Accent"),
                    FontFamily = new FontFamily("Consolas, Cascadia Mono"),
                });
                tb.Inlines.Add(new Run(ev.Rule + "  ")
                {
                    Foreground = (Brush)FindResource("Fg0"),
                });
                tb.Inlines.Add(new Run(ev.Detail)
                {
                    Foreground = (Brush)FindResource("Fg1"),
                });
                EvidencePanel.Children.Add(tb);
            }
        }

        TxtRoot.Text = info.RootEntries.Count == 0
            ? "—"
            : string.Join("   ", info.RootEntries.Take(40).Select(p => Path.GetFileName(p)));

        BtnLaunch.IsEnabled = info.IsActionable;
        BtnOpenDrive.IsEnabled = info.Kind != DiscKind.Empty;
    }

    private void ShowEmptyDetail(string message)
    {
        TxtKind.Text = "—";
        TxtTitle.Text = message;
        PropsGrid.Children.Clear();
        EvidencePanel.Children.Clear();
        TxtRoot.Text = "—";
        BtnLaunch.IsEnabled = false;
        BtnOpenDrive.IsEnabled = false;
    }

    private void AddProp(string label, string value)
    {
        var l = new TextBlock
        {
            Text = label,
            FontSize = 12,
            Foreground = (Brush)FindResource("Fg1"),
            Margin = new Thickness(0, 3, 12, 3),
        };
        var v = new TextBlock
        {
            Text = value,
            FontSize = 12,
            Foreground = (Brush)FindResource("Fg0"),
            Margin = new Thickness(0, 3, 24, 3),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        PropsGrid.Children.Add(l);
        PropsGrid.Children.Add(v);
    }

    // ------------------------------------------------------------------ 启动

    private bool IsDuplicated(char letter, DiscInfo info)
    {
        string key = $"{info.Kind}|{info.Serial}|{info.Title}|{info.TotalSize}";
        if (_lastLaunch.TryGetValue(letter, out var prev)
            && prev.Key == key
            && (DateTime.Now - prev.At).TotalSeconds < 20)
            return true;

        _lastLaunch[letter] = (key, DateTime.Now);
        return false;
    }

    private void Launch(DiscInfo info, bool fromAuto)
    {
        var outcome = Launcher.Launch(info, _config);
        TxtStatus.Text = outcome.Message;

        if (outcome.Ok)
        {
            // 托盘常驻时这是唯一的可见反馈
            _tray?.ShowBalloonTip(2000, info.KindDisplay, outcome.Message,
                WinForms.ToolTipIcon.Info);
        }
        else
        {
            Log.Warn($"[启动未完成] {outcome.Message}");
            if (!fromAuto)
                MessageBox.Show(this, outcome.Message, "无法启动",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void BtnLaunch_Click(object sender, RoutedEventArgs e)
    {
        if (_current is { IsActionable: true })
            Launch(_current, fromAuto: false);
    }

    private void BtnReprobe_Click(object sender, RoutedEventArgs e) =>
        RefreshDrives(autoLaunch: false);

    private void BtnRescan_Click(object sender, RoutedEventArgs e) =>
        RefreshDrives(autoLaunch: false);

    private void BtnOpenDrive_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null) return;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = _current.RootPath,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Log.Warn($"打开光盘失败：{ex.Message}");
        }
    }

    private void BtnOpenConfig_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string dir = Path.GetDirectoryName(App.ConfigPath)!;
            Directory.CreateDirectory(dir);
            if (!File.Exists(App.ConfigPath)) _config.Save();

            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{App.ConfigPath}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Log.Warn($"打开配置目录失败：{ex.Message}");
        }
    }

    private void ChkAuto_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        _config.AutoLaunch = ChkAuto.IsChecked == true;
        _config.Save();
        Log.Info($"自动启动：{(_config.AutoLaunch ? "开" : "关")}");
    }

    private void ChkAutoStart_Changed(object sender, RoutedEventArgs e)
    {
        // 回滚勾选会再次触发本事件，不加这道闸就会：写失败 → 记一次 → 回滚
        // → 再触发 → 再记一次，同一动作被记两遍
        if (!IsLoaded || _autoStartUpdating) return;

        bool want = ChkAutoStart.IsChecked == true;
        if (AutoStart.Set(want, out string message))
        {
            Log.Info($"[自启] {message}");
            return;
        }

        // 不用模态框：开关没生效不该打断用户，而且模态会阻塞 UI 线程。
        // 一次点击只留一行日志（含补救办法），避免又变成"点一次出两行"。
        Log.Warn($"[自启] {message}；{AutoStart.ManualHint}");

        // 回滚勾选，别让开关看起来"打开了"
        _autoStartUpdating = true;
        try
        {
            ChkAutoStart.IsChecked = AutoStart.IsEnabled();
        }
        finally
        {
            _autoStartUpdating = false;
        }

        TxtStatus.Text = "开机自启未生效：写入启动项被拒绝，原因见运行日志";
    }

    // ------------------------------------------------------------------ 托盘

    private void SetupTray()
    {
        try
        {
            var menu = new WinForms.ContextMenuStrip();

            menu.Items.Add("显示主窗口", null, (_, _) => ShowFromTray());
            menu.Items.Add("重新扫描", null,
                (_, _) => Dispatcher.Invoke(() => RefreshDrives(autoLaunch: false)));
            menu.Items.Add("打开配置目录", null,
                (_, _) => Dispatcher.Invoke(() => BtnOpenConfig_Click(this, new RoutedEventArgs())));
            menu.Items.Add(new WinForms.ToolStripSeparator());
            menu.Items.Add("退出", null, (_, _) => ExitApp());

            _tray = new WinForms.NotifyIcon
            {
                Icon = LoadAppIcon(),
                Text = "Discern",
                Visible = true,
                ContextMenuStrip = menu,
            };
            _tray.DoubleClick += (_, _) => ShowFromTray();
        }
        catch (Exception ex)
        {
            Log.Warn($"托盘图标创建失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 托盘图标：从 WPF 资源里取 ico，并按系统小图标尺寸重新取一次，
    /// 这样 16px 托盘拿到的是真正的 16px 帧，而不是把 32px 缩下去。
    /// </summary>
    private static System.Drawing.Icon LoadAppIcon()
    {
        try
        {
            var res = System.Windows.Application.GetResourceStream(
                new Uri("pack://application:,,,/Discern.ico"));

            if (res?.Stream != null)
            {
                using var stream = res.Stream;
                using var full = new System.Drawing.Icon(stream);
                return new System.Drawing.Icon(full, WinForms.SystemInformation.SmallIconSize);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"读取内置图标失败：{ex.Message}");
        }

        // 兜底：直接用 exe 自带的图标
        try
        {
            if (Environment.ProcessPath is { Length: > 0 } p &&
                System.Drawing.Icon.ExtractAssociatedIcon(p) is { } ico)
                return ico;
        }
        catch { /* ignore */ }

        return System.Drawing.SystemIcons.Application;
    }

    private void ShowFromTray()
    {
        Dispatcher.Invoke(() =>
        {
            Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();

            // 强制前置一次，否则从托盘唤回时可能被其他窗口压住
            Topmost = true;
            Topmost = false;
        });
    }

    private void ExitApp()
    {
        _exiting = true;
        Dispatcher.Invoke(() =>
        {
            _tray?.Dispose();
            _tray = null;
            _watcher.Dispose();
            Application.Current.Shutdown();
        });
    }
}
