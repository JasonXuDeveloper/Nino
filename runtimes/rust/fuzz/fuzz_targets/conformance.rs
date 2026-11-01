//! The hand-written codecs of the corpus's types (tests/conformance/types.rs) on coverage-guided input: the first
//! byte picks the type, the rest is read as it (README.md, "Fuzzing").
#![no_main]

#[path = "check.rs"]
mod check;
#[allow(dead_code)]
#[path = "../../tests/common/harness.rs"]
mod harness;
#[allow(dead_code, clippy::all)]
#[path = "../../tests/conformance/json.rs"]
mod json;
#[allow(dead_code, clippy::all)]
#[path = "../../tests/conformance/types.rs"]
mod types;

use json::{Visit, J};
use nino::{NinoRead, NinoWrite};

struct Run<'a>(&'a [u8]);

impl Visit<()> for Run<'_> {
    fn visit<T: NinoRead + NinoWrite + J + 'static>(self) {
        check::check::<T>(self.0);
    }
}

libfuzzer_sys::fuzz_target!(|data: &[u8]| {
    let names = check::names(include_str!("../../tests/conformance/types.rs"), "pub fn with_type");
    if let Some((name, bytes)) = check::pick(&names, data) {
        types::with_type(name, Run(bytes));
    }
});
