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

#[path = "common/harness.rs"]
mod harness;

use harness::{allowance, corpus, corpus_or_skip, measured, mutate, on_large_stack, read_json, written_back, Rng};

use json::{Visit, J};
use nino::{NinoRead, NinoWrite};
use serde_json::Value;

// ---------------------------------------------------------------------------------------------- visitors

/// A valid case's bytes, and the bytes of a case of the same type to read them into (`deserialize_into`): the
/// value read into must equal the value read fresh.
struct Valid<'a> {
    bytes: &'a [u8],
    target: &'a [u8],
}

impl Visit<Result<(Value, Vec<u8>, Value), nino::Error>> for Valid<'_> {
    fn visit<T: NinoRead + NinoWrite + J + 'static>(self) -> Result<(Value, Vec<u8>, Value), nino::Error> {
        let value: T = nino::deserialize_exact(self.bytes)?;
        let json = value.j();
        let bytes = nino::serialize(&value)?;
        let mut target: T = nino::deserialize_exact(self.target)?;
        into_exact(self.bytes, &mut target)?;
        Ok((json, bytes, target.j()))
    }
}

/// [`nino::deserialize_into`], failing when bytes are left after the value (as `deserialize_exact`).
fn into_exact<T: NinoRead>(data: &[u8], value: &mut T) -> Result<(), nino::Error> {
    let mut r = nino::Reader::new(data);
    value.read_into(&mut r)?;
    match r.remaining() {
        0 => Ok(()),
        n => Err(nino::Error::new(nino::ErrorKind::TrailingData, n as i32)),
    }
}

struct Decode<'a>(&'a [u8]);

impl Visit<Result<(), nino::Error>> for Decode<'_> {
    fn visit<T: NinoRead + NinoWrite + J + 'static>(self) -> Result<(), nino::Error> {
        nino::deserialize_exact::<T>(self.0).map(drop)
    }
}

/// Hostile bytes read into the value of `target` (bytes of the same type), which must then still be a value that
/// can be written. `None` when `target` is not valid itself (a fuzz seed from the malicious cases).
struct DecodeInto<'a> {
    bytes: &'a [u8],
    target: &'a [u8],
}

impl Visit<Option<Result<(), nino::Error>>> for DecodeInto<'_> {
    fn visit<T: NinoRead + NinoWrite + J + 'static>(self) -> Option<Result<(), nino::Error>> {
        let mut value: T = nino::deserialize_exact(self.target).ok()?;
        let into = into_exact(self.bytes, &mut value);
        let _ = nino::serialize(&value);
        Some(into)
    }
}

/// Reads hostile bytes into the value of a valid case: it must fail where a fresh read fails, and accept where it
/// accepts.
fn check_into(ty: &str, bytes: &[u8], target: &[u8], fresh_ok: bool) -> Option<String> {
    let into = types::with_type(ty, DecodeInto { bytes, target })??;
    (into.is_ok() != fresh_ok).then(|| format!("read into a valid value: {into:?}, read fresh: ok={fresh_ok}"))
}

// ---------------------------------------------------------------------------------------------- the corpus

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
    let Some(dir) = corpus_or_skip("conformance") else { return };
    let index = read_json(&dir.join("index.json"));
    assert_eq!(index["corpusVersion"], 2, "corpus version");
    let mut failures = Vec::new();
    let (mut passed, mut skipped) = (0, Vec::new());

    // Valid: read, compare, write back; read into the value of the type's previous case.
    let mut targets: std::collections::HashMap<String, Vec<u8>> = std::collections::HashMap::new();
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
        let target = targets.get(ty).cloned().unwrap_or_else(|| bytes.clone());
        let Some(result) = types::with_type(ty, Valid { bytes: &bytes, target: &target }) else {
            failures.push(format!("{name}: no codec for {ty}"));
            continue;
        };
        match result {
            Err(e) => failures.push(format!("{name}: read failed: {e:?}")),
            Ok((json, written, into)) => {
                if let Some(d) = diff("$", &json, &case["value"]) {
                    failures.push(format!("{name}: {d}"));
                    continue;
                }
                if let Some(d) = diff("$", &into, &case["value"]) {
                    failures.push(format!("{name}: read into the previous {ty}: {d}"));
                    continue;
                }
                targets.insert(ty.to_string(), bytes.clone());
                if let Some(failure) = written_back(name, &case, &bytes, &written) {
                    failures.push(failure);
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
        if let Some(e) = targets.get(ty).and_then(|t| check_into(ty, &bytes, t, result.is_ok())) {
            failures.push(format!("{name}: {e}"));
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
                Some(Err(_)) => {
                    if let Some(e) = check_into(ty, prefix, &bytes, false) {
                        failures.push(format!("{name}: prefix of {n} bytes: {e}"));
                        break;
                    }
                    prefixes += 1;
                }
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
    on_large_stack(run);
}

// ---------------------------------------------------------------------------------------------- mutational fuzzing

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
            let outcome = std::panic::catch_unwind(|| {
                let (result, peak) = measured(|| types::with_type(ty, Decode(&data)));
                // Read into the seed's value too: the same verdict, no panic.
                let into = result.as_ref().and_then(|r| check_into(ty, &data, seed, r.is_ok()));
                (result, peak, into)
            });
            match outcome {
                Err(_) => {
                    failures.push(format!("{name} iteration {iteration}: panicked on {}", hex(&data)));
                }
                Ok((_, _, Some(e))) => failures.push(format!("{name} iteration {iteration}: {e} on {}", hex(&data))),
                Ok((_, peak, None)) if peak > allowance(data.len()) => {
                    failures.push(format!("{name} iteration {iteration}: allocated {peak} bytes for {}", data.len()));
                }
                Ok((Some(Ok(())), _, None)) => accepted += 1,
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
