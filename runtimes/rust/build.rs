//! Sets `nino_avx512` when the compiler has the AVX-512 target features and intrinsics (stable since Rust 1.89), so
//! that the crate builds on its MSRV (1.88) without the AVX-512 kernels of `src/simd/x86.rs`. No dependencies.

use std::process::Command;

fn main() {
    println!("cargo::rustc-check-cfg=cfg(nino_avx512)");
    println!("cargo::rerun-if-changed=build.rs");
    println!("cargo::rerun-if-env-changed=RUSTC");
    let rustc = std::env::var_os("RUSTC").unwrap_or_else(|| "rustc".into());
    let Ok(output) = Command::new(rustc).arg("--version").output() else { return };
    // "rustc 1.89.0 (...)", "rustc 1.90.0-nightly (...)".
    let version = String::from_utf8_lossy(&output.stdout);
    let mut parts = version.split_whitespace().nth(1).unwrap_or("").split('.').map(|p| p.parse::<u32>().ok());
    let (major, minor) = (parts.next().flatten(), parts.next().flatten());
    if major.is_some_and(|m| m > 1) || (major == Some(1) && minor.is_some_and(|m| m >= 89)) {
        println!("cargo::rustc-cfg=nino_avx512");
    }
}
