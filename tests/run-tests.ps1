# Discern 识别引擎回归测试
#
# 在没有物理光驱、没有管理员权限的环境下验证识别规则：
#   1. 用 IsoForge 把每个 spec 展开成真实目录树（等价于光盘的文件层视图）
#   2. 调用 Discern.exe --probe-dir 做识别，结果写入临时文件
#   3. 断言 KIND / 序列号 / 可启动性
#
# 同时对每个 spec 生成真实 ISO，供有管理员权限时挂载做端到端验证。

$ErrorActionPreference = 'Stop'

$root      = Split-Path -Parent $PSScriptRoot
$isoForge  = Join-Path $root 'tests\IsoForge\bin\Release\net10.0\IsoForge.exe'
$launcher  = Join-Path $root 'Discern\bin\Release\net10.0-windows\Discern.exe'
$isoDir    = Join-Path $root 'tests\iso'
$treeDir   = Join-Path $root 'tests\trees'
$outDir    = Join-Path $root 'tests\out'

foreach ($p in @($isoForge, $launcher)) {
    if (-not (Test-Path $p)) { throw "缺少构建产物: $p" }
}

Remove-Item $treeDir -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $outDir  -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $treeDir, $outDir | Out-Null

# slug, 期望 Kind, 期望 Serial, 期望可启动, 额外参数
$cases = @(
    @{ Spec='ps2';       Kind='Ps2Game';       Serial='SLUS_200.62'; Launch=$true;  Extra=@() },
    @{ Spec='ps1';       Kind='Ps1Game';       Serial='SLES_123.45'; Launch=$true;  Extra=@() },
    @{ Spec='pcgame';    Kind='PcGame';        Serial=$null;         Launch=$true;  Extra=@() },
    @{ Spec='dvd';       Kind='DvdVideo';      Serial=$null;         Launch=$true;  Extra=@() },
    @{ Spec='plaindata'; Kind='DataDisc';      Serial=$null;         Launch=$false; Extra=@() },
    @{ Spec='audio';     Kind='AudioCd';       Serial=$null;         Launch=$true;  Extra=@() },
    @{ Spec='dreamcast'; Kind='DreamcastGame'; Serial=$null;         Launch=$true;  Extra=@('--boot-magic=dreamcast') }
)

function Read-Field($text, $name) {
    $m = [regex]::Match($text, "(?m)^$([regex]::Escape($name))\s*:\s*(.*)$")
    if ($m.Success) { return $m.Groups[1].Value.Trim() }
    return ''
}

$fail = 0
$rows = @()

foreach ($c in $cases) {
    $spec  = Join-Path $isoDir "$($c.Spec).json"
    $iso   = Join-Path $isoDir "$($c.Spec).iso"
    $tree  = Join-Path $treeDir $c.Spec
    $out   = Join-Path $outDir "$($c.Spec).txt"

    & $isoForge $spec $iso   | Out-Null
    & $isoForge $spec $tree --tree | Out-Null

    $args = @('--probe-dir', $tree, "--out=$out") + $c.Extra
    $proc = Start-Process -FilePath $launcher -ArgumentList $args -Wait -PassThru -WindowStyle Hidden

    if (-not (Test-Path $out)) { throw "探测没有产出结果: $($c.Spec)" }

    $text = Get-Content $out -Raw -Encoding UTF8
    $kind   = Read-Field $text 'KIND'
    $serial = Read-Field $text '序列号'
    $launch = Read-Field $text '可自动启动'

    $ok = ($kind -eq $c.Kind)
    if ($ok -and $c.Serial) { $ok = ($serial -eq $c.Serial) }
    if ($ok) { $ok = ($launch -eq $c.Launch.ToString()) }

    if (-not $ok) { $fail++ }

    $rows += [pscustomobject]@{
        用例      = $c.Spec
        期望      = $c.Kind
        实际      = $kind
        序列号    = $serial
        结果      = $(if ($ok) { 'PASS' } else { 'FAIL' })
    }
}

$rows | Format-Table -AutoSize

Write-Host ''
if ($fail -eq 0) {
    Write-Host "全部 $($cases.Count) 个用例通过" -ForegroundColor Green
} else {
    Write-Host "$fail / $($cases.Count) 个用例失败" -ForegroundColor Red
    Write-Host "详细输出见 $outDir"
}

exit $fail
