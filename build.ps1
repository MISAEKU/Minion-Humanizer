#requires -Version 5.1
<#
    构建 MinionHumanizer，并把可加载的产物收集到 dist/MinionHumanizer/。

    这个脚本会自动找齐两样东西，正常情况下你不需要配任何环境变量：

      1) .NET 10 SDK
         - 优先用系统里已装的（dotnet --list-sdks 里要有 10.x）
         - 没有就用本仓库自带的 ../.toolchain/dotnet/dotnet.exe

      2) Dalamud 程序集目录（DalamudLibPath）
         - 国服：%APPDATA%\XIVLauncherCN\addon\Hooks\dev\
         - 国际服：%APPDATA%\XIVLauncher\addon\Hooks\dev\
         - 或环境变量 DALAMUD_HOME

    用法：
      ./build.ps1                     # Release 构建
      ./build.ps1 -Configuration Debug
      ./build.ps1 -Zip                # 额外打包 dist/MinionHumanizer.zip，用于自建 Dalamud 仓库
#>
[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string] $Configuration = 'Release',
    [switch] $Zip
)

$ErrorActionPreference = 'Stop'

$root      = $PSScriptRoot
$project   = Join-Path $root 'MinionHumanizer/MinionHumanizer.csproj'
$outputDir = Join-Path $root "MinionHumanizer/bin/$Configuration"
$distDir   = Join-Path $root 'dist/MinionHumanizer'

# ── 1) 找 SDK ─────────────────────────────────────────────────────────────
function Resolve-Dotnet {
    $candidates = @()

    $bundled = Join-Path $root '../.toolchain/dotnet/dotnet.exe'
    if (Test-Path $bundled) { $candidates += (Resolve-Path $bundled).Path }

    $onPath = (Get-Command dotnet.exe -ErrorAction SilentlyContinue).Source
    if ($onPath) { $candidates += $onPath }

    foreach ($candidate in $candidates) {
        try {
            $sdks = & $candidate --list-sdks 2>$null
        } catch {
            continue
        }
        if ($sdks -match '(?m)^10\.') { return $candidate }
    }
    return $null
}

$dotnet = Resolve-Dotnet
if (-not $dotnet) {
    throw @"
找不到 .NET 10 SDK。

请任选一种方式：
  a) 安装 .NET 10 SDK：https://dotnet.microsoft.com/download/dotnet/10.0
  b) 或者把便携版 SDK 解压到 $(Join-Path $root '../.toolchain/dotnet')（内含 dotnet.exe 即可）
"@
}
Write-Host "==> 使用 SDK: $dotnet" -ForegroundColor Cyan

# ── 2) 找 Dalamud 程序集 ──────────────────────────────────────────────────
function Resolve-DalamudLib {
    $candidates = @(
        (Join-Path $env:APPDATA 'XIVLauncherCN\addon\Hooks\dev'),   # 国服
        (Join-Path $env:APPDATA 'XIVLauncher\addon\Hooks\dev')      # 国际服
    )
    if ($env:DALAMUD_HOME) { $candidates = @($env:DALAMUD_HOME) + $candidates }

    foreach ($dir in $candidates) {
        if ($dir -and (Test-Path (Join-Path $dir 'Dalamud.dll'))) { return $dir }
    }
    return $null
}

$lib = Resolve-DalamudLib
if ($lib) {
    Write-Host "==> 使用 Dalamud 程序集: $lib" -ForegroundColor Cyan
} else {
    Write-Warning "没找到 Dalamud 程序集目录。构建会失败。请先让 XIVLauncher 至少跑过一次 Dalamud。"
}

# ── 3) 构建 ───────────────────────────────────────────────────────────────
Write-Host "==> 构建 $project ($Configuration)" -ForegroundColor Cyan
& $dotnet build $project -c $Configuration
if ($LASTEXITCODE -ne 0) {
    throw "dotnet build 失败（exit $LASTEXITCODE）"
}

if (-not (Test-Path $outputDir)) {
    throw "找不到构建输出目录：$outputDir"
}

# ── 4) 收集产物 ───────────────────────────────────────────────────────────
Write-Host "==> 收集产物到 $distDir" -ForegroundColor Cyan
if (Test-Path $distDir) {
    Remove-Item $distDir -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $distDir | Out-Null

# Dalamud 从 dev location 加载插件只需要这两个文件。
# deps.json 不是必需的（它会试图解析 Dalamud 自身的程序集），所以不放进 dist。
foreach ($name in @('MinionHumanizer.dll', 'MinionHumanizer.json')) {
    $source = Join-Path $outputDir $name
    if (-not (Test-Path $source)) { throw "产物缺失：$source" }
    Copy-Item $source $distDir -Force
}

Get-ChildItem $distDir | Format-Table Name, Length -AutoSize

if ($Zip) {
    $zipPath = Join-Path $root 'dist/MinionHumanizer.zip'
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }

    Compress-Archive -Path (Join-Path $distDir '*') -DestinationPath $zipPath
    Write-Host "==> 已打包 $zipPath" -ForegroundColor Green
    Write-Host "    把它的下载地址填进 repo.json 的 DownloadLinkInstall / Update。" -ForegroundColor Yellow
}

# ── 5) 保证「启动游戏时自动加载」────────────────────────────────────
# Dalamud 只会自动加载**同时**满足下面两个条件的本地插件：
#   · DevPluginLoadLocations 里有这个 dll，且 IsEnabled = true
#   · DevPluginSettings[<该 dll 路径>].StartOnBoot = true
# 缺任何一条，游戏启动时就得手動去 /xlplugins 点一次。
# 这两处都在 %APPDATA%\XIVLauncherCN\dalamudConfig.json 里，改它必须**游戏未运行**。
$autoLoadPy     = Join-Path $env:USERPROFILE '.workbuddy\binaries\python\envs\default\Scripts\python.exe'
$autoLoadScript = Join-Path $root 'tools\ensure_autoload.py'

# Python 子进程输出是 UTF-8，而 PowerShell 5.1 默认按 GBK 解码子进程输出 →
# 中文提示会变成乱码。先把控制台输出编码统一切到 UTF-8。
try {
    $OutputEncoding = [System.Text.Encoding]::UTF8
    [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
} catch {
    # 某些宿主环境不允许改控制台编码，失败不影响功能
}

Write-Host "==> 校验「启动游戏自动加载」配置" -ForegroundColor Cyan
if ((Test-Path $autoLoadPy) -and (Test-Path $autoLoadScript)) {
    & $autoLoadPy $autoLoadScript --plugin $distDir
    if ($LASTEXITCODE -eq 3) {
        Write-Host "    游戏正在运行，配置没改 —— Dalamud 退出时会用内存里的旧值覆盖磁盘。" -ForegroundColor Yellow
        Write-Host "    退出游戏后重新构建一次即可（或手动执行 autoload.cmd）。" -ForegroundColor Yellow
    }
} else {
    Write-Host "    跳过：找不到 python（$autoLoadPy）" -ForegroundColor Yellow
    Write-Host "    需要时手动执行： autoload.cmd" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "完成。" -ForegroundColor Green
Write-Host ""
Write-Host "安装（国服 XIVLauncherCN）：" -ForegroundColor Yellow
Write-Host "  开发插件位置与「启动自动加载」已由上面的步骤自动登记好，无需手动操作。" -ForegroundColor Yellow
Write-Host "  下次启动游戏它会自己加载；控制台命令是 /mhuman（/minionhuman 亦可）。" -ForegroundColor Green
