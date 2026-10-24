//! The conformance corpus over the code nino-gen generates (`tests/generated/nino_types.rs`, from the corpus's
//! .NET types; Nino.Codegen.Tests checks it is what nino-gen writes today): every valid case read and written back
//! byte for byte, every malicious case and every truncated valid case rejected with memory bounded by a small
//! multiple of the input, and mutated cases read without a panic. The JSON comparison is `tests/conformance.rs`'s,
//! over the hand-written codecs; here the bytes written back stand for the value read.
//!
//! The corpus is found at `NINO_CORPUS` or at `conformance/corpus` in the repository. Without one the test passes
//! with a note, unless `NINO_CORPUS_REQUIRED=1`.

#![allow(clippy::unwrap_used)]

#[rustfmt::skip]
#[path = "generated/nino_types.rs"]
mod nino_types;

/// Shapes the corpus does not declare (tests/Nino.Codegen.Tests/Shapes.cs): compiled, and written and read back.
#[rustfmt::skip]
#[path = "generated/shapes.rs"]
mod shapes;

use std::alloc::{GlobalAlloc, Layout, System};
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicUsize, Ordering};

use nino::{NinoRead, NinoWrite};
use nino_types::{with_nino_type, NinoVisit};
use serde_json::Value;

/// Valid cases holding a closed generic type (`Conformance.Box<int>`...) in a polymorphic slot, behind the type id of
/// its closed .NET name: nino-gen generates `Box<T>` but no slot variant for it (docs/codegen.md, "Generic types").
const CLOSED_GENERICS: [&str; 2] = ["polymorphism", "polymorphism-generic-root"];

// ---------------------------------------------------------------------------------------------- allocation tracking

struct Counting;

static CURRENT: AtomicUsize = AtomicUsize::new(0);
static PEAK: AtomicUsize = AtomicUsize::new(0);

unsafe impl GlobalAlloc for Counting {
    unsafe fn alloc(&self, layout: Layout) -> *mut u8 {
        let p = unsafe { System.alloc(layout) };
        if !p.is_null() {
            let now = CURRENT.fetch_add(layout.size(), Ordering::Relaxed) + layout.size();
            PEAK.fetch_max(now, Ordering::Relaxed);
        }
        p
    }

    unsafe fn dealloc(&self, p: *mut u8, layout: Layout) {
        unsafe { System.dealloc(p, layout) };
        CURRENT.fetch_sub(layout.size(), Ordering::Relaxed);
    }

    unsafe fn realloc(&self, p: *mut u8, layout: Layout, new: usize) -> *mut u8 {
        let q = unsafe { System.realloc(p, layout, new) };
        if !q.is_null() {
            if new >= layout.size() {
                let now = CURRENT.fetch_add(new - layout.size(), Ordering::Relaxed) + (new - layout.size());
                PEAK.fetch_max(now, Ordering::Relaxed);
            } else {
                CURRENT.fetch_sub(layout.size() - new, Ordering::Relaxed);
            }
        }
        q
    }
}

#[global_allocator]
static ALLOCATOR: Counting = Counting;

fn measured<R>(f: impl FnOnce() -> R) -> (R, usize) {
    let base = CURRENT.load(Ordering::Relaxed);
    PEAK.store(base, Ordering::Relaxed);
    let r = f();
    (r, PEAK.load(Ordering::Relaxed).saturating_sub(base))
}

/// The same allowance as tests/conformance.rs: 256 bytes per input byte, plus 64 KiB.
fn allowance(size: usize) -> usize {
    256 * size + (64 << 10)
}

// ---------------------------------------------------------------------------------------------- visitors

struct RoundTrip<'a>(&'a [u8]);

impl NinoVisit<Result<Vec<u8>, nino::Error>> for RoundTrip<'_> {
    fn visit<T: NinoRead + NinoWrite + 'static>(self) -> Result<Vec<u8>, nino::Error> {
        let value: T = nino::deserialize_exact(self.0)?;
        nino::serialize(&value)
    }
}

struct Decode<'a>(&'a [u8]);

impl NinoVisit<Result<(), nino::Error>> for Decode<'_> {
    fn visit<T: NinoRead + NinoWrite + 'static>(self) -> Result<(), nino::Error> {
        nino::deserialize_exact::<T>(self.0).map(drop)
    }
}

// ---------------------------------------------------------------------------------------------- the corpus

fn corpus() -> Option<PathBuf> {
    if let Ok(dir) = std::env::var("NINO_CORPUS") {
        return Some(PathBuf::from(dir));
    }
    let dir = Path::new(env!("CARGO_MANIFEST_DIR")).join("../../conformance/corpus");
    dir.join("index.json").exists().then_some(dir)
}

fn read_json(path: &Path) -> Value {
    let text = std::fs::read_to_string(path).unwrap_or_else(|e| panic!("{}: {e}", path.display()));
    // Deep cases nest their values about 1,000 levels deep.
    let mut de = serde_json::Deserializer::from_str(&text);
    de.disable_recursion_limit();
    serde::Deserialize::deserialize(&mut de).unwrap()
}

fn unhex(s: &str) -> Vec<u8> {
    (0..s.len()).step_by(2).map(|i| u8::from_str_radix(&s[i..i + 2], 16).unwrap()).collect()
}

fn optional(case: &Value) -> bool {
    case.get("optional").is_some_and(|o| !o.is_null())
}

fn run() {
    let Some(dir) = corpus() else {
        assert!(
            std::env::var("NINO_CORPUS_REQUIRED").as_deref() != Ok("1"),
            "NINO_CORPUS_REQUIRED=1 but no corpus at NINO_CORPUS or conformance/corpus"
        );
        eprintln!("generated: no corpus (set NINO_CORPUS); skipped");
        return;
    };
    let index = read_json(&dir.join("index.json"));
    assert_eq!(index["corpusVersion"], 1, "corpus version");
    let mut failures = Vec::new();
    let (mut passed, mut skipped) = (0, Vec::new());

    for name in index["valid"].as_array().unwrap() {
        let name = name.as_str().unwrap();
        let case = read_json(&dir.join(format!("valid/{name}.json")));
        let ty = case["type"].as_str().unwrap();
        if optional(&case) || CLOSED_GENERICS.contains(&name) {
            skipped.push(name.to_string());
            continue;
        }
        let bytes = std::fs::read(dir.join(format!("valid/{name}.bin"))).unwrap();
        let Some(result) = with_nino_type(ty, RoundTrip(&bytes)) else {
            failures.push(format!("{name}: no generated type {ty}"));
            continue;
        };
        match result {
            Err(e) => failures.push(format!("{name}: read or write failed: {e:?}")),
            Ok(written) => {
                let expected =
                    if case["roundTrip"] == true { bytes.clone() } else { unhex(case["reencoded"].as_str().unwrap()) };
                if written != expected {
                    let at = written
                        .iter()
                        .zip(&expected)
                        .position(|(a, b)| a != b)
                        .unwrap_or(written.len().min(expected.len()));
                    failures.push(format!(
                        "{name}: written back differently ({} bytes, {} expected, first difference at {at})",
                        written.len(),
                        expected.len()
                    ));
                    continue;
                }
                passed += 1;
            }
        }
    }

    for name in index["malicious"].as_array().unwrap() {
        let name = name.as_str().unwrap();
        let case = read_json(&dir.join(format!("malicious/{name}.json")));
        let ty = case["type"].as_str().unwrap();
        if optional(&case) {
            skipped.push(name.to_string());
            continue;
        }
        let bytes = std::fs::read(dir.join(format!("malicious/{name}.bin"))).unwrap();
        let (result, peak) = measured(|| with_nino_type(ty, Decode(&bytes)));
        let Some(result) = result else {
            failures.push(format!("{name}: no generated type {ty}"));
            continue;
        };
        if result.is_ok() && case["expected"] != "error-or-value" {
            failures.push(format!("{name}: accepted ({})", case["exercises"].as_str().unwrap_or("")));
            continue;
        }
        if peak > allowance(bytes.len()) {
            failures.push(format!("{name}: allocated {peak} bytes for {} bytes of input", bytes.len()));
            continue;
        }
        passed += 1;
    }

    let truncations = read_json(&dir.join("malicious/truncations.json"));
    let mut prefixes = 0;
    for entry in truncations["cases"].as_array().unwrap() {
        let name = entry["case"].as_str().unwrap();
        let ty = entry["type"].as_str().unwrap();
        if optional(entry) || CLOSED_GENERICS.contains(&name) {
            skipped.push(format!("{name} prefixes"));
            continue;
        }
        let bytes = std::fs::read(dir.join(format!("valid/{name}.bin"))).unwrap();
        for n in 0..bytes.len() {
            let prefix = &bytes[..n];
            let (result, peak) = measured(|| with_nino_type(ty, Decode(prefix)));
            match result {
                None => {
                    failures.push(format!("{name}: no generated type {ty}"));
                    break;
                }
                Some(Ok(())) => {
                    failures.push(format!("{name}: prefix of {n} bytes accepted"));
                    break;
                }
                Some(Err(_)) if peak > allowance(n) => {
                    failures.push(format!("{name}: prefix of {n} bytes allocated {peak} bytes"));
                    break;
                }
                Some(Err(_)) => prefixes += 1,
            }
        }
    }

    eprintln!(
        "generated: {passed} cases and {prefixes} truncated prefixes passed; skipped: {}",
        if skipped.is_empty() { "none".to_string() } else { skipped.join(", ") }
    );
    assert!(failures.is_empty(), "{} failures:\n{}", failures.len(), failures.join("\n"));
}

/// xorshift64*: deterministic, so a failure reproduces from its seed and iteration.
struct Rng(u64);

impl Rng {
    fn next(&mut self) -> u64 {
        self.0 ^= self.0 >> 12;
        self.0 ^= self.0 << 25;
        self.0 ^= self.0 >> 27;
        self.0.wrapping_mul(0x2545_F491_4F6C_DD1D)
    }

    fn below(&mut self, n: usize) -> usize {
        (self.next() % n.max(1) as u64) as usize
    }
}

/// Mutates every valid and malicious case `NINO_FUZZ_ITERATIONS` times (default 100: tests/conformance.rs runs
/// the longer fuzz) with bit flips, cuts, insertions and interesting i32s: an error or a value, never a panic, and
/// never memory out of proportion to the input.
fn fuzz() {
    let Some(dir) = corpus() else {
        eprintln!("generated fuzz: no corpus (set NINO_CORPUS); skipped");
        return;
    };
    let iterations: usize = std::env::var("NINO_FUZZ_ITERATIONS").ok().and_then(|v| v.parse().ok()).unwrap_or(100);
    let index = read_json(&dir.join("index.json"));
    let mut failures = Vec::new();
    let mut k = 0u64;
    for group in ["valid", "malicious"] {
        for name in index[group].as_array().unwrap() {
            let name = name.as_str().unwrap();
            let case = read_json(&dir.join(format!("{group}/{name}.json")));
            if optional(&case) {
                continue;
            }
            let ty = case["type"].as_str().unwrap();
            let seed = std::fs::read(dir.join(format!("{group}/{name}.bin"))).unwrap();
            k += 1;
            let mut rng = Rng(0x9E37_79B9_7F4A_7C15 ^ k.wrapping_mul(0xD1B5_4A32_D192_ED03));
            for iteration in 0..iterations {
                let mut data = seed.clone();
                let len = data.len();
                match rng.below(5) {
                    0 if len > 0 => {
                        let i = rng.below(len);
                        data[i] ^= 1 << rng.below(8);
                    }
                    1 if len >= 4 => {
                        let i = rng.below(len - 3);
                        let v = [0i32, -1, -2, 127, 128, i32::MAX, i32::MIN, 0x4000_0000][rng.below(8)];
                        data[i..i + 4].copy_from_slice(&v.to_le_bytes());
                    }
                    2 if len > 0 => data.truncate(rng.below(len)),
                    3 => {
                        let i = rng.below(len + 1);
                        data.insert(i, rng.next() as u8);
                    }
                    _ if len > 0 => {
                        let i = rng.below(len);
                        data.remove(i);
                    }
                    _ => data.push(rng.next() as u8),
                }
                let outcome = std::panic::catch_unwind(|| measured(|| with_nino_type(ty, Decode(&data))));
                match outcome {
                    Err(_) => failures.push(format!("{name} iteration {iteration}: panicked")),
                    Ok((_, peak)) if peak > allowance(data.len()) => {
                        failures.push(format!("{name} iteration {iteration}: allocated {peak} bytes"))
                    }
                    Ok(_) => {}
                }
            }
        }
    }
    assert!(failures.is_empty(), "{} failures:\n{}", failures.len(), failures.join("\n"));
}

/// The tests measure memory through one global counter: one at a time, on a large stack (deep cases).
static SERIAL: std::sync::Mutex<()> = std::sync::Mutex::new(());

fn on_large_stack(f: fn()) {
    let _guard = SERIAL.lock().unwrap_or_else(|e| e.into_inner());
    std::thread::Builder::new().stack_size(512 << 20).spawn(f).unwrap().join().unwrap();
}

#[test]
fn generated_corpus() {
    on_large_stack(run);
}

#[test]
fn shapes_round_trip() {
    let leaf = shapes::Recursive { value: 2, children: None };
    let holder = shapes::Holder {
        thing: Some(shapes::IThingRef::ThingStruct(Box::new(shapes::ThingStruct { id: 1, extra: -1 }))),
        things: Some(vec![
            None,
            Some(shapes::IThingRef::ThingClass(Box::new(shapes::ThingClass { id: 3, label: Some("x".into()) }))),
        ]),
        tree: shapes::Recursive { value: 1, children: Some(vec![leaf.clone(), leaf]) },
        flags: 1 << 40,
        gaps: Some(vec![shapes::Gappy { small: 7, large: -7 }]),
        names: Some([(1, Some("one".to_string())), (2, None)].into_iter().collect()),
        tags: Some(["a".to_string(), "b".to_string()].into_iter().collect()),
        pair: (5, None),
        entry: (Some("k".into()), 9),
        maybe: Some(4),
        keywords: Some(Box::new(shapes::Keywords { r#type: 1, self_: 2, r#match: 3, ..Default::default() })),
        ..Default::default()
    };
    let bytes = nino::serialize(&Some(Box::new(holder.clone()))).unwrap();
    let back: Option<Box<shapes::Holder>> = nino::deserialize_exact(&bytes).unwrap();
    assert_eq!(back.as_deref(), Some(&holder));

    let renamed = shapes::Renamed { new_name: 5, other: Some("o".into()) };
    let bytes = nino::serialize(&Some(Box::new(renamed.clone()))).unwrap();
    let back: Option<Box<shapes::Renamed>> = nino::deserialize_exact(&bytes).unwrap();
    assert_eq!(back.as_deref(), Some(&renamed));

    let forest =
        shapes::ForestHolder { forest: Some(vec![Some(shapes::Forest(vec![Some(shapes::Forest::default()), None]))]) };
    let bytes = nino::serialize(&Some(Box::new(forest.clone()))).unwrap();
    let back: Option<Box<shapes::ForestHolder>> = nino::deserialize_exact(&bytes).unwrap();
    assert_eq!(back.as_deref(), Some(&forest));
}

/// Generic types: closed uses read and written with the codecs of their arguments.
#[test]
fn generic_shapes_round_trip() {
    use shapes::{Bag, BoxType, Keys, Node, Pair};
    let uses = shapes::Uses {
        number: Some(Box::new(BoxType { value: -42 })),
        text: Some(Box::new(BoxType { value: Some("text".into()) })),
        pair: Pair { key: 1, value: Some("one".into()) },
        nested: Some(Box::new(BoxType { value: Some(Box::new(BoxType { value: 7 })) })),
        mixed: Some(Box::new(BoxType {
            value: Pair { key: Some("g".into()), value: shapes::Gappy { small: 1, large: 2 } },
        })),
        boxes: Some(vec![Some(Box::new(BoxType { value: 1 })), None]),
        bag: Some(Box::new(Bag {
            items: Some(vec![Some(Box::new(BoxType { value: 5 })), None]),
            by_name: Some([("a".to_string(), Some(Box::new(BoxType { value: 6 })))].into_iter().collect()),
            inner: Some(Box::new(BoxType { value: Some(Box::new(BoxType { value: 8 })) })),
            pair: Pair { key: 3, value: None },
            boxes: Some(vec![None]),
            tuple: (4, None),
            ..Default::default()
        })),
        chain: Some(Box::new(Node {
            value: Some("a".into()),
            next: Some(Box::new(Node { value: Some("b".into()), next: None })),
        })),
        array: Some(Box::new(BoxType { value: Some(vec![1, 2]) })),
        tuple: Some(Box::new(BoxType { value: (9, Some("nine".into())) })),
        maybe: Some(Box::new(BoxType { value: Some(7) })),
        entry: Some(Box::new(BoxType { value: (2, Some("two".into())) })),
        set: Some(Box::new(BoxType { value: Some(["x".to_string()].into_iter().collect()) })),
        int_keys: Some(Box::new(Keys {
            set: Some([1, 2].into_iter().collect()),
            by_key: Some([(3, 4)].into_iter().collect()),
        })),
        ..Default::default()
    };
    let bytes = nino::serialize(&Some(Box::new(uses.clone()))).unwrap();
    let back: Option<Box<shapes::Uses>> = nino::deserialize_exact(&bytes).unwrap();
    assert_eq!(back.as_deref(), Some(&uses));
}

#[test]
fn generated_fuzz() {
    on_large_stack(fuzz);
}
