//! The code nino-gen writes for the corpus's types (tests/generated/nino_types.rs) on coverage-guided input: the
//! first byte picks the type, the rest is read as it (README.md, "Fuzzing").
#![no_main]

#[allow(dead_code, clippy::all)]
#[rustfmt::skip]
#[path = "../../tests/generated/nino_types.rs"]
mod nino_types;
#[path = "check.rs"]
mod check;
#[allow(dead_code)]
#[path = "../../tests/common/harness.rs"]
mod harness;

use nino::{NinoRead, NinoWrite};
use nino_types::NinoVisit;

struct Run<'a>(&'a [u8]);

impl NinoVisit<()> for Run<'_> {
    fn visit<T: NinoRead + NinoWrite + 'static>(self) {
        check::check::<T>(self.0);
    }
}

libfuzzer_sys::fuzz_target!(|data: &[u8]| {
    let names = check::names(include_str!("../../tests/generated/nino_types.rs"), "pub fn with_nino_type");
    if let Some((name, bytes)) = check::pick(&names, data) {
        nino_types::with_nino_type(name, Run(bytes));
    }
});
