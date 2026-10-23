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

/// What a decoder may allocate for `size` bytes of input: 64 bytes per input byte, plus 64 KiB.
fn allowance(size: usize) -> usize {
    64 * size + (64 << 10)
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
    assert_eq!(index["corpusVersion"], 1, "corpus version");
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

#[test]
fn conformance_corpus() {
    // Deep cases nest their JSON (and the reference values) about 1,000 levels deep: compare and drop them on a
    // large stack. The reader itself never recurses more than 128 class bodies deep for deferrable types.
    std::thread::Builder::new().stack_size(512 << 20).spawn(run).unwrap().join().unwrap();
}
