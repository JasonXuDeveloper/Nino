//! x86_64's kernels: the instruction sets found at run time ([`tier`]), the one-pass string writers for each
//! ([`wide_sse41`], [`wide_avx2`], [`wide_avx512`]) and the reader's decoders ([`latin1_ssse3`], [`utf16_sse41`],
//! [`latin1_avx512`], [`utf16_avx512`]). See the module root for what each writes and how it is tested.

use core::arch::x86_64::*;
use core::sync::atomic::{AtomicU8, Ordering};

use super::{KEEP16, KEEP8};

// ------------------------------------------------------------------------------------------------ the tiers

/// The instruction sets the kernels use, each with all of those before it.
#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord)]
pub(crate) enum Tier {
    /// SSE2: every x86_64 CPU.
    Sse2 = 1,
    /// SSSE3 (`pshufb`): every x86_64 CPU since 2006 but a few early AMD ones.
    Ssse3,
    /// SSE4.1 and POPCNT.
    Sse41,
    /// AVX2, BMI1, BMI2 and LZCNT: Intel Haswell (2013) and AMD Zen and later.
    Avx2,
    /// AVX-512 F, BW, VL and VBMI2 (`vpcompressb`/`w`): Intel Ice Lake (2019) and AMD Zen 4 and later. Only with a
    /// compiler that has its intrinsics (Rust 1.89, `nino_avx512`, set by build.rs).
    Avx512,
}

/// The tier found, 0 before the first look.
static TIER: AtomicU8 = AtomicU8::new(0);

/// The best tier this CPU has: one load after the first call.
#[inline(always)]
pub(crate) fn tier() -> Tier {
    match TIER.load(Ordering::Relaxed) {
        1 => Tier::Sse2,
        2 => Tier::Ssse3,
        3 => Tier::Sse41,
        4 => Tier::Avx2,
        5 => Tier::Avx512,
        _ => detect(),
    }
}

/// Asks the CPU (std caches its answers; this caches the tier).
#[cold]
#[inline(never)]
fn detect() -> Tier {
    use std::arch::is_x86_feature_detected as has;
    let ssse3 = has!("ssse3");
    let sse41 = ssse3 && has!("sse4.1") && has!("popcnt");
    let avx2 = sse41 && has!("avx2") && has!("bmi1") && has!("bmi2") && has!("lzcnt");
    let tier = if avx2 && avx512() {
        Tier::Avx512
    } else if avx2 {
        Tier::Avx2
    } else if sse41 {
        Tier::Sse41
    } else if ssse3 {
        Tier::Ssse3
    } else {
        Tier::Sse2
    };
    let tier = match std::env::var("NINO_TIER").ok().as_deref() {
        // TEMP-BENCH
        Some("sse2") => Tier::Sse2,
        Some("ssse3") => Tier::Ssse3,
        Some("sse41") => Tier::Sse41,
        Some("avx2") => Tier::Avx2.min(tier),
        _ => tier, // TEMP-BENCH
    }; // TEMP-BENCH
    TIER.store(tier as u8, Ordering::Relaxed);
    tier
}

/// Whether the CPU has the AVX-512 the kernels use (and the compiler its intrinsics).
fn avx512() -> bool {
    #[cfg(nino_avx512)]
    {
        use std::arch::is_x86_feature_detected as has;
        has!("avx512f") && has!("avx512bw") && has!("avx512vl") && has!("avx512vbmi2")
    }
    #[cfg(not(nino_avx512))]
    false
}

// ------------------------------------------------------------------------------------------------ shuffles

/// `DOWN[s]`: the shuffle that moves byte `k + s` to byte `k` (zeros past 15). `UP[s]`: the one that moves byte
/// `k - s` to byte `k` for `s <= k < s + 8` (zeros elsewhere).
// Every index is below the array's length (s <= 16, k < 16).
#[allow(clippy::indexing_slicing, clippy::arithmetic_side_effects)]
const fn shift_shuffles<const N: usize>(down: bool) -> [[u8; 16]; N] {
    let mut table = [[0x80u8; 16]; N];
    let mut s = 0;
    while s < N {
        let mut k = 0;
        while k < 16 {
            if down && k + s < 16 {
                table[s][k] = (k + s) as u8;
            } else if !down && s <= k && k < s + 8 {
                table[s][k] = (k - s) as u8;
            }
            k += 1;
        }
        s += 1;
    }
    table
}

static DOWN: [[u8; 16]; 17] = shift_shuffles::<17>(true);
static UP: [[u8; 16]; 9] = shift_shuffles::<9>(false);

/// Row `k` of a table of shuffles, as a vector.
///
/// # Safety
/// `k < N`.
#[inline(always)]
unsafe fn row<const N: usize>(table: &[[u8; 16]; N], k: usize) -> __m128i {
    // SAFETY: `k < N` (the caller's contract): a row of 16 bytes; SSE2 is part of every x86_64 target.
    unsafe { _mm_loadu_si128(table.get_unchecked(k).as_ptr().cast()) }
}

/// The 16 bytes of the text from `i` (zeros past `len`), read without touching a byte outside `src[..len]`: one load
/// while 16 are left; else the text's last 16 shifted down; in text shorter than 16 bytes, two overlapping words, or
/// for fewer than eight bytes, a word built in a register.
///
/// # Safety
/// The CPU has SSSE3, `i < len`, and `len` bytes are readable at `src`.
#[inline]
#[target_feature(enable = "ssse3")]
unsafe fn window(src: *const u8, len: usize, i: usize) -> __m128i {
    // `i < len`: no difference below wraps, and every shift is by less than 64.
    let rest = len.wrapping_sub(i);
    if rest >= 16 {
        // SAFETY: `i + 16 <= len`.
        return unsafe { _mm_loadu_si128(src.add(i).cast::<__m128i>()) };
    }
    if len >= 16 {
        // SAFETY: the last 16 bytes end at `len`.
        let last = unsafe { _mm_loadu_si128(src.add(len.wrapping_sub(16)).cast::<__m128i>()) };
        // SAFETY: `16 - rest` is 1..15, a row of `DOWN`.
        return _mm_shuffle_epi8(last, unsafe { row(&DOWN, 16usize.wrapping_sub(rest)) });
    }
    // SAFETY: `i < len`.
    let src = unsafe { src.add(i) };
    if rest >= 8 {
        // SAFETY: the first eight bytes from `src` and the last eight before `src + rest` (`8 <= rest`).
        let (first, last) = unsafe {
            (_mm_loadl_epi64(src.cast::<__m128i>()), _mm_loadl_epi64(src.add(rest.wrapping_sub(8)).cast::<__m128i>()))
        };
        // SAFETY: `rest - 8` is 0..7, a row of `UP`.
        return _mm_or_si128(first, _mm_shuffle_epi8(last, unsafe { row(&UP, rest.wrapping_sub(8)) }));
    }
    let word = if rest >= 4 {
        // SAFETY: the first four bytes from `src` and the last four before `src + rest` (`4 <= rest`).
        let (first, last) = unsafe {
            (src.cast::<[u8; 4]>().read_unaligned(), src.add(rest.wrapping_sub(4)).cast::<[u8; 4]>().read_unaligned())
        };
        u64::from(u32::from_le_bytes(first))
            | (u64::from(u32::from_le_bytes(last)) << (rest.wrapping_sub(4).wrapping_mul(8)))
    } else {
        // One to three bytes: the first, the middle and the last (some of them the same byte).
        let (mid, end) = (rest / 2, rest.wrapping_sub(1));
        // SAFETY: `mid <= end < rest`.
        let (a, m, l) = unsafe { (src.read(), src.add(mid).read(), src.add(end).read()) };
        u64::from(a) | (u64::from(m) << mid.wrapping_mul(8)) | (u64::from(l) << end.wrapping_mul(8))
    };
    _mm_cvtsi64_si128(word as i64)
}

/// The bytes a step of `width` bytes at `i` takes (see the module root, "spans"): all that are left at the text's end;
/// all `width` when the byte after them starts a char; else those before the last char that starts in the window
/// (`starts`, a bit a byte, the window's first byte lowest), which the next step takes whole.
///
/// # Safety
/// `rest = len - i`, and when `rest > width`, the byte at `src + i + width` is readable.
#[inline(always)]
unsafe fn span(src: *const u8, i: usize, rest: usize, width: usize, starts: u64) -> usize {
    if rest <= width {
        rest
    // SAFETY: `i + width < len`.
    } else if unsafe { src.add(i.wrapping_add(width)).read() } as i8 >= -64 {
        width
    } else {
        // Valid UTF-8 has a char start in every four bytes, so `starts` is not zero: 0..63.
        63usize.wrapping_sub(starts.leading_zeros() as usize)
    }
}

/// The bit set of the first `q` (1..=64) bytes.
#[inline(always)]
fn first(q: usize) -> u64 {
    // `1 <= q <= 64`: the shift is 0..63.
    u64::MAX >> (64usize.wrapping_sub(q) & 63)
}

// ------------------------------------------------------------------------------------------------ SSE4.1 writer

/// The code units of the chars that start in each 16-bit lane, given the lane's byte (`b0`, zero-extended) and the
/// two after it, for text whose longest char has `LONGEST` bytes (2, 3 or 4; the lanes of longer ones are wrong):
/// one byte (ASCII), two, three, the lead byte of four (the high surrogate) or a continuation byte. A continuation
/// byte's lane holds the low surrogate of a four-byte char that started two bytes before it: the writers keep that
/// lane (and no other continuation byte's).
#[inline]
#[target_feature(enable = "sse4.1")]
fn units_sse41<const LONGEST: u8>(b0: __m128i, b1: __m128i, b2: __m128i) -> __m128i {
    let c1 = _mm_and_si128(b1, _mm_set1_epi16(0x3F));
    let u2 = _mm_or_si128(_mm_slli_epi16::<6>(_mm_and_si128(b0, _mm_set1_epi16(0x1F))), c1);
    let at_least = |t: i16| _mm_cmpgt_epi16(b0, _mm_set1_epi16(t));
    if LONGEST == 2 {
        return _mm_blendv_epi8(b0, u2, at_least(0xBF));
    }
    let u3 = _mm_or_si128(
        _mm_or_si128(_mm_slli_epi16::<12>(b0), _mm_slli_epi16::<6>(c1)),
        _mm_and_si128(b2, _mm_set1_epi16(0x3F)),
    );
    if LONGEST == 3 {
        return _mm_blendv_epi8(_mm_blendv_epi8(b0, u2, at_least(0xBF)), u3, at_least(0xDF));
    }
    // The low surrogate: the last ten bits of the code point, the third byte's four and the fourth's six.
    let low = _mm_or_si128(_mm_and_si128(u2, _mm_set1_epi16(0x3FF)), _mm_set1_epi16(0xDC00u16 as i16));
    // The high surrogate: 0xD800 + (code point >> 10) - 0x40, from the lead byte's three bits, the second byte's six
    // and the third's two high ones.
    let top = _mm_or_si128(
        _mm_or_si128(_mm_slli_epi16::<8>(_mm_and_si128(b0, _mm_set1_epi16(7))), _mm_slli_epi16::<2>(c1)),
        _mm_and_si128(_mm_srli_epi16::<4>(b2), _mm_set1_epi16(3)),
    );
    let high = _mm_add_epi16(top, _mm_set1_epi16(0xD7C0u16 as i16));
    let u = _mm_blendv_epi8(b0, low, at_least(0x7F));
    let u = _mm_blendv_epi8(u, u2, at_least(0xBF));
    let u = _mm_blendv_epi8(u, u3, at_least(0xDF));
    _mm_blendv_epi8(u, high, at_least(0xEF))
}

/// Both halves' code units of the window `v` (see [`units_sse41`]), for its longest char: the low eight lanes' and the
/// high eight's.
#[inline]
#[target_feature(enable = "sse4.1")]
fn halves_sse41(v: __m128i, three: u32, four: u32) -> (__m128i, __m128i) {
    let zero = _mm_setzero_si128();
    let (n1, n2) = (_mm_srli_si128::<1>(v), _mm_srli_si128::<2>(v));
    let (lo, hi) = (|x| _mm_unpacklo_epi8(x, zero), |x| _mm_unpackhi_epi8(x, zero));
    if three == 0 {
        (units_sse41::<2>(lo(v), lo(n1), zero), units_sse41::<2>(hi(v), hi(n1), zero))
    } else if four == 0 {
        (units_sse41::<3>(lo(v), lo(n1), lo(n2)), units_sse41::<3>(hi(v), hi(n1), hi(n2)))
    } else {
        (units_sse41::<4>(lo(v), lo(n1), lo(n2)), units_sse41::<4>(hi(v), hi(n1), hi(n2)))
    }
}

/// [`super::wide`] with SSSE3, SSE4.1 and POPCNT: in one pass, 16 bytes of text a step from [`window`], each char's
/// bytes combined in its first byte's lane and the lanes of the chars that end in the window moved to the front
/// ([`KEEP8`], [`KEEP16`]). As Latin-1 while no byte is above 0xC3 (no char above U+00FF); at the first that is, the
/// text is written again from its start as UTF-16. The header is written last.
///
/// # Safety
/// The CPU has SSSE3, SSE4.1 and POPCNT, and `text.len() <= WIDE_MAX`.
#[target_feature(enable = "ssse3,sse4.1,popcnt")]
pub(crate) unsafe fn wide_sse41(out: &mut Vec<u8>, text: &str) {
    let (src, len) = (text.as_ptr(), text.len());
    // UTF-16 takes at most two bytes per byte of UTF-8, Latin-1 one; a step stores up to 32 bytes from where its
    // output starts. `len <= i32::MAX`: no sum below wraps.
    out.reserve(len.wrapping_mul(2).wrapping_add(4 + 32));
    let at = out.len();
    // Invariants: `i <= len` is a char boundary of the valid UTF-8 at `src`, and `o <= i` bytes (Latin-1) or
    // `o <= 2 * i` (UTF-16) are written after the header, so each store (8 or 16 bytes at `o`, at most 32 from where
    // the step started) ends within the room reserved: `4 + 2 * len + 32` bytes from `at`.
    // SAFETY: as just said.
    let base = unsafe { out.as_mut_ptr().add(at) };
    // SAFETY: as just said.
    let dst = unsafe { base.add(4) };
    let (mut i, mut o) = (0usize, 0usize);
    let below_c0 = _mm_set1_epi8(-64);
    'latin1: {
        let (above_c3, low6, top2) = (_mm_set1_epi8(0xC3u8 as i8), _mm_set1_epi8(0x3F), _mm_set1_epi8(0xC0u8 as i8));
        while i < len {
            // SAFETY: SSSE3, and `i < len`.
            let v = unsafe { window(src, len, i) };
            let over = _mm_subs_epu8(v, above_c3);
            if _mm_testz_si128(over, over) == 0 {
                break 'latin1;
            }
            let starts = !(_mm_movemask_epi8(_mm_cmplt_epi8(v, below_c0)) as u32) & 0xFFFF;
            // SAFETY: `rest = len - i`; the byte after the window is read only when there is one.
            let q = unsafe { span(src, i, len.wrapping_sub(i), 16, u64::from(starts)) };
            // A two-byte char's code point: its lead byte's two low bits, then its continuation byte's six. An ASCII
            // byte is its own; continuation bytes are dropped.
            let two =
                _mm_or_si128(_mm_and_si128(_mm_slli_epi16::<6>(v), top2), _mm_and_si128(_mm_srli_si128::<1>(v), low6));
            let chars = _mm_blendv_epi8(v, two, v);
            let keep = starts & first(q) as u32;
            let (k0, k1) = ((keep & 0xFF) as usize, (keep >> 8) as usize);
            // SAFETY: `k0, k1 < 256`; the stores end within the room (the invariants), the second at `o + k0.count`.
            unsafe {
                _mm_storel_epi64(dst.add(o).cast::<__m128i>(), _mm_shuffle_epi8(chars, row(&KEEP8, k0)));
                o = o.wrapping_add(k0.count_ones() as usize);
                let high = _mm_srli_si128::<8>(chars);
                _mm_storel_epi64(dst.add(o).cast::<__m128i>(), _mm_shuffle_epi8(high, row(&KEEP8, k1)));
            }
            o = o.wrapping_add(k1.count_ones() as usize);
            i = i.wrapping_add(q);
        }
        // SAFETY: the header's four bytes are reserved at `base`, and `o` bytes after it written.
        unsafe { super::finish(out, at, o, true) };
        return;
    }
    // A char above U+00FF: UTF-16 from the start, over what was written.
    (i, o) = (0, 0);
    let zero = _mm_setzero_si128();
    while i < len {
        let rest = len.wrapping_sub(i);
        // SAFETY: SSSE3, and `i < len`.
        let v = unsafe { window(src, len, i) };
        let high = _mm_movemask_epi8(v) as u32;
        if high == 0 {
            // ASCII: each byte widened, 32 bytes stored, as many as the window holds kept.
            // SAFETY: the stores end within the room (the invariants).
            unsafe {
                _mm_storeu_si128(dst.add(o).cast::<__m128i>(), _mm_unpacklo_epi8(v, zero));
                _mm_storeu_si128(dst.add(o).add(16).cast::<__m128i>(), _mm_unpackhi_epi8(v, zero));
            }
            let n = rest.min(16);
            i = i.wrapping_add(n);
            o = o.wrapping_add(n.wrapping_mul(2));
            continue;
        }
        let starts = !(_mm_movemask_epi8(_mm_cmplt_epi8(v, below_c0)) as u32) & 0xFFFF;
        // SAFETY: `rest = len - i`.
        let q = unsafe { span(src, i, rest, 16, u64::from(starts)) };
        // A four-byte char's lane and the lane two after it (its low surrogate), when it ends in the window.
        // Lead bytes of chars of three bytes or more, and of four.
        // (`v > -33` as signed bytes is ASCII too, which `high` takes out).
        let three = _mm_movemask_epi8(_mm_cmpgt_epi8(v, _mm_set1_epi8(-33))) as u32 & high;
        let four = _mm_movemask_epi8(_mm_cmpgt_epi8(v, _mm_set1_epi8(-17))) as u32 & high;
        let keep = (starts | (four << 2)) & first(q) as u32;
        let (low, high) = halves_sse41(v, three, four);
        let (k0, k1) = ((keep & 0xFF) as usize, ((keep >> 8) & 0xFF) as usize);
        // SAFETY: `k0, k1 < 256`; the stores end within the room (the invariants), the second at `o + 2 * k0.count`.
        unsafe {
            _mm_storeu_si128(dst.add(o).cast::<__m128i>(), _mm_shuffle_epi8(low, row(&KEEP16, k0)));
            o = o.wrapping_add((k0.count_ones() as usize).wrapping_mul(2));
            _mm_storeu_si128(dst.add(o).cast::<__m128i>(), _mm_shuffle_epi8(high, row(&KEEP16, k1)));
        }
        o = o.wrapping_add((k1.count_ones() as usize).wrapping_mul(2));
        i = i.wrapping_add(q);
    }
    // SAFETY: the header's four bytes are reserved at `base`, and `o` bytes after it written.
    unsafe { super::finish(out, at, o, false) };
}

// ------------------------------------------------------------------------------------------------ AVX-512 writer

/// The bit set of the first `n` lanes of 64, `n` clamped to 0..=64.
#[cfg(nino_avx512)]
#[inline(always)]
fn lanes64(n: usize) -> u64 {
    if n >= 64 {
        u64::MAX
    } else {
        // `n < 64`.
        (1u64 << n).wrapping_sub(1)
    }
}

/// The 16-bit lanes of `v` that `keep` sets, moved to the front (`vpcompressw`). Under Miri, which does not have
/// the instruction, lane by lane (the same result), so that the memory accesses around it are checked.
#[cfg(nino_avx512)]
#[inline]
#[target_feature(enable = "avx512f,avx512bw,avx512vbmi2")]
fn compress16(keep: u32, v: __m512i) -> __m512i {
    #[cfg(not(miri))]
    return _mm512_maskz_compress_epi16(keep, v);
    #[cfg(miri)]
    {
        let lanes: [u16; 32] = unsafe { core::mem::transmute(v) };
        let mut out = [0u16; 32];
        let mut n = 0usize;
        for (k, &lane) in lanes.iter().enumerate() {
            if keep >> k & 1 != 0 {
                if let Some(slot) = out.get_mut(n) {
                    *slot = lane;
                }
                n = n.wrapping_add(1);
            }
        }
        // SAFETY: 64 bytes either way.
        unsafe { core::mem::transmute::<[u16; 32], __m512i>(out) }
    }
}

/// [`compress16`] for bytes (`vpcompressb`).
#[cfg(nino_avx512)]
#[inline]
#[target_feature(enable = "avx512f,avx512bw,avx512vbmi2")]
fn compress8(keep: u64, v: __m512i) -> __m512i {
    #[cfg(not(miri))]
    return _mm512_maskz_compress_epi8(keep, v);
    #[cfg(miri)]
    {
        // SAFETY: 64 bytes either way.
        let lanes: [u8; 64] = unsafe { core::mem::transmute(v) };
        let mut out = [0u8; 64];
        let mut n = 0usize;
        for (k, &lane) in lanes.iter().enumerate() {
            if keep >> k & 1 != 0 {
                if let Some(slot) = out.get_mut(n) {
                    *slot = lane;
                }
                n = n.wrapping_add(1);
            }
        }
        // SAFETY: 64 bytes either way.
        unsafe { core::mem::transmute::<[u8; 64], __m512i>(out) }
    }
}

/// [`super::wide`] with AVX-512 (F, BW, VL, VBMI2): the text read with masked loads, which touch no byte past its
/// end (no window to build at its end); 64 bytes a step as Latin-1, 32 as UTF-16 (a 16-bit lane a byte), each char's
/// bytes combined in its first byte's lane as in [`wide_sse41`], and the lanes kept moved to the front with one
/// `vpcompressb`/`w` and stored whole. Text of up to 32 bytes (64 as Latin-1) is one step.
///
/// # Safety
/// The CPU has AVX-512 F, BW, VL and VBMI2, AVX2, BMI1, BMI2, LZCNT and POPCNT, and `text.len() <= WIDE_MAX`.
#[cfg(nino_avx512)]
#[target_feature(enable = "avx512f,avx512bw,avx512vl,avx512vbmi2,avx2,bmi1,bmi2,lzcnt,popcnt")]
pub(crate) unsafe fn wide_avx512(out: &mut Vec<u8>, text: &str) {
    let (src, len) = (text.as_ptr(), text.len());
    // A step stores 64 bytes from where its output starts: `4 + 2 * len + 64` bytes of room. `len <= i32::MAX`: no
    // sum below wraps.
    out.reserve(len.wrapping_mul(2).wrapping_add(4 + 64));
    let at = out.len();
    // Invariants: `i <= len` is a char boundary of the valid UTF-8 at `src`, and `o <= i` bytes (Latin-1) or
    // `o <= 2 * i` (UTF-16) are written after the header, so each store (64 bytes at `o`) ends within the room.
    // SAFETY: as just said.
    let dst = unsafe { out.as_mut_ptr().add(at).add(4) };
    let (mut i, mut o) = (0usize, 0usize);
    'latin1: {
        while i < len {
            let rest = len.wrapping_sub(i);
            // SAFETY: the masks enable the bytes before `len` only (`rest` from `i`, `rest - 1` from `i + 1`), and
            // `i + 1 <= len`: the pointers are within the text or one past it.
            let (v, next) = unsafe {
                (
                    _mm512_maskz_loadu_epi8(lanes64(rest), src.add(i).cast()),
                    _mm512_maskz_loadu_epi8(lanes64(rest.wrapping_sub(1)), src.add(i).add(1).cast()),
                )
            };
            if _mm512_cmpge_epu8_mask(v, _mm512_set1_epi8(0xC4u8 as i8)) != 0 {
                break 'latin1;
            }
            let starts = _mm512_cmpge_epi8_mask(v, _mm512_set1_epi8(-64));
            // SAFETY: `rest = len - i`.
            let q = unsafe { span(src, i, rest, 64, starts) };
            let two = _mm512_or_si512(
                _mm512_and_si512(_mm512_slli_epi16::<6>(v), _mm512_set1_epi8(0xC0u8 as i8)),
                _mm512_and_si512(next, _mm512_set1_epi8(0x3F)),
            );
            let chars = _mm512_mask_blend_epi8(_mm512_movepi8_mask(v), v, two);
            let keep = starts & first(q);
            // SAFETY: the store ends within the room (the invariants).
            unsafe { _mm512_storeu_si512(dst.add(o).cast(), compress8(keep, chars)) };
            o = o.wrapping_add(keep.count_ones() as usize);
            i = i.wrapping_add(q);
        }
        // SAFETY: the header's four bytes are reserved at `at`, and `o` bytes after it written.
        unsafe { super::finish(out, at, o, true) };
        return;
    }
    (i, o) = (0, 0);
    let at_least = |v: __m256i, t: u8| _mm256_cmpge_epu8_mask(v, _mm256_set1_epi8(t as i8));
    while i < len {
        let rest = len.wrapping_sub(i);
        // SAFETY: as above, with 32 bytes from `i`, `i + 1` and `i + 2` (`rest`, `rest - 1` and `rest - 2` of them,
        // clamped at 0: a pointer past the text's end is formed only with no byte enabled, and `i + 2 <= len + 1`).
        let (v, n1, n2) = unsafe {
            let load = |k: usize| {
                _mm256_maskz_loadu_epi8(
                    lanes64(rest.saturating_sub(k)) as u32,
                    src.wrapping_add(i.wrapping_add(k)).cast(),
                )
            };
            (load(0), load(1), load(2))
        };
        let starts = _mm256_cmpge_epi8_mask(v, _mm256_set1_epi8(-64));
        // SAFETY: `rest = len - i`.
        let q = unsafe { span(src, i, rest, 32, u64::from(starts)) };
        let keep = (starts | (at_least(v, 0xF0) << 2)) & first(q) as u32;
        let (b0, b1, b2) = (_mm512_cvtepu8_epi16(v), _mm512_cvtepu8_epi16(n1), _mm512_cvtepu8_epi16(n2));
        let c1 = _mm512_and_si512(b1, _mm512_set1_epi16(0x3F));
        let u2 = _mm512_or_si512(_mm512_slli_epi16::<6>(_mm512_and_si512(b0, _mm512_set1_epi16(0x1F))), c1);
        let low = _mm512_or_si512(_mm512_and_si512(u2, _mm512_set1_epi16(0x3FF)), _mm512_set1_epi16(0xDC00u16 as i16));
        let u3 = _mm512_or_si512(
            _mm512_or_si512(_mm512_slli_epi16::<12>(b0), _mm512_slli_epi16::<6>(c1)),
            _mm512_and_si512(b2, _mm512_set1_epi16(0x3F)),
        );
        let top = _mm512_or_si512(
            _mm512_or_si512(
                _mm512_slli_epi16::<8>(_mm512_and_si512(b0, _mm512_set1_epi16(7))),
                _mm512_slli_epi16::<2>(c1),
            ),
            _mm512_and_si512(_mm512_srli_epi16::<4>(b2), _mm512_set1_epi16(3)),
        );
        let high = _mm512_add_epi16(top, _mm512_set1_epi16(0xD7C0u16 as i16));
        let u = _mm512_mask_blend_epi16(_mm256_movepi8_mask(v), b0, low);
        let u = _mm512_mask_blend_epi16(at_least(v, 0xC0), u, u2);
        let u = _mm512_mask_blend_epi16(at_least(v, 0xE0), u, u3);
        let u = _mm512_mask_blend_epi16(at_least(v, 0xF0), u, high);
        // SAFETY: the store ends within the room (the invariants).
        unsafe { _mm512_storeu_si512(dst.add(o).cast(), compress16(keep, u)) };
        o = o.wrapping_add((keep.count_ones() as usize).wrapping_mul(2));
        i = i.wrapping_add(q);
    }
    // SAFETY: the header's four bytes are reserved at `at`, and `o` bytes after it written.
    unsafe { super::finish(out, at, o, false) };
}
