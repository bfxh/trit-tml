//! 只用上游 `trit-gpu`（不改库）测「同一个 A 矩阵乘多个图案」到底快不快。
//!
//! 结论用途：决定 pattern 扫描要不要支持「一次匹配 N 个图案」。
//! 现在的实现把每个窗口摊成一行、n 恒等于 1，等于拿 GPU 做一个巨型点积——
//! 这是 GPU 最不擅长的形状。n 提到 64 才是矩阵乘。

use std::time::Instant;

#[cfg(feature = "gpu")]
fn main() {
    let m: usize = 32768; // 窗口数（>65532×words 会踩上游 dispatch 网格上限直接 panic）
    let words: usize = 2; // 32 位字 = 32 trit，64 长度图案 = 2 字
    for n in [1usize, 8, 64, 256] {
        let a_pos: Vec<u32> = (0..m * words)
            .map(|i| (i as u32).wrapping_mul(2654435761))
            .collect();
        let a_neg: Vec<u32> = (0..m * words)
            .map(|i| (i as u32).wrapping_mul(40503))
            .collect();
        let b_pos: Vec<u32> = (0..n * words)
            .map(|i| (i as u32).wrapping_mul(97))
            .collect();
        let b_neg: Vec<u32> = (0..n * words)
            .map(|i| (i as u32).wrapping_mul(31))
            .collect();

        let t = Instant::now();
        let out = trit_gpu::forward::matmul_packed(m, n, words, &a_pos, &a_neg, &b_pos, &b_neg);
        let dt = t.elapsed();
        let cpu_t = Instant::now();
        // CPU 位并行镜像：m 行各做一次 dot_words（用满 n 列里的第一列）
        let mut acc = 0i64;
        for row in 0..m {
            let base = row * words;
            acc += trit_gpu::dot_words(
                &a_pos[base..base + words],
                &a_neg[base..base + words],
                &b_pos[..words],
                &b_neg[..words],
            );
        }
        let cpu_dt = cpu_t.elapsed();
        println!(
            "n={n:<4} gpu={:>8.1}ms  cpu(同等工作量,1列)={:>7.1}ms  out_len={} (cpu总计 {acc})",
            dt.as_secs_f64() * 1e3,
            cpu_dt.as_secs_f64() * 1e3,
            out.as_ref().map(|v| v.len()).unwrap_or(0)
        );
    }
}

#[cfg(not(feature = "gpu"))]
fn main() {
    eprintln!(
        "这个示例要直连上游 GPU 后端：cargo run --release -p trit-rt --example gpu_matmul_bench --features gpu"
    );
}
