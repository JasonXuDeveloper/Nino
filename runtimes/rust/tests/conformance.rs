//! The shared conformance corpus (conformance/README.md): every valid case read, compared with its JSON and
//! written back byte for byte; every malicious case and every truncated valid case rejected, with no panic and
//! memory bounded by a small multiple of the input.
//!
//! The corpus is found at `NINO_CORPUS` or at `conformance/corpus` in the repository. Without one the test passes
//! with a note, unless `NINO_CORPUS_REQUIRED=1` (CI sets it whenever the repository has a corpus).

#![allow(clippy::unwrap_used)]

#[path = "conformance/json.rs"]
mod json;
#[path = "conformance/types.rs"]
mod types;

use std::alloc::{GlobalAlloc, Layout, System};
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicUsize, Ordering};

use json::{Visit, J};
use nino::{NinoRead, NinoWrite};
use serde_json::Value;

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

/// Runs `f`, returning its result and the most memory it held at once beyond what was held before.
fn measured<R>(f: impl FnOnce() -> R) -> (R, usize) {
    let base = CURRENT.load(Ordering::Relaxed);
    PEAK.store(base, Ordering::Relaxed);
    let r = f();
    (r, PEAK.load(Ordering::Relaxed).saturating_sub(base))
}

/// What a decoder may allocate for `size` bytes of input: 256 bytes per input byte, plus 64 KiB. Collections stay
/// within 16 bytes per input byte up front; the most per byte is a chain of deferred bodies, where each presence byte
/// becomes an object, a queue record and a log entry (about 100 bytes, three times that while a queue grows).
fn allowance(size: usize) -> usize {
    256 * size + (64 << 10)
}

// ---------------------------------------------------------------------------------------------- visitors

struct Valid<'a>(&'a [u8]);

impl Visit<Result<(Value, Vec<u8>), nino::Error>> for Valid<'_> {
    fn visit<T: NinoRead + NinoWrite + J + 'static>(self) -> Result<(Value, Vec<u8>), nino::Error> {
        let value: T = nino::deserialize_exact(self.0)?;
        let json = value.j();
        let bytes = nino::serialize(&value)?;
        Ok((json, bytes))
    }
}

struct Decode<'a>(&'a [u8]);

impl Visit<Result<(), nino::Error>> for Decode<'_> {
    fn visit<T: NinoRead + NinoWrite + J + 'static>(self) -> Result<(), nino::Error> {
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
    let mut de = serde_json::Deserializer::from_str(&text);
    de.disable_recursion_limit();
    serde::Deserialize::deserialize(&mut de).unwrap()
}

fn unhex(s: &str) -> Vec<u8> {
    (0..s.len()).step_by(2).map(|i| u8::from_str_radix(&s[i..i + 2], 16).unwrap()).collect()
}

/// The first place two JSON values differ, for the failure message.
fn diff(path: &str, a: &Value, b: &Value) -> Option<String> {
    match (a, b) {
        (Value::Object(x), Value::Object(y)) => {
            for (k, v) in x {
                match y.get(k) {
                    Some(w) => {
                        if let Some(d) = diff(&format!("{path}.{k}"), v, w) {
                            return Some(d);
                        }
                    }
                    None => return Some(format!("{path}.{k}: missing in the expected value")),
                }
            }
            y.keys().find(|k| !x.contains_key(*k)).map(|k| format!("{path}.{k}: missing in the value read"))
        }
        (Value::Array(x), Value::Array(y)) => {
            if x.len() != y.len() {
                return Some(format!("{path}: {} items read, {} expected", x.len(), y.len()));
            }
            x.iter().zip(y).enumerate().find_map(|(i, (v, w))| diff(&format!("{path}[{i}]"), v, w))
        }
        _ => (a != b).then(|| format!("{path}: read {a}, expected {b}")),
    }
}

fn run() {
    let Some(dir) = corpus() else {
        assert!(
            std::env::var("NINO_CORPUS_REQUIRED").as_deref() != Ok("1"),
            "NINO_CORPUS_REQUIRED=1 but no corpus at NINO_CORPUS or conformance/corpus"
        );
        eprintln!("conformance: no corpus (set NINO_CORPUS); skipped");
        return;
    };
    let index = read_json(&dir.join("index.json"));
    assert_eq!(index["corpusVersion"], 2, "corpus version");
    let mut failures = Vec::new();
    let (mut passed, mut skipped) = (0, Vec::new());

    // Valid: read, compare, write back.
    for name in index["valid"].as_array().unwrap() {
        let name = name.as_str().unwrap();
        let case = read_json(&dir.join(format!("valid/{name}.json")));
        let bytes = std::fs::read(dir.join(format!("valid/{name}.bin"))).unwrap();
        let ty = case["type"].as_str().unwrap();
        if case.get("optional").is_some_and(|o| !o.is_null()) {
            skipped.push(format!("{name} (optional: {})", case["optional"]));
            continue;
        }
        if types::NOT_EMULATED.contains(&ty) {
            skipped.push(format!("{name} (in tests/generated.rs)"));
            continue;
        }
        let Some(result) = types::with_type(ty, Valid(&bytes)) else {
            failures.push(format!("{name}: no codec for {ty}"));
            continue;
        };
        match result {
            Err(e) => failures.push(format!("{name}: read failed: {e:?}")),
            Ok((json, written)) => {
                if let Some(d) = diff("$", &json, &case["value"]) {
                    failures.push(format!("{name}: {d}"));
                    continue;
                }
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

    // Malicious: an error, bounded memory.
    for name in index["malicious"].as_array().unwrap() {
        let name = name.as_str().unwrap();
        let case = read_json(&dir.join(format!("malicious/{name}.json")));
        let bytes = std::fs::read(dir.join(format!("malicious/{name}.bin"))).unwrap();
        let ty = case["type"].as_str().unwrap();
        if case.get("optional").is_some_and(|o| !o.is_null()) {
            skipped.push(format!("{name} (optional: {})", case["optional"]));
            continue;
        }
        if types::NOT_EMULATED.contains(&ty) {
            skipped.push(format!("{name} (in tests/generated.rs)"));
            continue;
        }
        let (result, peak) = measured(|| types::with_type(ty, Decode(&bytes)));
        let Some(result) = result else {
            failures.push(format!("{name}: no codec for {ty}"));
            continue;
        };
        let may_succeed = case["expected"] == "error-or-value";
        if result.is_ok() && !may_succeed {
            failures.push(format!("{name}: accepted ({})", case["exercises"].as_str().unwrap_or("")));
            continue;
        }
        if peak > allowance(bytes.len()) {
            failures.push(format!("{name}: allocated {peak} bytes for {} bytes of input", bytes.len()));
            continue;
        }
        passed += 1;
    }

    // Truncations: every strict prefix of the listed valid cases is an error.
    let truncations = read_json(&dir.join("malicious/truncations.json"));
    let mut prefixes = 0;
    for entry in truncations["cases"].as_array().unwrap() {
        let name = entry["case"].as_str().unwrap();
        let ty = entry["type"].as_str().unwrap();
        if entry.get("optional").is_some_and(|o| !o.is_null()) {
            skipped.push(format!("{name} prefixes (optional)"));
            continue;
        }
        if types::NOT_EMULATED.contains(&ty) {
            skipped.push(format!("{name} prefixes (in tests/generated.rs)"));
            continue;
        }
        let bytes = std::fs::read(dir.join(format!("valid/{name}.bin"))).unwrap();
        for n in 0..bytes.len() {
            let prefix = &bytes[..n];
            let (result, peak) = measured(|| types::with_type(ty, Decode(prefix)));
            match result {
                None => {
                    failures.push(format!("{name}: no codec for {ty}"));
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
        "conformance: {passed} cases and {prefixes} truncated prefixes passed; skipped: {}",
        if skipped.is_empty() { "none".to_string() } else { skipped.join(", ") }
    );
    assert!(failures.is_empty(), "{} failures:\n{}", failures.len(), failures.join("\n"));
}

/// The tests measure memory through one global counter: one at a time.
static SERIAL: std::sync::Mutex<()> = std::sync::Mutex::new(());

fn on_large_stack(f: fn()) {
    let _guard = SERIAL.lock().unwrap_or_else(|e| e.into_inner());
    // Deep cases nest their JSON (and the values read) about 1,000 levels deep: compare and drop them on a large
    // stack. The reader itself never recurses more than 128 class bodies deep for deferrable types.
    std::thread::Builder::new().stack_size(512 << 20).spawn(f).unwrap().join().unwrap();
}

#[test]
fn conformance_corpus() {
    on_large_stack(run);
}

// ---------------------------------------------------------------------------------------------- mutational fuzzing

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

const INTERESTING: [i32; 14] =
    [0, 1, -1, -2, 2, 127, 128, 255, 256, i32::MAX, i32::MIN, 0x4000_0000, 0x8000_0004u32 as i32, 0x7FFF_FFF0];

/// One mutation of `data`: a bit flip, an interesting byte or i32 (a length, a header, a type id), a cut, an
/// insertion, a deletion or a copied chunk.
fn mutate(rng: &mut Rng, data: &mut Vec<u8>) {
    let len = data.len();
    match rng.below(8) {
        0 if len > 0 => {
            let i = rng.below(len);
            data[i] ^= 1 << rng.below(8);
        }
        1 if len > 0 => {
            let i = rng.below(len);
            data[i] = [0u8, 1, 2, 0x7F, 0x80, 0xFE, 0xFF][rng.below(7)];
        }
        2 if len >= 4 => {
            let i = rng.below(len - 3);
            let v = INTERESTING[rng.below(INTERESTING.len())];
            data[i..i + 4].copy_from_slice(&v.to_le_bytes());
        }
        3 if len >= 4 => {
            // Nudge an i32 (a length or an offset) by a little.
            let i = rng.below(len - 3);
            let v = i32::from_le_bytes(data[i..i + 4].try_into().unwrap());
            let d = rng.below(17) as i32 - 8;
            data[i..i + 4].copy_from_slice(&v.wrapping_add(d).to_le_bytes());
        }
        4 if len > 0 => data.truncate(rng.below(len)),
        5 => {
            let i = rng.below(len + 1);
            let n = 1 + rng.below(8);
            for _ in 0..n {
                data.insert(i, rng.next() as u8);
            }
        }
        6 if len > 0 => {
            let i = rng.below(len);
            let n = 1 + rng.below((len - i).min(16));
            data.drain(i..i + n);
        }
        _ if len > 0 => {
            let from = rng.below(len);
            let n = 1 + rng.below((len - from).min(64));
            let chunk = data[from..from + n].to_vec();
            let at = rng.below(len);
            data.splice(at..at, chunk);
        }
        _ => data.push(rng.next() as u8),
    }
}

/// Mutates every valid corpus case `NINO_FUZZ_ITERATIONS` times (default 300) and reads each result as the case's
/// type: an error or a value, but never a panic, and never memory out of proportion to the input.
fn fuzz() {
    let Some(dir) = corpus() else {
        eprintln!("fuzz: no corpus (set NINO_CORPUS); skipped");
        return;
    };
    let iterations: usize = std::env::var("NINO_FUZZ_ITERATIONS").ok().and_then(|v| v.parse().ok()).unwrap_or(300);
    let index = read_json(&dir.join("index.json"));
    let mut cases: Vec<(String, String, Vec<u8>)> = Vec::new();
    for group in ["valid", "malicious"] {
        for name in index[group].as_array().unwrap() {
            let name = name.as_str().unwrap();
            let case = read_json(&dir.join(format!("{group}/{name}.json")));
            if case.get("optional").is_some_and(|o| !o.is_null()) {
                continue;
            }
            let bytes = std::fs::read(dir.join(format!("{group}/{name}.bin"))).unwrap();
            cases.push((name.to_string(), case["type"].as_str().unwrap().to_string(), bytes));
        }
    }
    let mut failures = Vec::new();
    let (mut accepted, mut rejected) = (0usize, 0usize);
    for (k, (name, ty, seed)) in cases.iter().enumerate() {
        let mut rng = Rng(0x9E37_79B9_7F4A_7C15 ^ (k as u64 + 1).wrapping_mul(0xD1B5_4A32_D192_ED03));
        for iteration in 0..iterations {
            let mut data = seed.clone();
            for _ in 0..1 + rng.below(4) {
                mutate(&mut rng, &mut data);
            }
            let outcome = std::panic::catch_unwind(|| measured(|| types::with_type(ty, Decode(&data))));
            match outcome {
                Err(_) => {
                    failures.push(format!("{name} iteration {iteration}: panicked on {}", hex(&data)));
                }
                Ok((_, peak)) if peak > allowance(data.len()) => {
                    failures.push(format!("{name} iteration {iteration}: allocated {peak} bytes for {}", data.len()));
                }
                Ok((Some(Ok(())), _)) => accepted += 1,
                Ok(_) => rejected += 1,
            }
            if failures.len() > 20 {
                break;
            }
        }
    }
    eprintln!("fuzz: {} inputs from {} seeds: {accepted} read, {rejected} rejected", accepted + rejected, cases.len());
    assert!(failures.is_empty(), "{} failures:\n{}", failures.len(), failures.join("\n"));
}

fn hex(data: &[u8]) -> String {
    data.iter().map(|b| format!("{b:02x}")).collect()
}

#[test]
fn mutational_fuzz() {
    on_large_stack(fuzz);
}
