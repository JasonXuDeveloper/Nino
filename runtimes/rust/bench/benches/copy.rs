//! Development-only block-copy probe, separate from the published benchmark.
//!
//! `cargo bench -p nino-bench --bench copy -- block-copy` compares each available ISA with Rust's ordinary
//! slice copy. Repeat on x64 with `GLIBC_TUNABLES=glibc.cpu.x86_rep_movsb_threshold=2112` set before launch.

use std::hint::black_box;
use std::time::Duration;

use criterion::{criterion_group, criterion_main, BenchmarkId, Criterion, Throughput};

#[path = "copy/kernels.rs"]
mod kernels;

fn check_copy(copy: kernels::Copy) {
    let src = (0..512).map(|i| (i * 73 + i / 256) as u8).collect::<Vec<_>>();
    let mut dst = vec![0; src.len()];
    for off in 0..64 {
        let (d, s) = (64 + off, 64 + off * 17 % 64);
        for n in 0..=288 {
            dst.fill(0xA5);
            // SAFETY: The benchmark has confirmed the kernel's CPU features; the slices do not alias.
            unsafe { copy(&mut dst[d..d + n], &src[s..s + n]) };
            assert_eq!(dst[d..d + n], src[s..s + n], "n={n} src={s} dst={d}");
            assert_eq!((dst[d - 1], dst[d + n]), (0xA5, 0xA5), "n={n} src={s} dst={d}");
        }
    }
}

fn page_offset(bytes: &[u8], offset: usize) -> usize {
    bytes.as_ptr().align_offset(4096) + offset
}

fn benches(c: &mut Criterion) {
    let copies = kernels::available();
    for (_, copy) in &copies {
        check_copy(*copy);
    }
    let mut group = c.benchmark_group("block-copy");
    const VARIANTS: usize = 4;
    for size in [2048, 2113, 4096, 65536, 262144, 1048576] {
        group.throughput(Throughput::Bytes(size as u64));
        for dst_offset in [0, 4] {
            for (name, copy) in &copies {
                group.bench_function(BenchmarkId::new(*name, format!("{size}-offset{dst_offset}")), |b| {
                    let src = (0..VARIANTS)
                        .map(|i| (0..size + 4096).map(|j| (j * 73 + i) as u8).collect::<Vec<_>>())
                        .collect::<Vec<_>>();
                    let mut dst = vec![vec![0; size + 4096 + 128 * VARIANTS]; VARIANTS];
                    let src_off = src.iter().map(|v| page_offset(v, 0)).collect::<Vec<_>>();
                    let dst_off =
                        dst.iter().enumerate().map(|(i, v)| page_offset(v, 128 * i + dst_offset)).collect::<Vec<_>>();
                    let mut i = 0;
                    b.iter(|| {
                        let src = &src[i][src_off[i]..src_off[i] + size];
                        let dst = &mut dst[i][dst_off[i]..dst_off[i] + size];
                        // SAFETY: The benchmark has confirmed the kernel's CPU features; the slices do not alias.
                        unsafe { copy(black_box(dst), black_box(src)) };
                        i = (i + 1) % VARIANTS;
                    });
                });
            }
        }
    }
    group.finish();
}

criterion_group! {
    name = group;
    config = Criterion::default().warm_up_time(Duration::from_millis(150))
        .measurement_time(Duration::from_millis(400)).sample_size(30);
    targets = benches
}
criterion_main!(group);
