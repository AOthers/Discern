# Discern

> **Put a disc in. It figures out the rest.**
>
> 插盘，剩下的事它来辨。

Windows 桌面小工具：**插入光盘后自动判断这是什么盘，然后做该做的事。**

名字取自 `disc` + `discern`（辨识、分辨）——一张来历不明的盘塞进来，它认出这是什么。

- 音乐 CD → 交给音乐播放器播放
- PC 游戏盘 → 直接启动游戏 / autorun 入口
- PS1 / PS2 / Dreamcast / Saturn / Sega CD / PC Engine CD → 用对应模拟器加载光盘
- DVD / Blu-ray 视频盘 → 播放器打开

---

## 先读这一段：它必须先运行，才能识别光盘

**Discern 不是驱动，也不是系统服务。** 它是一个普通桌面程序：
只有进程活着，Windows 才会把「设备变更」消息投递给它
（`WM_DEVICECHANGE` 只发给用 `RegisterDeviceNotificationW` 登记过的进程）。
所以「光盘一放进光驱就自动识别」的前提是 —— **Discern 已经在后台跑着**。

关掉它，插盘就什么都不会发生。这一点没有绕过的办法，任何同类工具都一样。

### 让它一直跑着

推荐做法（勾一次，以后永久生效）：

1. 双击 `app\Discern.exe`（先跑一次 `.\build.ps1 -Publish` 生成）
2. 勾上工具条右侧的 **「开机常驻」**

勾上之后写入的是：

```
HKCU\Software\Microsoft\Windows\CurrentVersion\Run
  Discern = "…\Discern.exe" --background
```

登录 Windows 后它就在托盘里待命，**不弹窗口、不占任务栏**，插盘即识别。
想让它退出：右键托盘图标 → 退出。关窗口只是隐藏到托盘，不会停掉监听。

### 手动启动

```powershell
.\app\Discern.exe              # 显示主窗口
.\app\Discern.exe --background # 直接进托盘（自启用的是这个）
```

同一时刻只允许一个实例运行 —— 自启的常驻进程和手动双击不会各起一个去抢同一张盘。

---

## 一、可行性结论与同类项目调研

**结论：完全可实现，而且不需要任何驱动或内核态组件。** Windows 原生提供两条路：

1. `WM_DEVICECHANGE` + `DBT_DEVICEARRIVAL`，配合 `RegisterDeviceNotificationW` 注册
   `DBT_DEVTYP_VOLUME`（拿盘符）与 `GUID_DEVINTERFACE_CDROM`（设备级到达事件）。
2. `IOCTL_CDROM_READ_TOC` 直接读光盘目录区（TOC），这是**区分音乐 CD 与数据盘唯一可靠的判据**
   （音乐 CD 没有文件系统，`DriveInfo.IsReady` 甚至可能是 `false`）。

现成的同类项目里，最贴近需求的是 **[LMauricius/EmuDiscer](https://github.com/LMauricius/EmuDiscer)**——
"A simple tool for automatically launching emulators when a game disc is inserted"，它做的正是
「识别光盘属于哪个主机 → 启动对应模拟器」。相关参考还有
[CosmicScale/Retro-GEM-PS2-Disc-Launcher](https://github.com/CosmicScale/Retro-GEM-PS2-Disc-Launcher)
（PS2 端，按游戏 ID 启动）。微软官方文档
[Detecting Media Insertion or Removal](https://learn.microsoft.com/en-us/windows/win32/devio/detecting-media-insertion-or-removal)
是设备通知的标准做法。

本项目相对于它们的差异点：

| 能力 | EmuDiscer | 本项目 |
| --- | --- | --- |
| 光盘插入检测 | ✅ | ✅ |
| 平台自动识别 | ✅ | ✅（多一层引导扇区魔数） |
| **逐游戏**识别 + 专属参数 | ❌ | ✅ `GameEntry` 游戏库 |
| PC 游戏盘直接启动 | ❌ | ✅ autorun / exe |
| 音乐 CD 交给播放器 | ❌ | ✅ TOC 判据 + `.cda` 降级 |
| 可配置 JSON 库 | 部分 | ✅ 全量可改 |
| 无管理员权限降级运行 | — | ✅ 自动降级 + 提示 |

---

## 二、识别原理（证据权重从高到低）

| 权重 | 证据 | 结论 |
| --- | --- | --- |
| 100 | TOC 全部为音频轨（`control & 0x04 == 0`） | 音乐 CD |
| 98 | `SYSTEM.CNF` 含 `BOOT2 = cdrom0:\SLUS_200.62;1` | PS2 |
| 98 | `SYSTEM.CNF` 含 `BOOT = cdrom:\SLES_123.45;1` | PS1 |
| 95 | 扇区 0 含 `SEGADISCSYSTEM` | Sega CD |
| 95 | 扇区 0 含 `SEGA SEGASATURN` | Saturn |
| 95 | 扇区 0 含 `SEGA SEGAKATANA` | Dreamcast |
| 95 | 扇区 0 含 `PC Engine CD-ROM SYSTEM` | PC Engine CD |
| 90 | 存在 `VIDEO_TS\VIDEO_TS.IFO` | DVD 视频 |
| 90 | 存在 `BDMV` | Blu-ray |
| 85 | 根目录 `1ST_READ.BIN` | Dreamcast（次级判据） |
| 85 | `autorun.inf` 含 `open=` / `shellexecute=` | PC 游戏 |
| 75 | 根目录有 `*.exe` | PC 游戏 / 软件（`setup`/`install` 视为软件） |
| 70 | 根目录只有 `*.cda`（无裸设备时的降级判据） | 音乐 CD |
| 10 | 全不命中 | 普通数据盘 |

序列号用 `[A-Z]{2,4}[_\-\s]?\d{3}[.\-]\d{2}` 归一化成 `SLUS_200.62` 形式，
可直接作为游戏库主键。

识别依据会逐条显示在界面「识别依据」区——**不是黑箱，每一条判断都摆出来**。

---

## 三、权限模型

读取 TOC 和裸扇区（`\\.\D:`）需要管理员权限。程序**不强制提权**：

- 普通权限：用文件系统视图识别（PS1/PS2/PC/DVD/`.cda` 降级都能正常工作），
  只是拿不到引导扇区魔数，Sega 系盘会识别不出来。
- 管理员权限：全部判据可用，界面会显示「管理员模式 · 可读裸扇区」。

主窗口右上角的徽标会直接显示当前状态。需要全功能时用管理员身份运行一次即可。

---

## 四、目录结构

```
Discern/
├─ App.xaml / App.xaml.cs        入口、全局样式、--probe / --emulators / --autostart 模式
├─ MainWindow.xaml / .xaml.cs    主界面（暗色，左光驱列表 / 右详情 / 底部日志）
├─ Discern.ico                  应用图标（多分辨率，由 tools\make-icon.ps1 生成）
├─ app.manifest                  asInvoker + PerMonitorV2 DPI + longPathAware
├─ Core/
│  ├─ NativeMethods.cs           所有 P/Invoke
│  ├─ OpticalDrive.cs            光驱句柄、TOC、裸扇区、ISO9660 PVD
│  ├─ DiscInfo.cs                识别结果模型（含中文类别名）
│  ├─ DiscProbe.cs               识别引擎（Probe / ProbePath）
│  ├─ DriveWatcher.cs            WM_DEVICECHANGE 监听 + 状态差分
│  ├─ PathSearch.cs              相对路径解析（应用目录 + 逐级父目录）
│  ├─ AutoStart.cs               开机自启（HKCU Run 项读写与失效修复）
│  ├─ AppConfig.cs               library.json 模型与默认值
│  ├─ Launcher.cs                命令组装与启动
│  └─ Log.cs                     内存环形缓冲 + 文件日志
Emulators/                       随项目附带的模拟器（见第六节）
├─ pcsx2/                        PCSX2 1.7.3869（PS2）
└─ RetroArch/                    RetroArch 1.15.0 + cores/（其余光盘机种）
app/                             -Publish 产物，日常从这里启动（首跑后出现）
docs/
├─ icon-256.png                  图标 256px 预览
└─ icon-sizes.png                各尺寸在深/浅背景下的对比板
tools/
└─ make-icon.ps1                生成 Discern.ico（改配色 / 几何后重跑一次即可）
tests/
├─ IsoForge/                     极简 ISO9660 镜像 / 目录树生成器（造测试盘）
├─ iso/                          测试用例定义（.json）与生成物（.iso）
└─ run-tests.ps1                 识别引擎回归测试
```

---

## 五、构建与运行

```powershell
.\build.ps1            # 构建 Release
.\build.ps1 -Test      # 构建 + 跑识别回归测试
.\build.ps1 -Debug     # 构建 Debug
.\build.ps1 -Publish   # 构建 + 发布一份到 .\app\（推荐，用于日常运行）
```

`-Publish` 产出的目录布局（`Emulators` 是共享的，不复制）：

```
app\Discern.exe          日常从这里启动
app\Data\library.json    配置
Emulators\               同级目录，PathSearch 向上查找能直接命中
```

不加 `-Publish` 时的临时产物在
`Discern\bin\Release\net10.0-windows\Discern.exe`。
注意这个路径下勾「开机常驻」会把 `bin` 路径写进注册表，
重新编译后就要重勾一次 —— 所以建议用 `app\Discern.exe`。

直接调 `dotnet` 的话注意两点（脚本已经处理好）：

```powershell
$env:DOTNET_CLI_HOME = 'E:\Game\Discern\.dotnet'
$env:NUGET_PACKAGES  = 'E:\Game\Discern\.nuget'
dotnet build Discern.sln -c Release -m:1
```

- `DOTNET_CLI_HOME` / `NUGET_PACKAGES` 指到仓库内，避免写用户目录时被沙箱拦。
- **`-m:1` 不能省**：多节点 MSBuild 用命名管道通信，在受限 shell 里会中途取消编译
  （表现为 `MSB5021 已取消生成`）。单节点构建结果完全一致。

### 命令行自检

```powershell
# 列出所有 CD-ROM 的识别结果
Discern.exe --probe

# 只看某个盘符
Discern.exe --probe --drive=D

# 识别任意目录（无需光驱，用于验证/调试）
Discern.exe --probe-dir "D:\GameDisc"

# 结果同时写到文件（WinExe 不方便接管道时用）
Discern.exe --probe-dir "D:\GameDisc" --out=result.txt

# 检查模拟器 / 核心是否都能解析到（换机器或挪目录后先跑这个）
Discern.exe --emulators
```

输出包含类别、平台、标题、序列号、音轨、全部证据，以及**启动计划**
（解析到的可执行文件 + 实际命令行），便于确认配置是否生效。

---

## 六、配置文件

首次运行自动生成 `Data\library.json`（与 exe 同目录），点界面「打开配置目录」可直接定位。

```jsonc
{
  "Version": 2,                // 结构版本，旧文件会自动备份为 .v0.bak 后重建
  "AutoLaunch": true,          // 插入后是否自动启动
  "SettleDelayMs": 1200,       // 插入后等待文件系统挂载的时间

  "Audio": {                   // 音乐 CD
    "DisplayName": "系统默认播放器",
    "Executable": "",
    "Arguments": "{disc}"
  },

  "PcGame": {                  // PC 盘：留空则交给系统（autorun / 默认关联）
    "DisplayName": "系统默认（autorun / 资源管理器）",
    "Executable": "",
    "Arguments": "{disc}"
  },

  "Emulators": {
    // PS2 —— 单机 PCSX2，-disc 直接读物理光驱
    "ps2": {
      "DisplayName": "PCSX2 1.7（PS2）",
      "Executable": "Emulators\\pcsx2\\pcsx2x64.exe",
      "Core": "",
      "Arguments": "-disc \"{disc}\" -fullscreen -nogui",
      "SearchPaths": [ "Emulators\\pcsx2\\pcsx2x64-avx2.exe" ]
    },

    // PS1 —— RetroArch + SwanStation 核心
    "ps1": {
      "DisplayName": "RetroArch · SwanStation（PS1）",
      "Executable": "Emulators\\RetroArch\\retroarch.exe",
      "Core": "Emulators\\RetroArch\\cores\\swanstation_libretro.dll",
      "Arguments": "-L \"{core}\" \"{disc}\""
    }
  },

  "Games": [                   // 逐游戏覆盖（可选）
    {
      "Serial": "SLUS_200.62",
      "Title": "最终幻想 X",
      "Emulator": "ps2",
      "Arguments": "-disc \"{disc}\" -fullscreen -nogui -slowboot"
    }
  ]
}
```

### 占位符

| 占位符 | 含义 | 示例 |
| --- | --- | --- |
| `{disc}` | 光盘根路径 | `D:\` |
| `{device}` | 裸设备路径 | `\\.\D:` |
| `{drive}` | 盘符 | `D` |
| `{core}` | 已解析的核心文件绝对路径 | `...\cores\swanstation_libretro.dll` |
| `{serial}` | 序列号 | `SLUS_200.62` |
| `{title}` | 识别出的标题 | `SLUS_200.62` |
| `{image}` | 镜像文件路径 | 预留 |

### 内置模拟器映射

`Emulators\` 里的两个模拟器已经按平台接好，开箱即用：

| 平台键 | 处理方式 | 核心 / 参数 |
| --- | --- | --- |
| `ps2` | PCSX2 1.7.3869 | `-disc "{disc}" -fullscreen -nogui` |
| `ps1` | RetroArch · SwanStation | `swanstation_libretro.dll` |
| `dvdvideo` | MPC-HC（自动探测） | `"{disc}"` |
| `bluray` | MPC-HC（自动探测） | `"{disc}"` |
| `dreamcast` | RetroArch · Flycast | `flycast_libretro.dll` |
| `saturn` | RetroArch · Beetle Saturn | `mednafen_saturn_libretro.dll` |
| `segacd` | RetroArch · Genesis Plus GX | `genesis_plus_gx_libretro.dll` |
| `pcengine` | RetroArch · Beetle PCE | `mednafen_pce_libretro.dll` |

**目前的主攻目标是 PS1 和 PS2**——这两个有真实盘可验，也最常用。
后面四个机种的配置是现成的，但没在真机上跑过，属于「已接线、未验收」。
`RetroArch\cores\` 里还有 `mednafen_pcfx`、`neocd`、`opera`(3DO)、`same_cdi`、
`ppsspp` 等核心，没有对应的识别规则，暂时用不上。

**路径写法**：`Executable` / `Core` / `SearchPaths` 都可以写相对路径，
程序会从 exe 所在目录开始逐级向上查找（最多 8 级），
所以 `Emulators\...` 在 `bin\Release\net10.0-windows\` 里也能找到项目根的 `Emulators`。
换机器、挪目录都不用改配置；不确定时跑 `Discern.exe --emulators` 看解析结果。

### 音乐和视频：不需要另装播放器

这两类都走系统里**已经存在**的处理程序，不需要额外准备。

**音乐 CD** → Windows 自带。程序对盘符触发 `play` 动作，走的是注册表里
`HKCR\AudioCD\shell\play` 登记的处理器。本机实测指向：

```
C:\Program Files (x86)\Windows Media Player\wmplayer.exe /device:AudioCD "%L"
```

`Audio` 配置项的 `Executable` 留空就是这个意思——交给系统，不绑死某个播放器。
你以后装了别的音乐软件并把它设为音频 CD 的默认处理，这里自动跟着变。

**DVD / Blu-ray 视频** → MPC-HC。本机没装 VLC，但 K-Lite 自带 MPC-HC：

```
C:\Program Files (x86)\K-Lite Codec Pack\MPC-HC64\mpc-hc64.exe
```

实测直接传目录路径就能打开（`VIDEO_TS` 结构会被识别成 DVD）。
`SearchPaths` 里列了几种常见安装位置，换机器会自动找；
都找不到时退回系统默认处理，并在日志里说明。

Blu-ray 需要 BD 光驱 + MPC-HC 的 libbluray 支持，没条件验证。


### 图标

`Discern.ico` 由脚本生成，不依赖设计工具：

```powershell
.\tools\make-icon.ps1 -Preview 256
```

概念是**光圈式光盘**：光驱（optical disc）和眼睛（optical eye）共用一个词根，
所以盘面画成镜头虹膜——中心孔是瞳孔，周围 12 片叶片。

工程上的几个决定：

- **9 个尺寸**（16/20/24/32/40/48/64/128/256），覆盖标题栏、任务栏、
  托盘、资源管理器各档 DPI。20 和 40 是给 125%/150% 缩放用的。
- **分级绘制**：叶片和反光弧只在 48px 以上出现；16–24px 省略内圈，
  只保留实心环 + 一道高光，缩到最小也不会糊成一团。
- **全部帧用 32bpp DIB**，不用 PNG 压缩帧。`System.Drawing.Icon`（托盘图标
  走的就是它）不能解析 PNG 帧，会抛「请求的范围扩展超过了数组的结尾」。
  代价是文件 381 KB，换的是任何读取路径都不会翻车。
- 外缘是**深色描边**而不是白色内发光：图标要同时在深色任务栏和白色
  资源管理器背景上成立，白边在白底会直接消失。

### 物理光盘 vs 镜像文件

这是唯一需要动手调的地方，**取决于核心本身是否支持读物理光驱**：

| 机种 | 物理光盘 | 说明 |
| --- | --- | --- |
| PS2 | ✅ 直接可用 | PCSX2 的 `-disc` 就是为物理光驱设计的 |
| PS1 | ⚠ 看核心 | SwanStation 内置 `CDImageDeviceWin32`，多数情况可用；不行就把 `{disc}` 换成 `{device}` |
| Sega CD / PCE-CD | ⚠ 看核心 | Genesis Plus GX / Beetle PCE 带设备读取代码，同上 |
| Saturn | ❌ 需镜像 | Beetle Saturn 只认镜像文件（.cue/.chd） |
| Dreamcast | ❌ 需镜像 | GD-ROM 物理上 PC 光驱读不了 |

`{disc}` 传的是 `D:\`，`{device}` 传的是 `\\.\D:`。
遇到某个核心不认物理盘时，改这一个占位符即可，其余不用动。
实在不行就把盘做成 `.cue/.bin` 或 `.chd`，用 `Games[]` 里给该序列号单独指定 `Arguments`。

内置平台键：`ps1` `ps2` `dreamcast` `saturn` `segacd` `pcengine` `dvdvideo` `bluray`。
`Executable`、`Core`、`SearchPaths` 都支持 `%LOCALAPPDATA%` 这类环境变量。

---

## 七、测试

```powershell
.\tests\run-tests.ps1
```

流程：`IsoForge` 把每个用例定义展开成真实目录树 → 调 `Discern.exe --probe-dir` 识别 →
断言类别 / 序列号 / 可启动性。当前 7 个用例全部通过：

| 用例 | 期望 | 结果 |
| --- | --- | --- |
| ps2 | Ps2Game，SLUS_200.62 | PASS |
| ps1 | Ps1Game，SLES_123.45 | PASS |
| pcgame | PcGame | PASS |
| dvd | DvdVideo | PASS |
| plaindata | DataDisc（不可启动） | PASS |
| audio | AudioCd | PASS |
| dreamcast | DreamcastGame | PASS |

`IsoForge` 同时生成真实 `.iso`（合法 ISO9660，含 PVD + 路径表 + 目录记录）。
在有管理员权限的机器上可以做完整端到端验证：

```powershell
Mount-DiskImage -ImagePath tests\iso\ps2.iso     # 挂载后会被识别为 CD-ROM 驱动器
.\Discern\...\Discern.exe --probe
Dismount-DiskImage -ImagePath tests\iso\ps2.iso
```

> 注：`Mount-DiskImage` 需要管理员权限，普通权限下报「拒绝访问」。

---

## 八、已知限制

1. **PC 光驱读不出 GameCube / Wii / Xbox / Xbox 360 / PS3 / PSP 的盘。**
   这些是逆向旋转或专有格式的介质，物理层就读不了，与软件无关。这类游戏只能用镜像文件。
2. **原版 PS1/PS2 盘有防拷扇区**，某些模拟器需要额外配置才能读物理盘；
   能读到文件系统不代表模拟器一定能直接加载，必要时改用镜像。
3. Blu-ray 需要蓝光光驱；普通 DVD 光驱放进去不会有任何反应。
4. 音乐 CD 的曲目信息（艺术家 / 曲名）来自 CD-Text 或在线数据库，本项目只负责交给播放器，
   由播放器自己解析。
5. `SettleDelayMs` 默认 1200ms。如果某些光驱挂载慢导致首次识别为空，把它调大即可。
