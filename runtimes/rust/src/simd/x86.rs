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

static DOWN: super::Lines<[[u8; 16]; 17]> = super::Lines(shift_shuffles::<17>(true));
static UP: super::Lines<[[u8; 16]; 9]> = super::Lines(shift_shuffles::<9>(false));

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
        return _mm_shuffle_epi8(last, unsafe { row(&DOWN.0, 16usize.wrapping_sub(rest)) });
    }
    // SAFETY: `i < len`.
    let src = unsafe { src.add(i) };
    if rest >= 8 {
        // SAFETY: the first eight bytes from `src` and the last eight before `src + rest` (`8 <= rest`).
        let (first, last) = unsafe {
            (_mm_loadl_epi64(src.cast::<__m128i>()), _mm_loadl_epi64(src.add(rest.wrapping_sub(8)).cast::<__m128i>()))
        };
        // SAFETY: `rest - 8` is 0..7, a row of `UP`.
        return _mm_or_si128(first, _mm_shuffle_epi8(last, unsafe { row(&UP.0, rest.wrapping_sub(8)) }));
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

/// The 32 bytes of the text from `i` (zeros past `len`) as two vectors, read without touching a byte outside
/// `src[..len]` ([`window`] for each half that is not whole).
///
/// # Safety
/// The CPU has SSSE3, `i < len`, and `len` bytes are readable at `src`.
#[inline]
#[target_feature(enable = "ssse3")]
unsafe fn window32(src: *const u8, len: usize, i: usize) -> (__m128i, __m128i) {
    // `i < len`: no difference or sum below wraps.
    let rest = len.wrapping_sub(i);
    // SAFETY: with 32 bytes left, both halves are whole; with more than 16, the first is and the second starts before
    // `len`; else the first starts before `len`.
    unsafe {
        if rest >= 32 {
            return (_mm_loadu_si128(src.add(i).cast()), _mm_loadu_si128(src.add(i).add(16).cast()));
        }
        if rest > 16 {
            return (_mm_loadu_si128(src.add(i).cast()), window(src, len, i.wrapping_add(16)));
        }
        (window(src, len, i), _mm_setzero_si128())
    }
}

/// [`super::wide`] with SSSE3, SSE4.1 and POPCNT: in one pass, 16 bytes of text a step from [`window`], each char's bytes
/// combined in its first byte's lane and the lanes of the chars that end in the window moved to the front
/// ([`KEEP8`], [`KEEP16`]). As Latin-1 while no byte is above 0xC3 (no char above U+00FF); at the first that is, the
/// text is written again from its start as UTF-16. A four-byte char ends a step before it and is written alone, as
/// a surrogate pair. The header is written last.
///
/// # Safety
/// The CPU has SSSE3, SSE4.1 and POPCNT, and `text.len() <= WIDE_MAX`.
#[inline]
#[target_feature(enable = "ssse3,sse4.1,popcnt")]
pub(crate) unsafe fn wide_sse41<const ONE: bool>(out: &mut Vec<u8>, text: &str) {
    let (src, len) = (text.as_ptr(), text.len());
    // UTF-16 takes at most two bytes per byte of UTF-8, Latin-1 one; a step stores up to 32 bytes from where its
    // output starts. `len <= i32::MAX`: no sum below wraps.
    let room = if ONE { 1 } else { super::kernel_room(len) };
    out.reserve(len.wrapping_mul(2).wrapping_add(room).wrapping_add(32));
    let at = out.len();
    // Invariants: `i <= len` is a char boundary of the valid UTF-8 at `src`, and `o <= i` bytes (Latin-1) or
    // `o <= 2 * i` (UTF-16) are written after the header, so each store (8 or 16 bytes at `o`, at most 32 from where
    // the step started) ends within the room reserved: `room + 2 * len + 32` bytes from `at`.
    // SAFETY: as just said.
    let base = unsafe { out.as_mut_ptr().add(at) };
    // SAFETY: as just said.
    let dst = unsafe { base.add(room) };
    let (mut i, mut o) = (0usize, 0usize);
    let below_c0 = _mm_set1_epi8(-64);
    // The chars that end in the window at `i` (a bit set of lanes): all that start there at the text's end; else
    // those before the last char start in it, or all when the byte after it starts a char.
    let span = |i: usize, starts: u32, rest: usize| -> usize {
        if rest <= 16 {
            rest
        // SAFETY: `i + 16 < len`.
        } else if unsafe { src.add(i.wrapping_add(16)).read() } as i8 >= -64 {
            16
        } else {
            // A char starts in the first 13 bytes: the next window takes it.
            31usize.wrapping_sub(starts.leading_zeros() as usize)
        }
    };
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
            let q = span(i, starts, len.wrapping_sub(i));
            // A two-byte char's code point: its lead byte's two low bits, then its continuation byte's six. An ASCII
            // byte is its own; continuation bytes are dropped.
            let two =
                _mm_or_si128(_mm_and_si128(_mm_slli_epi16::<6>(v), top2), _mm_and_si128(_mm_srli_si128::<1>(v), low6));
            let chars = _mm_blendv_epi8(v, two, v);
            let keep = starts & 1u32.wrapping_shl(q as u32).wrapping_sub(1);
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
        // SAFETY: the header's room is reserved at `at`, and `o` bytes after it written.
        unsafe { super::finish::<ONE>(out, at, room, o, true) };
        return;
    }
    // A char above U+00FF: UTF-16 from the start, over what was written.
    (i, o) = (0, 0);
    let zero = _mm_setzero_si128();
    let (low6, low5, low4) = (_mm_set1_epi16(0x3F), _mm_set1_epi16(0x1F), _mm_set1_epi16(0x0F));
    let (above_bf, above_df) = (_mm_set1_epi16(0xBF), _mm_set1_epi16(0xDF));
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
        let q = span(i, starts, rest);
        // Lead bytes of four-byte chars among the chars taken: the step ends before the first.
        let four = _mm_movemask_epi8(_mm_cmpgt_epi8(v, _mm_set1_epi8(-17))) as u32
            & high
            & 1u32.wrapping_shl(q as u32).wrapping_sub(1);
        let q = if four == 0 {
            q
        } else if four & 1 == 0 {
            four.trailing_zeros() as usize
        } else {
            // A four-byte char at `i`: a surrogate pair. Valid UTF-8 has its four bytes, and it takes four bytes
            // of output.
            // SAFETY: as just said.
            let b = |k: usize| u32::from(unsafe { src.add(i.wrapping_add(k)).read() });
            let c = ((b(0) & 0x07) << 18) | ((b(1) & 0x3F) << 12) | ((b(2) & 0x3F) << 6) | (b(3) & 0x3F);
            let v = c.wrapping_sub(0x1_0000);
            let pair = (0xD800 | (v >> 10)) | ((0xDC00 | (v & 0x3FF)) << 16);
            // SAFETY: as just said.
            unsafe { dst.add(o).cast::<[u8; 4]>().write_unaligned(pair.to_le_bytes()) };
            i = i.wrapping_add(4);
            o = o.wrapping_add(4);
            continue;
        };
        let (n1, n2) = (_mm_srli_si128::<1>(v), _mm_srli_si128::<2>(v));
        // Each byte in a 16-bit lane with the two after it: the code unit of a char of one, two or three bytes
        // starting there, chosen by its first byte.
        let units = |b0: __m128i, b1: __m128i, b2: __m128i| {
            let c1 = _mm_and_si128(b1, low6);
            let u2 = _mm_or_si128(_mm_slli_epi16::<6>(_mm_and_si128(b0, low5)), c1);
            let u3 = _mm_or_si128(
                _mm_or_si128(_mm_slli_epi16::<12>(_mm_and_si128(b0, low4)), _mm_slli_epi16::<6>(c1)),
                _mm_and_si128(b2, low6),
            );
            let u = _mm_blendv_epi8(b0, u2, _mm_cmpgt_epi16(b0, above_bf));
            _mm_blendv_epi8(u, u3, _mm_cmpgt_epi16(b0, above_df))
        };
        let low = units(_mm_unpacklo_epi8(v, zero), _mm_unpacklo_epi8(n1, zero), _mm_unpacklo_epi8(n2, zero));
        let high = units(_mm_unpackhi_epi8(v, zero), _mm_unpackhi_epi8(n1, zero), _mm_unpackhi_epi8(n2, zero));
        let keep = starts & 1u32.wrapping_shl(q as u32).wrapping_sub(1);
        let (k0, k1) = ((keep & 0xFF) as usize, (keep >> 8) as usize);
        // SAFETY: `k0, k1 < 256`; the stores end within the room (the invariants), the second at `o + 2 * k0.count`.
        unsafe {
            _mm_storeu_si128(dst.add(o).cast::<__m128i>(), _mm_shuffle_epi8(low, row(&KEEP16, k0)));
            o = o.wrapping_add((k0.count_ones() as usize).wrapping_mul(2));
            _mm_storeu_si128(dst.add(o).cast::<__m128i>(), _mm_shuffle_epi8(high, row(&KEEP16, k1)));
        }
        o = o.wrapping_add((k1.count_ones() as usize).wrapping_mul(2));
        i = i.wrapping_add(q);
    }
    // SAFETY: the header's room is reserved at `at`, and `o` bytes after it written (`o / 2` code units, at most
    // `len`).
    unsafe { super::finish::<ONE>(out, at, room, o, false) };
}

// ------------------------------------------------------------------------------------------------ AVX2 writer

/// The code units of the chars that start in each of 16 16-bit lanes, given the lane's byte (`b0`, zero-extended) and the
/// two after it, for text whose longest char has `LONGEST` bytes (2, 3 or 4; the lanes of longer ones are wrong):
/// one byte (ASCII), two, three, the lead byte of four (the high surrogate) or a continuation byte. A continuation
/// byte's lane holds the low surrogate of a four-byte char that started two bytes before it: the writers keep that
/// lane (and no other continuation byte's).
#[inline]
#[target_feature(enable = "avx2")]
fn units_avx2<const LONGEST: u8>(b0: __m256i, b1: __m256i, b2: __m256i) -> __m256i {
    let set = _mm256_set1_epi16;
    let c1 = _mm256_and_si256(b1, set(0x3F));
    let u2 = _mm256_or_si256(_mm256_slli_epi16::<6>(_mm256_and_si256(b0, set(0x1F))), c1);
    let at_least = |t: i16| _mm256_cmpgt_epi16(b0, set(t));
    if LONGEST == 2 {
        return _mm256_blendv_epi8(b0, u2, at_least(0xBF));
    }
    let u3 = _mm256_or_si256(
        _mm256_or_si256(_mm256_slli_epi16::<12>(b0), _mm256_slli_epi16::<6>(c1)),
        _mm256_and_si256(b2, set(0x3F)),
    );
    if LONGEST == 3 {
        return _mm256_blendv_epi8(_mm256_blendv_epi8(b0, u2, at_least(0xBF)), u3, at_least(0xDF));
    }
    let low = _mm256_or_si256(_mm256_and_si256(u2, set(0x3FF)), set(0xDC00u16 as i16));
    let top = _mm256_or_si256(
        _mm256_or_si256(_mm256_slli_epi16::<8>(_mm256_and_si256(b0, set(7))), _mm256_slli_epi16::<2>(c1)),
        _mm256_and_si256(_mm256_srli_epi16::<4>(b2), set(3)),
    );
    let high = _mm256_add_epi16(top, set(0xD7C0u16 as i16));
    let u = _mm256_blendv_epi8(b0, low, at_least(0x7F));
    let u = _mm256_blendv_epi8(u, u2, at_least(0xBF));
    let u = _mm256_blendv_epi8(u, u3, at_least(0xDF));
    _mm256_blendv_epi8(u, high, at_least(0xEF))
}

/// The code units of the 16 bytes `v` (the bytes after them in `next`), for the window's longest char.
#[inline]
#[target_feature(enable = "avx2")]
fn lanes_avx2(v: __m128i, next: __m128i, three: u32, four: u32) -> __m256i {
    let (n1, n2) = (_mm_alignr_epi8::<1>(next, v), _mm_alignr_epi8::<2>(next, v));
    let (b0, b1, b2) = (_mm256_cvtepu8_epi16(v), _mm256_cvtepu8_epi16(n1), _mm256_cvtepu8_epi16(n2));
    if three == 0 {
        units_avx2::<2>(b0, b1, b2)
    } else if four == 0 {
        units_avx2::<3>(b0, b1, b2)
    } else {
        units_avx2::<4>(b0, b1, b2)
    }
}

/// [`wide_sse41`] with AVX2: the same steps, the code units of a window's 32 bytes computed in two vectors of 16
/// lanes instead of four of eight.
///
/// # Safety
/// The CPU has AVX2, SSSE3, SSE4.1 and POPCNT, and `text.len() <= WIDE_MAX`.
#[inline]
#[target_feature(enable = "avx2,ssse3,sse4.1,popcnt")]
pub(crate) unsafe fn wide_avx2<const ONE: bool>(out: &mut Vec<u8>, text: &str) {
    let (src, len) = (text.as_ptr(), text.len());
    // As in `wide_sse41`: `4 + 2 * len + 64` bytes of room.
    let room = if ONE { 1 } else { super::kernel_room(len) };
    out.reserve(len.wrapping_mul(2).wrapping_add(room).wrapping_add(64));
    let at = out.len();
    // Invariants: as in `wide_sse41`.
    // SAFETY: as just said.
    let dst = unsafe { out.as_mut_ptr().add(at).add(room) };
    let (mut i, mut o) = (0usize, 0usize);
    let zero = _mm_setzero_si128();
    let starts = |v: __m256i| u64::from(!(_mm256_movemask_epi8(_mm256_cmpgt_epi8(_mm256_set1_epi8(-64), v)) as u32));
    'latin1: {
        let (low6, top2) = (_mm256_set1_epi8(0x3F), _mm256_set1_epi8(0xC0u8 as i8));
        while i < len {
            let rest = len.wrapping_sub(i);
            // SAFETY: SSSE3, and `i < len`.
            let (lo, hi) = unsafe { window32(src, len, i) };
            let v = _mm256_set_m128i(hi, lo);
            let over = _mm256_subs_epu8(v, _mm256_set1_epi8(0xC3u8 as i8));
            if _mm256_testz_si256(over, over) == 0 {
                break 'latin1;
            }
            let s = starts(v);
            // SAFETY: `rest = len - i`; the byte after the window is read only when there is one.
            let q = unsafe { span(src, i, rest, 32, s) };
            let keep = s & first(q);
            let next = _mm256_set_m128i(zero, hi);
            let two = _mm256_or_si256(
                _mm256_and_si256(_mm256_slli_epi16::<6>(v), top2),
                _mm256_and_si256(_mm256_alignr_epi8::<1>(next, v), low6),
            );
            let chars = _mm256_blendv_epi8(v, two, v);
            for (half, keep) in
                [(_mm256_castsi256_si128(chars), keep & 0xFFFF), (_mm256_extracti128_si256::<1>(chars), keep >> 16)]
            {
                let (k0, k1) = ((keep & 0xFF) as usize, ((keep >> 8) & 0xFF) as usize);
                // SAFETY: `k0, k1 < 256`; the stores end within the room (the invariants).
                unsafe {
                    _mm_storel_epi64(dst.add(o).cast::<__m128i>(), _mm_shuffle_epi8(half, row(&KEEP8, k0)));
                    o = o.wrapping_add(k0.count_ones() as usize);
                    let high = _mm_srli_si128::<8>(half);
                    _mm_storel_epi64(dst.add(o).cast::<__m128i>(), _mm_shuffle_epi8(high, row(&KEEP8, k1)));
                }
                o = o.wrapping_add(k1.count_ones() as usize);
            }
            i = i.wrapping_add(q);
        }
        // SAFETY: the header's room is reserved at `at`, and `o` bytes after it written.
        unsafe { super::finish::<ONE>(out, at, room, o, true) };
        return;
    }
    (i, o) = (0, 0);
    while i < len {
        let rest = len.wrapping_sub(i);
        // SAFETY: SSSE3, and `i < len`.
        let (lo, hi) = unsafe { window32(src, len, i) };
        let v = _mm256_set_m128i(hi, lo);
        let high = _mm256_movemask_epi8(v) as u32;
        if high == 0 {
            // ASCII: each byte widened, 64 bytes stored, as many as the window holds kept.
            // SAFETY: the stores end within the room (the invariants).
            unsafe {
                _mm256_storeu_si256(dst.add(o).cast(), _mm256_cvtepu8_epi16(lo));
                _mm256_storeu_si256(dst.add(o).add(32).cast(), _mm256_cvtepu8_epi16(hi));
            }
            let n = rest.min(32);
            i = i.wrapping_add(n);
            o = o.wrapping_add(n.wrapping_mul(2));
            continue;
        }
        let s = starts(v);
        // SAFETY: `rest = len - i`.
        let q = unsafe { span(src, i, rest, 32, s) };
        let at_least = |t: i8| _mm256_movemask_epi8(_mm256_cmpgt_epi8(v, _mm256_set1_epi8(t))) as u32 & high;
        let (three, four) = (at_least(-33), at_least(-17));
        let keep = (s | (u64::from(four) << 2)) & first(q);
        for (v, next, keep) in [(lo, hi, keep & 0xFFFF), (hi, zero, (keep >> 16) & 0xFFFF)] {
            let units = lanes_avx2(v, next, three, four);
            let (k0, k1) = ((keep & 0xFF) as usize, ((keep >> 8) & 0xFF) as usize);
            // SAFETY: `k0, k1 < 256`.
            let shuffle = unsafe { _mm256_set_m128i(row(&KEEP16, k1), row(&KEEP16, k0)) };
            let packed = _mm256_shuffle_epi8(units, shuffle);
            // SAFETY: the stores end within the room (the invariants).
            unsafe {
                _mm_storeu_si128(dst.add(o).cast::<__m128i>(), _mm256_castsi256_si128(packed));
                o = o.wrapping_add((k0.count_ones() as usize).wrapping_mul(2));
                _mm_storeu_si128(dst.add(o).cast::<__m128i>(), _mm256_extracti128_si256::<1>(packed));
            }
            o = o.wrapping_add((k1.count_ones() as usize).wrapping_mul(2));
        }
        i = i.wrapping_add(q);
    }
    // SAFETY: the header's room is reserved at `at`, and `o` bytes after it written.
    unsafe { super::finish::<ONE>(out, at, room, o, false) };
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
// AVX-512 is stable since Rust 1.89, after the MSRV: build.rs sets `nino_avx512` only for a compiler that has it.
#[allow(clippy::incompatible_msrv)]
#[inline]
#[target_feature(enable = "avx512f,avx512bw,avx512vbmi2")]
fn compress16(keep: u32, v: __m512i) -> __m512i {
    #[cfg(not(miri))]
    return _mm512_maskz_compress_epi16(keep, v);
    // SAFETY: 64 bytes either way.
    #[cfg(miri)]
    unsafe {
        core::mem::transmute::<[u16; 32], __m512i>(compress_lanes(
            u64::from(keep),
            core::mem::transmute::<__m512i, [u16; 32]>(v),
        ))
    }
}

/// Moves the lanes of `lanes` that `keep` sets to the front, zeros after them: what `vpcompressb`/`w` do, for Miri.
#[cfg(all(nino_avx512, miri))]
fn compress_lanes<T: Copy + Default, const N: usize>(keep: u64, lanes: [T; N]) -> [T; N] {
    let mut out = [T::default(); N];
    let mut n = 0usize;
    for (k, &lane) in lanes.iter().enumerate() {
        if keep >> k & 1 != 0 {
            if let Some(slot) = out.get_mut(n) {
                *slot = lane;
            }
            n = n.wrapping_add(1);
        }
    }
    out
}

/// [`compress8`] for 32 bytes.
#[cfg(nino_avx512)]
// AVX-512 is stable since Rust 1.89, after the MSRV: build.rs sets `nino_avx512` only for a compiler that has it.
#[allow(clippy::incompatible_msrv)]
#[inline]
#[target_feature(enable = "avx512f,avx512bw,avx512vl,avx512vbmi2")]
fn compress8x32(keep: u32, v: __m256i) -> __m256i {
    #[cfg(not(miri))]
    return _mm256_maskz_compress_epi8(keep, v);
    // SAFETY: 32 bytes either way.
    #[cfg(miri)]
    unsafe {
        core::mem::transmute::<[u8; 32], __m256i>(compress_lanes(
            u64::from(keep),
            core::mem::transmute::<__m256i, [u8; 32]>(v),
        ))
    }
}

/// [`compress16`] for bytes (`vpcompressb`).
#[cfg(nino_avx512)]
// AVX-512 is stable since Rust 1.89, after the MSRV: build.rs sets `nino_avx512` only for a compiler that has it.
#[allow(clippy::incompatible_msrv)]
#[inline]
#[target_feature(enable = "avx512f,avx512bw,avx512vbmi2")]
fn compress8(keep: u64, v: __m512i) -> __m512i {
    #[cfg(not(miri))]
    return _mm512_maskz_compress_epi8(keep, v);
    // SAFETY: 64 bytes either way.
    #[cfg(miri)]
    unsafe {
        core::mem::transmute::<[u8; 64], __m512i>(compress_lanes(keep, core::mem::transmute::<__m512i, [u8; 64]>(v)))
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
// AVX-512 is stable since Rust 1.89, after the MSRV: build.rs sets `nino_avx512` only for a compiler that has it.
#[allow(clippy::incompatible_msrv)]
#[inline]
#[target_feature(enable = "avx512f,avx512bw,avx512vl,avx512vbmi2,avx2,bmi1,bmi2,lzcnt,popcnt")]
pub(crate) unsafe fn wide_avx512<const ONE: bool>(out: &mut Vec<u8>, text: &str) {
    let (src, len) = (text.as_ptr(), text.len());
    // A step stores 64 bytes from where its output starts: `4 + 2 * len + 64` bytes of room. `len <= i32::MAX`: no
    // sum below wraps.
    let room = if ONE { 1 } else { super::kernel_room(len) };
    out.reserve(len.wrapping_mul(2).wrapping_add(room).wrapping_add(64));
    let at = out.len();
    // Invariants: `i <= len` is a char boundary of the valid UTF-8 at `src`, and `o <= i` bytes (Latin-1) or
    // `o <= 2 * i` (UTF-16) are written after the header, so each store (64 bytes at `o`) ends within the room.
    // SAFETY: as just said.
    let dst = unsafe { out.as_mut_ptr().add(at).add(room) };
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
        // SAFETY: the header's room is reserved at `at`, and `o` bytes after it written.
        unsafe { super::finish::<ONE>(out, at, room, o, true) };
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
    // SAFETY: the header's room is reserved at `at`, and `o` bytes after it written.
    unsafe { super::finish::<ONE>(out, at, room, o, false) };
}

// ------------------------------------------------------------------------------------------------ lists of strings

/// A list kernel: the strings from the start of `values` while each is under 64 bytes (a one-byte header), short
/// ASCII through [`super::ascii_short`] and other text through the tier's one-pass writer with `ONE`, inlined into
/// the loop (a call a string, with its vector constants set up each time, is what [`super::wide`] costs); returns
/// how many it wrote. Text of 64 bytes or more is left to [`crate::strings::write`].
macro_rules! strings_kernel {
    ($(#[$attr:meta])* $name:ident, $features:literal, $wide:ident) => {
        $(#[$attr])*
        /// # Safety
        #[doc = concat!("The CPU has what `", stringify!($wide), "` needs (", $features, ").")]
        #[target_feature(enable = $features)]
        pub(crate) unsafe fn $name(out: &mut Vec<u8>, values: &[String]) -> usize {
            for (k, v) in values.iter().enumerate() {
                let len = v.len();
                if len <= super::SHORT && super::ascii_short(out, v.as_bytes()) {
                    continue;
                }
                if len >= 64 {
                    return k;
                }
                // SAFETY: the CPU has what the kernel needs (the caller's contract), and the text is under 64 bytes.
                unsafe { $wide::<true>(out, v) };
            }
            values.len()
        }
    };
}

strings_kernel!(
    /// The list kernel with SSE4.1 ([`wide_sse41`]).
    strings_sse41,
    "ssse3,sse4.1,popcnt",
    wide_sse41
);
strings_kernel!(
    /// The list kernel with AVX2 ([`wide_avx2`]).
    strings_avx2,
    "avx2,ssse3,sse4.1,popcnt",
    wide_avx2
);
// AVX-512 is stable since Rust 1.89, after the MSRV: build.rs sets `nino_avx512` only for a compiler that has it.
strings_kernel!(
    /// The list kernel with AVX-512 ([`wide_avx512`]).
    #[cfg(nino_avx512)]
    #[allow(clippy::incompatible_msrv)]
    strings_avx512,
    "avx512f,avx512bw,avx512vl,avx512vbmi2,avx2,bmi1,bmi2,lzcnt,popcnt",
    wide_avx512
);

// ------------------------------------------------------------------------------------------------ the reader's text

/// [`super::latin1_utf8`] with SSSE3: 16 bytes a step, stored as they are when ASCII; else each byte as a 16-bit lane
/// of its UTF-8 (a lead byte, and above 0x7F a continuation byte) and each half's lanes packed with one `pshufb`
/// ([`super::EXPAND`]). The last bytes, and those past the room a step needs, a word at a time.
///
/// # Safety
/// The CPU has SSSE3.
/// `room` bytes are writable at `dst`, at least the UTF-8's length.
#[target_feature(enable = "ssse3")]
pub(crate) unsafe fn latin1_ssse3(bytes: &[u8], dst: *mut u8, room: usize) -> usize {
    let (src, len) = (bytes.as_ptr(), bytes.len());
    let (mut k, mut o) = (0usize, 0usize);
    let (zero, lead_bits, cont_bits) = (_mm_setzero_si128(), _mm_set1_epi8(0xC0u8 as i8), _mm_set1_epi8(0x80u8 as i8));
    // Invariant: `o <= room` is the UTF-8 of `bytes[..k]`, so `room - o` is at least that of the rest (no wrap). A step
    // reads 16 bytes and stores at most 32 from `o`.
    while k.wrapping_add(16) <= len && room.wrapping_sub(o) >= 32 {
        // SAFETY: `k + 16 <= len`.
        let v = unsafe { _mm_loadu_si128(src.add(k).cast()) };
        let high = _mm_movemask_epi8(v) as u32;
        if high == 0 {
            // SAFETY: 32 bytes of room from `o`.
            unsafe { _mm_storeu_si128(dst.add(o).cast(), v) };
            k = k.wrapping_add(16);
            o = o.wrapping_add(16);
            continue;
        }
        let is_high = _mm_cmplt_epi8(v, zero);
        // A high byte's lead is 0xC0 and its two top bits (each byte's, from a 16-bit shift masked to two bits).
        let top = _mm_and_si128(_mm_srli_epi16::<6>(v), _mm_set1_epi8(3));
        let lead = _mm_or_si128(_mm_and_si128(is_high, _mm_or_si128(lead_bits, top)), _mm_andnot_si128(is_high, v));
        let cont = _mm_or_si128(_mm_and_si128(v, _mm_set1_epi8(0x3F)), cont_bits);
        let (m0, m1) = ((high & 0xFF) as usize, (high >> 8) as usize);
        // SAFETY: `m0, m1 < 256`; 32 bytes of room from `o`, and the second store starts at most 16 after it.
        unsafe {
            _mm_storeu_si128(
                dst.add(o).cast(),
                _mm_shuffle_epi8(_mm_unpacklo_epi8(lead, cont), row(&super::EXPAND.0, m0)),
            );
            o = o.wrapping_add(8).wrapping_add(m0.count_ones() as usize);
            _mm_storeu_si128(
                dst.add(o).cast(),
                _mm_shuffle_epi8(_mm_unpackhi_epi8(lead, cont), row(&super::EXPAND.0, m1)),
            );
            o = o.wrapping_add(8).wrapping_add(m1.count_ones() as usize);
        }
        k = k.wrapping_add(16);
    }
    // SAFETY: the invariant: the room from `o` holds the rest's UTF-8.
    unsafe { super::latin1_words(bytes, k, dst, o) }
}

/// [`super::utf16_utf8`] with SSE4.1: eight units a step, by their longest UTF-8: ASCII narrowed; below U+0800 each
/// unit's two bytes in its lane and the lanes packed with [`super::EXPAND`]; else four units at a time in 32-bit lanes
/// of up to three bytes, packed with [`super::PACK3`]. Units with a surrogate, the last units, and those past the room
/// a step needs, as the plain decoder does.
///
/// # Safety
/// The CPU has SSSE3 and SSE4.1. `room` bytes are writable at `dst`, at least what the units count.
#[target_feature(enable = "ssse3,sse4.1")]
pub(crate) unsafe fn utf16_sse41(units: &[[u8; 2]], dst: *mut u8, room: usize) -> usize {
    let (src, n) = (units.as_ptr().cast::<u8>(), units.len());
    let (mut k, mut o) = (0usize, 0usize);
    let set = _mm_set1_epi16;
    // Invariant: `o <= room` holds the UTF-8 of `units[..k]`, and `room - o` at least what the rest count (no wrap). A
    // step reads eight units and stores at most 28 bytes from `o`.
    while k.wrapping_add(8) <= n && room.wrapping_sub(o) >= 32 {
        // SAFETY: `k + 8 <= n` units of two bytes.
        let w = unsafe { _mm_loadu_si128(src.add(k.wrapping_mul(2)).cast()) };
        if _mm_testz_si128(w, set(0xFF80u16 as i16)) != 0 {
            // SAFETY: 32 bytes of room.
            unsafe { _mm_storel_epi64(dst.add(o).cast(), _mm_packus_epi16(w, w)) };
            k = k.wrapping_add(8);
            o = o.wrapping_add(8);
            continue;
        }
        let ascii = _mm_cmpeq_epi16(_mm_and_si128(w, set(0xFF80u16 as i16)), _mm_setzero_si128());
        let two = _mm_or_si128(
            _mm_or_si128(set(0xC0), _mm_srli_epi16::<6>(w)),
            _mm_slli_epi16::<8>(_mm_or_si128(_mm_and_si128(w, set(0x3F)), set(0x80))),
        );
        if _mm_testz_si128(w, set(0xF800u16 as i16)) != 0 {
            // Below U+0800: one or two bytes a unit.
            let lanes = _mm_blendv_epi8(two, w, ascii);
            let m = (!_mm_movemask_epi8(_mm_packs_epi16(ascii, ascii)) & 0xFF) as usize;
            // SAFETY: `m < 256`; 32 bytes of room.
            unsafe { _mm_storeu_si128(dst.add(o).cast(), _mm_shuffle_epi8(lanes, row(&super::EXPAND.0, m))) };
            k = k.wrapping_add(8);
            o = o.wrapping_add(8).wrapping_add(m.count_ones() as usize);
            continue;
        }
        let surrogate = _mm_cmpeq_epi16(_mm_and_si128(w, set(0xF800u16 as i16)), set(0xD800u16 as i16));
        if _mm_testz_si128(surrogate, surrogate) == 0 {
            // SAFETY: the invariant; the plain decoder takes these eight units (and the low half of a pair after them).
            (k, o) = unsafe { super::utf16_words(units, k, k.wrapping_add(8), dst, o) };
            continue;
        }
        let at_least_800 = _mm_movemask_epi8(_mm_xor_si128(
            _mm_cmpeq_epi16(_mm_and_si128(w, set(0xF800u16 as i16)), _mm_setzero_si128()),
            set(-1),
        )) as u32;
        let index = (!_mm_movemask_epi8(ascii) as u32 & 0x5555) | (at_least_800 & 0xAAAA);
        for (half, quad) in [(0u32, w), (8, _mm_srli_si128::<8>(w))] {
            let u = _mm_cvtepu16_epi32(quad);
            let s32 = _mm_set1_epi32;
            let one = u;
            let two = _mm_or_si128(
                _mm_or_si128(s32(0xC0), _mm_srli_epi32::<6>(u)),
                _mm_slli_epi32::<8>(_mm_or_si128(_mm_and_si128(u, s32(0x3F)), s32(0x80))),
            );
            let three = _mm_or_si128(
                _mm_or_si128(
                    _mm_or_si128(s32(0xE0), _mm_srli_epi32::<12>(u)),
                    _mm_slli_epi32::<8>(_mm_or_si128(_mm_and_si128(_mm_srli_epi32::<6>(u), s32(0x3F)), s32(0x80))),
                ),
                _mm_slli_epi32::<16>(_mm_or_si128(_mm_and_si128(u, s32(0x3F)), s32(0x80))),
            );
            let lanes = _mm_blendv_epi8(one, two, _mm_cmpgt_epi32(u, s32(0x7F)));
            let lanes = _mm_blendv_epi8(lanes, three, _mm_cmpgt_epi32(u, s32(0x7FF)));
            let super::Counted(shuffle, bytes) =
                super::PACK3.0.get(((index >> half) & 0xFF) as usize).copied().unwrap_or(super::Counted([0x80; 16], 0));
            // SAFETY: 32 bytes of room from where the step started; the first half stores 16 bytes from there, the
            // second from at most 12 after it.
            unsafe {
                _mm_storeu_si128(dst.add(o).cast(), _mm_shuffle_epi8(lanes, _mm_loadu_si128(shuffle.as_ptr().cast())))
            };
            o = o.wrapping_add(usize::from(bytes));
        }
        k = k.wrapping_add(8);
    }
    // SAFETY: the invariant.
    unsafe { super::utf16_words(units, k, n, dst, o).1 }
}

/// Spreads the low 16 or 32 bits of `bits` to the bits of `mask` (`pdep`). Under Miri, bit by bit.
#[cfg(nino_avx512)]
#[inline]
#[target_feature(enable = "bmi2")]
fn deposit(bits: u64, mask: u64) -> u64 {
    #[cfg(not(miri))]
    return _pdep_u64(bits, mask);
    #[cfg(miri)]
    {
        let (mut out, mut k) = (0u64, 0u32);
        for b in 0..64 {
            if mask >> b & 1 != 0 {
                out |= (bits >> k & 1) << b;
                k += 1;
            }
        }
        out
    }
}

/// [`super::latin1_utf8`] with AVX-512 (F, BW, VL, VBMI2) and BMI2: 32 bytes a step from a masked load (no byte past
/// the text's end is touched); ASCII stored as it is, else each byte as a 16-bit lane of its UTF-8 and the bytes kept
/// moved to the front with one `vpcompressb`; every store masked to the bytes written, so no room past them is needed.
///
/// # Safety
/// The CPU has AVX-512 F, BW, VL and VBMI2, BMI2 and POPCNT. The UTF-8's length is writable at `dst`.
#[cfg(nino_avx512)]
// AVX-512 is stable since Rust 1.89, after the MSRV: build.rs sets `nino_avx512` only for a compiler that has it.
#[allow(clippy::incompatible_msrv)]
#[target_feature(enable = "avx512f,avx512bw,avx512vl,avx512vbmi2,avx2,bmi2,popcnt")]
pub(crate) unsafe fn latin1_avx512(bytes: &[u8], dst: *mut u8) -> usize {
    let (src, len) = (bytes.as_ptr(), bytes.len());
    let (mut k, mut o) = (0usize, 0usize);
    // Invariant: `o` bytes of UTF-8 written for `bytes[..k]`; each store writes the UTF-8 of the bytes it took.
    while k < len {
        let take = len.wrapping_sub(k).min(32);
        let valid = lanes64(take) as u32;
        // SAFETY: the mask enables the `take` bytes from `k`, all before `len`.
        let v = unsafe { _mm256_maskz_loadu_epi8(valid, src.add(k).cast()) };
        let high = _mm256_movepi8_mask(v);
        if high == 0 {
            // SAFETY: as many bytes as were taken, all ASCII.
            unsafe { _mm256_mask_storeu_epi8(dst.add(o).cast(), valid, v) };
            k = k.wrapping_add(take);
            o = o.wrapping_add(take);
            continue;
        }
        let is_high = _mm256_movm_epi8(high);
        let top = _mm256_and_si256(_mm256_srli_epi16::<6>(v), _mm256_set1_epi8(3));
        let lead = _mm256_blendv_epi8(v, _mm256_or_si256(_mm256_set1_epi8(0xC0u8 as i8), top), is_high);
        let cont = _mm256_or_si256(_mm256_and_si256(v, _mm256_set1_epi8(0x3F)), _mm256_set1_epi8(0x80u8 as i8));
        let pairs = _mm512_or_si512(_mm512_cvtepu8_epi16(lead), _mm512_slli_epi16::<8>(_mm512_cvtepu8_epi16(cont)));
        let keep = deposit(u64::from(valid), 0x5555_5555_5555_5555) | deposit(u64::from(high), 0xAAAA_AAAA_AAAA_AAAA);
        let n = keep.count_ones() as usize;
        // SAFETY: the `n` bytes stored are the UTF-8 of the bytes taken.
        unsafe { _mm512_mask_storeu_epi8(dst.add(o).cast(), lanes64(n), compress8(keep, pairs)) };
        k = k.wrapping_add(take);
        o = o.wrapping_add(n);
    }
    o
}

/// [`super::utf16_utf8`] with AVX-512 (F, BW, VL, VBMI2) and BMI2: 16 units a step from masked loads, each unit's
/// UTF-8 in a 32-bit lane (one to three bytes; a high surrogate followed by a low one, the pair's four, and that low
/// one none; an unpaired surrogate U+FFFD's three), the bytes kept moved to the front with one `vpcompressb` and
/// stored masked to them. A high surrogate in a step's last lane with a unit after it is left to the next step.
///
/// # Safety
/// The CPU has AVX-512 F, BW, VL and VBMI2, BMI2 and POPCNT. What the units count is writable at `dst`.
#[cfg(nino_avx512)]
// AVX-512 is stable since Rust 1.89, after the MSRV: build.rs sets `nino_avx512` only for a compiler that has it.
#[allow(clippy::incompatible_msrv)]
#[target_feature(enable = "avx512f,avx512bw,avx512vl,avx512vbmi2,avx2,bmi2,popcnt")]
pub(crate) unsafe fn utf16_avx512(units: &[[u8; 2]], dst: *mut u8) -> usize {
    let (src, n) = (units.as_ptr().cast::<u8>(), units.len());
    let (mut k, mut o) = (0usize, 0usize);
    let s32 = _mm512_set1_epi32;
    let tagged = |u: __m512i, tag: i32| _mm512_cmpeq_epi32_mask(_mm512_and_si512(u, s32(0xFC00)), s32(tag));
    // Invariant: `o` bytes written for `units[..k]`, no more than they count; a step writes no more than its units
    // count (a pair counts six bytes and takes four).
    while k < n {
        let rest = n.wrapping_sub(k);
        // SAFETY: the masks enable the units before `n` only (`rest` from `k`, `rest - 1` from `k + 1`); `k + 1 <= n`.
        let (w, next) = unsafe {
            let at = src.add(k.wrapping_mul(2));
            (
                _mm256_maskz_loadu_epi16(lanes64(rest) as u16, at.cast()),
                _mm256_maskz_loadu_epi16(lanes64(rest.wrapping_sub(1)) as u16, at.add(2).cast()),
            )
        };
        let all = lanes64(rest) as u16;
        let ge80 = _mm256_mask_cmpgt_epu16_mask(all, w, _mm256_set1_epi16(0x7F));
        if ge80 == 0 {
            // ASCII: narrowed, stored masked to the units taken.
            let take = rest.min(16);
            // SAFETY: `take` bytes, one a unit, no more than the units count.
            unsafe { _mm_mask_storeu_epi8(dst.add(o).cast(), all, _mm256_cvtepi16_epi8(w)) };
            k = k.wrapping_add(take);
            o = o.wrapping_add(take);
            continue;
        }
        if _mm256_mask_cmpgt_epu16_mask(all, w, _mm256_set1_epi16(0x7FF)) == 0 {
            // Below U+0800: each unit's one or two bytes in its 16-bit lane, compressed.
            let two = _mm256_or_si256(
                _mm256_or_si256(_mm256_set1_epi16(0xC0), _mm256_srli_epi16::<6>(w)),
                _mm256_slli_epi16::<8>(_mm256_or_si256(
                    _mm256_and_si256(w, _mm256_set1_epi16(0x3F)),
                    _mm256_set1_epi16(0x80),
                )),
            );
            let lanes = _mm256_mask_blend_epi16(ge80, w, two);
            let keep = (deposit(u64::from(all), 0x5555_5555) | deposit(u64::from(ge80), 0xAAAA_AAAA)) as u32;
            let (take, bytes) = (rest.min(16), keep.count_ones() as usize);
            // SAFETY: the `bytes` stored are the UTF-8 of the units taken, what they count.
            unsafe { _mm256_mask_storeu_epi8(dst.add(o).cast(), lanes64(bytes) as u32, compress8x32(keep, lanes)) };
            k = k.wrapping_add(take);
            o = o.wrapping_add(bytes);
            continue;
        }
        let (u, x) = (_mm512_cvtepu16_epi32(w), _mm512_cvtepu16_epi32(next));
        let (hs, ls) = (tagged(u, 0xD800), tagged(u, 0xDC00));
        let mut valid = lanes64(rest) as u16;
        let mut take = rest.min(16);
        if rest > 16 && hs & 0x8000 != 0 {
            // A high surrogate last, with a unit after it: the next step takes it with that unit.
            valid = 0x7FFF;
            take = 15;
        }
        let pair_high = hs & tagged(x, 0xDC00) & valid;
        let pair_low = (pair_high << 1) & valid;
        let unpaired = (hs | ls) & !pair_high & !pair_low & valid;
        let two = _mm512_or_si512(
            _mm512_or_si512(s32(0xC0), _mm512_srli_epi32::<6>(u)),
            _mm512_slli_epi32::<8>(_mm512_or_si512(_mm512_and_si512(u, s32(0x3F)), s32(0x80))),
        );
        let three = _mm512_or_si512(
            _mm512_or_si512(
                _mm512_or_si512(s32(0xE0), _mm512_srli_epi32::<12>(u)),
                _mm512_slli_epi32::<8>(_mm512_or_si512(
                    _mm512_and_si512(_mm512_srli_epi32::<6>(u), s32(0x3F)),
                    s32(0x80),
                )),
            ),
            _mm512_slli_epi32::<16>(_mm512_or_si512(_mm512_and_si512(u, s32(0x3F)), s32(0x80))),
        );
        // A pair's code point and its four bytes.
        let c = _mm512_add_epi32(
            _mm512_or_si512(_mm512_slli_epi32::<10>(_mm512_and_si512(u, s32(0x3FF))), _mm512_and_si512(x, s32(0x3FF))),
            s32(0x1_0000),
        );
        // A continuation byte from six bits already shifted down (constant shifts: Miri lacks the variable ones).
        let cont = |bits: __m512i| -> __m512i { _mm512_or_si512(_mm512_and_si512(bits, s32(0x3F)), s32(0x80)) };
        let four = _mm512_or_si512(
            _mm512_or_si512(
                _mm512_or_si512(s32(0xF0), _mm512_srli_epi32::<18>(c)),
                _mm512_slli_epi32::<8>(cont(_mm512_srli_epi32::<12>(c))),
            ),
            _mm512_or_si512(_mm512_slli_epi32::<16>(cont(_mm512_srli_epi32::<6>(c))), _mm512_slli_epi32::<24>(cont(c))),
        );
        let ge80 = _mm512_cmpgt_epu32_mask(u, s32(0x7F));
        let ge800 = _mm512_cmpgt_epu32_mask(u, s32(0x7FF));
        let lanes = _mm512_mask_blend_epi32(ge80, u, two);
        let lanes = _mm512_mask_blend_epi32(ge800, lanes, three);
        let lanes = _mm512_mask_blend_epi32(unpaired, lanes, s32(0x00BD_BFEF));
        let lanes = _mm512_mask_blend_epi32(pair_high, lanes, four);
        let first = valid & !pair_low;
        let keep = deposit(u64::from(first), 0x1111_1111_1111_1111)
            | deposit(u64::from(first & ge80), 0x2222_2222_2222_2222)
            | deposit(u64::from(first & ge800), 0x4444_4444_4444_4444)
            | deposit(u64::from(pair_high), 0x8888_8888_8888_8888);
        let bytes = keep.count_ones() as usize;
        // SAFETY: the `bytes` stored are the UTF-8 of the units taken, no more than they count.
        unsafe { _mm512_mask_storeu_epi8(dst.add(o).cast(), lanes64(bytes), compress8(keep, lanes)) };
        k = k.wrapping_add(take);
        o = o.wrapping_add(bytes);
    }
    o
}
