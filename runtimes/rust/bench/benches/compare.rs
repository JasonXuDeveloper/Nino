//! The cases of the .NET suite (runtimes/csharp/benchmarks) on the bytes it wrote (generate.sh): for each, the code
//! nino-gen generates (src/model.rs) reads .NET's Nino bytes and must write them back, then every adapter that can
//! express the case is timed. Each case is a criterion group named after it, with the functions
//! `serialize/<adapter>` and `deserialize/<adapter>`; its encoded sizes go to stderr as `<case>: N bytes nino, M
//! prost, ...` (runtimes_report.py rust reads both). An adapter that cannot express a case (it fails, panics, or
//! reads back a value that writes other Nino bytes) is left out of it with the reason on stderr. A new C# case needs
//! no edit here: the model and the data carry it.
//!
//! NINO_BENCH_DATA moves data/, NINO_BENCH_QUICK=1 runs the quick subset with short timings, NINO_BENCH_CASES (a
//! regular expression) picks cases by name.

use std::hint::black_box;
use std::panic::{catch_unwind, AssertUnwindSafe};
use std::path::{Path, PathBuf};
use std::rc::Rc;

use criterion::{criterion_group, criterion_main, BenchmarkId, Criterion};
use nino::{NinoRead, NinoWrite};
use nino_bench::model::{with_nino_serde_type, NinoSerdeVisit};
use nino_bench::proto::{with_message, ProtoVisit};
use serde::de::DeserializeOwned;
use serde::{Deserialize, Serialize};

// ---------------------------------------------------------------------------------------------- cases

/// A case of the .NET suite, as Nino.Benchmark --write-data lists it in cases.json.
#[derive(Deserialize)]
struct Case {
    #[serde(rename = "case")]
    name: String,
    /// The .NET type's runtime-form name, which the model looks its type up by.
    #[serde(rename = "type")]
    type_name: String,
    /// The Protocol Buffers message protobuf-net's bytes follow (cases.proto, workloads.proto).
    proto: Option<String>,
    quick: bool,
}

fn data_dir() -> PathBuf {
    std::env::var_os("NINO_BENCH_DATA")
        .map(PathBuf::from)
        .unwrap_or_else(|| Path::new(env!("CARGO_MANIFEST_DIR")).join("data"))
}

/// The bytes a library wrote for a case in .NET (nino, protobuf), or `None`.
fn bytes(case: &Case, library: &str) -> Option<Vec<u8>> {
    std::fs::read(data_dir().join(format!("{}.{library}.bin", case.name))).ok()
}

/// cases.json's cases, filtered by NINO_BENCH_QUICK and NINO_BENCH_CASES; none (with a note) without the data.
fn cases() -> Vec<Case> {
    let path = data_dir().join("cases.json");
    let Ok(text) = std::fs::read_to_string(&path) else {
        eprintln!("no benchmark data at {} (run generate.sh --data, or set NINO_BENCH_DATA)", path.display());
        return Vec::new();
    };
    let all: Vec<Case> = serde_json::from_str(&text).expect("cases.json");
    let only = std::env::var("NINO_BENCH_CASES").ok().map(|p| regex_lite(&p));
    let quick = quick();
    all.into_iter().filter(|c| (!quick || c.quick) && only.as_ref().is_none_or(|m| m(&c.name))).collect()
}

/// NINO_BENCH_CASES: names separated by `|`, each matched as a whole, or with a `*` for any run of characters.
fn regex_lite(pattern: &str) -> impl Fn(&str) -> bool {
    let alternatives: Vec<String> = pattern.split('|').map(str::to_owned).collect();
    move |name| alternatives.iter().any(|a| glob(a, name))
}

fn glob(pattern: &str, name: &str) -> bool {
    match pattern.split_once('*') {
        None => pattern == name,
        Some((head, tail)) => {
            name.len() >= head.len()
                && name.starts_with(head)
                && (0..=name.len() - head.len()).any(|i| glob(tail, &name[head.len() + i..]))
        }
    }
}

fn quick() -> bool {
    std::env::var("NINO_BENCH_QUICK").as_deref() == Ok("1")
}

// ---------------------------------------------------------------------------------------------- adapters

/// An adapter's two timed operations on one value: `encode` writes its bytes into the buffer (cleared first, its
/// room reused where the library can), `decode` reads them into a new value, which it drops.
struct Codec {
    name: &'static str,
    bytes: Vec<u8>,
    encode: Encode,
    decode: Decode,
}

type Encode = Box<dyn FnMut(&mut Vec<u8>)>;
type Decode = Box<dyn FnMut(&[u8])>;

/// A case's codecs, in the order of the report's columns (runtimes_report.py rust), and the adapters that cannot
/// express it with the reason.
type Codecs = (Vec<Codec>, Vec<(&'static str, String)>);

const BINCODE: bincode::config::Configuration = bincode::config::standard();

/// A serde format: its encoder (into a buffer) and decoder.
struct Format<T> {
    name: &'static str,
    encode: fn(&T, &mut Vec<u8>) -> Result<(), String>,
    decode: fn(&[u8]) -> Result<T, String>,
}

fn formats<T: Serialize + DeserializeOwned>() -> [Format<T>; 4] {
    fn s<E: ToString>(e: E) -> String {
        e.to_string()
    }
    [
        Format {
            name: "rmp-serde",
            encode: |v, out| rmp_serde::encode::write(out, v).map_err(s),
            decode: |b| rmp_serde::from_slice(b).map_err(s),
        },
        Format {
            name: "bincode",
            encode: |v, out| bincode::serde::encode_into_std_write(v, out, BINCODE).map(drop).map_err(s),
            decode: |b| bincode::serde::decode_from_slice(b, BINCODE).map(|(v, _)| v).map_err(s),
        },
        Format {
            name: "postcard",
            encode: |v, out| postcard::to_io(v, out).map(drop).map_err(s),
            decode: |b| postcard::from_bytes(b).map_err(s),
        },
        Format {
            name: "serde_json",
            encode: |v, out| serde_json::to_writer(out, v).map_err(s),
            decode: |b| serde_json::from_slice(b).map_err(s),
        },
    ]
}

/// Runs `f`, and turns a panic into the reason the case cannot be expressed.
fn guarded<R>(f: impl FnOnce() -> Result<R, String>) -> Result<R, String> {
    catch_unwind(AssertUnwindSafe(f)).unwrap_or_else(|p| {
        let what = p.downcast_ref::<String>().cloned().or_else(|| p.downcast_ref::<&str>().map(|s| s.to_string()));
        Err(format!("panics: {}", what.unwrap_or_default()))
    })
}

/// Reads the case's value with the generated code from .NET's Nino bytes (which it must write back), and builds
/// the codecs of every serde adapter that reads it back to a value writing the same Nino bytes.
struct SerdeCodecs<'a>(&'a Case, &'a [u8]);

impl NinoSerdeVisit<Result<Codecs, String>> for SerdeCodecs<'_> {
    fn visit<T: NinoRead + NinoWrite + Serialize + DeserializeOwned + 'static>(self) -> Result<Codecs, String> {
        let SerdeCodecs(case, data) = self;
        let value: T = match nino::deserialize_exact(data) {
            Ok(v) => v,
            // An object holding a .NET type Rust has no form of (BclTypesTest's BigInteger, Uri...).
            Err(e) if case.type_name == "System.Object" => {
                return Err(format!("an object holding a .NET type with no Rust form: {e}"))
            }
            Err(e) => panic!("{}: reading .NET's bytes: {e}", case.name),
        };
        let want = nino::serialize(&value).expect("writing the value back");
        assert!(want == data, "{}: the generated code writes other bytes than .NET's", case.name);
        let value = Rc::new(value);
        let (mut codecs, mut skipped) = (Vec::new(), Vec::new());
        let v = Rc::clone(&value);
        codecs.push(Codec {
            name: "nino",
            bytes: want.clone(),
            encode: Box::new(move |out| nino::serialize_into(black_box(&*v), out).unwrap()),
            decode: Box::new(|b| drop(black_box(nino::deserialize::<T>(b).unwrap()))),
        });
        if let Some(proto) = &case.proto {
            match protobuf(case, proto) {
                Ok(codec) => codecs.push(codec),
                Err(why) => skipped.push(("prost", why)),
            }
        }
        for f in formats::<T>() {
            let checked = guarded(|| {
                let mut bytes = Vec::new();
                (f.encode)(&value, &mut bytes).map_err(|e| format!("encode: {e}"))?;
                let back = (f.decode)(&bytes).map_err(|e| format!("decode: {e}"))?;
                let got = nino::serialize(&back).map_err(|e| format!("the value it read back: {e}"))?;
                if got != want {
                    return Err(format!(
                        "reads back another value ({} Nino bytes, {} expected)",
                        got.len(),
                        want.len()
                    ));
                }
                Ok(bytes)
            });
            match checked {
                Ok(bytes) => {
                    let v = Rc::clone(&value);
                    let (encode, decode) = (f.encode, f.decode);
                    codecs.push(Codec {
                        name: f.name,
                        bytes,
                        encode: Box::new(move |out| encode(black_box(&*v), out).unwrap()),
                        decode: Box::new(move |b| drop(black_box(decode(b).unwrap()))),
                    });
                }
                Err(why) => skipped.push((f.name, why)),
            }
        }
        Ok((codecs, skipped))
    }
}

/// prost on the message of the case's protobuf-net contract, parsed from protobuf-net's bytes once, as an
/// application that keeps its data in generated prost structs would.
fn protobuf(case: &Case, proto: &str) -> Result<Codec, String> {
    struct Prost(Vec<u8>);
    impl ProtoVisit<Result<Codec, String>> for Prost {
        fn visit<M: prost::Message + Default + PartialEq + 'static>(self) -> Result<Codec, String> {
            let message = M::decode(&self.0[..]).map_err(|e| format!("protobuf-net's bytes: {e}"))?;
            let bytes = message.encode_to_vec();
            if M::decode(&bytes[..]).ok().as_ref() != Some(&message) {
                return Err("reads back another message".into());
            }
            Ok(Codec {
                name: "prost",
                bytes,
                encode: Box::new(move |out| black_box(&message).encode(out).unwrap()),
                decode: Box::new(|b| drop(black_box(M::decode(b).unwrap()))),
            })
        }
    }
    let data = bytes(case, "protobuf").ok_or("no protobuf-net bytes")?;
    with_message(proto, Prost(data)).unwrap_or_else(|| Err(format!("no message {proto} in the .proto files")))
}

// ---------------------------------------------------------------------------------------------- timing

fn compare(c: &mut Criterion) {
    for case in cases() {
        let data = bytes(&case, "nino").unwrap_or_else(|| panic!("{}: no Nino bytes", case.name));
        let Some(result) = with_nino_serde_type(&case.type_name, SerdeCodecs(&case, &data)) else {
            eprintln!(
                "{}: skipped: nino-gen writes no Rust type for {} (generate.sh's warnings say why)",
                case.name, case.type_name
            );
            continue;
        };
        let (mut codecs, skipped) = match result {
            Ok(r) => r,
            Err(why) => {
                eprintln!("{}: skipped: {why}", case.name);
                continue;
            }
        };
        for (name, why) in skipped {
            eprintln!("{}/{name}: skipped: {why}", case.name);
        }
        let sizes: Vec<String> = codecs.iter().map(|c| format!("{} {}", c.bytes.len(), c.name)).collect();
        eprintln!("{}: {}", case.name, sizes.join(", ").replacen(' ', " bytes ", 1));

        let mut g = c.benchmark_group(&case.name);
        if codecs[0].bytes.len() >= 1 << 20 {
            // A run of a large case takes milliseconds: ten samples, not a hundred.
            g.sample_size(10);
        }
        let mut out = Vec::new();
        for codec in &mut codecs {
            g.bench_function(BenchmarkId::new("serialize", codec.name), |b| {
                b.iter(|| {
                    out.clear();
                    (codec.encode)(&mut out);
                    black_box(out.len())
                })
            });
        }
        for codec in &mut codecs {
            let bytes = std::mem::take(&mut codec.bytes);
            g.bench_function(BenchmarkId::new("deserialize", codec.name), |b| {
                b.iter(|| (codec.decode)(black_box(&bytes)))
            });
        }
        g.finish();
    }
}

/// The release's timing (300 ms warm-up, 1 s measured: about 1,000 benchmarks in some 25 minutes, as the Go
/// benchmark's 300 ms each), or quick mode's (NINO_BENCH_QUICK=1, the trend check a pull request runs: 100 ms, 300
/// ms, ten samples).
fn config() -> Criterion {
    let ms = std::time::Duration::from_millis;
    if quick() {
        Criterion::default().warm_up_time(ms(100)).measurement_time(ms(300)).sample_size(10)
    } else {
        Criterion::default().warm_up_time(ms(300)).measurement_time(ms(1000))
    }
}

criterion_group! {
    name = benches;
    config = config();
    targets = compare
}
criterion_main!(benches);
