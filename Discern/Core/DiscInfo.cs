namespace Discern.Core;

/// <summary>光盘类别。决定后续动作（播音乐 / 启动 PC 游戏 / 启动模拟器）。</summary>
public enum DiscKind
{
    /// <summary>光驱里没盘。</summary>
    Empty = 0,
    /// <summary>识别失败。</summary>
    Unknown,
    /// <summary>纯音乐 CD（CDDA），全部曲目均为音频轨。</summary>
    AudioCd,
    /// <summary>普通数据盘，未命中任何平台特征。</summary>
    DataDisc,
    /// <summary>PC / Windows 游戏盘。</summary>
    PcGame,
    /// <summary>普通 PC 软件安装盘。</summary>
    PcSoftware,
    /// <summary>PlayStation 1 游戏盘。</summary>
    Ps1Game,
    /// <summary>PlayStation 2 游戏盘。</summary>
    Ps2Game,
    /// <summary>Dreamcast 游戏盘。</summary>
    DreamcastGame,
    /// <summary>Sega CD / Mega-CD 游戏盘。</summary>
    SegaCdGame,
    /// <summary>Sega Saturn 游戏盘。</summary>
    SaturnGame,
    /// <summary>PC Engine CD / TurboGrafx-CD 游戏盘。</summary>
    PcEngineCdGame,
    /// <summary>DVD 视频盘。</summary>
    DvdVideo,
    /// <summary>Blu-ray 视频盘。</summary>
    BluRayVideo,
}

/// <summary>单条识别证据，用于界面展示「为什么判定成这个」。</summary>
public sealed record DiscEvidence(string Rule, string Detail, int Weight);

/// <summary>一次光盘探测的完整结果。</summary>
public sealed class DiscInfo
{
    public required char DriveLetter { get; init; }
    public required string RootPath { get; init; }

    public DiscKind Kind { get; set; } = DiscKind.Unknown;

    /// <summary>平台标识，与 library.json 的 emulators 键对应（ps1 / ps2 / dreamcast ...）。</summary>
    public string? Platform { get; set; }

    /// <summary>盘面标题：优先 SYSTEM.CNF 指向的可执行名，其次卷标。</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>光盘序列号，例如 SLUS_200.62 / SLES_123.45。</summary>
    public string? Serial { get; set; }

    public string VolumeLabel { get; set; } = string.Empty;
    public string FileSystem { get; set; } = string.Empty;
    public long TotalSize { get; set; } = -1;

    /// <summary>TOC 音轨列表，裸设备不可用时为空。</summary>
    public IReadOnlyList<TocTrack> Tracks { get; set; } = Array.Empty<TocTrack>();

    /// <summary>根目录条目（最多保留前 200 个，避免大目录拖慢界面）。</summary>
    public IReadOnlyList<string> RootEntries { get; set; } = Array.Empty<string>();

    /// <summary>判定依据。</summary>
    public List<DiscEvidence> Evidence { get; } = new();

    /// <summary>裸设备通道是否可用（否则为降级识别）。</summary>
    public bool RawAccessAvailable { get; set; }

    /// <summary>是否已识别为可直接启动的实体（游戏或音乐）。</summary>
    public bool IsActionable =>
        Kind is DiscKind.AudioCd or DiscKind.PcGame or DiscKind.PcSoftware
             or DiscKind.Ps1Game or DiscKind.Ps2Game or DiscKind.DreamcastGame
             or DiscKind.SegaCdGame or DiscKind.SaturnGame or DiscKind.PcEngineCdGame
             or DiscKind.DvdVideo or DiscKind.BluRayVideo;

    public string KindDisplay => Kind switch
    {
        DiscKind.Empty => "空",
        DiscKind.Unknown => "未识别",
        DiscKind.AudioCd => "音乐 CD",
        DiscKind.DataDisc => "数据盘",
        DiscKind.PcGame => "PC 游戏",
        DiscKind.PcSoftware => "PC 软件",
        DiscKind.Ps1Game => "PlayStation 游戏",
        DiscKind.Ps2Game => "PlayStation 2 游戏",
        DiscKind.DreamcastGame => "Dreamcast 游戏",
        DiscKind.SegaCdGame => "Sega CD 游戏",
        DiscKind.SaturnGame => "Sega Saturn 游戏",
        DiscKind.PcEngineCdGame => "PC Engine CD 游戏",
        DiscKind.DvdVideo => "DVD 视频",
        DiscKind.BluRayVideo => "Blu-ray 视频",
        _ => "未识别",
    };

    public override string ToString() =>
        $"[{DriveLetter}:] {KindDisplay} — {Title} {Serial}".Trim();
}
