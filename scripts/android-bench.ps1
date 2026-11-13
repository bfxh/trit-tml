# trit-tml 真机 benchmark 脚本（Android）
# ========================================
# 把 scan_bench / mem_bench 等 example 推到 Android 真机跑，
# 收集输出 + /proc/self/status，拿真实的 scan 延迟和 RSS。
#
# 用法（Windows 或 PowerShell 7）：
#   ./scripts/android-bench.ps1 -Device arm64 -Build            # Linux/macOS 先编译再 push
#   ./scripts/android-bench.ps1 -Device arm64                  # 假设已有预编译 binary
#   ./scripts/android-bench.ps1 -Device arm64 -SkipScan         # 只跑 mem_bench
#   ./scripts/android-bench.ps1 -Device armeabi -OnlyScan       # 只跑 scan_bench
#
# 产出：
#   - 终端打印汇总表（scan ms / mem MB / CPU 占用）
#   - ./artifacts/android-bench-<timestamp>.json（完整原始输出）

param(
    [Parameter(Mandatory)]
    [ValidateSet("arm64", "armeabi")]
    [string]$Device,

    # 从源码编译 Android binary（需要 cargo-ndk + NDK，Linux/macOS 生效）
    [switch]$Build,

    [switch]$SkipScan,
    [switch]$SkipMem,

    # 直接指定 artifact（跳过自动查找）
    [string]$ScanBinary = "",
    [string]$MemBinary = ""
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$RemoteDir = "/data/local/tmp/trit-bench"
$Timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
$OutDir = Join-Path $RepoRoot "artifacts"
if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }

$TargetMap = @{
    "arm64"   = "aarch64-linux-android"
    "armeabi" = "armv7-linux-androideabi"
}
$Target = $TargetMap[$Device]

# ── 1. 前置检查 ──────────────────────────────────────────────────────────────

Write-Host "== trit-tml Android Benchmark ==" -ForegroundColor Cyan
Write-Host "device=$Device  target=$Target  repo=$RepoRoot"

$adb = Get-Command adb -ErrorAction SilentlyContinue
if (-not $adb) { Write-Error "adb 未找到，请先装 Android SDK platform-tools 并加到 PATH" }

$devices = (adb devices | Select-Object -Skip 1 | ForEach-Object { ($_ -split "`t")[0] }) | Where-Object { $_ -and $_ -ne "List of devices attached" }
if ($devices.Count -eq 0) { Write-Error "adb 没看到任何设备；连上真机开 USB 调试，或 adb connect <ip>:<port>" }
Write-Host "adb device: $($devices -join ', ')" -ForegroundColor Green

# ── 2. 编译（可选） ───────────────────────────────────────────────────────────

if ($Build) {
    $os = [System.Environment]::OSVersion.Platform
    if ($os -eq [System.PlatformID]::Win32NT) {
        Write-Warning "Windows 上没有 Android NDK linker，--build 会在 link 阶段失败；" +
                      "建议在 Linux runner 上跑，或用 CI artifact 下载预编译 binary。"
    }
    $cargoNdk = Get-Command "cargo-ndk" -ErrorAction SilentlyContinue
    if (-not $cargoNdk) { Write-Error "没找到 cargo-ndk；先装：cargo install cargo-ndk" }

    Write-Host "`n[build] $Target release-mobile no-default-features ..." -ForegroundColor Cyan
    Push-Location $RepoRoot
    try {
        foreach ($ex in @("scan_bench", "mem_bench")) {
            Write-Host "  building --example $ex ..."
            cargo ndk -t $Target -- build -p trit-rt --profile release-mobile --no-default-features --example $ex
            if ($LASTEXITCODE -ne 0) { Write-Error "build --example $ex 失败" }
        }
    }
    finally { Pop-Location }
    Write-Host "  done." -ForegroundColor Green
}

# ── 3. 定位 binary ───────────────────────────────────────────────────────────

function Find-Binary([string]$Example) {
    if ($Example -eq "scan_bench" -and $ScanBinary) { return $ScanBinary }
    if ($Example -eq "mem_bench" -and $MemBinary) { return $MemBinary }

    # cargo-ndk 产出路径：target/<target>/release-mobile/examples/<name>
    $candidates = @(
        (Join-Path $RepoRoot "target\$Target\release-mobile\examples\$Example"),
        (Join-Path $RepoRoot "target\$Target\release-mobile\examples\$Example.exe"),
        (Join-Path $RepoRoot "target\$Target\release\examples\$Example"),
        (Join-Path $RepoRoot "target\$Target\release\examples\$Example.exe")
    )
    foreach ($c in $candidates) {
        if (Test-Path $c) { return $c }
    }
    Write-Error "找不到 $Example binary：期望在 target/$Target/release-mobile/examples/$Example 附近"
}

$scanBin = if ($SkipScan) { $null } else { Find-Binary "scan_bench" }
$memBin  = if ($SkipMem)  { $null } else { Find-Binary "mem_bench" }

# ── 4. push + chmod ──────────────────────────────────────────────────────────

adb shell "rm -rf $RemoteDir && mkdir -p $RemoteDir" | Out-Null

$binaries = @()
if ($scanBin) { $binaries += @{ Local = $scanBin; Remote = "$RemoteDir/scan_bench" } }
if ($memBin)  { $binaries += @{ Local = $memBin;  Remote = "$RemoteDir/mem_bench"  } }

foreach ($b in $binaries) {
    Write-Host "`n[push] $(Split-Path $b.Local -Leaf) -> $($b.Remote)" -ForegroundColor Cyan
    adb push $b.Local $b.Remote | Out-Null
    adb shell "chmod 755 $($b.Remote)" | Out-Null
    $remoteSize = (adb shell "stat -c %s $($b.Remote)").Trim()
    Write-Host "  pushed, size=$remoteSize bytes" -ForegroundColor Green
}

# ── 5. scan_bench ────────────────────────────────────────────────────────────

if ($scanBin) {
    $scanSizes = @(1 << 14, 1 << 16, 1 << 18, 1 << 20)  # 16K .. 1M trits
    $scanResults = @()

    foreach ($len in $scanSizes) {
        Write-Host "`n[scan_bench] len=$len ($([math]::Round($len/1024)) K trits)" -ForegroundColor Cyan
        $out = adb shell "$RemoteDir/scan_bench --len $len 2>&1"
        $rssBefore = (adb shell "cat /proc/self/status 2>/dev/null | grep VmRSS || echo 0").Trim()
        Write-Host $out

        $scanResults += [pscustomobject]@{
            Len       = $len
            Ms        = (($out | Select-String "scan=(\S+)ms" | Select-Object -First 1).Matches.Groups[1].Value)
            Backend   = (($out | Select-String "backend=(\S+)" | Select-Object -First 1).Matches.Groups[1].Value)
            Windows   = (($out | Select-String "windows=(\d+)" | Select-Object -First 1).Matches.Groups[1].Value)
        }
    }

    Write-Host "`n--- scan_bench 汇总 ---" -ForegroundColor Yellow
    Write-Host ("{0,-12} {1,-12} {2,-12} {3,-12}" -f "len(trit)", "scan(ms)", "Mwin/s", "backend")
    foreach ($r in $scanResults) {
        $ms = [double]$r.Ms
        $mws = if ($ms -gt 0) { [math]::Round([double]$r.Windows / $ms / 1000.0, 2) } else { 0 }
        Write-Host ("{0,-12} {1,-12} {2,-12} {3,-12}" -f $r.Len, $r.Ms, $mws, $r.Backend)
    }
}

# ── 6. mem_bench ─────────────────────────────────────────────────────────────

if ($memBin) {
    $memTicks = @(2_000, 10_000, 20_000)
    $memResults = @()

    foreach ($ticks in $memTicks) {
        Write-Host "`n[mem_bench] ticks=$ticks" -ForegroundColor Cyan
        $out = adb shell "$RemoteDir/mem_bench --ticks $ticks 2>&1"
        Write-Host $out

        # 取最后一行 "done rss=X.XMB peak=Y.YMB"
        $lastDone = ($out -split "`n" | Where-Object { $_ -match "^done" } | Select-Object -Last 1)
        if ($lastDone -match "rss=([\d.]+)MB") {
            $memResults += [pscustomobject]@{
                Ticks = $ticks
                RssMB = [double]$Matches[1]
                PeakMB = if ($lastDone -match "peak=([\d.]+)MB") { [double]$Matches[1] } else { 0 }
            }
        }
    }

    Write-Host "`n--- mem_bench 汇总 ---" -ForegroundColor Yellow
    Write-Host ("{0,-12} {1,-12} {2,-12}" -f "ticks", "rss(MB)", "peak(MB)")
    foreach ($r in $memResults) {
        Write-Host ("{0,-12} {1,-12} {2,-12}" -f $r.Ticks, $r.RssMB, $r.PeakMB)
    }
}

# ── 7. 收集系统信息 ─────────────────────────────────────────────────────────

$sysInfo = @{
    Build      = (adb shell "getprop ro.build.display.id").Trim()
    Sdk        = (adb shell "getprop ro.build.version.sdk").Trim()
    ABIs       = (adb shell "getprop ro.product.cpu.abilist").Trim()
    MemTotalKB = (adb shell "grep MemTotal /proc/meminfo 2>/dev/null | awk '{print \$2}'").Trim()
    CPUCount   = (adb shell "nproc 2>/dev/null || grep -c processor /proc/cpuinfo").Trim()
    # CPU 型号（老设备的 cpuinfo 里存 Model name）
    CpuModel   = (adb shell "grep 'Model name' /proc/cpuinfo 2>/dev/null | head -1 | cut -d: -f2-").Trim()
}

Write-Host "`n--- 设备信息 ---" -ForegroundColor Yellow
$sysInfo.GetEnumerator() | ForEach-Object { Write-Host ("  {0,-12} {1}" -f $_.Key, $_.Value) }

# ── 8. 存档 JSON ─────────────────────────────────────────────────────────────

$artifactPath = Join-Path $OutDir "android-bench-$Timestamp.json"
$report = @{
    timestamp = $Timestamp
    device    = $Device
    target    = $Target
    sys       = $sysInfo
    scan      = if ($scanBin) { $scanResults } else { @() }
    mem       = if ($memBin)  { $memResults }  else { @() }
}
$report | ConvertTo-Json -Depth 4 | Out-File $artifactPath -Encoding utf8
Write-Host "`n完整报告：$artifactPath" -ForegroundColor Green

adb shell "rm -rf $RemoteDir" | Out-Null
Write-Host "清理完成。"
