#!/usr/bin/env bash
# trit-tml Android 真机 benchmark (bash)
# 用途同 scripts/android-bench.ps1，但在 Linux/macOS 上 --build 能真的跑 cargo-ndk 交叉编译。
#
#   ./scripts/android-bench.sh --device arm64 --build
#   ./scripts/android-bench.sh --device arm64
#   ./scripts/android-bench.sh --device armeabi --skip-scan
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
REMOTE_DIR="/data/local/tmp/trit-bench"
TIMESTAMP="$(date +%Y%m%d-%H%M%S)"
OUT_DIR="$REPO_ROOT/artifacts"
mkdir -p "$OUT_DIR"

DEVICE="arm64"
BUILD=0
SKIP_SCAN=0
SKIP_MEM=0
SCAN_BIN=""
MEM_BIN=""

while [[ $# -gt 0 ]]; do
    case "$1" in
        --device)       DEVICE="$2"; shift 2 ;;
        --build)        BUILD=1; shift ;;
        --skip-scan)    SKIP_SCAN=1; shift ;;
        --skip-mem)     SKIP_MEM=1; shift ;;
        --scan-bin)     SCAN_BIN="$2"; shift 2 ;;
        --mem-bin)      MEM_BIN="$2"; shift 2 ;;
        *) echo "unknown arg: $1"; exit 1 ;;
    esac
done

case "$DEVICE" in
    arm64)   TARGET="aarch64-linux-android" ;;
    armeabi) TARGET="armv7-linux-androideabi" ;;
    *) echo "--device must be arm64 or armeabi"; exit 1 ;;
esac

echo "== trit-tml Android Benchmark =="
echo "device=$DEVICE  target=$TARGET  repo=$REPO_ROOT"

# ── preflight ──────────────────────────────────────────────────────────────
command -v adb >/dev/null || { echo "adb not found"; exit 1; }
adb devices | awk 'NR>1 && $2=="device" {found=1} END{exit found?0:1}' || \
    { echo "no adb device connected"; exit 1; }

# ── build ──────────────────────────────────────────────────────────────────
if [[ $BUILD -eq 1 ]]; then
    command -v cargo-ndk >/dev/null || { echo "cargo-ndk not found: cargo install cargo-ndk"; exit 1; }
    echo "[build] $Target release-mobile no-default-features ..."
    (cd "$REPO_ROOT" && \
     cargo ndk -t "$TARGET" -- build -p trit-rt \
               --profile release-mobile --no-default-features \
               --example scan_bench --example mem_bench)
    echo "  done."
fi

# ── locate binary ──────────────────────────────────────────────────────────
find_bin() {
    local name="$1"
    [[ "$name" == "scan_bench" && -n "$SCAN_BIN" ]] && { echo "$SCAN_BIN"; return; }
    [[ "$name" == "mem_bench"  && -n "$MEM_BIN"  ]] && { echo "$MEM_BIN";  return; }
    for p in \
        "$REPO_ROOT/target/$TARGET/release-mobile/examples/$name" \
        "$REPO_ROOT/target/$TARGET/release/examples/$name"; do
        [[ -f "$p" ]] && { echo "$p"; return; }
    done
    echo "not found" >&2; exit 1
}

[[ $SKIP_SCAN -eq 0 ]] && SCAN_BIN="$(find_bin scan_bench)"
[[ $SKIP_MEM  -eq 0 ]] && MEM_BIN="$(find_bin mem_bench)"

# ── push ───────────────────────────────────────────────────────────────────
adb shell "rm -rf $REMOTE_DIR && mkdir -p $REMOTE_DIR" >/dev/null
push_bin() {
    local local="$1" remote="$2"
    echo "[push] $(basename "$local") -> $remote"
    adb push "$local" "$remote" >/dev/null
    adb shell "chmod 755 $remote" >/dev/null
}
[[ -n "${SCAN_BIN:-}" ]] && push_bin "$SCAN_BIN" "$REMOTE_DIR/scan_bench"
[[ -n "${MEM_BIN:-}"  ]] && push_bin "$MEM_BIN"  "$REMOTE_DIR/mem_bench"

# ── scan_bench ─────────────────────────────────────────────────────────────
if [[ -n "${SCAN_BIN:-}" ]]; then
    echo ""
    echo "--- scan_bench ---"
    echo "len(trit)  scan(ms)  Mwin/s  backend"
    for len in 16384 65536 262144 1048576; do
        out="$(adb shell "$REMOTE_DIR/scan_bench --len $len 2>&1")"
        ms="$(echo "$out"  | grep -oP 'scan=\K[\d.]+')"
        backend="$(echo "$out" | grep -oP 'backend=\K\S+')"
        windows="$(echo "$out" | grep -oP 'windows=\K\d+')"
        mws="$(awk -v w="$windows" -v m="$ms" 'BEGIN{ if(m>0) printf "%.2f", w/m/1000; else print 0 }')"
        printf "%-10s %-10s %-8s %s\n" "$len" "$ms" "$mws" "$backend"
    done
fi

# ── mem_bench ──────────────────────────────────────────────────────────────
if [[ -n "${MEM_BIN:-}" ]]; then
    echo ""
    echo "--- mem_bench ---"
    echo "ticks   rss(MB)  peak(MB)"
    for ticks in 2000 10000 20000; do
        out="$(adb shell "$REMOTE_DIR/mem_bench --ticks $ticks 2>&1")"
        last="$(echo "$out" | grep '^done' | tail -1)"
        rss="$(echo "$last"  | grep -oP 'rss=\K[\d.]+')"
        peak="$(echo "$last" | grep -oP 'peak=\K[\d.]+')"
        printf "%-8s %-10s %s\n" "$ticks" "${rss:-?}" "${peak:-?}"
    done
fi

# ── device info ────────────────────────────────────────────────────────────
echo ""
echo "--- device ---"
adb shell -c 'printf "build=%s\nsdk=%s\nabis=%s\nmem_total_kb=%s\ncpu_count=%s\ncpu_model=%s\n" \
    "$(getprop ro.build.display.id)" \
    "$(getprop ro.build.version.sdk)" \
    "$(getprop ro.product.cpu.abilist)" \
    "$(grep MemTotal /proc/meminfo 2>/dev/null | awk "{print \\$2}")" \
    "$(nproc 2>/dev/null || grep -c processor /proc/cpuinfo)" \
    "$(grep -oP "Model name.*:\\s*\\K.*" /proc/cpuinfo 2>/dev/null | head -1)"'

# ── cleanup ────────────────────────────────────────────────────────────────
adb shell "rm -rf $REMOTE_DIR" >/dev/null
echo "done."
