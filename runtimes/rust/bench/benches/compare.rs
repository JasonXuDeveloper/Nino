//! Nino against serde_json, bincode, rmp-serde (MessagePack) and postcard on the same values.
//!
//! `cargo bench -p nino-bench` (add a filter: `cargo bench -p nino-bench -- record`). Numbers are local and rough:
//! compare them on one machine, back to back.

use std::hint::black_box;

use criterion::{criterion_group, criterion_main, BenchmarkId, Criterion, Throughput};
use nino::{NinoBody, NinoRead, NinoWrite, Reader, Result, Writer};
use serde::{Deserialize, Serialize};

// ---------------------------------------------------------------------------------------------- the values

/// `[NinoType] sealed class Record { int Id; string Name; long Score; double Ratio; bool Active; List<int> Tags;
/// Point Origin; }` with `[NinoType] struct Point { int X; int Y; }` (raw).
#[derive(Debug, Default, Clone, PartialEq, Serialize, Deserialize)]
pub struct Record {
    pub id: i32,
    pub name: Option<String>,
    pub score: i64,
    pub ratio: f64,
    pub active: bool,
    pub tags: Option<Vec<i32>>,
    pub origin: Point,
}

#[derive(Debug, Default, Clone, Copy, PartialEq, Serialize, Deserialize)]
pub struct Point {
    pub x: i32,
    pub y: i32,
}

// What the templates generate: a run of fixed-size members is one bounds check.
impl NinoRead for Point {
    const MIN_SIZE: usize = 8;

    #[inline]
    fn read(r: &mut Reader<'_>) -> Result<Self> {
        let b = r.array::<8>()?;
        let (x, y) = b.split_at(4);
        Ok(Point {
            x: i32::from_le_bytes(x.try_into().unwrap_or_default()),
            y: i32::from_le_bytes(y.try_into().unwrap_or_default()),
        })
    }
}

impl NinoWrite for Point {
    #[inline]
    fn write<'a>(&'a self, w: &mut Writer<'a>) -> Result<()> {
        let mut b = [0u8; 8];
        b[..4].copy_from_slice(&self.x.to_le_bytes());
        b[4..].copy_from_slice(&self.y.to_le_bytes());
        w.array(b);
        Ok(())
    }
}

impl NinoBody for Record {
    #[inline]
    fn read_body(&mut self, r: &mut Reader<'_>) -> Result<()> {
        self.id = r.i32()?;
        self.name = r.string()?;
        let b = r.array::<17>()?;
        self.score = i64::from_le_bytes(b[..8].try_into().unwrap_or_default());
        self.ratio = f64::from_le_bytes(b[8..16].try_into().unwrap_or_default());
        self.active = match b[16] {
            0 => false,
            1 => true,
            v => return Err(nino::Error::new(nino::ErrorKind::InvalidBool, v.into())),
        };
        self.tags = r.read()?;
        self.origin = r.read()?;
        Ok(())
    }

    #[inline]
    fn write_body<'a>(&'a self, w: &mut Writer<'a>) -> Result<()> {
        w.i32(self.id);
        w.string(self.name.as_deref())?;
        w.i64(self.score);
        w.f64(self.ratio);
        w.bool(self.active);
        w.write(&self.tags)?;
        w.write(&self.origin)
    }
}

impl NinoRead for Record {
    #[inline]
    fn read(r: &mut Reader<'_>) -> Result<Self> {
        r.read_sealed_value()
    }

    #[inline]
    fn read_option(r: &mut Reader<'_>) -> Result<Option<Self>> {
        r.read_sealed()
    }
}

impl NinoWrite for Record {
    #[inline]
    fn write<'a>(&'a self, w: &mut Writer<'a>) -> Result<()> {
        w.write_sealed(Some(self))
    }

    #[inline]
    fn write_option<'a>(value: Option<&'a Self>, w: &mut Writer<'a>) -> Result<()> {
        w.write_sealed(value)
    }
}

fn record(i: i32) -> Record {
    Record {
        id: i,
        name: Some(format!("record number {i}")),
        score: i64::from(i) * 1_000_003,
        ratio: f64::from(i) / 7.0,
        active: i % 2 == 0,
        tags: Some((0..8).map(|k| k * i).collect()),
        origin: Point { x: i, y: -i },
    }
}

// ---------------------------------------------------------------------------------------------- the harness

macro_rules! formats {
    ($c:expr, $group:literal, $value:expr, $t:ty) => {{
        let value: $t = $value;
        let mut g = $c.benchmark_group($group);
        let nino_bytes = nino::serialize(&value).unwrap();
        g.throughput(Throughput::Bytes(nino_bytes.len() as u64));

        let mut out = Vec::with_capacity(nino_bytes.len());
        g.bench_function(BenchmarkId::new("serialize", "nino"), |b| {
            b.iter(|| {
                out.clear();
                nino::serialize_into(black_box(&value), &mut out).unwrap();
                black_box(out.len())
            })
        });
        g.bench_function(BenchmarkId::new("serialize", "bincode"), |b| {
            b.iter(|| {
                out.clear();
                bincode::serialize_into(&mut out, black_box(&value)).unwrap();
                black_box(out.len())
            })
        });
        g.bench_function(BenchmarkId::new("serialize", "postcard"), |b| {
            b.iter(|| black_box(postcard::to_allocvec(black_box(&value)).unwrap()))
        });
        g.bench_function(BenchmarkId::new("serialize", "rmp-serde"), |b| {
            b.iter(|| {
                out.clear();
                rmp_serde::encode::write(&mut out, black_box(&value)).unwrap();
                black_box(out.len())
            })
        });
        g.bench_function(BenchmarkId::new("serialize", "serde_json"), |b| {
            b.iter(|| {
                out.clear();
                serde_json::to_writer(&mut out, black_box(&value)).unwrap();
                black_box(out.len())
            })
        });

        let bincode_bytes = bincode::serialize(&value).unwrap();
        let postcard_bytes = postcard::to_allocvec(&value).unwrap();
        let rmp_bytes = rmp_serde::to_vec(&value).unwrap();
        let json_bytes = serde_json::to_vec(&value).unwrap();
        eprintln!(
            "{}: {} bytes nino, {} bincode, {} postcard, {} rmp-serde, {} serde_json",
            $group,
            nino_bytes.len(),
            bincode_bytes.len(),
            postcard_bytes.len(),
            rmp_bytes.len(),
            json_bytes.len()
        );
        g.bench_function(BenchmarkId::new("deserialize", "nino"), |b| {
            b.iter(|| black_box(nino::deserialize::<$t>(black_box(&nino_bytes)).unwrap()))
        });
        g.bench_function(BenchmarkId::new("deserialize", "bincode"), |b| {
            b.iter(|| black_box(bincode::deserialize::<$t>(black_box(&bincode_bytes)).unwrap()))
        });
        g.bench_function(BenchmarkId::new("deserialize", "postcard"), |b| {
            b.iter(|| black_box(postcard::from_bytes::<$t>(black_box(&postcard_bytes)).unwrap()))
        });
        g.bench_function(BenchmarkId::new("deserialize", "rmp-serde"), |b| {
            b.iter(|| black_box(rmp_serde::from_slice::<$t>(black_box(&rmp_bytes)).unwrap()))
        });
        g.bench_function(BenchmarkId::new("deserialize", "serde_json"), |b| {
            b.iter(|| black_box(serde_json::from_slice::<$t>(black_box(&json_bytes)).unwrap()))
        });
        g.finish();
    }};
}

fn benches(c: &mut Criterion) {
    formats!(c, "record", Some(record(42)), Option<Record>);
    formats!(c, "records-1000", (0..1000).map(|i| Some(record(i))).collect(), Vec<Option<Record>>);
    formats!(c, "i32-10000", (0..10_000).map(|i| i * 7919).collect(), Vec<i32>);
    formats!(c, "f64-10000", (0..10_000).map(|i| f64::from(i) * 0.5).collect(), Vec<f64>);
    formats!(c, "strings-1000", (0..1000).map(|i| format!("string {i}")).collect(), Vec<String>);
}

criterion_group! {
    name = compare;
    config = Criterion::default().warm_up_time(std::time::Duration::from_millis(500)).measurement_time(std::time::Duration::from_secs(2));
    targets = benches
}
criterion_main!(compare);
