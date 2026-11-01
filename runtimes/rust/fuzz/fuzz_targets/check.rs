//! What both targets check of one input, and how an input names its type.

use std::sync::OnceLock;

use nino::{NinoRead, NinoWrite};

use crate::harness::{allowance, measured};

/// The type names a `with_type`-style function of `source` matches, in order (the first byte of an input indexes
/// them; `seeds.py` writes the corpus's cases in the same order).
pub fn names(source: &'static str, function: &str) -> &'static [&'static str] {
    static NAMES: OnceLock<Vec<&'static str>> = OnceLock::new();
    NAMES.get_or_init(|| {
        let body = &source[source.find(function).expect("the dispatch function")..];
        let body = &body[..body.find("_ => return None").expect("the end of the dispatch")];
        body.split('"').skip(1).step_by(2).filter(|s| s.starts_with("Conformance.")).collect()
    })
}

pub fn pick<'a>(names: &[&'static str], data: &'a [u8]) -> Option<(&'static str, &'a [u8])> {
    let (&first, rest) = data.split_first()?;
    Some((names[usize::from(first) % names.len()], rest))
}

/// Reads `data` as `T`: never a panic (libFuzzer reports one), memory in proportion to the input; a value read is
/// written, and those bytes read back and written again the same; reading into that value gives the fresh verdict.
pub fn check<T: NinoRead + NinoWrite + 'static>(data: &[u8]) {
    let (fresh, peak) = measured(|| nino::deserialize_exact::<T>(data));
    assert!(peak <= allowance(data.len()), "allocated {peak} bytes for {} bytes of input", data.len());
    let Ok(value) = fresh else { return };
    let bytes = nino::serialize(&value).expect("a value read is written");
    let again: T = nino::deserialize_exact(&bytes).expect("what was written reads back");
    assert_eq!(nino::serialize(&again).expect("written again"), bytes, "a value read back writes the same bytes");
    let mut into = again;
    let mut r = nino::Reader::new(data);
    let verdict = into.read_into(&mut r).and_then(|()| match r.remaining() {
        0 => Ok(()),
        n => Err(nino::Error::new(nino::ErrorKind::TrailingData, n as i32)),
    });
    assert!(verdict.is_ok(), "reading into a value rejected what a fresh read accepted: {verdict:?}");
    let _ = nino::serialize(&into);
}
