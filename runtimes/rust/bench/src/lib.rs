//! The .NET suite's cases in Rust (benches/compare.rs): their types and codecs as nino-gen writes them (`model`,
//! generate.sh) and the messages of their protobuf-net contracts (`proto`, build.rs).

/// What nino-gen writes for the cases' types (generate.sh; do not edit).
#[rustfmt::skip]
#[allow(clippy::all, clippy::pedantic, unused)]
pub mod model;

pub mod proto;
