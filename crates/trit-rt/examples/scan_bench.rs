//! 大规模 pattern 扫描的实测台（开发用，不进 DLL）。
//!
//! 目的只有一个：把「一个 4M trit 世界投影跑一次 64 长度图案扫描」到底花多少时间、
//! 峰值内存多大、GPU 能不能接住，用数字摆出来——而不是靠猜。
//!
//! 用法：
//!   cargo run --release -p trit-rt --example scan_bench
//!   cargo run --release -p trit-rt --example scan_bench --features gpu
//!   cargo run --release -p trit-rt --example scan_bench --features gpu -- --gpu on

use std::sync::atomic::AtomicBool;
use std::sync::{Arc, Mutex};
use std::time::Instant;

use trit_rt::gpu::{GpuBackend, GpuConfig, GpuPolicy};
use trit_rt::scan::{JobState, ScanJob, ScanKind, ScanRequest};

fn main() {
    let args: Vec<String> = std::env::args().collect();
    let policy = match args.iter().position(|a| a == "--gpu") {
        Some(i) => match args.get(i + 1).map(String::as_str) {
            Some("on") => GpuPolicy::On,
            Some("off") => GpuPolicy::Off,
            _ => GpuPolicy::Auto,
        },
        None => GpuPolicy::Auto,
    };
    let len: usize = args
        .iter()
        .position(|a| a == "--len")
        .and_then(|i| args.get(i + 1))
        .and_then(|v| v.parse().ok())
        .unwrap_or(1 << 22);
    let pattern_len: usize = 64;

    // 确定性伪随机三值世界（1/3 各态），保证每次跑的是同一份数据。
    let values: Vec<u8> = (0..len)
        .map(|i| {
            let x = (i as u64).wrapping_mul(0x9E37_79B9_7F4A_7C15);
            ((x >> 33) % 3) as u8
        })
        .collect();
    // 图案取中段的一段，保证至少有一个满分命中。
    let start = len / 2;
    let pattern: Vec<u8> = values[start..start + pattern_len].to_vec();

    let request = ScanRequest {
        owner: "bench".to_string(),
        kind: "pattern".to_string(),
        trits: values,
        pattern,
        threshold: 0,
        chunk: 0,
        oneshot: true,
        priority: 0,
        top: 0,
    };

    let backend = Arc::new(Mutex::new(GpuBackend::new(GpuConfig {
        policy,
        min_trits: trit_rt::gpu::DEFAULT_MIN_TRITS,
    })));

    // 探测一定要单独计时：它是 wgpu 初始化 + 1M trit 的 CPU/GPU 对拍，
    // 在游戏里发生在主线程上，是「开 GPU 到底卡不卡装载」的关键数字。
    let tp = Instant::now();
    backend.lock().unwrap().probe(len);
    let probed = tp.elapsed();

    let t0 = Instant::now();
    let job = ScanJob::new(
        1,
        &request,
        ScanKind::Pattern,
        4096,
        Arc::new(AtomicBool::new(false)),
        Arc::new(Default::default()),
        Arc::new(Mutex::new(Vec::new())),
        Arc::clone(&backend),
    );
    let built = t0.elapsed();

    let t1 = Instant::now();
    let record = job.run_oneshot();
    let ran = t1.elapsed();

    let value = record.result_value();
    let matches = value
        .get("matches")
        .and_then(|m| m.as_array())
        .map(|a| a.len())
        .unwrap_or(0);
    let status = backend.lock().unwrap().status();
    let windows = len.saturating_sub(pattern_len) + 1;

    println!(
        "len={len} pattern={pattern_len} windows={windows} backend={}",
        record.backend
    );
    println!("probe={:.1}ms", probed.as_secs_f64() * 1e3);
    println!(
        "build={:.1}ms scan={:.1}ms ({:.1} Mwin/s) matches={} state={:?}",
        built.as_secs_f64() * 1e3,
        ran.as_secs_f64() * 1e3,
        windows as f64 / ran.as_secs_f64() / 1e6,
        matches,
        JobState::Done
    );
    println!(
        "gpu: built_with_gpu={} available={} enabled={} adapter={:?} backend={:?} jobs={} gpu_jobs={} fallbacks={} reason={:?}",
        status.built_with_gpu,
        status.available,
        status.enabled,
        status.adapter,
        status.backend,
        status.jobs,
        status.gpu_jobs,
        status.fallbacks,
        status.reason
    );
}
