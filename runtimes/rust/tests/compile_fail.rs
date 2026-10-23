//! What the derive rejects, and that its error names the field: each case in tests/compile_fail must fail to compile
//! with the message in its .stderr. `TRYBUILD=overwrite cargo test --features derive --test compile_fail` rewrites
//! the .stderr files after a deliberate change to a message.

#[test]
fn derive_rejects() {
    trybuild::TestCases::new().compile_fail("tests/compile_fail/*.rs");
}
