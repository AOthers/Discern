# Discern build script
#
# - Keeps NuGet / dotnet CLI state inside the repo (.dotnet, .nuget) so the build
#   never touches the user profile.
# - Forces -m:1 (single MSBuild node). Multi-node builds need named pipes, which
#   are unavailable in this sandboxed shell; single-node is equivalent in output.
#
# Usage:
#   .\build.ps1                 build Release
#   .\build.ps1 -Test           build Release, then run the recognition tests
#   .\build.ps1 -Debug          build Debug
#   .\build.ps1 -Publish        build + publish a clean copy to .\app\
#   .\build.ps1 -Publish -SelfContained   bundle the .NET runtime too (~150 MB)
#
# -Publish output layout (Emulators is shared, not copied):
#   app\
#     Discern.exe
#     Data\library.json
#   Emulators\        <- already next to app\, PathSearch finds it one level up
#
# The published exe path is what the "开机常驻" toggle writes to the registry,
# so publish once and launch from app\ rather than from bin\Release.

param(
    [switch]$Test,
    [switch]$Debug,
    [switch]$Clean,
    [switch]$Publish,
    [switch]$SelfContained
)

$ErrorActionPreference = 'Stop'

$root      = $PSScriptRoot
$config    = if ($Debug) { 'Debug' } else { 'Release' }
$solution  = Join-Path $root 'Discern.sln'
$project   = Join-Path $root 'Discern\Discern.csproj'
$appDir    = Join-Path $root 'app'

$env:DOTNET_CLI_HOME                = Join-Path $root '.dotnet'
$env:NUGET_PACKAGES                 = Join-Path $root '.nuget'
$env:DOTNET_NOLOGO                  = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT    = '1'

if ($Clean) {
    Write-Host "== cleaning ==" -ForegroundColor Cyan
    & dotnet clean $solution -c $config -m:1 --nologo | Out-Null
}

Write-Host "== build ($config) ==" -ForegroundColor Cyan
& dotnet build $solution -c $config -m:1 --nologo
if ($LASTEXITCODE -ne 0) { throw "build failed with exit code $LASTEXITCODE" }

$launcher = Join-Path $root "Discern\bin\$config\net10.0-windows\Discern.exe"
if (-not (Test-Path $launcher)) { throw "missing output: $launcher" }

Write-Host ""
Write-Host "output : $launcher" -ForegroundColor Green

if ($Test) {
    Write-Host ""
    Write-Host "== tests ==" -ForegroundColor Cyan
    & (Join-Path $root 'tests\run-tests.ps1')
    if ($LASTEXITCODE -ne 0) { throw "tests failed with exit code $LASTEXITCODE" }
}

if ($Publish) {
    Write-Host ""
    Write-Host "== publish -> $appDir ==" -ForegroundColor Cyan

    # app\ 会被整个删掉重建，但 Data\library.json 是用户数据（游戏库、路径、
    # 开关），必须原样带过去，否则每次发布都把配置清空。
    $savedConfig = $null
    $existingConfig = Join-Path $appDir 'Data\library.json'
    if (Test-Path $existingConfig) {
        $savedConfig = [IO.Path]::Combine([IO.Path]::GetTempPath(), "discern-library-$([guid]::NewGuid().ToString('N')).json")
        Copy-Item $existingConfig $savedConfig -Force
    }

    if (Test-Path $appDir) { Remove-Item $appDir -Recurse -Force }

    $pubArgs = @(
        'publish', $project,
        '-c', $config,
        '-r', 'win-x64',
        '-o', $appDir,
        '-m:1',
        '--nologo'
    )
    if ($SelfContained) {
        $pubArgs += '--self-contained', 'true'
    } else {
        $pubArgs += '--self-contained', 'false'
    }

    & dotnet @pubArgs
    if ($LASTEXITCODE -ne 0) { throw "publish failed with exit code $LASTEXITCODE" }

    $published = Join-Path $appDir 'Discern.exe'
    if (-not (Test-Path $published)) { throw "missing published exe: $published" }

    if ($savedConfig) {
        $dest = Join-Path $appDir 'Data\library.json'
        New-Item -ItemType Directory -Force -Path (Split-Path $dest) | Out-Null
        Copy-Item $savedConfig $dest -Force
        Remove-Item $savedConfig -Force
        Write-Host "preserved : $dest (用户配置已保留)" -ForegroundColor Green
    }

    Write-Host ""
    Write-Host "published : $published" -ForegroundColor Green
    Write-Host "emulators : $(Join-Path $root 'Emulators') (shared, not copied)" -ForegroundColor Green
    Write-Host ""
    Write-Host "从 app\Discern.exe 启动；勾选「开机常驻」后注册表记录的也是这个路径。" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "done." -ForegroundColor Green
