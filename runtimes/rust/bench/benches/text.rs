//! Nino's string writer and reader on 100 strings of one class of text and one length, for comparing changes to the
//! transcoding (`src/simd.rs`) back to back on one machine. Not part of the published comparison (`compare.rs`).
//!
//! `cargo bench -p nino-bench --bench text` (add a filter: `-- write/latin1`).

use std::hint::black_box;

use criterion::{criterion_group, criterion_main, BenchmarkId, Criterion, Throughput};

/// Text of about `len` chars of each class: ASCII, Latin-1 (Nino writes one byte a char), Cyrillic and CJK (UTF-16),
/// and the mixed text of compare.rs's third kind (Cyrillic, CJK and a char outside the BMP).
fn sample(class: &str, len: usize, i: usize) -> String {
    let unit = match class {
        "ascii" => "string ",
        "latin1" => "crème brûlée ",
        "cyrillic" => "строка ",
        "cjk" => "字符串",
        _ => "строка 字符串 🎉 ",
    };
    let mut s: String = unit.chars().cycle().skip(i % 3).take(len.saturating_sub(4)).collect();
    s.push_str(&format!("{}", i % 1000));
    s
}

fn benches(c: &mut Criterion) {
    for class in ["ascii", "latin1", "cyrillic", "cjk", "mixed"] {
        for len in [16usize, 24, 64, 300, 2000] {
            let values: Vec<String> = (0..100).map(|i| sample(class, len, i)).collect();
            let bytes = nino::serialize(&values).unwrap();
            let id = format!("{class}/{len}");
            let mut g = c.benchmark_group("text");
            g.throughput(Throughput::Elements(100));
            let mut out = Vec::with_capacity(bytes.len());
            g.bench_function(BenchmarkId::new("write", &id), |b| {
                b.iter(|| {
                    out.clear();
                    nino::serialize_into(black_box(&values), &mut out).unwrap();
                    black_box(out.len())
                })
            });
            g.bench_function(BenchmarkId::new("read", &id), |b| {
                b.iter(|| black_box(nino::deserialize::<Vec<String>>(black_box(&bytes)).unwrap()))
            });
            g.finish();
        }
    }
}

criterion_group! {
    name = group;
    config = Criterion::default().warm_up_time(std::time::Duration::from_millis(300)).measurement_time(std::time::Duration::from_secs(1));
    targets = benches
}
criterion_main!(group);
