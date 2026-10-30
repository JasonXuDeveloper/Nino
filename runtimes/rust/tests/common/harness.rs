//! What the corpus tests (`tests/conformance.rs` over the hand-written codecs, `tests/generated.rs` over nino-gen's)
//! share: the allocator that measures what a read holds, the corpus's location and files, the mutations of the fuzz,
//! and the large stack the deep cases need. Included by `#[path]`, as `conformance/json.rs` is.

use std::alloc::{GlobalAlloc, Layout, System};
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicUsize, Ordering};

use serde_json::Value;

// ---------------------------------------------------------------------------------------------- allocation tracking

struct Counting;

static CURRENT: AtomicUsize = AtomicUsize::new(0);
static PEAK: AtomicUsize = AtomicUsize::new(0);

unsafe impl GlobalAlloc for Counting {
    unsafe fn alloc(&self, layout: Layout) -> *mut u8 {
        let p = unsafe { System.alloc(layout) };
        if !p.is_null() {
            let now = CURRENT.fetch_add(layout.size(), Ordering::Relaxed) + layout.size();
            PEAK.fetch_max(now, Ordering::Relaxed);
        }
        p
    }

    unsafe fn dealloc(&self, p: *mut u8, layout: Layout) {
        unsafe { System.dealloc(p, layout) };
        CURRENT.fetch_sub(layout.size(), Ordering::Relaxed);
    }

    unsafe fn realloc(&self, p: *mut u8, layout: Layout, new: usize) -> *mut u8 {
        let q = unsafe { System.realloc(p, layout, new) };
        if !q.is_null() {
            if new >= layout.size() {
                let now = CURRENT.fetch_add(new - layout.size(), Ordering::Relaxed) + (new - layout.size());
                PEAK.fetch_max(now, Ordering::Relaxed);
            } else {
                CURRENT.fetch_sub(layout.size() - new, Ordering::Relaxed);
            }
        }
        q
    }
}

#[global_allocator]
static ALLOCATOR: Counting = Counting;

/// Runs `f`, returning its result and the most memory it held at once beyond what was held before.
pub fn measured<R>(f: impl FnOnce() -> R) -> (R, usize) {
    let base = CURRENT.load(Ordering::Relaxed);
    PEAK.store(base, Ordering::Relaxed);
    let r = f();
    (r, PEAK.load(Ordering::Relaxed).saturating_sub(base))
}

/// What a decoder may allocate for `size` bytes of input: 256 bytes per input byte, plus 64 KiB. Collections stay
/// within 16 bytes per input byte up front; the most per byte is a chain of deferred bodies, where each presence byte
/// becomes an object, a queue record and a log entry (about 100 bytes, three times that while a queue grows).
pub fn allowance(size: usize) -> usize {
    256 * size + (64 << 10)
}

// ---------------------------------------------------------------------------------------------- the corpus

/// The corpus at `NINO_CORPUS` or at `conformance/corpus` in the repository, or `None` (then the test passes with a
/// note naming `test`, unless `NINO_CORPUS_REQUIRED=1`).
pub fn corpus_or_skip(test: &str) -> Option<PathBuf> {
    let dir = corpus();
    if dir.is_none() {
        assert!(
            std::env::var("NINO_CORPUS_REQUIRED").as_deref() != Ok("1"),
            "NINO_CORPUS_REQUIRED=1 but no corpus at NINO_CORPUS or conformance/corpus"
        );
        eprintln!("{test}: no corpus (set NINO_CORPUS); skipped");
    }
    dir
}

pub fn corpus() -> Option<PathBuf> {
    if let Ok(dir) = std::env::var("NINO_CORPUS") {
        return Some(PathBuf::from(dir));
    }
    let dir = Path::new(env!("CARGO_MANIFEST_DIR")).join("../../conformance/corpus");
    dir.join("index.json").exists().then_some(dir)
}

pub fn read_json(path: &Path) -> Value {
    let text = std::fs::read_to_string(path).unwrap_or_else(|e| panic!("{}: {e}", path.display()));
    let mut de = serde_json::Deserializer::from_str(&text);
    de.disable_recursion_limit();
    serde::Deserialize::deserialize(&mut de).unwrap()
}

pub fn unhex(s: &str) -> Vec<u8> {
    (0..s.len()).step_by(2).map(|i| u8::from_str_radix(&s[i..i + 2], 16).unwrap()).collect()
}

// ---------------------------------------------------------------------------------------------- mutations

/// xorshift64*: deterministic, so a failure reproduces from its seed and iteration.
pub struct Rng(pub u64);

impl Rng {
    pub fn next(&mut self) -> u64 {
        self.0 ^= self.0 >> 12;
        self.0 ^= self.0 << 25;
        self.0 ^= self.0 >> 27;
        self.0.wrapping_mul(0x2545_F491_4F6C_DD1D)
    }

    pub fn below(&mut self, n: usize) -> usize {
        (self.next() % n.max(1) as u64) as usize
    }
}

const INTERESTING: [i32; 14] =
    [0, 1, -1, -2, 2, 127, 128, 255, 256, i32::MAX, i32::MIN, 0x4000_0000, 0x8000_0004u32 as i32, 0x7FFF_FFF0];

/// One mutation of `data`: a bit flip, an interesting byte or i32 (a length, a header, a type id), a cut, an
/// insertion, a deletion or a copied chunk.
pub fn mutate(rng: &mut Rng, data: &mut Vec<u8>) {
    let len = data.len();
    match rng.below(8) {
        0 if len > 0 => {
            let i = rng.below(len);
            data[i] ^= 1 << rng.below(8);
        }
        1 if len > 0 => {
            let i = rng.below(len);
            data[i] = [0u8, 1, 2, 0x7F, 0x80, 0xFE, 0xFF][rng.below(7)];
        }
        2 if len >= 4 => {
            let i = rng.below(len - 3);
            let v = INTERESTING[rng.below(INTERESTING.len())];
            data[i..i + 4].copy_from_slice(&v.to_le_bytes());
        }
        3 if len >= 4 => {
            // Nudge an i32 (a length or an offset) by a little.
            let i = rng.below(len - 3);
            let v = i32::from_le_bytes(data[i..i + 4].try_into().unwrap());
            let d = rng.below(17) as i32 - 8;
            data[i..i + 4].copy_from_slice(&v.wrapping_add(d).to_le_bytes());
        }
        4 if len > 0 => data.truncate(rng.below(len)),
        5 => {
            let i = rng.below(len + 1);
            let n = 1 + rng.below(8);
            for _ in 0..n {
                data.insert(i, rng.next() as u8);
            }
        }
        6 if len > 0 => {
            let i = rng.below(len);
            let n = 1 + rng.below((len - i).min(16));
            data.drain(i..i + n);
        }
        _ if len > 0 => {
            let from = rng.below(len);
            let n = 1 + rng.below((len - from).min(64));
            let chunk = data[from..from + n].to_vec();
            let at = rng.below(len);
            data.splice(at..at, chunk);
        }
        _ => data.push(rng.next() as u8),
    }
}

/// The tests measure memory through one global counter: one at a time.
static SERIAL: std::sync::Mutex<()> = std::sync::Mutex::new(());

pub fn on_large_stack(f: fn()) {
    let _guard = SERIAL.lock().unwrap_or_else(|e| e.into_inner());
    // Deep cases nest their JSON (and the values read) about 1,000 levels deep: compare and drop them on a large
    // stack. The reader itself never recurses more than 128 class bodies deep for deferrable types.
    std::thread::Builder::new().stack_size(512 << 20).spawn(f).unwrap().join().unwrap();
}
