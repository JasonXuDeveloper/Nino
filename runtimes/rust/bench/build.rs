//! The Protocol Buffers messages of the .NET suite's protobuf-net contracts (runtimes/csharp/benchmarks:
//! Fixtures/cases.proto and Workloads/workloads.proto, which `Nino.Benchmark --write-proto` writes), compiled with
//! protox (no protoc) into prost types, and `with_message`: a case's message by its name (cases.json's `proto`), as `with_nino_serde_type`
//! finds its value's type.

use std::fmt::Write as _;
use std::path::PathBuf;

use heck::ToUpperCamelCase;

fn main() -> Result<(), Box<dyn std::error::Error>> {
    let benchmark = PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../../csharp/benchmarks/Nino.Benchmark");
    let fixtures = benchmark.join("Fixtures");
    let workloads = benchmark.join("Workloads");
    let files = [fixtures.join("cases.proto"), workloads.join("workloads.proto")];
    for file in &files {
        println!("cargo:rerun-if-changed={}", file.display());
    }
    let set = protox::compile(&files, [&fixtures, &workloads])?;

    // One arm per top-level message of the suite's packages, by its name (cases.json's "proto"); a name both have is
    // nino.cases' message, as the Go benchmark looks it up.
    let mut arms = String::new();
    let mut names = std::collections::HashSet::new();
    for file in set.file.iter().filter(|f| f.package().starts_with("nino.")) {
        let module = file.package().replace('.', "::");
        for message in &file.message_type {
            let name = message.name();
            if !names.insert(name.to_owned()) {
                continue;
            }
            writeln!(arms, "        \"{name}\" => f.visit::<{module}::{}>(),", name.to_upper_camel_case())?;
        }
    }
    let out = PathBuf::from(std::env::var("OUT_DIR")?);
    std::fs::write(
        out.join("registry.rs"),
        format!(
            "/// Runs `f` with the prost type of the message of that full name (cases.json's `proto`: `SimpleClassBase`), or returns\n\
             /// `None` for a message the files do not have.\n\
             pub fn with_message<R>(name: &str, f: impl ProtoVisit<R>) -> Option<R> {{\n    Some(match name {{\n{arms}        _ => return None,\n    }})\n}}\n"
        ),
    )?;
    prost_build::Config::new().compile_fds(set)?;
    Ok(())
}
