//! The string writer's transcoding of UTF-8 into Latin-1 or UTF-16 LE, straight into a `Vec`'s spare capacity: the
//! crate's only `unsafe` code. The reader does not use it.
//!
//! Text is taken a block of 16 bytes at a time ([`Block`]). A block of ASCII is stored as it is (Latin-1) or with
//! each byte widened to a code unit (UTF-16). Where the target has a byte shuffle (SSSE3 on x86_64, found at run
//! time; NEON's `tbl` on Arm64) and the text is 48 bytes or more ([`PAIRS_FROM`]), a block of one- and two-byte chars
//! (Latin-1, Greek, Cyrillic, Hebrew, Arabic, ...) is transcoded whole: the bytes that start a char select, for each
//! half, a shuffle from [`PAIRS`] that puts each char's bytes into a 16-bit lane, where its code unit is computed;
//! the text's last bytes are taken the same way, from its last 16. Elsewhere, a block step advances past its leading
//! ASCII, and the chars after it are decoded one by one (three and four bytes always), the text's last bytes a word
//! at a time. Bytes stored past what a step writes are overwritten by the next one, or lie past the `Vec`'s length
//! at the end: room for a whole block is reserved past the longest the text can take.
//!
//! Every target writes the same bytes: x86_64 takes SSE2 (its baseline) and SSSE3, little-endian Arm64 NEON, and
//! every other target (32-bit, WebAssembly, big-endian) two `u64` words a block. The tests compare each way a target
//! has with a plain decoder for every char class at every position, and CI runs them under Miri (x86_64 with and
//! without SSSE3, Arm64, big-endian s390x).

#![allow(unsafe_code)]
#![deny(unsafe_op_in_unsafe_fn, clippy::undocumented_unsafe_blocks)]

/// Bytes per block.
const BLOCK: usize = 16;

/// One step of the transcoding, at a char boundary with a block of text readable.
trait Block {
    /// Whether the steps transcode two-byte chars too (else they stop at the first non-ASCII byte).
    const PAIRS: bool;

    /// Writes the Latin-1 bytes of a prefix of whole chars of the `BLOCK` bytes at `src` (valid UTF-8 from `src`),
    /// all of them at most U+00FF; returns the bytes read and written (0 and 0 when the first char is not ASCII
    /// and is not transcoded).
    ///
    /// # Safety
    /// `BLOCK` bytes are readable at `src` and writable at `dst`, and a char starts at `src`.
    unsafe fn latin1(src: *const u8, dst: *mut u8) -> (usize, usize);

    /// [`Block::latin1`] for UTF-16 code units: returns the bytes read and the bytes (two a unit) written.
    ///
    /// # Safety
    /// `BLOCK` bytes are readable at `src` and `2 * BLOCK` writable at `dst`, and a char starts at `src`.
    unsafe fn utf16(src: *const u8, dst: *mut u8) -> (usize, usize);

    /// The text's last bytes, `src[i..len]` (fewer than `BLOCK`), as Latin-1 at `dst` in one step: the bytes
    /// written, or `None` when they hold a char above U+00FF or the target has no such step.
    ///
    /// # Safety
    /// `i < len`, `src[..len]` is readable and valid UTF-8 from `i` (a char boundary), and `BLOCK` bytes are writable
    /// at `dst`.
    #[inline(always)]
    unsafe fn latin1_last(_src: *const u8, _i: usize, _len: usize, _dst: *mut u8) -> Option<usize> {
        None
    }

    /// [`Block::latin1_last`] for UTF-16 code units (`None` at a char of three bytes or more); `2 * BLOCK` bytes are
    /// writable at `dst`.
    ///
    /// # Safety
    /// As for [`Block::latin1_last`].
    #[inline(always)]
    unsafe fn utf16_last(_src: *const u8, _i: usize, _len: usize, _dst: *mut u8) -> Option<usize> {
        None
    }
}

/// Two `u64` words, ASCII only: the targets without 128-bit vectors (and the tests, everywhere).
#[cfg(any(test, not(any(target_arch = "x86_64", all(target_arch = "aarch64", target_endian = "little")))))]
pub(crate) struct Words;

const HIGH: u64 = 0x8080_8080_8080_8080;

/// The ASCII bytes before the first one with its high bit set, of two words read little-endian.
#[cfg(any(test, not(any(target_arch = "x86_64", all(target_arch = "aarch64", target_endian = "little")))))]
#[inline(always)]
fn ascii_prefix(a: u64, b: u64) -> usize {
    let (a, b) = (a & HIGH, b & HIGH);
    // trailing_zeros is at most 64, so neither sum can wrap.
    if a != 0 {
        (a.trailing_zeros() / 8) as usize
    } else {
        (b.trailing_zeros() / 8).wrapping_add(8) as usize
    }
}

/// The ASCII bytes of a word read little-endian before the first non-ASCII one (8 when all are).
#[inline(always)]
fn ascii_prefix8(w: u64) -> usize {
    // At most 64 / 8 = 8.
    ((w & HIGH).trailing_zeros() / 8) as usize
}

/// The low four bytes of `w` as four little-endian code units.
#[inline(always)]
fn spread(w: u64) -> u64 {
    let w = w & 0xFFFF_FFFF;
    let w = (w | (w << 16)) & 0x0000_FFFF_0000_FFFF;
    (w | (w << 8)) & 0x00FF_00FF_00FF_00FF
}

#[cfg(any(test, not(any(target_arch = "x86_64", all(target_arch = "aarch64", target_endian = "little")))))]
impl Block for Words {
    const PAIRS: bool = false;

    #[inline(always)]
    unsafe fn latin1(src: *const u8, dst: *mut u8) -> (usize, usize) {
        // SAFETY: the caller makes 16 bytes readable at `src` and writable at `dst`; every access is unaligned.
        let n = unsafe {
            let a = src.cast::<[u8; 8]>().read_unaligned();
            let b = src.add(8).cast::<[u8; 8]>().read_unaligned();
            dst.cast::<[u8; 8]>().write_unaligned(a);
            dst.add(8).cast::<[u8; 8]>().write_unaligned(b);
            ascii_prefix(u64::from_le_bytes(a), u64::from_le_bytes(b))
        };
        (n, n)
    }

    #[inline(always)]
    unsafe fn utf16(src: *const u8, dst: *mut u8) -> (usize, usize) {
        // SAFETY: the caller makes 16 bytes readable at `src` and 32 writable at `dst`; every access is unaligned.
        let n = unsafe {
            let a = u64::from_le_bytes(src.cast::<[u8; 8]>().read_unaligned());
            let b = u64::from_le_bytes(src.add(8).cast::<[u8; 8]>().read_unaligned());
            for (k, w) in [spread(a), spread(a >> 32), spread(b), spread(b >> 32)].into_iter().enumerate() {
                dst.add(k.wrapping_mul(8)).cast::<[u8; 8]>().write_unaligned(w.to_le_bytes());
            }
            ascii_prefix(a, b)
        };
        // At most 16.
        (n, n.wrapping_mul(2))
    }
}

/// For each set of the first eight bytes of a block that start a char (bit `p` for byte `p`): the shuffle that puts
/// the `k`-th such byte and the byte after it in the 16-bit lane `k` (0x80, a zero byte, past the last), and the
/// number of chars.
#[cfg(any(target_arch = "x86_64", all(target_arch = "aarch64", target_endian = "little")))]
static PAIRS: [([u8; 16], u8); 256] = pair_shuffles();

// Every index is below the array's length (k < 8, m < 256).
#[cfg(any(target_arch = "x86_64", all(target_arch = "aarch64", target_endian = "little")))]
#[allow(clippy::indexing_slicing, clippy::arithmetic_side_effects)]
const fn pair_shuffles() -> [([u8; 16], u8); 256] {
    let mut table = [([0x80u8; 16], 0u8); 256];
    let mut m = 0;
    while m < 256 {
        let mut k = 0;
        let mut p = 0;
        while p < 8 {
            if m & (1 << p) != 0 {
                table[m].0[2 * k] = p as u8;
                table[m].0[2 * k + 1] = p as u8 + 1;
                k += 1;
            }
            p += 1;
        }
        table[m].1 = k as u8;
        m += 1;
    }
    table
}

/// The chars a pair step takes, from the bit sets of the first bytes of its block (bit `p` for byte `p`): bytes
/// 0x80 and up (`high`), lead bytes of two bytes or more (`lead`, 0xC0 and up), and lead bytes of chars it does not
/// take (`big`). It takes the chars that start before the first `big` byte among the first eight; returns the bit set
/// of their first bytes (the bytes that are not continuation bytes) and the bytes they span: up to that `big` byte,
/// or with none, the eight and the continuation byte of a two-byte char starting at the eighth.
#[cfg(any(target_arch = "x86_64", all(target_arch = "aarch64", target_endian = "little")))]
#[inline(always)]
fn pair_span(high: u32, lead: u32, big: u32) -> (usize, usize) {
    // At most 8, so neither the shift nor the sums can overflow.
    let limit = (big | 0x100).trailing_zeros();
    let starts = (!high | lead) & (1u32 << limit).wrapping_sub(1);
    let read = limit.wrapping_add(u32::from(limit == 8 && lead & 0x80 != 0));
    (starts as usize, read as usize)
}

/// The chars a pair step takes from a block of 16 bytes with no `big` byte (see [`pair_span`]): all that start in
/// it but a two-byte char starting at its last byte (`last`), which the next step takes; the bit set of their first
/// bytes and the bytes they span.
#[cfg(any(target_arch = "x86_64", all(target_arch = "aarch64", target_endian = "little")))]
#[inline(always)]
fn whole_span(high: u32, lead: u32, last: u8) -> (usize, usize) {
    // The span depends only on a byte loaded on its own, not on the vector's bit sets, so that the next step's load
    // need not wait for them.
    let starts = (!high | (lead & 0x7FFF)) & 0xFFFF;
    (starts as usize, 16usize.wrapping_sub(usize::from(last >= 0xC0)))
}

/// The text's bytes from `i` to `len` (fewer than 16) as two words read little-endian, zero past `len`, for text
/// shorter than 16 bytes; and the bit set of those bytes.
///
/// # Safety
/// `i < len < 16`, and `len` bytes are readable at `src`.
#[cfg(any(target_arch = "x86_64", all(target_arch = "aarch64", target_endian = "little")))]
#[inline(always)]
unsafe fn short_window(src: *const u8, i: usize, len: usize) -> (u64, u64, u32) {
    // `i < len < 16`: no difference or shift below wraps.
    let rest = len.wrapping_sub(i);
    // SAFETY: the caller's contract; with eight bytes or more, the last eight end at `len`.
    let (low, high) = unsafe {
        if rest >= 8 {
            let last = u64::from_le_bytes(src.add(len.wrapping_sub(8)).cast::<[u8; 8]>().read_unaligned());
            // Bytes `i + 8..len` (none when `rest` is 8: a shift by 64, which `checked_shr` turns into zero).
            let shift = 16u32.wrapping_sub(rest as u32).wrapping_mul(8);
            (word(src, i, len), last.checked_shr(shift).unwrap_or(0))
        } else {
            (word(src, i, len), 0)
        }
    };
    (low, high, (1u32 << rest).wrapping_sub(1))
}

/// For the last step of text of 16 bytes or more: the bit set of the bytes from `i` among its last 16.
#[cfg(any(target_arch = "x86_64", all(target_arch = "aarch64", target_endian = "little")))]
#[inline(always)]
fn last_valid(i: usize, len: usize) -> u32 {
    // `len - 16 <= i < len`: the bytes from `i` are the last `len - i` (1..15) of the 16.
    (0xFFFFu32 << 16usize.wrapping_sub(len.wrapping_sub(i))) & 0xFFFF
}

/// x86_64's baseline: SSE2, ASCII only.
#[cfg(target_arch = "x86_64")]
pub(crate) struct Sse2;

#[cfg(target_arch = "x86_64")]
impl Block for Sse2 {
    const PAIRS: bool = false;

    #[inline(always)]
    unsafe fn latin1(src: *const u8, dst: *mut u8) -> (usize, usize) {
        use core::arch::x86_64::{__m128i, _mm_loadu_si128, _mm_movemask_epi8, _mm_storeu_si128};
        // SAFETY: SSE2 is part of every x86_64 target. The caller makes 16 bytes readable at `src` and writable at
        // `dst`; loadu and storeu take any alignment.
        let n = unsafe {
            let v = _mm_loadu_si128(src.cast::<__m128i>());
            _mm_storeu_si128(dst.cast::<__m128i>(), v);
            // Bit i is byte i's high bit; the bit above the 16 makes an all-ASCII block count 16.
            ((_mm_movemask_epi8(v) as u32) | 0x1_0000).trailing_zeros() as usize
        };
        (n, n)
    }

    #[inline(always)]
    unsafe fn utf16(src: *const u8, dst: *mut u8) -> (usize, usize) {
        // SAFETY: the caller's contract.
        let n = unsafe { sse2_widen(src, dst) };
        (n, n.wrapping_mul(2))
    }
}

/// Widens the 16 bytes at `src` to 16 code units at `dst`; returns how many of them, from the first, are ASCII.
///
/// # Safety
/// 16 bytes are readable at `src` and 32 writable at `dst`.
#[cfg(target_arch = "x86_64")]
#[inline(always)]
unsafe fn sse2_widen(src: *const u8, dst: *mut u8) -> usize {
    use core::arch::x86_64::{
        __m128i, _mm_loadu_si128, _mm_movemask_epi8, _mm_setzero_si128, _mm_storeu_si128, _mm_unpackhi_epi8,
        _mm_unpacklo_epi8,
    };
    // SAFETY: SSE2 is part of every x86_64 target. The caller makes 16 bytes readable at `src` and 32 writable at
    // `dst`. Interleaved with zeros, each byte is a little-endian code unit.
    unsafe {
        let v = _mm_loadu_si128(src.cast::<__m128i>());
        let zero = _mm_setzero_si128();
        _mm_storeu_si128(dst.cast::<__m128i>(), _mm_unpacklo_epi8(v, zero));
        _mm_storeu_si128(dst.add(16).cast::<__m128i>(), _mm_unpackhi_epi8(v, zero));
        ((_mm_movemask_epi8(v) as u32) | 0x1_0000).trailing_zeros() as usize
    }
}

/// x86_64 with SSSE3 (`pshufb`, every x86_64 CPU since 2006 bar a few early AMD ones), checked at run time:
/// ASCII and two-byte chars.
#[cfg(target_arch = "x86_64")]
pub(crate) struct Ssse3;

#[cfg(target_arch = "x86_64")]
impl Ssse3 {
    /// The code units of the chars starting in the first eight bytes of `v`, one a 16-bit lane, given the bit set of
    /// those that start a char (`starts`); and how many there are. Right when none of the chars is longer than two
    /// bytes.
    ///
    /// # Safety
    /// The CPU has SSSE3.
    #[inline]
    #[target_feature(enable = "ssse3")]
    unsafe fn pairs(v: core::arch::x86_64::__m128i, starts: usize) -> (core::arch::x86_64::__m128i, usize) {
        use core::arch::x86_64::{
            __m128i, _mm_and_si128, _mm_andnot_si128, _mm_cmplt_epi16, _mm_loadu_si128, _mm_or_si128, _mm_set1_epi16,
            _mm_shuffle_epi8, _mm_slli_epi16, _mm_srli_epi16,
        };
        let (shuffle, count) = PAIRS.get(starts & 0xFF).copied().unwrap_or(([0x80; 16], 0));
        // SAFETY: SSSE3 (the caller's contract) and SSE2; `shuffle` is 16 bytes.
        let units = unsafe {
            // Lane k: the k-th char's first byte (low) and the byte after it (high).
            let g = _mm_shuffle_epi8(v, _mm_loadu_si128(shuffle.as_ptr().cast::<__m128i>()));
            let low = _mm_and_si128(g, _mm_set1_epi16(0xFF));
            let ascii = _mm_cmplt_epi16(low, _mm_set1_epi16(0x80));
            // A lead byte's five bits, then its continuation byte's six.
            let two = _mm_or_si128(
                _mm_slli_epi16::<6>(_mm_and_si128(g, _mm_set1_epi16(0x1F))),
                _mm_and_si128(_mm_srli_epi16::<8>(g), _mm_set1_epi16(0x3F)),
            );
            _mm_or_si128(_mm_and_si128(ascii, low), _mm_andnot_si128(ascii, two))
        };
        (units, usize::from(count))
    }

    /// The vector holding the text's bytes from `i` to `len` (fewer than 16), and the bit set of their lanes: the
    /// text's last 16 bytes, or with fewer, its bytes from `i` and zeros.
    ///
    /// # Safety
    /// `i < len`, and `len` bytes are readable at `src`.
    #[inline(always)]
    unsafe fn last(src: *const u8, i: usize, len: usize) -> (core::arch::x86_64::__m128i, u32) {
        use core::arch::x86_64::{__m128i, _mm_loadu_si128, _mm_set_epi64x};
        // SAFETY: SSE2; with 16 bytes or more, the last 16 end at `len`.
        unsafe {
            if len >= 16 {
                return (_mm_loadu_si128(src.add(len.wrapping_sub(16)).cast::<__m128i>()), last_valid(i, len));
            }
            let (low, high, valid) = short_window(src, i, len);
            (_mm_set_epi64x(high as i64, low as i64), valid)
        }
    }

    /// The bit sets of the 16 bytes of `v` (bit `p` for byte `p`): bytes 0x80 and up, 0xC0 and up (lead bytes of
    /// two bytes or more), and `big` and up.
    #[inline(always)]
    fn masks(v: core::arch::x86_64::__m128i, big: u8) -> (u32, u32, u32) {
        use core::arch::x86_64::{_mm_cmpeq_epi8, _mm_max_epu8, _mm_movemask_epi8, _mm_set1_epi8};
        // SAFETY: SSE2 is part of every x86_64 target; these touch no memory. `max(v, t) == v` is `v >= t`, unsigned.
        unsafe {
            let at_least = |t: u8| _mm_movemask_epi8(_mm_cmpeq_epi8(_mm_max_epu8(v, _mm_set1_epi8(t as i8)), v)) as u32;
            (_mm_movemask_epi8(v) as u32, at_least(0xC0), at_least(big))
        }
    }
}

#[cfg(target_arch = "x86_64")]
impl Block for Ssse3 {
    const PAIRS: bool = true;

    #[inline]
    #[target_feature(enable = "ssse3")]
    unsafe fn latin1(src: *const u8, dst: *mut u8) -> (usize, usize) {
        use core::arch::x86_64::{
            __m128i, _mm_loadu_si128, _mm_packus_epi16, _mm_srli_si128, _mm_storel_epi64, _mm_storeu_si128,
        };
        // SAFETY: SSSE3 (this function's feature, checked by `transcode`) and SSE2. The caller makes 16 bytes readable
        // at `src` and writable at `dst`; a char starts at `src`, and valid UTF-8 follows. `count` is at most 8, so
        // both halves' 8 bytes fit.
        unsafe {
            let v = _mm_loadu_si128(src.cast::<__m128i>());
            let (high, lead, big) = Self::masks(v, 0xC4);
            if high == 0 {
                _mm_storeu_si128(dst.cast::<__m128i>(), v);
                return (16, 16);
            }
            if big == 0 {
                // Both halves; a lead byte at the last byte is left for the next step.
                let (starts, read) = whole_span(high, lead, src.add(15).read());
                let (low, count) = Self::pairs(v, starts & 0xFF);
                let (rest, more) = Self::pairs(_mm_srli_si128::<8>(v), starts >> 8);
                _mm_storel_epi64(dst.cast::<__m128i>(), _mm_packus_epi16(low, low));
                _mm_storel_epi64(dst.add(count).cast::<__m128i>(), _mm_packus_epi16(rest, rest));
                return (read, count.wrapping_add(more));
            }
            // Up to a char above U+00FF (a lead byte 0xC4 or above), which ends the Latin-1.
            let (starts, read) = pair_span(high, lead, big);
            let (units, count) = Self::pairs(v, starts);
            _mm_storel_epi64(dst.cast::<__m128i>(), _mm_packus_epi16(units, units));
            (read, count)
        }
    }

    #[inline]
    #[target_feature(enable = "ssse3")]
    unsafe fn utf16(src: *const u8, dst: *mut u8) -> (usize, usize) {
        use core::arch::x86_64::{__m128i, _mm_loadu_si128, _mm_srli_si128, _mm_storeu_si128};
        // SAFETY: SSSE3 (this function's feature) and SSE2. The caller makes 16 bytes readable at `src` and 32
        // writable at `dst`; a char starts at `src`, and valid UTF-8 follows. `count` is at most 8, so both halves'
        // 16 bytes fit.
        unsafe {
            let v = _mm_loadu_si128(src.cast::<__m128i>());
            let (high, lead, big) = Self::masks(v, 0xE0);
            if high == 0 {
                sse2_widen(src, dst);
                return (16, 32);
            }
            if big == 0 {
                let (starts, read) = whole_span(high, lead, src.add(15).read());
                let (low, count) = Self::pairs(v, starts & 0xFF);
                let (rest, more) = Self::pairs(_mm_srli_si128::<8>(v), starts >> 8);
                _mm_storeu_si128(dst.cast::<__m128i>(), low);
                _mm_storeu_si128(dst.add(count.wrapping_mul(2)).cast::<__m128i>(), rest);
                return (read, count.wrapping_add(more).wrapping_mul(2));
            }
            // Up to a char of three bytes or more (a lead byte 0xE0 or above).
            let (starts, read) = pair_span(high, lead, big);
            let (units, count) = Self::pairs(v, starts);
            _mm_storeu_si128(dst.cast::<__m128i>(), units);
            (read, count.wrapping_mul(2))
        }
    }

    #[inline]
    #[target_feature(enable = "ssse3")]
    unsafe fn latin1_last(src: *const u8, i: usize, len: usize, dst: *mut u8) -> Option<usize> {
        use core::arch::x86_64::{_mm_packus_epi16, _mm_srli_si128, _mm_storel_epi64};
        // SAFETY: SSSE3 (this function's feature) and SSE2; the caller's contract. `count` is at most 8, so both
        // halves' 8 bytes fit in the 16 writable.
        unsafe {
            let (v, valid) = Self::last(src, i, len);
            let (high, lead, big) = Self::masks(v, 0xC4);
            if big & valid != 0 {
                return None;
            }
            let starts = (!high | lead) & valid;
            let (low, count) = Self::pairs(v, (starts & 0xFF) as usize);
            let (rest, more) = Self::pairs(_mm_srli_si128::<8>(v), (starts >> 8) as usize);
            _mm_storel_epi64(dst.cast(), _mm_packus_epi16(low, low));
            _mm_storel_epi64(dst.add(count).cast(), _mm_packus_epi16(rest, rest));
            Some(count.wrapping_add(more))
        }
    }

    #[inline]
    #[target_feature(enable = "ssse3")]
    unsafe fn utf16_last(src: *const u8, i: usize, len: usize, dst: *mut u8) -> Option<usize> {
        use core::arch::x86_64::{_mm_srli_si128, _mm_storeu_si128};
        // SAFETY: SSSE3 (this function's feature) and SSE2; the caller's contract. `count` is at most 8, so both
        // halves' 16 bytes fit in the 32 writable.
        unsafe {
            let (v, valid) = Self::last(src, i, len);
            let (high, lead, big) = Self::masks(v, 0xE0);
            if big & valid != 0 {
                return None;
            }
            let starts = (!high | lead) & valid;
            let (low, count) = Self::pairs(v, (starts & 0xFF) as usize);
            let (rest, more) = Self::pairs(_mm_srli_si128::<8>(v), (starts >> 8) as usize);
            _mm_storeu_si128(dst.cast(), low);
            _mm_storeu_si128(dst.add(count.wrapping_mul(2)).cast(), rest);
            Some(count.wrapping_add(more).wrapping_mul(2))
        }
    }
}

/// Little-endian Arm64, short text: NEON (part of every such target), ASCII only.
#[cfg(all(target_arch = "aarch64", target_endian = "little"))]
pub(crate) struct NeonAscii;

#[cfg(all(target_arch = "aarch64", target_endian = "little"))]
impl NeonAscii {
    /// The ASCII bytes before the first non-ASCII one: each byte's high bit spread over a lane, narrowed to four bits
    /// a byte (`shrn`), one bit scan.
    #[inline(always)]
    fn ascii_prefix(v: core::arch::aarch64::uint8x16_t) -> usize {
        use core::arch::aarch64::{
            vcltzq_s8, vget_lane_u64, vreinterpret_u64_u8, vreinterpretq_s8_u8, vreinterpretq_u16_u8, vshrn_n_u16,
        };
        // SAFETY: NEON is part of every little-endian Arm64 target; these touch no memory.
        let nibbles = unsafe {
            let high = vcltzq_s8(vreinterpretq_s8_u8(v));
            vget_lane_u64::<0>(vreinterpret_u64_u8(vshrn_n_u16::<4>(vreinterpretq_u16_u8(high))))
        };
        // At most 64 / 4 = 16.
        (nibbles.trailing_zeros() / 4) as usize
    }
}

#[cfg(all(target_arch = "aarch64", target_endian = "little"))]
impl Block for NeonAscii {
    const PAIRS: bool = false;

    #[inline(always)]
    unsafe fn latin1(src: *const u8, dst: *mut u8) -> (usize, usize) {
        use core::arch::aarch64::{vld1q_u8, vst1q_u8};
        // SAFETY: NEON. The caller makes 16 bytes readable at `src` and writable at `dst`; ld1 and st1 take any
        // alignment.
        let v = unsafe {
            let v = vld1q_u8(src);
            vst1q_u8(dst, v);
            v
        };
        let n = Self::ascii_prefix(v);
        (n, n)
    }

    #[inline(always)]
    unsafe fn utf16(src: *const u8, dst: *mut u8) -> (usize, usize) {
        // SAFETY: the caller's contract.
        let v = unsafe { neon_widen(src, dst) };
        let n = Self::ascii_prefix(v);
        (n, n.wrapping_mul(2))
    }
}

/// Widens the 16 bytes at `src` to 16 code units at `dst`; returns the bytes.
///
/// # Safety
/// 16 bytes are readable at `src` and 32 writable at `dst`.
#[cfg(all(target_arch = "aarch64", target_endian = "little"))]
#[inline(always)]
unsafe fn neon_widen(src: *const u8, dst: *mut u8) -> core::arch::aarch64::uint8x16_t {
    use core::arch::aarch64::{vdupq_n_u8, vld1q_u8, vst1q_u8, vzip1q_u8, vzip2q_u8};
    // SAFETY: NEON. The caller makes 16 bytes readable at `src` and 32 writable at `dst`. Interleaved with zeros, each
    // byte is a little-endian code unit.
    unsafe {
        let v = vld1q_u8(src);
        let zero = vdupq_n_u8(0);
        vst1q_u8(dst, vzip1q_u8(v, zero));
        vst1q_u8(dst.add(16), vzip2q_u8(v, zero));
        v
    }
}

/// Little-endian Arm64, longer text: NEON with `tbl`, ASCII and two-byte chars.
#[cfg(all(target_arch = "aarch64", target_endian = "little"))]
pub(crate) struct Neon;

#[cfg(all(target_arch = "aarch64", target_endian = "little"))]
impl Neon {
    /// The bit sets of the 16 bytes of `v`: bytes 0x80 and up, 0xC0 and up, and `big` and up.
    #[inline(always)]
    fn masks(v: core::arch::aarch64::uint8x16_t, big: u8) -> (u32, u32, u32) {
        use core::arch::aarch64::{vaddv_u8, vand_u8, vcgeq_u8, vcreate_u8, vdupq_n_u8, vget_high_u8, vget_low_u8};
        // SAFETY: NEON; these touch no memory.
        unsafe {
            // Each half's bytes ANDed with their bit (1, 2, 4, ...) and added up: one bit a byte.
            let at_least = |t: u8| -> u32 {
                let set = vcgeq_u8(v, vdupq_n_u8(t));
                let weights = vcreate_u8(0x8040_2010_0804_0201);
                u32::from(vaddv_u8(vand_u8(vget_low_u8(set), weights)))
                    | (u32::from(vaddv_u8(vand_u8(vget_high_u8(set), weights))) << 8)
            };
            (at_least(0x80), at_least(0xC0), at_least(big))
        }
    }

    /// The code units of the chars starting in the first eight bytes of `v`, one a 16-bit lane, given the bit set of
    /// those that start a char (`starts`), and how many there are. None of the chars is longer than two bytes.
    #[inline(always)]
    fn pairs(v: core::arch::aarch64::uint8x16_t, starts: usize) -> (core::arch::aarch64::uint16x8_t, usize) {
        use core::arch::aarch64::{
            vandq_u16, vbslq_u16, vcltq_u16, vdupq_n_u16, vld1q_u8, vorrq_u16, vqtbl1q_u8, vreinterpretq_u16_u8,
            vshlq_n_u16, vshrq_n_u16,
        };
        let (shuffle, count) = PAIRS.get(starts & 0xFF).copied().unwrap_or(([0x80; 16], 0));
        // SAFETY: NEON; `shuffle` is 16 bytes.
        let units = unsafe {
            // Lane k: the k-th char's first byte (low) and the byte after it (high); indices of 0x80 give zeros.
            let g = vreinterpretq_u16_u8(vqtbl1q_u8(v, vld1q_u8(shuffle.as_ptr())));
            let low = vandq_u16(g, vdupq_n_u16(0xFF));
            // A lead byte's five bits, then its continuation byte's six.
            let two = vorrq_u16(
                vshlq_n_u16::<6>(vandq_u16(g, vdupq_n_u16(0x1F))),
                vandq_u16(vshrq_n_u16::<8>(g), vdupq_n_u16(0x3F)),
            );
            vbslq_u16(vcltq_u16(low, vdupq_n_u16(0x80)), low, two)
        };
        (units, usize::from(count))
    }

    /// Both halves' code units for the bit set `starts`: the low half's lanes, the high half's, and their counts.
    #[inline(always)]
    fn halves(
        v: core::arch::aarch64::uint8x16_t,
        starts: usize,
    ) -> (core::arch::aarch64::uint16x8_t, usize, core::arch::aarch64::uint16x8_t, usize) {
        use core::arch::aarch64::{vdupq_n_u8, vextq_u8};
        let (low, count) = Self::pairs(v, starts & 0xFF);
        // SAFETY: NEON; this touches no memory.
        let rest = unsafe { vextq_u8::<8>(v, vdupq_n_u8(0)) };
        let (high, more) = Self::pairs(rest, starts >> 8);
        (low, count, high, more)
    }

    /// The vector holding the text's bytes from `i` to `len` (fewer than 16), and the bit set of their lanes.
    ///
    /// # Safety
    /// `i < len`, and `len` bytes are readable at `src`.
    #[inline(always)]
    unsafe fn last(src: *const u8, i: usize, len: usize) -> (core::arch::aarch64::uint8x16_t, u32) {
        use core::arch::aarch64::{vcombine_u8, vcreate_u8, vld1q_u8};
        // SAFETY: NEON; with 16 bytes or more, the last 16 end at `len`.
        unsafe {
            if len >= 16 {
                return (vld1q_u8(src.add(len.wrapping_sub(16))), last_valid(i, len));
            }
            let (low, high, valid) = short_window(src, i, len);
            (vcombine_u8(vcreate_u8(low), vcreate_u8(high)), valid)
        }
    }
}

#[cfg(all(target_arch = "aarch64", target_endian = "little"))]
impl Block for Neon {
    const PAIRS: bool = true;

    #[inline(always)]
    unsafe fn latin1(src: *const u8, dst: *mut u8) -> (usize, usize) {
        use core::arch::aarch64::{vld1q_u8, vmaxvq_u8, vmovn_u16, vst1_u8, vst1q_u8};
        // SAFETY: NEON. The caller makes 16 bytes readable at `src` and writable at `dst`; a char starts at `src`, and
        // valid UTF-8 follows. `count` is at most 8, so both halves' 8 bytes fit.
        unsafe {
            let v = vld1q_u8(src);
            if vmaxvq_u8(v) < 0x80 {
                vst1q_u8(dst, v);
                return (16, 16);
            }
            let (high, lead, big) = Self::masks(v, 0xC4);
            if big == 0 {
                let (starts, read) = whole_span(high, lead, src.add(15).read());
                let (low, count, rest, more) = Self::halves(v, starts);
                vst1_u8(dst, vmovn_u16(low));
                vst1_u8(dst.add(count), vmovn_u16(rest));
                return (read, count.wrapping_add(more));
            }
            let (starts, read) = pair_span(high, lead, big);
            let (units, count) = Self::pairs(v, starts);
            vst1_u8(dst, vmovn_u16(units));
            (read, count)
        }
    }

    #[inline(always)]
    unsafe fn utf16(src: *const u8, dst: *mut u8) -> (usize, usize) {
        use core::arch::aarch64::{vld1q_u8, vmaxvq_u8, vreinterpretq_u8_u16, vst1q_u8};
        // SAFETY: NEON. The caller makes 16 bytes readable at `src` and 32 writable at `dst`; a char starts at `src`,
        // and valid UTF-8 follows. Each 16-bit lane is a little-endian code unit; `count` is at most 8, so both
        // halves' 16 bytes fit.
        unsafe {
            let v = vld1q_u8(src);
            if vmaxvq_u8(v) < 0x80 {
                neon_widen(src, dst);
                return (16, 32);
            }
            let (high, lead, big) = Self::masks(v, 0xE0);
            if big == 0 {
                let (starts, read) = whole_span(high, lead, src.add(15).read());
                let (low, count, rest, more) = Self::halves(v, starts);
                vst1q_u8(dst, vreinterpretq_u8_u16(low));
                vst1q_u8(dst.add(count.wrapping_mul(2)), vreinterpretq_u8_u16(rest));
                return (read, count.wrapping_add(more).wrapping_mul(2));
            }
            let (starts, read) = pair_span(high, lead, big);
            let (units, count) = Self::pairs(v, starts);
            vst1q_u8(dst, vreinterpretq_u8_u16(units));
            (read, count.wrapping_mul(2))
        }
    }

    #[inline(always)]
    unsafe fn latin1_last(src: *const u8, i: usize, len: usize, dst: *mut u8) -> Option<usize> {
        use core::arch::aarch64::{vmovn_u16, vst1_u8};
        // SAFETY: NEON; the caller's contract. `count` is at most 8, so both halves' 8 bytes fit in the 16 writable.
        unsafe {
            let (v, valid) = Self::last(src, i, len);
            let (high, lead, big) = Self::masks(v, 0xC4);
            if big & valid != 0 {
                return None;
            }
            let (low, count, rest, more) = Self::halves(v, ((!high | lead) & valid) as usize);
            vst1_u8(dst, vmovn_u16(low));
            vst1_u8(dst.add(count), vmovn_u16(rest));
            Some(count.wrapping_add(more))
        }
    }

    #[inline(always)]
    unsafe fn utf16_last(src: *const u8, i: usize, len: usize, dst: *mut u8) -> Option<usize> {
        use core::arch::aarch64::{vreinterpretq_u8_u16, vst1q_u8};
        // SAFETY: NEON; the caller's contract. `count` is at most 8, so both halves' 16 bytes fit in the 32 writable.
        unsafe {
            let (v, valid) = Self::last(src, i, len);
            let (high, lead, big) = Self::masks(v, 0xE0);
            if big & valid != 0 {
                return None;
            }
            let (low, count, rest, more) = Self::halves(v, ((!high | lead) & valid) as usize);
            vst1q_u8(dst, vreinterpretq_u8_u16(low));
            vst1q_u8(dst.add(count.wrapping_mul(2)), vreinterpretq_u8_u16(rest));
            Some(count.wrapping_add(more).wrapping_mul(2))
        }
    }
}

/// The shortest text (bytes of UTF-8) the pair steps take: in shorter text, few chars are left to them after the
/// last whole block, and decoding chars one by one, whose branches the CPU predicts, is faster (measured on x86_64;
/// Arm64 takes the same threshold, unmeasured).
#[cfg(any(target_arch = "x86_64", all(target_arch = "aarch64", target_endian = "little")))]
const PAIRS_FROM: usize = 48;

/// Appends `text` to `out` as Latin-1 (one byte a char) and returns true when every char is at most U+00FF; else
/// appends it as UTF-16 LE code units and returns false.
#[cfg(target_arch = "x86_64")]
#[inline]
pub(crate) fn transcode(out: &mut Vec<u8>, text: &str) -> bool {
    // A load and a test after the first call (std caches what the CPU has).
    if text.len() >= PAIRS_FROM && std::arch::is_x86_feature_detected!("ssse3") {
        // SAFETY: the CPU has SSSE3.
        return unsafe { transcode_ssse3(out, text) };
    }
    transcode_with::<Sse2>(out, text)
}

/// [`transcode`] with SSSE3: the steps are compiled into this function, which has the feature.
///
/// # Safety
/// The CPU has SSSE3.
#[cfg(target_arch = "x86_64")]
#[target_feature(enable = "ssse3")]
unsafe fn transcode_ssse3(out: &mut Vec<u8>, text: &str) -> bool {
    transcode_with::<Ssse3>(out, text)
}

/// Appends `text` to `out` as Latin-1 (one byte a char) and returns true when every char is at most U+00FF; else
/// appends it as UTF-16 LE code units and returns false.
#[cfg(not(target_arch = "x86_64"))]
#[inline]
pub(crate) fn transcode(out: &mut Vec<u8>, text: &str) -> bool {
    #[cfg(all(target_arch = "aarch64", target_endian = "little"))]
    if text.len() >= PAIRS_FROM {
        return transcode_with::<Neon>(out, text);
    }
    #[cfg(all(target_arch = "aarch64", target_endian = "little"))]
    return transcode_with::<NeonAscii>(out, text);
    #[cfg(not(all(target_arch = "aarch64", target_endian = "little")))]
    return transcode_with::<Words>(out, text);
}

/// The eight bytes of `src[i..len]` from `i` as a word read little-endian, zero past `len`.
///
/// # Safety
/// `i < len`, and `len` bytes are readable at `src`.
#[inline(always)]
unsafe fn word(src: *const u8, i: usize, len: usize) -> u64 {
    // `i < len`, so neither difference wraps.
    let rest = len.wrapping_sub(i);
    if rest >= 8 {
        // SAFETY: `i + 8 <= len`.
        return u64::from_le_bytes(unsafe { src.add(i).cast::<[u8; 8]>().read_unaligned() });
    }
    if len >= 8 {
        // The text's last eight bytes, shifted down so that byte `i` comes first (`rest` is 1..7).
        // SAFETY: `8 <= len`.
        let w = u64::from_le_bytes(unsafe { src.add(len.wrapping_sub(8)).cast::<[u8; 8]>().read_unaligned() });
        return w >> (8usize.wrapping_sub(rest).wrapping_mul(8));
    }
    // SAFETY: `i + rest = len`.
    unsafe { word_short(src, i, rest) }
}

/// [`word`] in text shorter than a word, a byte at a time.
///
/// # Safety
/// `rest` bytes are readable at `src + i`.
#[inline(never)]
unsafe fn word_short(src: *const u8, i: usize, rest: usize) -> u64 {
    let mut w = [0u8; 8];
    for (k, to) in w.iter_mut().take(rest).enumerate() {
        // SAFETY: `k < rest`.
        *to = unsafe { src.add(i.wrapping_add(k)).read() };
    }
    u64::from_le_bytes(w)
}

/// [`transcode`] with the block `B`. Latin-1 first; at the first char above U+00FF, the Latin-1 bytes written so far
/// are widened in place to code units and the rest of the text is written as UTF-16.
#[inline(always)]
fn transcode_with<B: Block>(out: &mut Vec<u8>, text: &str) -> bool {
    let bytes = text.as_bytes();
    let start = out.len();
    // Latin-1 takes at most one byte per byte of UTF-8; the last block stores up to BLOCK bytes past it.
    out.reserve(bytes.len().saturating_add(BLOCK));
    // SAFETY: `out` has room for `bytes.len() + BLOCK` bytes from `start`, and `latin1` writes no further (its
    // contract), over bytes that are valid UTF-8 (a `str`).
    let (read, written) = unsafe { latin1::<B>(bytes, out.as_mut_ptr().add(start)) };
    // SAFETY: `latin1` initialized the `written` bytes from `start`, within the room reserved.
    unsafe { out.set_len(start.wrapping_add(written)) };
    if read == bytes.len() {
        return true;
    }
    // `written` code units for what was read, at most one per byte of UTF-8 for the rest (two for the four-byte
    // chars), and the last block's 2 * BLOCK bytes. `written <= read <= bytes.len() <= isize::MAX`.
    let rest = bytes.len().wrapping_sub(read);
    out.reserve(written.saturating_add(rest.saturating_mul(2)).saturating_add(2 * BLOCK));
    let base = out.as_mut_ptr();
    // SAFETY: the room reserved holds `2 * written` bytes from `start`. Each Latin-1 byte becomes the code unit at
    // twice its offset, last first, so no byte is overwritten before it is read.
    unsafe {
        let at = base.add(start);
        let mut i = written;
        while i != 0 {
            i = i.wrapping_sub(1);
            let unit = u16::from(at.add(i).read());
            at.add(i.wrapping_mul(2)).cast::<[u8; 2]>().write_unaligned(unit.to_le_bytes());
        }
    }
    let done = start.wrapping_add(written.wrapping_mul(2));
    // SAFETY: from `done`, the room holds `2 * rest + 2 * BLOCK` bytes, what `utf16` writes at most for `rest` bytes
    // of text; `read` is a char boundary (where `latin1` stopped), so the rest is valid UTF-8.
    let units = unsafe { utf16::<B>(bytes.get_unchecked(read..), base.add(done)) };
    // SAFETY: `start..done` holds the widened code units and `utf16` initialized `units` bytes after them.
    unsafe { out.set_len(done.wrapping_add(units)) };
    false
}

/// Writes the Latin-1 bytes of `bytes` (valid UTF-8) at `dst`, up to the first char above U+00FF; returns how many
/// bytes it read (`bytes.len()`, or that char's offset) and wrote.
///
/// # Safety
/// `bytes` is valid UTF-8, and `bytes.len() + BLOCK` bytes are writable at `dst`.
#[inline(always)]
unsafe fn latin1<B: Block>(bytes: &[u8], dst: *mut u8) -> (usize, usize) {
    let (src, len) = (bytes.as_ptr(), bytes.len());
    // Blocks are read while a whole one is left (at `i < stop`, so `i + BLOCK <= len`); the last bytes one by one.
    let stop = len.saturating_add(1).saturating_sub(BLOCK);
    // Invariant: `o <= i <= len` (a byte or more of UTF-8 per byte written), so `o + BLOCK <= len + BLOCK`, and no
    // sum below can wrap (`len <= isize::MAX`).
    let (mut i, mut o) = (0usize, 0usize);
    let mut tried = false;
    while i < len {
        if i < stop {
            // SAFETY: `i + BLOCK <= len` bytes are readable, and `o + BLOCK <= len + BLOCK` writable; a char starts
            // at `i`. The step writes no more than it reads.
            let (r, n) = unsafe { B::latin1(src.add(i), dst.add(o)) };
            i = i.wrapping_add(r);
            o = o.wrapping_add(n);
            if B::PAIRS && r != 0 {
                // Straight to the next step: the next char is not looked at here, so that no branch depends on it.
                continue;
            }
        }
        // The chars up to the next block: non-ASCII ones, and ASCII ones past the last block.
        while i < len {
            if B::PAIRS && i >= stop && !tried {
                // Once: past a char it does not take, the rest is decoded here.
                tried = true;
                // SAFETY: `i < len`, a char boundary, and `o + BLOCK <= len + BLOCK`.
                if let Some(n) = unsafe { B::latin1_last(src, i, len, dst.add(o)) } {
                    return (len, o.wrapping_add(n));
                }
            }
            // SAFETY: `i < len`, and `o <= i`.
            let b = unsafe { src.add(i).read() };
            if b < 0x80 {
                if i < stop {
                    break;
                }
                // Past the last block: up to eight at a time from a word, stored whole (`o + 8 <= len + BLOCK`).
                // SAFETY: `i < len`.
                // SAFETY: `i < len`, and `o + 8 <= len + BLOCK`.
                let w = unsafe { word(src, i, len) };
                // SAFETY: as just said.
                unsafe { dst.add(o).cast::<[u8; 8]>().write_unaligned(w.to_le_bytes()) };
                let n = ascii_prefix8(w).min(len.wrapping_sub(i));
                i = i.wrapping_add(n);
                o = o.wrapping_add(n);
                continue;
            }
            if b >= 0xC4 {
                // The lead byte of a char above U+00FF.
                return (i, o);
            }
            if B::PAIRS && i < stop {
                // The block step takes two-byte chars.
                break;
            }
            // A lead byte 0xC2 or 0xC3 (U+0080..U+00FF): its two low bits, then its continuation byte's six. Valid
            // UTF-8 has that byte, so `i + 1 < len`.
            // SAFETY: as just said, and `o < i`.
            unsafe { dst.add(o).write((b << 6) | (src.add(i.wrapping_add(1)).read() & 0x3F)) };
            i = i.wrapping_add(2);
            o = o.wrapping_add(1);
        }
    }
    (i, o)
}

/// Writes the UTF-16 LE code units of `bytes` (valid UTF-8) at `dst`; returns the bytes written.
///
/// # Safety
/// `bytes` is valid UTF-8, and `2 * bytes.len() + 2 * BLOCK` bytes are writable at `dst`.
#[inline(always)]
unsafe fn utf16<B: Block>(bytes: &[u8], dst: *mut u8) -> usize {
    let (src, len) = (bytes.as_ptr(), bytes.len());
    let stop = len.saturating_add(1).saturating_sub(BLOCK);
    // Invariant: `o <= 2 * i`, `i <= len` (a char's code units take at most twice its bytes of UTF-8), so
    // `o + 2 * BLOCK <= 2 * len + 2 * BLOCK`, and no sum below can wrap.
    let (mut i, mut o) = (0usize, 0usize);
    let mut tried = false;
    // SAFETY: for each call below, a char starts at `i < len`, so `o + 2 * (char's bytes) <= 2 * len`.
    let unit = |o: usize, u: u32| unsafe { dst.add(o).cast::<[u8; 2]>().write_unaligned((u as u16).to_le_bytes()) };
    while i < len {
        if i < stop {
            // SAFETY: `i + BLOCK <= len` bytes are readable, and `o + 2 * BLOCK` writable; a char starts at `i`.
            // The step writes no more than two bytes per byte it reads.
            let (r, n) = unsafe { B::utf16(src.add(i), dst.add(o)) };
            i = i.wrapping_add(r);
            o = o.wrapping_add(n);
            if B::PAIRS && r != 0 {
                // Straight to the next step: the next char is not looked at here, so that no branch depends on it.
                continue;
            }
        }
        while i < len {
            if B::PAIRS && i >= stop && !tried {
                // Once: past a char it does not take, the rest is decoded here.
                tried = true;
                // SAFETY: `i < len`, a char boundary, and `o + 2 * BLOCK <= 2 * len + 2 * BLOCK`.
                if let Some(n) = unsafe { B::utf16_last(src, i, len, dst.add(o)) } {
                    return o.wrapping_add(n);
                }
            }
            // SAFETY: a char starts at `i < len`, and valid UTF-8 has all of its bytes before `len`.
            let at = |k: usize| u32::from(unsafe { src.add(i.wrapping_add(k)).read() });
            let b = at(0);
            if b < 0x80 {
                if i < stop {
                    break;
                }
                // Past the last block: up to eight at a time from a word, widened and stored whole
                // (`o + 16 <= 2 * len + 2 * BLOCK`).
                // SAFETY: `i < len`.
                // SAFETY: `i < len`, and `o + 16 <= 2 * len + 2 * BLOCK`.
                let w = unsafe { word(src, i, len) };
                // SAFETY: as just said.
                unsafe {
                    dst.add(o).cast::<[u8; 8]>().write_unaligned(spread(w).to_le_bytes());
                    dst.add(o).add(8).cast::<[u8; 8]>().write_unaligned(spread(w >> 32).to_le_bytes());
                }
                let n = ascii_prefix8(w).min(len.wrapping_sub(i));
                i = i.wrapping_add(n);
                o = o.wrapping_add(n.wrapping_mul(2));
            } else if b < 0xE0 {
                if B::PAIRS && i < stop {
                    // The block step takes two-byte chars.
                    break;
                }
                if i < stop {
                    // A run of two-byte chars (Cyrillic, Greek, Arabic, ...): four or two at a time, from a word.
                    // SAFETY: `i + BLOCK <= len` bytes are readable.
                    let w = u64::from_le_bytes(unsafe { src.add(i).cast::<[u8; 8]>().read_unaligned() });
                    if w & 0xC0E0_C0E0_C0E0_C0E0 == 0x80C0_80C0_80C0_80C0 {
                        // Each 16-bit lane is a lead byte (five bits) and a continuation byte (six).
                        let units = ((w & 0x001F_001F_001F_001F) << 6) | ((w >> 8) & 0x003F_003F_003F_003F);
                        // SAFETY: eight bytes of UTF-8 starting at `i < len`, so `o + 8 <= 2 * len`.
                        unsafe { dst.add(o).cast::<[u8; 8]>().write_unaligned(units.to_le_bytes()) };
                        i = i.wrapping_add(8);
                        o = o.wrapping_add(8);
                        continue;
                    }
                    if w & 0xC0E0_C0E0 == 0x80C0_80C0 {
                        let units = ((w & 0x001F_001F) << 6) | ((w >> 8) & 0x003F_003F);
                        // SAFETY: as above, with four bytes.
                        unsafe { dst.add(o).cast::<[u8; 4]>().write_unaligned((units as u32).to_le_bytes()) };
                        i = i.wrapping_add(4);
                        o = o.wrapping_add(4);
                        continue;
                    }
                }
                unit(o, ((b & 0x1F) << 6) | (at(1) & 0x3F));
                i = i.wrapping_add(2);
                o = o.wrapping_add(2);
            } else if b < 0xF0 {
                if i < stop {
                    // Two three-byte chars (CJK, ...) at a time, from a word.
                    // SAFETY: `i + BLOCK <= len` bytes are readable.
                    let w = u64::from_le_bytes(unsafe { src.add(i).cast::<[u8; 8]>().read_unaligned() });
                    if w & 0xC0C0_F0C0_C0F0 == 0x8080_E080_80E0 {
                        let unit3 = |w: u64| ((w & 0x0F) << 12) | ((w & 0x3F00) >> 2) | ((w >> 16) & 0x3F);
                        let units = unit3(w) | (unit3(w >> 24) << 16);
                        // SAFETY: six bytes of UTF-8 starting at `i < len`, so `o + 4 <= 2 * len`.
                        unsafe { dst.add(o).cast::<[u8; 4]>().write_unaligned((units as u32).to_le_bytes()) };
                        i = i.wrapping_add(6);
                        o = o.wrapping_add(4);
                        continue;
                    }
                }
                unit(o, ((b & 0x0F) << 12) | ((at(1) & 0x3F) << 6) | (at(2) & 0x3F));
                i = i.wrapping_add(3);
                o = o.wrapping_add(2);
            } else {
                // Above U+FFFF: a surrogate pair. Four bytes of UTF-8, four bytes of code units.
                let c = ((b & 0x07) << 18) | ((at(1) & 0x3F) << 12) | ((at(2) & 0x3F) << 6) | (at(3) & 0x3F);
                let v = c.wrapping_sub(0x1_0000);
                unit(o, 0xD800 | (v >> 10));
                unit(o.wrapping_add(2), 0xDC00 | (v & 0x3FF));
                i = i.wrapping_add(4);
                o = o.wrapping_add(4);
            }
        }
    }
    o
}

#[cfg(test)]
mod tests {
    use super::*;

    /// The plain decoder: Latin-1 when every char is at most U+00FF, else UTF-16 LE.
    fn reference(text: &str) -> (Vec<u8>, bool) {
        if text.chars().all(|c| u32::from(c) <= 0xFF) {
            (text.chars().map(|c| u32::from(c) as u8).collect(), true)
        } else {
            (text.encode_utf16().flat_map(u16::to_le_bytes).collect(), false)
        }
    }

    /// Every way this target can transcode: words, and the vector blocks it has.
    type Way = fn(&mut Vec<u8>, &str) -> bool;

    fn ways() -> Vec<(&'static str, Way)> {
        #[allow(unused_mut)]
        let mut ways: Vec<(&'static str, Way)> = vec![("words", transcode_with::<Words>), ("default", transcode)];
        #[cfg(target_arch = "x86_64")]
        {
            ways.push(("sse2", transcode_with::<Sse2>));
            if std::arch::is_x86_feature_detected!("ssse3") {
                // SAFETY: the CPU has SSSE3.
                ways.push(("ssse3", |out, text| unsafe { transcode_ssse3(out, text) }));
            }
        }
        #[cfg(all(target_arch = "aarch64", target_endian = "little"))]
        {
            ways.push(("neon-ascii", transcode_with::<NeonAscii>));
            ways.push(("neon", transcode_with::<Neon>));
        }
        ways
    }

    fn check(text: &str) {
        let expected = reference(text);
        for (name, way) in ways() {
            // After a few bytes already in the buffer, so the output is unaligned.
            for prefix in if cfg!(miri) { [3usize].as_slice() } else { [0usize, 3].as_slice() } {
                let prefix = *prefix;
                let mut out = vec![0xAA; prefix];
                let latin1 = way(&mut out, text);
                assert_eq!(
                    (out.get(prefix..).unwrap_or_default(), latin1),
                    (&expected.0[..], expected.1),
                    "{name}: {text:?}"
                );
            }
        }
    }

    const CLASSES: [char; 12] =
        ['a', '\u{7F}', '\u{80}', 'é', 'ÿ', 'Ā', '\u{7FF}', '\u{800}', '字', '\u{FFFF}', '\u{10000}', '😀'];

    /// Every length up to 200 chars with every char class at every position. Under Miri, which is slow, the lengths
    /// around a block's and a word's edges and the pair steps' threshold, at the first, a middle and the last
    /// position.
    fn shapes() -> Vec<(usize, Vec<usize>)> {
        if cfg!(miri) {
            [0, 1, 2, 7, 8, 9, 15, 16, 17, 23, 24, 31, 32, 33, 47, 48]
                .into_iter()
                .map(|len: usize| (len, if len == 0 { vec![] } else { vec![0, len / 2, len.saturating_sub(1)] }))
                .collect()
        } else {
            (0..=200).map(|len| (len, (0..len).collect())).collect()
        }
    }

    #[test]
    fn every_class_at_every_position() {
        // On fillers of ASCII, Latin-1 and wider text, so blocks start and end at every offset of a run.
        let fillers: &[char] = if cfg!(miri) { &['x', 'é', 'Ж', '字'] } else { &['x', 'é', 'Ж', '字', '😀'] };
        for &filler in fillers {
            for (len, positions) in shapes() {
                check(&core::iter::repeat_n(filler, len).collect::<String>());
                for at in positions {
                    for c in CLASSES {
                        let text: String = (0..len).map(|i| if i == at { c } else { filler }).collect();
                        check(&text);
                    }
                }
            }
        }
    }

    #[test]
    fn runs_and_random_text() {
        // Runs of each class between ASCII runs of every length around a block.
        let (ks, ns): (&[usize], &[usize]) =
            if cfg!(miri) { (&[0, 17], &[1, 9]) } else { (&[0, 1, 7, 15, 16, 17, 31], &[1, 2, 9, 17]) };
        for &k in ks {
            for &n in ns {
                for c1 in CLASSES {
                    for c2 in CLASSES {
                        let mut text = "x".repeat(k);
                        text.extend(core::iter::repeat_n(c1, n));
                        text.push_str(&"y".repeat(k));
                        text.extend(core::iter::repeat_n(c2, n));
                        check(&text);
                    }
                }
            }
        }
        // Text drawn at random, half ASCII and half the classes, up to 300 chars.
        let mut seed = 0x9E37_79B9_7F4A_7C15u64;
        for _ in 0..if cfg!(miri) { 20 } else { 20_000 } {
            seed ^= seed << 13;
            seed ^= seed >> 7;
            seed ^= seed << 17;
            let len = (seed % 300) as usize;
            let mut s = seed;
            let text: String = (0..len)
                .map(|_| {
                    s = s.wrapping_mul(6_364_136_223_846_793_005).wrapping_add(1);
                    CLASSES.get((s >> 59) as usize).copied().unwrap_or('q')
                })
                .collect();
            check(&text);
        }
    }
}
