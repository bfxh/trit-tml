//! 线格式编解码的「模糊往返」测试：用确定性 PRNG 喂入数千个随机载荷，
//! 验证 `Writer`/`Reader` 的 trits 往返、响应装框/拆框往返、单字节 trit 收敛幂等，
//! 以及畸形帧返回 `None` 而不是 panic。与 `dotnet` 侧的 `WireFuzzTests` 对称。

use trit_core::value::Ternary;
use trit_rt::prng::DeterministicPrng;
use trit_rt::wire::{Reader, Writer, frame_response, trit_byte, trit_from_byte, unframe_response};

/// 从确定性 PRNG 取 `n` 个随机 trit。
fn random_trits(prng: &mut DeterministicPrng, n: usize) -> Vec<Ternary> {
    (0..n).map(|_| prng.next_trit()).collect()
}

#[test]
fn trits_round_trip_across_thousands_of_payloads() {
    let mut prng = DeterministicPrng::new(0xBEEF);
    prng.reseed(0xBEEF, 1);
    for _ in 0..3000 {
        // 长度 0..600 之间的随机值，覆盖跨字节边界（每字节 5 个 trit）。
        let len = (prng.next_u64() % 600) as usize;
        let expected = random_trits(&mut prng, len);

        let mut w = Writer::new();
        w.trits(&expected);
        let bytes = w.into_vec();

        let mut r = Reader::new(&bytes);
        let got = r.trits().expect("合法 trits 载荷必须能解");
        assert_eq!(got, expected, "trits 往返失配（len={len}）");
    }
}

#[test]
fn frame_unframe_round_trip_across_thousands_of_payloads() {
    let mut prng = DeterministicPrng::new(0xCAFE);
    prng.reseed(0xCAFE, 1);
    for _ in 0..3000 {
        // 随机长度 0..2000 的随机字节载荷。
        let len = (prng.next_u64() % 2000) as usize;
        let payload: Vec<u8> = (0..len).map(|_| (prng.next_u64() & 0xFF) as u8).collect();

        let framed = frame_response(&payload);
        let unframed = unframe_response(&framed).expect("合法帧必须能拆");
        assert_eq!(
            unframed,
            &payload[..],
            "frame/unframe 往返失配（len={len}）"
        );
    }
}

#[test]
fn single_byte_trit_convergence_is_idempotent() {
    // trit_from_byte 对 0/1/2 是恒等；对 3..=255 收敛成合法 trit 且幂等：
    // trit_byte(trit_from_byte(b)) == trit_from_byte(b)。
    for byte in 0u16..=255 {
        let byte = byte as u8;
        let trit = trit_from_byte(byte);
        let reconverged = trit_from_byte(trit_byte(trit));
        assert_eq!(trit, reconverged, "字节 {byte} 收敛必须幂等");
        // 0/1/2 必须逐位还原。
        if byte <= 2 {
            assert_eq!(trit_byte(trit), byte, "0/1/2 必须恒等还原");
        }
    }
}

#[test]
fn malformed_frames_return_none_not_panic() {
    // 长度不足 4 字节。
    assert!(unframe_response(&[]).is_none());
    assert!(unframe_response(&[1, 2, 3]).is_none());

    // 声明长度超过实际剩余：构造一个 u32 长度 = 0xFFFF_FFF0 的帧。
    let mut framed = vec![0xFF, 0xFF, 0xFF, 0xF0];
    framed.extend_from_slice(&[1, 2, 3]);
    assert!(unframe_response(&framed).is_none(), "溢出长度必须返回 None");

    // 声明的 4 + len 比实际多 1 字节。
    let payload = [9u8, 8, 7, 6, 5];
    let mut framed = frame_response(&payload);
    framed[3] += 1; // 把长度从 5 改成 6，超出实际 5 字节。
    assert!(
        unframe_response(&framed).is_none(),
        "长度比实际多必须返回 None"
    );
}

#[test]
fn reader_overrun_returns_decode_error_not_panic() {
    // 一个声明 100 个 trit 的载荷，但只给了 1 个字节（最多 5 个 trit）。
    let mut w = Writer::new();
    w.u32(100);
    w.bytes(&[0]);
    let bytes = w.into_vec();
    let mut r = Reader::new(&bytes);
    assert!(r.trits().is_err(), "长度不足的 trits 必须报 Decode 错误");

    // 截断的原语读取。
    let mut w = Writer::new();
    w.u8(1).u32(2); // 只有 5 字节，没有 u64 的 8 字节。
    let bytes = w.into_vec();
    let mut r = Reader::new(&bytes);
    let _ = r.u8();
    let _ = r.u32();
    assert!(r.u64().is_err(), "截断的 u64 读取必须报错误");
}
