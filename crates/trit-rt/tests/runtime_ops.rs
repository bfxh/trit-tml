//! v2 能力的内核级集成测试：直接走 `TritRuntime::dispatch`（不经过 C ABI），
//! 覆盖「提交 → 任务池执行 → 回收结果 → 预算记账」这条链路，以及 GPU / 指标。

use std::time::{Duration, Instant};

use trit_rt::RuntimeConfig;
use trit_rt::runtime::TritRuntime;
use trit_rt::wire::op;

fn runtime(config_json: &str) -> TritRuntime {
    let config = RuntimeConfig::from_json(config_json.as_bytes()).expect("配置合法");
    TritRuntime::new(config)
}

/// 轮询直到拿到 `count` 条结果。
fn poll_until(runtime: &mut TritRuntime, count: usize, timeout: Duration) -> Vec<u8> {
    let deadline = Instant::now() + timeout;
    loop {
        let response = runtime
            .dispatch(op::JOB_POLL, br#"{"limit":16}"#)
            .expect("JOB_POLL 成功");
        let value: serde_json::Value = serde_json::from_slice(&response).unwrap();
        if value["jobs"].as_array().map(Vec::len).unwrap_or(0) >= count {
            return response;
        }
        if Instant::now() > deadline {
            panic!("超时未拿到 {count} 条结果，最后一次响应：{value}");
        }
        std::thread::sleep(Duration::from_millis(5));
    }
}

#[test]
fn a_scan_job_runs_on_the_pool_and_can_be_reclaimed() {
    let mut runtime = runtime(r#"{"parallel":{"workers":2,"scan_chunk":2}}"#);
    let response = runtime
        .dispatch(
            op::SCAN_SUBMIT,
            br#"{"owner":"mod-a","kind":"histogram","trits":[0,1,2,2]}"#,
        )
        .expect("提交成功");
    let submitted: serde_json::Value = serde_json::from_slice(&response).unwrap();
    let job = submitted["job"].as_u64().expect("返回 job id");

    let response = poll_until(&mut runtime, 1, Duration::from_secs(5));
    let polled: serde_json::Value = serde_json::from_slice(&response).unwrap();
    let jobs = polled["jobs"].as_array().expect("jobs 是数组");
    assert_eq!(jobs.len(), 1);
    assert_eq!(jobs[0]["id"], serde_json::json!(job));
    assert_eq!(jobs[0]["state"], serde_json::json!("done"));
    assert_eq!(jobs[0]["result"]["histogram"]["neg"], serde_json::json!(1));
    assert_eq!(jobs[0]["result"]["histogram"]["pos"], serde_json::json!(2));
}

#[test]
fn jobs_from_many_mods_all_complete_and_are_billed_to_their_owner() {
    let mut runtime = runtime(r#"{"parallel":{"workers":4}}"#);
    for index in 0..8 {
        let owner = format!("mod-{}", index % 3);
        let payload = format!(r#"{{"owner":"{owner}","kind":"histogram","trits":[0,1,2]}}"#);
        let _ = runtime
            .dispatch(op::SCAN_SUBMIT, payload.as_bytes())
            .expect("提交成功");
    }
    let response = poll_until(&mut runtime, 8, Duration::from_secs(10));
    let polled: serde_json::Value = serde_json::from_slice(&response).unwrap();
    assert_eq!(polled["jobs"].as_array().map(Vec::len), Some(8));

    let budget: serde_json::Value =
        serde_json::from_slice(&runtime.dispatch(op::BUDGET, &[]).unwrap()).unwrap();
    assert!(budget["totals"]["submitted"].as_u64().unwrap_or(0) >= 8);
    let mods = budget["mods"].as_array().expect("mods 是数组");
    assert_eq!(mods.len(), 3, "三个 owner 各有一份账：{mods:?}");
}

#[test]
fn pattern_scan_matches_the_scalar_reference_across_word_boundaries() {
    let mut runtime = runtime(r#"{"parallel":{"workers":2,"scan_chunk":64}}"#);
    let mut data = Vec::new();
    for index in 0..200u32 {
        data.push((index % 3).to_string());
    }
    // 在下标 40（跨 32 字边界）处嵌三个 Pos
    data[40] = "2".to_string();
    data[41] = "2".to_string();
    data[42] = "2".to_string();
    let payload = format!(
        r#"{{"owner":"m","kind":"pattern","trits":[{}],"pattern":[2,2,2]}}"#,
        data.join(",")
    );
    let _ = runtime
        .dispatch(op::SCAN_SUBMIT, payload.as_bytes())
        .expect("提交成功");
    let response = poll_until(&mut runtime, 1, Duration::from_secs(5));
    let polled: serde_json::Value = serde_json::from_slice(&response).unwrap();
    let matches = polled["jobs"][0]["result"]["matches"]
        .as_array()
        .expect("matches 是数组");
    assert_eq!(matches.len(), 1, "应只命中一个窗口：{matches:?}");
    assert_eq!(matches[0]["index"], serde_json::json!(40));
    assert_eq!(matches[0]["score"], serde_json::json!(3));
    assert_eq!(
        polled["jobs"][0]["result"]["backend"],
        serde_json::json!("cpu")
    );
}

#[test]
fn gpu_status_is_always_answerable_and_off_means_off() {
    let mut runtime = runtime("{}");
    let status: serde_json::Value = serde_json::from_slice(
        &runtime
            .dispatch(op::GPU_STATUS, br#"{"policy":"off"}"#)
            .unwrap(),
    )
    .unwrap();
    assert_eq!(status["policy"], serde_json::json!("off"));
    assert_eq!(status["enabled"], serde_json::json!(false));
    assert!(status["built_with_gpu"].is_boolean());
    if status["available"] == serde_json::json!(false) {
        assert!(!status["reason"].as_str().unwrap_or_default().is_empty());
    }
}

#[test]
fn metrics_are_exportable_after_a_round() {
    let mut runtime = runtime(r#"{"parallel":{"workers":2}}"#);
    let _ = runtime
        .dispatch(
            op::SCAN_SUBMIT,
            br#"{"owner":"m","kind":"dot","trits":[2,2],"pattern":[2,0]}"#,
        )
        .expect("提交成功");
    let _ = poll_until(&mut runtime, 1, Duration::from_secs(5));
    let metrics: serde_json::Value =
        serde_json::from_slice(&runtime.dispatch(op::METRICS, &[]).unwrap()).unwrap();
    let text = metrics["prometheus"].as_str().expect("prometheus 是字符串");
    assert!(
        text.is_empty() || text.contains("trit_schedule_"),
        "指标文本应带上游前缀（或为空）：{text}"
    );
}

#[test]
fn capability_bits_advertise_parallel_and_budget() {
    let runtime = runtime("{}");
    let caps = runtime.capability_bits();
    assert!(caps & (1 << 9) != 0, "PARALLEL");
    assert!(caps & (1 << 11) != 0, "BUDGET");
}

/// 提交 `count` 个任务，归属轮流取自 `owners`。
fn submit_many(runtime: &mut TritRuntime, owners: &[&str], count: usize) -> Vec<u64> {
    let mut ids = Vec::with_capacity(count);
    for index in 0..count {
        let owner = owners[index % owners.len()];
        let payload = format!(r#"{{"owner":"{owner}","kind":"histogram","trits":[0,1,2]}}"#);
        let response = runtime
            .dispatch(op::SCAN_SUBMIT, payload.as_bytes())
            .expect("提交成功");
        ids.push(
            serde_json::from_slice::<serde_json::Value>(&response).unwrap()["job"]
                .as_u64()
                .expect("返回 job id"),
        );
    }
    ids
}

/// 按 owner 轮询，直到累计拿到 `count` 条结果。
fn poll_until_by_owner(
    runtime: &mut TritRuntime,
    owners: &[&str],
    count: usize,
    timeout: Duration,
) -> Vec<serde_json::Value> {
    let deadline = Instant::now() + timeout;
    let mut collected = Vec::new();
    while collected.len() < count {
        let mut payload = String::from(r#"{"limit":64,"owner":"#);
        for (index, owner) in owners.iter().enumerate() {
            if index > 0 {
                payload.push(',');
            }
            payload.push_str(&format!(r#""{owner}""#));
        }
        payload.push('}');

        let response = runtime
            .dispatch(op::JOB_POLL, payload.as_bytes())
            .expect("JOB_POLL 成功");
        let value: serde_json::Value = serde_json::from_slice(&response).unwrap();
        collected.extend(value["jobs"].as_array().cloned().unwrap_or_default());
        if collected.len() >= count {
            break;
        }
        if Instant::now() > deadline {
            panic!("超时未拿到 {count} 条结果，最后一次响应：{value}");
        }
        std::thread::sleep(Duration::from_millis(5));
    }
    collected
}

#[test]
fn polling_by_owner_returns_only_that_owners_results() {
    let mut runtime = runtime(r#"{"parallel":{"workers":4}}"#);
    // 交错提交：alpha / beta 各 4 个。
    let ids = submit_many(&mut runtime, &["alpha", "beta"], 8);
    let jobs = poll_until_by_owner(&mut runtime, &["alpha"], 4, Duration::from_secs(10));
    assert_eq!(jobs.len(), 4, "只应拿到 alpha 的 4 条：{jobs:?}");
    for job in &jobs {
        assert_eq!(job["owner"], serde_json::json!("alpha"));
    }

    let mut returned: Vec<u64> = jobs
        .iter()
        .map(|job| job["id"].as_u64().expect("id 是数字"))
        .collect();
    returned.sort_unstable();
    let mut expected: Vec<u64> = ids.iter().copied().step_by(2).collect();
    expected.sort_unstable();
    assert_eq!(returned, expected, "拿到的必须是 alpha 自己那几条");
}

#[test]
fn polling_by_owner_leaves_the_other_owners_results_in_the_queue() {
    let mut runtime = runtime(r#"{"parallel":{"workers":4}}"#);
    let ids = submit_many(&mut runtime, &["alpha", "beta"], 6);
    // 先把 alpha 的都取走。
    let alpha_jobs = poll_until_by_owner(&mut runtime, &["alpha"], 3, Duration::from_secs(10));
    assert_eq!(alpha_jobs.len(), 3);

    // beta 的 3 条必须还在队列里，一条都不能少。
    let beta_jobs = poll_until_by_owner(&mut runtime, &["beta"], 3, Duration::from_secs(10));
    assert_eq!(
        beta_jobs.len(),
        3,
        "beta 的结果不能被 alpha 的轮询吃掉：{beta_jobs:?}"
    );
    for job in &beta_jobs {
        assert_eq!(job["owner"], serde_json::json!("beta"));
    }

    let mut returned: Vec<u64> = beta_jobs
        .iter()
        .map(|job| job["id"].as_u64().expect("id 是数字"))
        .collect();
    returned.sort_unstable();
    let mut expected: Vec<u64> = ids.iter().copied().skip(1).step_by(2).collect();
    expected.sort_unstable();
    assert_eq!(returned, expected);
}

#[test]
fn polling_without_an_owner_filter_still_returns_everything() {
    let mut runtime = runtime(r#"{"parallel":{"workers":4}}"#);
    let _ = submit_many(&mut runtime, &["alpha", "beta"], 6);
    // owner 字段缺省 = 不限归属。
    let deadline = Instant::now() + Duration::from_secs(10);
    let mut total = 0usize;
    while total < 6 {
        let response = runtime
            .dispatch(op::JOB_POLL, br#"{"limit":64}"#)
            .expect("JOB_POLL 成功");
        let value: serde_json::Value = serde_json::from_slice(&response).unwrap();
        total += value["jobs"].as_array().map(Vec::len).unwrap_or(0);
        if total >= 6 {
            break;
        }
        if Instant::now() > deadline {
            panic!("超时未拿到 6 条结果，最后一次响应：{value}");
        }
        std::thread::sleep(Duration::from_millis(5));
    }
    assert_eq!(total, 6);
}

#[test]
fn an_unknown_owner_gets_nothing_and_consumes_nothing() {
    let mut runtime = runtime(r#"{"parallel":{"workers":2}}"#);
    let _ = submit_many(&mut runtime, &["alpha"], 2);
    // 先等任务真正完成（避免用"还没完成"来掩盖过滤错误）。
    let _ = poll_until_by_owner(&mut runtime, &["alpha"], 2, Duration::from_secs(10));
    // 队列已被取空；再问一个不存在的 owner，必须是空，且不能报错。
    let response = runtime
        .dispatch(op::JOB_POLL, br#"{"limit":16,"owner":"nobody"}"#)
        .expect("JOB_POLL 成功");
    let value: serde_json::Value = serde_json::from_slice(&response).unwrap();
    assert_eq!(value["jobs"].as_array().map(Vec::len), Some(0));
}

#[test]
fn an_empty_owner_filter_matches_the_unknown_owner() {
    let mut runtime = runtime(r#"{"parallel":{"workers":2}}"#);
    // 以空 owner 提交 → 归一化为 "unknown"。
    let response = runtime
        .dispatch(
            op::SCAN_SUBMIT,
            br#"{"owner":"","kind":"histogram","trits":[0,1,2]}"#,
        )
        .expect("提交成功");
    let job = serde_json::from_slice::<serde_json::Value>(&response).unwrap()["job"]
        .as_u64()
        .expect("返回 job id");

    let deadline = Instant::now() + Duration::from_secs(10);
    let mut found = false;
    while !found {
        let response = runtime
            .dispatch(op::JOB_POLL, br#"{"limit":16,"owner":"unknown"}"#)
            .expect("JOB_POLL 成功");
        let value: serde_json::Value = serde_json::from_slice(&response).unwrap();
        for entry in value["jobs"].as_array().cloned().unwrap_or_default() {
            assert_eq!(entry["owner"], serde_json::json!("unknown"));
            if entry["id"].as_u64() == Some(job) {
                found = true;
            }
        }
        if found {
            break;
        }
        if Instant::now() > deadline {
            panic!("以 \"unknown\" 过滤应能取到空 owner 提交的任务");
        }
        std::thread::sleep(Duration::from_millis(5));
    }
}
