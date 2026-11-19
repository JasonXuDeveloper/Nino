//! Development-only #263 probe of an actual `nino::deserialize::<Vec<u8>>` with Nino's four-byte header and the
//! platform's real allocator. The write rows take a naturally allocated reused Vec and put Nino's four-byte count
//! before the copied payload. Compare the stock paths and the candidate under the same glibc settings.

use std::hint::black_box;
use std::time::Duration;

use criterion::{criterion_group, criterion_main, BenchmarkId, Criterion, Throughput};

struct Input {
    bytes: Vec<u8>,
    start: usize,
}

impl Input {
    fn new(bytes: Vec<u8>, align_payload: bool) -> Self {
        if !align_payload {
            return Self { bytes, start: 0 };
        }
        Self::page_offset(bytes, 0)
    }

    fn page_offset(bytes: Vec<u8>, offset: usize) -> Self {
        // Place the Nino length four bytes before the desired payload offset within a page.
        let mut storage = vec![0; bytes.len() + 4095 + offset];
        let start = storage[4..].as_ptr().align_offset(4096) + offset;
        storage[start..].get_mut(..bytes.len()).unwrap().copy_from_slice(&bytes);
        storage.truncate(start + bytes.len());
        Self { bytes: storage, start }
    }

    fn wire(&self) -> &[u8] {
        &self.bytes[self.start..]
    }

    fn payload(&self) -> &[u8] {
        &self.wire()[4..]
    }
}

fn record_placement(residues: &mut Vec<(usize, usize, usize)>, src: &[u8], dst: &[u8]) {
    // Record placement rather than silently assuming the synthetic probe's dst+4 applies to this Vec.
    let src = src.as_ptr() as usize;
    let dst = dst.as_ptr() as usize;
    let sample = (src % 64, dst % 64, dst.wrapping_sub(src) % 4096);
    if residues.len() < 8 && !residues.contains(&sample) {
        residues.push(sample);
    }
}

fn benches(c: &mut Criterion) {
    let mut group = c.benchmark_group("byte-copy");
    for (name, size, variants, align_payload) in [
        ("control", 2048, 4, false),
        ("ordinary", 2113, 4, false),
        ("ordinary", 4096, 4, false),
        ("page-aligned", 2113, 4, true),
        ("page-aligned", 4096, 4, true),
        // A 32 MiB rotation shifts the source working set out of L2; output still has the real allocator's reuse.
        ("rotating", 4096, 8192, false),
    ] {
        let mut inputs = Vec::with_capacity(variants);
        let mut residues = Vec::new();
        for i in 0..variants {
            let value = (0..size).map(|j| (j * 73 + i / 256 + i) as u8).collect::<Vec<_>>();
            let input = Input::new(nino::serialize(&value).unwrap(), align_payload);
            assert_eq!(input.wire()[..4], i32::try_from(size).unwrap().to_le_bytes());
            assert_eq!(input.payload(), value);
            let decoded = nino::deserialize::<Vec<u8>>(input.wire()).unwrap();
            assert_eq!(decoded, value);
            assert!(nino::deserialize::<Vec<u8>>(&input.wire()[..input.wire().len() - 1]).is_err());
            record_placement(&mut residues, input.payload(), &decoded);
            inputs.push(input);
        }
        println!("byte-placement read-{name}/{size}: (src % 64, dst % 64, delta % 4096) {residues:?}");
        group.throughput(Throughput::Bytes(size as u64));
        group.bench_function(BenchmarkId::new(format!("read-{name}"), size), |b| {
            let mut i = 0;
            b.iter(|| {
                let input = inputs[i].wire();
                i = (i + 1) & (variants - 1);
                drop(black_box(nino::deserialize::<Vec<u8>>(black_box(input)).unwrap()));
            });
        });
        if align_payload || name == "control" {
            let mut outputs = Vec::with_capacity(variants);
            let mut residues = Vec::new();
            for input in &inputs {
                let mut out = Vec::with_capacity(input.wire().len());
                let original = out.as_ptr();
                nino::serialize_into(input.payload(), &mut out).unwrap();
                assert_eq!(out, input.wire());
                assert_eq!(out.as_ptr(), original, "writer reused its existing allocation");
                record_placement(&mut residues, input.payload(), &out[4..]);
                outputs.push(out);
            }
            println!("byte-placement write-reused/{size}: (src % 64, dst % 64, delta % 4096) {residues:?}");
            group.bench_function(BenchmarkId::new("write-reused", size), |b| {
                let mut i = 0;
                b.iter(|| {
                    let out = &mut outputs[i];
                    out.clear();
                    nino::serialize_into(black_box(inputs[i].payload()), out).unwrap();
                    black_box(out.as_slice());
                    i = (i + 1) & (variants - 1);
                });
            });
        }
    }
    group.finish();

    let mut mixed = c.benchmark_group("copy-mixed");
    for (name, sizes) in [
        ("control", [128, 1024, 2048, 2112, 4097, 8192, 32768, 65536]),
        ("window", [2113, 2176, 2304, 2560, 3072, 3584, 4032, 4096]),
    ] {
        let inputs: Vec<_> = (0..32)
            .map(|i| {
                let value: Vec<_> = (0..sizes[i % sizes.len()]).map(|j| (j * 73 + i) as u8).collect();
                Input::page_offset(nino::serialize(&value).unwrap(), (i * 29) % 4096)
            })
            .collect();
        let mut outputs: Vec<_> = inputs.iter().map(|input| Vec::with_capacity(input.wire().len())).collect();
        for (input, out) in inputs.iter().zip(&mut outputs) {
            nino::serialize_into(input.payload(), out).unwrap();
            assert_eq!(out, input.wire());
            let mut fresh = Vec::new();
            nino::serialize_into(input.payload(), &mut fresh).unwrap();
            assert_eq!(fresh, input.wire());
        }
        mixed.bench_function(BenchmarkId::new("write-reused", name), |b| {
            let mut i = 0;
            b.iter(|| {
                let out = &mut outputs[i];
                out.clear();
                nino::serialize_into(black_box(inputs[i].payload()), out).unwrap();
                black_box(out.as_slice());
                i = (i + 1) % inputs.len();
            });
        });
        mixed.bench_function(BenchmarkId::new("write-fresh", name), |b| {
            let mut i = 0;
            b.iter(|| {
                let mut out = Vec::new();
                nino::serialize_into(black_box(inputs[i].payload()), &mut out).unwrap();
                drop(black_box(out));
                i = (i + 1) % inputs.len();
            });
        });
    }
    mixed.finish();
}

criterion_group! {
    name = group;
    config = Criterion::default()
        .warm_up_time(Duration::from_millis(250))
        .measurement_time(Duration::from_secs(1))
        .sample_size(50);
    targets = benches
}
criterion_main!(group);
