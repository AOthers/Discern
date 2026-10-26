# Discern 开发文档

> 版本 **v1.0** · 最后更新 2026-10-26
>
> 面向后续维护者（包括未来的自己）。用户向的说明见 [README](../README.md)，
> 这里只写**实现细节、技术决定和已知问题**。

---

## 一、v1.0 的范围

**做完了的：**

- 插入光盘自动识别，识别后按类型分派
- PS2 盘 → PCSX2，PS1 盘 → RetroArch + SwanStation
- 音乐 CD → 系统默认（实测指向 Windows Media Player）
- DVD / Blu-ray → MPC-HC
- PC 游戏盘 → autorun / 资源管理器
- 托盘常驻、开机自启、单实例、命令行自检

**明确不做的（v1.0）：**

- 3DO / Neo Geo CD / PC-FX / CD-i / PSP 的识别规则
- 光盘镜像文件（`.cue/.bin/.chd/.iso`）支持
- 多碟游戏换盘
- 多语言（当前只有中文）

## 二、架构

```
DriveWatcher ──插拔事件──▶ MainWindow.RefreshDrives
                              │  (Task.Run)
                              ▼
                          DiscProbe.Probe
                              │
                              ▼
                          DiscInfo  ──────▶ Launcher.SelectProfile
                          (判据+证据)              │
                                                   ▼
                                            AppConfig.Emolators[key]
                                                   │
                                                   ▼
                                             Process.Start
```

| 文件 | 职责 |
| --- | --- |
| `Core/NativeMethods.cs` | 所有 P/Invoke 集中在此，其他文件不直接 DllImport |
| `Core/OpticalDrive.cs` | 光驱句柄、IOCTL TOC、裸扇区读、ISO9660 PVD |
| `Core/DiscProbe.cs` | 识别引擎，输出 `DiscInfo` + 加权证据列表 |
| `Core/DiscInfo.cs` | 识别结果模型，`DiscKind` 枚举 |
| `Core/DriveWatcher.cs` | `WM_DEVICECHANGE` 监听 + 状态差分 + 去抖 |
| `Core/PathSearch.cs` | 相对路径解析、真实路径解析（联接/符号链接） |
| `Core/AppConfig.cs` | `library.json` 模型、默认值、版本迁移 |
| `Core/Launcher.cs` | 开关选择、模板替换、进程启动、诊断输出 |
| `Core/AutoStart.cs` | HKCU Run 项读写、失效路径修复 |
| `Core/Log.cs` | 环形缓冲 + 文件追加 |
| `MainWindow.xaml.cs` | UI，唯一允许碰 WPF 控件的地方 |

**分层原则**：`Core` 不引用任何 `System.Windows.*`。唯一例外是
`MainWindow.LoadAppIcon()` 里用 `Application.GetResourceStream` 读图标，
它是 UI 层的代码，只是碰巧需要 WPF 的 pack URI。

## 三、关键技术决定

记录「为什么这么做」，避免以后被"优化"掉。

### 3.1 用状态差分，不信 `dbcv_unitmask`

`WM_DEVICECHANGE` 的 `DBT_DEVICEARRIVAL` 只给出变化的盘符位掩码。
实测事件顺序不可靠、也可能合并。所以 `DriveWatcher` 忽略掩码本身，
每次事件后重扫所有 `DriveType.CDRom` 盘符，与缓存状态做差分。

副作用：音频 CD 没有文件系统，`DriveInfo.IsReady` 可能是 false，
差分逻辑必须把「设备存在」和「有盘」分开判断，不能只看 IsReady。

### 3.2 TOC 是判音频盘的唯一可靠手段

音频 CD 没有文件系统，`Directory.EnumerateFileSystemEntries` 会失败。
唯一可靠判据是 `IOCTL_CDROM_READ_TOC`，读 `CDROM_TOC` 结构判断每条音轨的
`Control & 0x04`（数据轨标志）。

降级路径：没有裸设备权限时，用「根目录全是 `.cda`」的启发式（权重 70），
不如 TOC 可靠但聊胜于无。

### 3.3 默认不提权（`asInvoker`）

提权会带来 UAC 弹窗、拖放失效、无法从资源管理器正常启动等一连串问题。
所以 v1.0 走普通权限 + 降级识别 + UI 明确标注降级状态。

代价见 [已知问题 D3](#四已知问题本版不修)。

### 3.4 图标全部用 32bpp DIB，不用 PNG 压缩帧

`System.Drawing.Icon`（`NotifyIcon` 走的就是它）**无法解析 PNG 压缩的 ICO 帧**，
会抛 `请求的范围扩展超过了数组的结尾`。

代价是 ico 文件 381 KB（256×256 的 DIB 占 270 KB），换来任何读取路径都不翻车。
详见 [已知问题 D16](#四已知问题本版不修)。

### 3.5 解决方案构建必须 `-m:1`

多节点 MSBuild 靠命名管道通信，在受限 shell / 沙箱里会中途取消编译
（表现为 `MSB5021 已取消生成` 或干脆「0 个警告 0 个错误」却失败）。
单节点构建结果完全一致，只是慢一点。`build.ps1` 已固化。

### 3.6 配置版本号 + 迁移

`library.json` 带 `Version` 字段。读到低于 `CurrentVersion` 的文件时，
备份成 `.vN.bak` 后重建默认配置。

**这是个有争议的决定**，因为会丢用户数据，见 D1。

## 四、已知问题（本版不修）

按严重度排序。**"计划"列写的是打算怎么处理，不代表已排期。**

### 高

| 编号 | 问题 | 影响 | 计划 |
| --- | --- | --- | --- |
| **D1** | 配置版本升级会丢失用户数据。`AppConfig.LoadOrCreate` 遇到版本落后时直接 `CreateDefault()` 重建，用户填的 `Games[]` 游戏库、自定义 `Emulators` 路径全部丢光，只留一个 `.bak` | 用户一旦建立游戏库，下次改配置结构就误伤 | 改成按字段合并迁移：保留 `Emulators` / `Games` / 开关，只补新增字段。或加 `AutoMigrate: false` 让用户手动确认 |
| **D2** | **全部识别逻辑未在真实光驱上验证过**。开发机没有光驱，所有测试都是 `--probe-dir` 走文件系统层。TOC 解析、裸扇区读、`WM_DEVICECHANGE` 真实触发、PCSX2 的 `-disc` 全部未验证 | 核心功能处于「代码正确但未验收」状态 | 光驱到位后按[第五节](#五待验证清单等光驱到)逐条跑 |

### 中

| 编号 | 问题 | 影响 | 计划 |
| --- | --- | --- | --- |
| **D3** | 非管理员时识别能力大幅降级。引导扇区签名（Sega CD / Saturn / Dreamcast / PC Engine）**只有管理员能读**，这三类盘完全识别不出；Dreamcast 有 `1ST_READ.BIN` 兜底；音频 CD 退化为 `.cda` 启发式 | 普通权限下多机种不可用 | 加「以管理员重启」按钮；或在首次遇到需要裸扇区的盘时提示提权 |
| **D4** | 不支持镜像文件。`.cue/.bin/.chd/.iso` 完全不认，`{image}` 占位符是空壳 | Saturn / Dreamcast 实际必须用镜像（GD-ROM 物理上 PC 光驱读不了），这两条配置目前走不通 | 让「打开镜像」和「插入光盘」共用同一条识别链路：`DiscProbe` 增加从镜像文件探测的入口 |
| **D5** | 游戏标题显示的是序列号。`Games[]` 是空的，没有 `序列号 → 游戏名` 映射表 | 界面显示 `SLUS_200.62` 而不是游戏名 | 内置一份常见 PS1/PS2 序列号表；或支持从 Redump dat 导入 |
| **D12** | Saturn / Dreamcast / Sega CD / PC Engine 四条配置「已接线、未验收」 | 可能有参数问题 | 按 D2 一起验证；验不了的就在 README 里持续标注 |
| **D15** | 测试只覆盖识别引擎（7 个用例）。`Launcher`、`AutoStart`、`PathSearch`、UI 层没有自动化测试 | 改动容易引入回归 | 至少给 `Launcher.ApplyTemplate` / `SelectProfile` 和 `PathSearch` 补单元测试 |

### 低

| 编号 | 问题 | 影响 | 计划 |
| --- | --- | --- | --- |
| **D6** | 探测在线程池里可能长时间占用。已从 UI 线程挪走，但异常盘（划伤、外置 USB 光驱掉线）仍可能让单次探测拖到几十秒 | 扫描期间状态栏停在「正在读取光驱…」 | 给 `OpticalDrive` 的扇区读加超时；或对超时的盘标为「读取失败」而非无限等 |
| **D7** | `IsDuplicated` 的 20 秒窗口。同一张盘 20 秒内弹出再插入不会触发自动启动 | 快速换盘测试时困惑 | 改成按「弹出事件」清零该盘符的指纹 |
| **D8** | `Audio` / `PcGame` 的 `Arguments` 是死字段。这两个 profile 走 `ShellDefault`，参数模板被忽略 | 配置里写了不生效，误导 | 要么在 `ShellDefault` 里用上，要么从模型里去掉 |
| **D9** | 托盘图标对象未释放。`LoadAppIcon()` 返回的 `Icon` 生命周期等于进程 | 极小的句柄泄漏 | 在 `ExitApp` 里 Dispose |
| **D10** | `PathSearch` 向上查找 8 级。理论上可能命中 `E:\Emulators` 这类无关目录 | 配置写错时可能启动到意料之外的程序 | 命中时打日志（已部分做到）；或限制只向上 4 级 |
| **D11** | Blu-ray 未验证。需要 BD 光驱 + MPC-HC 的 libbluray 支持 | `bluray` 配置可能不可用 | 有条件时验证 |
| **D13** | `app.manifest` 的 DPI 设置与 WinForms 分析器冲突（`WFO0003`）。本项目是 WPF 为主，DPI 由清单的 PerMonitorV2 负责，从不运行 WinForms 消息循环 | 仅编译期警告，已用 `NoWarn` 压掉 | 保持现状 |
| **D14** | 没有安装包、代码签名、自动更新。publish 是框架依赖模式，目标机器需装 .NET 10 桌面运行时 | 分发不方便 | 需要时再做 |

## 五、待验证清单（等光驱到）

按顺序做，每步记录结果。**这是 v1.1 的前置条件。**

- [ ] `Discern.exe --probe` 能在真光驱上跑通，不抛异常
- [ ] 插入 PS2 盘 → 日志出现 `检测到光盘插入`，界面显示 `PlayStation 2 游戏`
- [ ] PCSX2 能通过 `-disc "{disc}"` 加载物理盘（**重点**：这是 PS2 路线的关键假设）
- [ ] 插入 PS1 盘 → SwanStation 能否读物理光驱。不行就把 `ps1` 的 `{disc}` 换成 `{device}`
- [ ] 取出盘 → 日志出现 `光盘已弹出`，列表行消失
- [ ] 插入音乐 CD → 界面显示 `音乐 CD`，点启动后 WMP 开始播放
- [ ] 同一张盘 20 秒内重复插拔的行为（对应 D7）
- [ ] 以管理员身份运行一次，对比 `裸扇区读取` 从「不可用」变为「可用」
- [ ] 一张划伤的盘：观察界面是否卡顿（对应 D6）

## 六、开发约定

### 中文脚本必须带 UTF-8 BOM

Windows PowerShell 5.1 读 `.ps1` 时若无 BOM 会按 ANSI(GBK) 解码，
中文注释和字符串会变乱码并导致语法错误（`字符串缺少终止符`）。

**注意**：`edit` 工具改写文件会丢掉 BOM。改完 `.ps1` 后务必重新保存：

```powershell
$p = 'build.ps1'
$t = [IO.File]::ReadAllText($p, [Text.Encoding]::UTF8)
[IO.File]::WriteAllText($p, $t, (New-Object Text.UTF8Encoding($true)))
```

验证：文件前 3 字节应为 `239,187,191`。

### 构建

```powershell
.\build.ps1              # Release
.\build.ps1 -Test        # + 回归测试
.\build.ps1 -Publish     # + 发布到 app\（保留 Data\library.json）
```

`build.ps1` 内部固定 `-m:1`，见 3.5。

### 新增一个平台的步骤

1. `DiscKind` 加枚举值
2. `DiscInfo.KindDisplay` 加中文名
3. `DiscProbe.Classify` 加判据（给合适的权重，落在现有 95/98/90/85/70 档位）
4. `AppConfig.CreateDefault` 加 `Emulators` 条目
5. `tests/iso/*.json` 加用例，`run-tests.ps1` 加一行
6. 同步 README 的平台表和本文档

### 不要做的事

- **不要**重新加 `.slnx`（本环境验证过的是经典 `.sln` + `-m:1`）
- **不要**给 csproj 加 `<Platforms>x64</Platforms>`（解决方案传的是 `Platform=AnyCPU`，会冲突）
- **不要**在 `Checked` 事件里弹模态框（会阻塞 UI 线程，实测能卡死自动化）
- **不要**在 `Log.Set` 之类的基础方法里记日志，日志由调用方负责，否则同一动作记两遍

## 七、测试策略

`tests/run-tests.ps1` 跑 7 个用例：

| 用例 | 期望 | 覆盖点 |
| --- | --- | --- |
| ps2 | `Ps2Game` + 序列号 `SLUS_200.62` | `SYSTEM.CNF` 的 `BOOT2` 分支 |
| ps1 | `Ps1Game` + 序列号 `SLES_123.45` | `SYSTEM.CNF` 的 `BOOT` 分支 |
| pcgame | `PcGame` | `autorun.inf` / 根目录 exe |
| dvd | `DvdVideo` | `VIDEO_TS\VIDEO_TS.IFO` 嵌套目录 |
| plaindata | `DataDisc` + 不可自动启动 | 兜底分类 |
| audio | `AudioCd` | `.cda` 降级启发式 |
| dreamcast | `DreamcastGame` | 引导签名（**注入**，非真实扇区） |

**测试的局限**（务必知道）：

- 全部走 `--probe-dir`，测的是文件系统层分类，**不是**真实光驱
- `dreamcast` 用 `--boot-magic=dreamcast` 注入签名，验证的是"给定签名后分类正确"，
  **不是**验证扇区 0 真的能读出来
- `audio` 走 `.cda` 降级路径，没测 TOC 解析

`tests/IsoForge` 是个极简 ISO9660 生成器，用来造测试盘（`*.iso` 和目录树两种输出）。
没有 Joliet、没有长文件名、没有多区段，够用即可。

## 八、环境备注

- 目标框架 `net10.0-windows`，`PlatformTarget=x64`
- 需要 .NET 10 SDK；`publish` 默认框架依赖，目标机器需 .NET 10 桌面运行时
- `Emulators\` 里是第三方二进制（PCSX2 1.7.3869 + RetroArch 1.15.0），**不入版本库**
- 开发期为了绕过沙箱建过目录联接 `E:\Game\光盘启动器 → E:\Game\Discern`。
  `PathSearch.GetRealPath` 就是为此加的，删掉联接后依然正确工作
