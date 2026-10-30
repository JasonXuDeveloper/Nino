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

// The Nino codecs below are what nino-gen's Rust templates generate for these two types
// (src/Nino.Codegen/Templates/rust/types.sbn): a raw struct read and written as one block, a class's members one by
// one, and a class held as `Option<Box<_>>` wherever it is a member, an element or the value written.
impl nino::Raw for Point {
    const SIZE: usize = 8;
    const ALIGN: usize = 4;

    fn decode_raw(b: &[u8]) -> nino::Result<Self> {
        let b = b.get(..8).ok_or_else(|| nino::Error::of(nino::ErrorKind::UnexpectedEnd))?;
        Ok(Self {
            x: <i32 as nino::Raw>::decode_raw(b.get(0..).unwrap_or_default())?,
            y: <i32 as nino::Raw>::decode_raw(b.get(4..).unwrap_or_default())?,
        })
    }

    fn encode_raw(&self, out: &mut [u8]) {
        if let Some(o) = out.get_mut(0..) {
            nino::Raw::encode_raw(&self.x, o);
        }
        if let Some(o) = out.get_mut(4..) {
            nino::Raw::encode_raw(&self.y, o);
        }
    }
}

impl NinoRead for Point {
    const MIN_SIZE: usize = 8;
    const FIXED_SIZE: usize = 8;

    fn decode_fixed(b: &[u8]) -> Result<Self> {
        <Self as nino::Raw>::decode_raw(b)
    }

    fn read(r: &mut Reader<'_>) -> Result<Self> {
        <Self as nino::Raw>::decode_raw(&r.array::<8>()?)
    }
}

impl NinoWrite for Point {
    const FIXED_SIZE: usize = 8;

    fn encode_fixed(&self, out: &mut [u8]) {
        nino::Raw::encode_raw(self, out);
    }

    fn write<'a>(&'a self, w: &mut Writer<'a>) -> Result<()> {
        let mut b = [0u8; 8];
        nino::Raw::encode_raw(self, &mut b);
        w.array(b);
        Ok(())
    }
}

impl NinoReadBody for Record {
    fn read_body(&mut self, r: &mut Reader<'_>) -> Result<()> {
        self.id = r.read()?;
        self.name = r.read()?;
        self.score = r.read()?;
        self.ratio = r.read()?;
        self.active = r.read()?;
        self.tags = r.read()?;
        self.origin = r.read()?;
        Ok(())
    }
}

impl NinoWriteBody for Record {
    fn write_body<'a>(&'a self, w: &mut Writer<'a>) -> Result<()> {
        w.write(&self.id)?;
        w.write(&self.name)?;
        w.write(&self.score)?;
        w.write(&self.ratio)?;
        w.write(&self.active)?;
        w.write(&self.tags)?;
        w.write(&self.origin)?;
        Ok(())
    }
}

impl NinoRead for Record {
    const MAY_DEFER: bool = true;
    nino::sealed_read!();
}

impl NinoWrite for Record {
    const PREFETCH: usize = 0usize
        .saturating_add(<Option<String> as NinoWrite>::PREFETCH)
        .saturating_add(<Option<Vec<i32>> as NinoWrite>::PREFETCH);

    fn prefetch(&self) {
        self.name.prefetch();
        self.tags.prefetch();
    }

    fn prefetch_members(&self) {
        self.name.prefetch_members();
        self.tags.prefetch_members();
    }

    nino::sealed_write!();
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

/// 1,000,000 records made in order and listed in a shuffled order (a fixed seed): each record, its name and its tags
/// are then somewhere else in memory than the one before, as in a long-lived heap, so writing the list waits on cache
/// misses (what Nino's prefetching takes on). The Go benchmark's `scattered-1m` lists the same.
fn scattered_records() -> Vec<Option<Box<Record>>> {
    let mut values: Vec<Option<Box<Record>>> = (0..1_000_000).map(|i| Some(Box::new(record(i)))).collect();
    // Fisher-Yates with xorshift64.
    let mut state = 0x9E37_79B9_7F4A_7C15u64;
    for i in (1..values.len()).rev() {
        state ^= state << 13;
        state ^= state >> 7;
        state ^= state << 17;
        values.swap(i, (state % (i as u64 + 1)) as usize);
    }
    values
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

/// 1,000 distinct non-negative integers in no order (xorshift64 from a fixed seed): a `HashSet<int>` as nino-gen
/// writes it (an `IndexSet`, in the order of the data). The Go and TypeScript benchmarks' set cases have the same
/// shape.
fn set_ints() -> indexmap::IndexSet<i32> {
    let mut set = indexmap::IndexSet::with_capacity(1000);
    let mut state = 0x2545_F491_4F6C_DD1Du64;
    while set.len() < 1000 {
        state ^= state << 13;
        state ^= state >> 7;
        state ^= state << 17;
        set.insert((state >> 33) as i32);
    }
    set
}

/// "member 0" to "member 999" in no order: a `HashSet<string>` as nino-gen writes it.
fn set_strings() -> indexmap::IndexSet<String> {
    let mut v: Vec<u32> = (0..1000).collect();
    let mut state = 0x9E37_79B9_7F4A_7C15u64;
    for i in (1..v.len()).rev() {
        state ^= state << 13;
        state ^= state >> 7;
        state ^= state << 17;
        v.swap(i, (state % (i as u64 + 1)) as usize);
    }
    v.into_iter().map(|k| format!("member {k}")).collect()
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

/// Nino alone on `$value`: for a case whose bytes differ only in Nino from one `formats!` already measures (the
/// other serializers write the same bytes for both), so the report shows it as its own row.
macro_rules! nino_only {
    ($c:expr, $group:literal, $elements:expr, $value:expr, $t:ty) => {{
        let value: $t = $value;
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
        assert_eq!(nino::deserialize::<$t>(&nino_bytes).unwrap(), value);
        eprintln!("{}: {} bytes nino", $group, nino_bytes.len());
        g.bench_function(BenchmarkId::new("deserialize", "nino"), |b| {
            b.iter(|| black_box(nino::deserialize::<$t>(black_box(&nino_bytes)).unwrap()))
        });
        g.finish();
    }};
}

/// bincode 2's standard configuration (little-endian, variable-length integers).
const BINCODE: bincode::config::Configuration = bincode::config::standard();

/// One group per case: serialize and deserialize with each serializer, Nino first. `$elements` is the number of
/// values in the case (criterion's throughput); `$proto` is the case as a protobuf message of type `$p`, and
/// `$from_proto` (the message itself when left out) what the deserialize timing makes of a decoded message: the
/// case's type where protobuf has no such type (a set).
macro_rules! formats {
    ($c:expr, $group:literal, $elements:expr, $value:expr, $t:ty, $proto:expr, $p:ty) => {
        formats!($c, $group, $elements, $value, $t, $proto, $p, |m: $p| m)
    };
    ($c:expr, $group:literal, $elements:expr, $value:expr, $t:ty, $proto:expr, $p:ty, $from_proto:expr) => {{
        let value: $t = $value;
        let message: $p = $proto(&value);
        let mut g = $c.benchmark_group($group);
        if $elements >= 100_000 {
            // A run of a large case takes milliseconds: ten samples, not a hundred.
            g.sample_size(10);
        }
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
            b.iter(|| black_box($from_proto(<$p as prost::Message>::decode(black_box(&prost_bytes[..])).unwrap())))
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
    let one = |v: &Option<Box<Record>>| proto::record(v.as_ref().unwrap());
    let many = |v: &Vec<Option<Box<Record>>>| proto::Records {
        values: v.iter().flatten().map(|r| proto::record(r)).collect(),
    };
    formats!(c, "record", 1, Some(Box::new(record(42))), Option<Box<Record>>, one, proto::Record);
    formats!(
        c,
        "records-1000",
        1000,
        (0..1000).map(|i| Some(Box::new(record(i)))).collect(),
        Vec<Option<Box<Record>>>,
        many,
        proto::Records
    );
    formats!(c, "scattered-1m", 1_000_000, scattered_records(), Vec<Option<Box<Record>>>, many, proto::Records);
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
    // Sets: protobuf has none, so its message is a repeated field of the elements, made into the set when read (as
    // the others read theirs), without checking for a repeated element as Nino does.
    let set_i32s = |v: &indexmap::IndexSet<i32>| proto::I32s { values: v.iter().copied().collect() };
    let i32_set = |m: proto::I32s| m.values.into_iter().collect::<indexmap::IndexSet<i32>>();
    formats!(c, "int-set-1000", 1000, set_ints(), indexmap::IndexSet<i32>, set_i32s, proto::I32s, i32_set);
    let set_texts = |v: &indexmap::IndexSet<String>| proto::Strings { values: v.iter().cloned().collect() };
    let text_set = |m: proto::Strings| m.values.into_iter().collect::<indexmap::IndexSet<String>>();
    formats!(
        c,
        "string-set-1000",
        1000,
        set_strings(),
        indexmap::IndexSet<String>,
        set_texts,
        proto::Strings,
        text_set
    );
    // The same strings as a `[NinoUtf8] string[]`: each as UTF-8 (wire format 4.2), read with the same checks
    // (strict UTF-8, the char count) as a `[NinoUtf8]` member, as the other serializers check theirs.
    nino_only!(
        c,
        "strings-mixed-utf8-1000",
        1000,
        mixed_strings().into_iter().map(nino::Utf8).collect(),
        Vec<nino::Utf8>
    );
}

criterion_group! {
    name = compare;
    config = Criterion::default().warm_up_time(std::time::Duration::from_millis(500)).measurement_time(std::time::Duration::from_secs(2));
    targets = benches
}
criterion_main!(compare);
