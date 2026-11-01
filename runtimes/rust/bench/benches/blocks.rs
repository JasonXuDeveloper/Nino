//! Nino's reader and writer on blocks of fixed-size elements other than numbers: `bool`, dates, decimals, GUIDs, a raw
//! struct as nino-gen generates it, plain structs as `#[derive]` writes them, tuples, nullable values and a dictionary, 10,000 of each (1,000 entries for the
//! dictionary). For comparing changes to the block paths back to back on one machine; not part of the published
//! comparison (`compare.rs`).
//!
//! `cargo bench -p nino-bench --bench blocks` (add a filter: `-- bool`).

use std::collections::HashMap;
use std::hint::black_box;

use criterion::{criterion_group, criterion_main, BenchmarkId, Criterion, Throughput};
use nino::{DateOnly, DateTime, DateTimeKind, Decimal, Guid, NinoRead, NinoWrite};

/// `[NinoType] struct Point { int X; int Y; }` (raw), as nino-gen's Rust templates generate it.
#[derive(Debug, Default, Clone, Copy, PartialEq)]
pub struct Point {
    pub x: i32,
    pub y: i32,
}

#[path = "common/point.rs"]
mod point;

/// A plain (not raw) struct whose members are all fixed-size, derived: its wire bytes are its memory.
#[derive(Debug, Default, Clone, Copy, PartialEq, nino::NinoRead, nino::NinoWrite)]
#[nino(kind = "struct")]
pub struct Plain {
    pub x: i32,
    pub y: f32,
}

/// A plain struct whose wire bytes are not its memory (13 bytes, no padding; a `bool` checked on read), derived.
#[derive(Debug, Default, Clone, Copy, PartialEq, nino::NinoRead, nino::NinoWrite)]
#[nino(kind = "struct")]
pub struct PlainMixed {
    pub id: i64,
    pub flag: bool,
    pub score: i32,
}

fn case<T>(c: &mut Criterion, name: &str, value: T)
where
    T: NinoRead + NinoWrite + PartialEq + std::fmt::Debug,
{
    let bytes = nino::serialize(&value).unwrap();
    assert_eq!(nino::deserialize::<T>(&bytes).unwrap(), value);
    let mut g = c.benchmark_group("blocks");
    g.throughput(Throughput::Bytes(bytes.len() as u64));
    let mut out = Vec::with_capacity(bytes.len());
    g.bench_function(BenchmarkId::new("write", name), |b| {
        b.iter(|| {
            out.clear();
            nino::serialize_into(black_box(&value), &mut out).unwrap();
            black_box(out.len())
        })
    });
    g.bench_function(BenchmarkId::new("read", name), |b| {
        b.iter(|| black_box(nino::deserialize::<T>(black_box(&bytes)).unwrap()))
    });
    g.finish();
}

fn benches(c: &mut Criterion) {
    const N: i32 = 10_000;
    case(c, "bool", (0..N).map(|i| i % 3 == 0).collect::<Vec<bool>>());
    case(c, "i8", (0..N).map(|i| i as i8).collect::<Vec<i8>>());
    case(c, "u64", (0..N).map(|i| 638_000_000_000_000_000 + i as u64 * 10_000_019).collect::<Vec<u64>>());
    let ticks = |i: i32| 638_000_000_000_000_000 + i64::from(i) * 10_000_019;
    case(c, "datetime", (0..N).map(|i| DateTime::new(ticks(i), DateTimeKind::Utc).unwrap()).collect::<Vec<_>>());
    case(c, "dateonly", (0..N).map(|i| DateOnly::new(730_000 + i).unwrap()).collect::<Vec<_>>());
    case(
        c,
        "decimal",
        (0..N)
            .map(|i| Decimal::new(u128::from(i as u32) * 1_000_003, (i % 29) as u8, i % 2 == 0).unwrap())
            .collect::<Vec<_>>(),
    );
    case(c, "guid", (0..N).map(|i| Guid((i as u128 * 0x9E37_79B9_7F4A_7C15).to_le_bytes())).collect::<Vec<_>>());
    case(c, "point", (0..N).map(|i| Point { x: i, y: -i }).collect::<Vec<_>>());
    case(c, "struct-i32-f32", (0..N).map(|i| Plain { x: i, y: i as f32 * 0.5 }).collect::<Vec<_>>());
    let mixed = |i: i32| PlainMixed { id: i64::from(i) * 7919, flag: i % 3 == 0, score: -i };
    case(c, "struct-i64-bool-i32", (0..N).map(mixed).collect::<Vec<_>>());
    case(c, "tuple-i32-i32", (0..N).map(|i| (i, -i)).collect::<Vec<(i32, i32)>>());
    case(c, "tuple-i64-bool", (0..N).map(|i| (i64::from(i), i % 2 == 0)).collect::<Vec<(i64, bool)>>());
    case(c, "option-i32", (0..N).map(|i| (i % 4 != 0).then_some(i)).collect::<Vec<Option<i32>>>());
    case(c, "map-i32-i32", (0..1000).map(|i| (i, i * 7)).collect::<HashMap<i32, i32>>());
}

criterion_group! {
    name = group;
    config = Criterion::default().warm_up_time(std::time::Duration::from_millis(300)).measurement_time(std::time::Duration::from_secs(1));
    targets = benches
}
criterion_main!(group);
