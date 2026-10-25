//! Nino against Protocol Buffers (prost), MessagePack (rmp-serde), bincode (2, standard configuration), postcard and
//! serde_json on the same values. The protobuf messages mirror the values field for field (`proto` below), derived
//! with prost: no protoc.
//!
//! `cargo bench -p nino-bench` (add a filter: `cargo bench -p nino-bench -- record`). Numbers are local and rough:
//! compare them on one machine, back to back.

use std::hint::black_box;

use criterion::{criterion_group, criterion_main, BenchmarkId, Criterion, Throughput};
use nino::{NinoRead, NinoReadBody, NinoWrite, NinoWriteBody, Reader, Result, Writer};
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

impl NinoReadBody for Record {
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
}

impl NinoWriteBody for Record {
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

// ---------------------------------------------------------------------------------------------- protobuf

/// The values as Protocol Buffers messages, field for field (as a `.proto` would declare them):
/// `message Point { int32 x = 1; int32 y = 2; }`,
/// `message Record { int32 id = 1; string name = 2; int64 score = 3; double ratio = 4; bool active = 5;
/// repeated int32 tags = 6; Point origin = 7; }`, and a message of one repeated field for each list.
mod proto {
    #[derive(Clone, PartialEq, prost::Message)]
    pub struct Point {
        #[prost(int32, tag = "1")]
        pub x: i32,
        #[prost(int32, tag = "2")]
        pub y: i32,
    }

    #[derive(Clone, PartialEq, prost::Message)]
    pub struct Record {
        #[prost(int32, tag = "1")]
        pub id: i32,
        #[prost(string, tag = "2")]
        pub name: String,
        #[prost(int64, tag = "3")]
        pub score: i64,
        #[prost(double, tag = "4")]
        pub ratio: f64,
        #[prost(bool, tag = "5")]
        pub active: bool,
        #[prost(int32, repeated, tag = "6")]
        pub tags: Vec<i32>,
        #[prost(message, optional, tag = "7")]
        pub origin: Option<Point>,
    }

    #[derive(Clone, PartialEq, prost::Message)]
    pub struct Records {
        #[prost(message, repeated, tag = "1")]
        pub values: Vec<Record>,
    }

    #[derive(Clone, PartialEq, prost::Message)]
    pub struct I32s {
        #[prost(int32, repeated, tag = "1")]
        pub values: Vec<i32>,
    }

    #[derive(Clone, PartialEq, prost::Message)]
    pub struct F64s {
        #[prost(double, repeated, tag = "1")]
        pub values: Vec<f64>,
    }

    #[derive(Clone, PartialEq, prost::Message)]
    pub struct Strings {
        #[prost(string, repeated, tag = "1")]
        pub values: Vec<String>,
    }

    pub fn record(r: &super::Record) -> Record {
        Record {
            id: r.id,
            name: r.name.clone().unwrap_or_default(),
            score: r.score,
            ratio: r.ratio,
            active: r.active,
            tags: r.tags.clone().unwrap_or_default(),
            origin: Some(Point { x: r.origin.x, y: r.origin.y }),
        }
    }
}

/// 1,000 strings of three kinds in turn: ASCII, Latin-1 (one byte a char in Nino) and other text (UTF-16 in Nino:
/// Cyrillic, CJK and a char outside the BMP). The Go and TypeScript benchmarks use the same strings.
fn mixed_strings() -> Vec<String> {
    (0..1000)
        .map(|i| match i % 3 {
            0 => format!("string {i}"),
            1 => format!("crème brûlée {i}"),
            _ => format!("строка 字符串 🎉 {i}"),
        })
        .collect()
}

// ---------------------------------------------------------------------------------------------- the harness

/// bincode 2's standard configuration (little-endian, variable-length integers).
const BINCODE: bincode::config::Configuration = bincode::config::standard();

/// One group per case: serialize and deserialize with each serializer, Nino first. `$elements` is the number of
/// values in the case (criterion's throughput); `$proto` is the case as a protobuf message of type `$p`.
macro_rules! formats {
    ($c:expr, $group:literal, $elements:expr, $value:expr, $t:ty, $proto:expr, $p:ty) => {{
        let value: $t = $value;
        let message: $p = $proto(&value);
        let mut g = $c.benchmark_group($group);
        let nino_bytes = nino::serialize(&value).unwrap();
        g.throughput(Throughput::Elements($elements));

        let mut out = Vec::with_capacity(nino_bytes.len());
        g.bench_function(BenchmarkId::new("serialize", "nino"), |b| {
            b.iter(|| {
                out.clear();
                nino::serialize_into(black_box(&value), &mut out).unwrap();
                black_box(out.len())
            })
        });
        g.bench_function(BenchmarkId::new("serialize", "prost"), |b| {
            b.iter(|| {
                out.clear();
                prost::Message::encode(black_box(&message), &mut out).unwrap();
                black_box(out.len())
            })
        });
        g.bench_function(BenchmarkId::new("serialize", "bincode"), |b| {
            b.iter(|| {
                out.clear();
                bincode::serde::encode_into_std_write(black_box(&value), &mut out, BINCODE).unwrap();
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

        let bincode_bytes = bincode::serde::encode_to_vec(&value, BINCODE).unwrap();
        let postcard_bytes = postcard::to_allocvec(&value).unwrap();
        let rmp_bytes = rmp_serde::to_vec(&value).unwrap();
        let json_bytes = serde_json::to_vec(&value).unwrap();
        let prost_bytes = prost::Message::encode_to_vec(&message);
        assert_eq!(<$p as prost::Message>::decode(&prost_bytes[..]).unwrap(), message);
        assert_eq!(nino::deserialize::<$t>(&nino_bytes).unwrap(), value);
        eprintln!(
            "{}: {} bytes nino, {} prost, {} bincode, {} postcard, {} rmp-serde, {} serde_json",
            $group,
            nino_bytes.len(),
            prost_bytes.len(),
            bincode_bytes.len(),
            postcard_bytes.len(),
            rmp_bytes.len(),
            json_bytes.len()
        );
        g.bench_function(BenchmarkId::new("deserialize", "nino"), |b| {
            b.iter(|| black_box(nino::deserialize::<$t>(black_box(&nino_bytes)).unwrap()))
        });
        g.bench_function(BenchmarkId::new("deserialize", "prost"), |b| {
            b.iter(|| black_box(<$p as prost::Message>::decode(black_box(&prost_bytes[..])).unwrap()))
        });
        g.bench_function(BenchmarkId::new("deserialize", "bincode"), |b| {
            b.iter(|| {
                black_box(bincode::serde::decode_from_slice::<$t, _>(black_box(&bincode_bytes), BINCODE).unwrap().0)
            })
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
    let one = |v: &Option<Record>| proto::record(v.as_ref().unwrap());
    let many = |v: &Vec<Option<Record>>| proto::Records { values: v.iter().flatten().map(proto::record).collect() };
    formats!(c, "record", 1, Some(record(42)), Option<Record>, one, proto::Record);
    formats!(
        c,
        "records-1000",
        1000,
        (0..1000).map(|i| Some(record(i))).collect(),
        Vec<Option<Record>>,
        many,
        proto::Records
    );
    let i32s = |v: &Vec<i32>| proto::I32s { values: v.clone() };
    formats!(c, "i32-10000", 10_000, (0..10_000).map(|i| i * 7919).collect(), Vec<i32>, i32s, proto::I32s);
    let f64s = |v: &Vec<f64>| proto::F64s { values: v.clone() };
    formats!(c, "f64-10000", 10_000, (0..10_000).map(|i| f64::from(i) * 0.5).collect(), Vec<f64>, f64s, proto::F64s);
    let strings = |v: &Vec<String>| proto::Strings { values: v.clone() };
    formats!(
        c,
        "strings-1000",
        1000,
        (0..1000).map(|i| format!("string {i}")).collect(),
        Vec<String>,
        strings,
        proto::Strings
    );
    formats!(c, "strings-mixed-1000", 1000, mixed_strings(), Vec<String>, strings, proto::Strings);
}

criterion_group! {
    name = compare;
    config = Criterion::default().warm_up_time(std::time::Duration::from_millis(500)).measurement_time(std::time::Duration::from_secs(2));
    targets = benches
}
criterion_main!(compare);
