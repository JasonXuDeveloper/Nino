//! The crate's only `unsafe` code: the string writer's transcoding of UTF-8 into Latin-1 or UTF-16 LE, straight into a
//! `Vec`'s spare capacity, and the reader's decoding of Latin-1 and UTF-16 into a `String` it builds
//! ([`latin1_string`], [`utf16_string`]: from bytes the reader has bounds-checked, and unchecked only over the UTF-8
//! they write themselves). ASCII text of up to 32 bytes, the most common string, is copied with its header by
//! [`ascii_short`] without a call to `memcpy`. Other text goes through [`wide`], to a one-pass kernel for the
//! instruction set the CPU has ([`x86`]: AVX-512, AVX2 or SSE4.1, found once at run time; [`neon`] on little-endian
//! Arm64 for text over [`SHORT_WIDE`] bytes): each char's bytes combined in a lane and the lanes packed, as Latin-1
//! until a char above U+00FF, then again from the start as UTF-16. Without one, text of up to 64 bytes is decoded
//! char by char with its header by [`short_wide`], and longer text goes through [`transcode`].
//!
//! [`transcode`] takes text a block of 16 bytes at a time ([`Block`]). A block of ASCII is stored as it is (Latin-1)
//! or with each byte widened to a code unit (UTF-16). Where the target has a byte shuffle (SSSE3 on x86_64, found at
//! run time; NEON's `tbl` on Arm64) and the text is 48 bytes or more ([`PAIRS_FROM`]), a block of one- and two-byte
//! chars (Latin-1, Greek, Cyrillic, Hebrew, Arabic, ...) is transcoded whole: the bytes that start a char select, for
//! each half, a shuffle from [`PAIRS`] that puts each char's bytes into a 16-bit lane, where its code unit is
//! computed; the text's last bytes are taken the same way, from its last 16. Elsewhere, a block step advances past
//! its leading ASCII, and the chars after it are decoded one by one (three and four bytes always), the text's last
//! bytes a word at a time. Bytes stored past what a step writes are overwritten by the next one, or lie past the
//! `Vec`'s length at the end: room for a whole step is reserved past the longest the text can take.
//!
//! The decoders take Latin-1 of [`SHORT_DECODE`] bytes or more and UTF-16 of [`SHORT_UTF16`] units or more with the
//! CPU's vector kernel (AVX-512, SSSE3 or SSE4.1 on x86_64, NEON on Arm64), the rest eight bytes a word.
//!
//! Every target writes the same bytes: x86_64 takes SSE2 (its baseline), SSSE3, SSE4.1, AVX2 and AVX-512 (the last
//! only when built with Rust 1.89 or later, `nino_avx512` from `build.rs`), little-endian Arm64 NEON, and every other
//! target (32-bit, WebAssembly, big-endian) two `u64` words a block. The tests compare every kernel the CPU has (not
//! only the one it picks) with a plain decoder for every char class at every position, and CI runs them under Miri
//! (x86_64 with SSE2 only, SSSE3, SSE4.1, AVX2 and AVX-512, Arm64, big-endian s390x).

#![allow(unsafe_code)]
#![deny(unsafe_op_in_unsafe_fn, clippy::undocumented_unsafe_blocks)]

#[cfg(all(target_arch = "aarch64", target_endian = "little"))]
mod neon;
#[cfg(target_arch = "x86_64")]
mod x86;

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

/// The top bit of each 16-bit lane of a word.
const HIGH16: u64 = 0x8000_8000_8000_8000;

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

/// Appends `head` (two one-byte headers: a `[NinoUtf8]` string's counts) and then `bytes`, at most [`SHORT`] of them,
/// and returns true; returns false, having appended nothing, for more bytes. One reservation, and stores of a constant
/// size (the first and last 16, 8 or 4 bytes, which may overlap, or three bytes): no call to `memcpy`.
#[inline(always)]
pub(crate) fn two_and_short(out: &mut Vec<u8>, head: [u8; 2], bytes: &[u8]) -> bool {
    let (src, len) = (bytes.as_ptr(), bytes.len());
    if len > SHORT {
        return false;
    }
    // `len <= 32`: none of the sums or differences below can wrap.
    out.reserve(len.wrapping_add(2));
    let at = out.len();
    // SAFETY: `2 + len` bytes of room at `dst`, and `len` readable at `src`; the stores cover `dst .. dst + 2 + len`
    // exactly: the head, then the first and last `k` bytes (16, 8 or 4, `k <= len < 2k`) at `2` and `2 + len - k`,
    // or for `len` 0 to 3 the first, middle and last byte (some of them the same).
    unsafe {
        let dst = out.as_mut_ptr().add(at);
        dst.cast::<[u8; 2]>().write_unaligned(head);
        let body = dst.add(2);
        if len >= 16 {
            let last = len.wrapping_sub(16);
            body.cast::<[u8; 16]>().write_unaligned(src.cast::<[u8; 16]>().read_unaligned());
            body.add(last).cast::<[u8; 16]>().write_unaligned(src.add(last).cast::<[u8; 16]>().read_unaligned());
        } else if len >= 8 {
            let last = len.wrapping_sub(8);
            body.cast::<[u8; 8]>().write_unaligned(src.cast::<[u8; 8]>().read_unaligned());
            body.add(last).cast::<[u8; 8]>().write_unaligned(src.add(last).cast::<[u8; 8]>().read_unaligned());
        } else if len >= 4 {
            let last = len.wrapping_sub(4);
            body.cast::<[u8; 4]>().write_unaligned(src.cast::<[u8; 4]>().read_unaligned());
            body.add(last).cast::<[u8; 4]>().write_unaligned(src.add(last).cast::<[u8; 4]>().read_unaligned());
        } else if len != 0 {
            let (middle, last) = (len / 2, len.wrapping_sub(1));
            body.write(src.read());
            body.add(middle).write(src.add(middle).read());
            body.add(last).write(src.add(last).read());
        }
        out.set_len(at.wrapping_add(2).wrapping_add(len));
    }
    true
}

/// The longest text [`ascii_short`] takes.
pub(crate) const SHORT: usize = 32;

/// Appends a Latin-1 string's header and bytes for `bytes` (at most [`SHORT`] of them) and returns true when they are
/// all ASCII; else appends nothing and returns false. See [`ascii_into`]. Every target.
#[inline(always)]
pub(crate) fn ascii_short(out: &mut Vec<u8>, bytes: &[u8]) -> bool {
    if bytes.len() > SHORT {
        return false;
    }
    // `len <= 32`: the sums cannot wrap. The room is reserved once the text is found ASCII (other text pays nothing
    // for it here).
    let (at, size) = (out.len(), bytes.len().wrapping_add(1));
    let room = || {
        out.reserve(size);
        // SAFETY: `at` is the length, within the allocation.
        unsafe { out.as_mut_ptr().add(at) }
    };
    // SAFETY: `room` reserves `1 + len` bytes from `at`, and `len <= SHORT`.
    if !unsafe { ascii_into::<true>(room, bytes) } {
        return false;
    }
    // SAFETY: the `1 + len` bytes from `at` are written.
    unsafe { out.set_len(at.wrapping_add(size)) };
    true
}

/// Writes a Latin-1 string's header and bytes for `bytes` at the pointer `room` returns, called once the bytes are
/// found ASCII, and returns true; else writes nothing, does not call `room`, and returns false. The bytes are read as the first and last 16 (or 8, or 4) bytes, which may
/// overlap, or as three bytes, tested, and stored as they were read, the header shifted in below the first block
/// (but at 32 bytes and below 4): no loop, no call to `memcpy`. With `VECTOR`, on x86-64, the 16-byte blocks are SSE2
/// registers: faster for one string ([`ascii_short`]), but not in [`ascii_strings`]' loop on AMD's Zen 4 and 5.
///
/// # Safety
/// `bytes.len() <= SHORT`, and `1 + bytes.len()` bytes are writable at the pointer `room` returns.
#[inline(always)]
unsafe fn ascii_into<const VECTOR: bool>(room: impl FnOnce() -> *mut u8, bytes: &[u8]) -> bool {
    let (src, len) = (bytes.as_ptr(), bytes.len());
    // `len <= 32`: none of the sums or differences below can wrap, and the header, `2 * len + 1`, is one byte.
    let header = (len as u8).wrapping_mul(2).wrapping_add(1);
    #[cfg(target_arch = "x86_64")]
    if VECTOR && len >= 16 {
        use core::arch::x86_64::{
            _mm_cvtsi32_si128, _mm_loadu_si128, _mm_movemask_epi8, _mm_or_si128, _mm_slli_si128, _mm_storeu_si128,
        };
        let last = len.wrapping_sub(16);
        // SAFETY: SSE2 is in every x86-64 CPU; `16 <= len` bytes are readable at `src`, so sixteen are at `src` and at
        // `src + len - 16`. The stores are those of the other targets' path below (in vector registers: two loads
        // and two stores, where words take four of each and a double shift).
        unsafe {
            let (a, b) = (_mm_loadu_si128(src.cast()), _mm_loadu_si128(src.add(last).cast()));
            if _mm_movemask_epi8(_mm_or_si128(a, b)) != 0 {
                return false;
            }
            let dst = room();
            if len < 32 {
                _mm_storeu_si128(
                    dst.cast(),
                    _mm_or_si128(_mm_slli_si128::<1>(a), _mm_cvtsi32_si128(i32::from(header))),
                );
            } else {
                dst.write(header);
                _mm_storeu_si128(dst.add(1).cast(), a);
            }
            _mm_storeu_si128(dst.add(1).add(last).cast(), b);
        }
        return true;
    }
    if len >= 16 {
        let last = len.wrapping_sub(16);
        // SAFETY: `16 <= len` bytes are readable at `src`, so sixteen are at `src` and at `src + len - 16`.
        let (a, b) =
            unsafe { (src.cast::<[u8; 16]>().read_unaligned(), src.add(last).cast::<[u8; 16]>().read_unaligned()) };
        if (u128::from_ne_bytes(a) | u128::from_ne_bytes(b)) & 0x8080_8080_8080_8080_8080_8080_8080_8080 != 0 {
            return false;
        }
        let dst = room();
        // SAFETY: the caller's room, `1 + len` bytes; the stores cover `dst .. dst + 1 + len` exactly (the second
        // block ends at `1 + last + 16 = 1 + len`): below 32 bytes, the header and the first 15 bytes as one block
        // (the second starts at `1 + last <= 16`, so it covers the 16th).
        unsafe {
            if len < 32 {
                let first = (u128::from_le_bytes(a) << 8) | u128::from(header);
                dst.cast::<[u8; 16]>().write_unaligned(first.to_le_bytes());
            } else {
                dst.write(header);
                dst.add(1).cast::<[u8; 16]>().write_unaligned(a);
            }
            dst.add(1).add(last).cast::<[u8; 16]>().write_unaligned(b);
        }
        return true;
    }
    if len >= 8 {
        let last = len.wrapping_sub(8);
        // SAFETY: `8 <= len` bytes are readable at `src`, so eight are at `src` and at `src + len - 8`.
        let (a, b) =
            unsafe { (src.cast::<[u8; 8]>().read_unaligned(), src.add(last).cast::<[u8; 8]>().read_unaligned()) };
        if (u64::from_ne_bytes(a) | u64::from_ne_bytes(b)) & HIGH != 0 {
            return false;
        }
        let dst = room();
        // SAFETY: the caller's room; the stores cover `dst .. dst + 1 + len` exactly (the second word ends at
        // `1 + last + 8 = 1 + len`): the header and the first seven bytes as one word (`len < 16`: the second starts
        // at `1 + last <= 8`, so it covers the eighth). One store fewer than a byte for the header.
        unsafe {
            let first = (u64::from_le_bytes(a) << 8) | u64::from(header);
            dst.cast::<[u8; 8]>().write_unaligned(first.to_le_bytes());
            dst.add(1).add(last).cast::<[u8; 8]>().write_unaligned(b);
        }
        return true;
    }
    if len >= 4 {
        let last = len.wrapping_sub(4);
        // SAFETY: `4 <= len` bytes are readable at `src`.
        let (a, b) =
            unsafe { (src.cast::<[u8; 4]>().read_unaligned(), src.add(last).cast::<[u8; 4]>().read_unaligned()) };
        if (u32::from_ne_bytes(a) | u32::from_ne_bytes(b)) & (HIGH as u32) != 0 {
            return false;
        }
        let dst = room();
        // SAFETY: as above, with four-byte halves (`len < 8`).
        unsafe {
            let first = (u32::from_le_bytes(a) << 8) | u32::from(header);
            dst.cast::<[u8; 4]>().write_unaligned(first.to_le_bytes());
            dst.add(1).add(last).cast::<[u8; 4]>().write_unaligned(b);
        }
        return true;
    }
    // 0 to 3 bytes: the first, the middle and the last (some of them the same byte).
    let (middle, last) = (len / 2, len.saturating_sub(1));
    let [c0, c1, c2] = if len == 0 {
        [0; 3]
    } else {
        // SAFETY: `middle` and `last` are below `len`, which is not 0.
        unsafe { [src.read(), src.add(middle).read(), src.add(last).read()] }
    };
    if (c0 | c1 | c2) & 0x80 != 0 {
        return false;
    }
    let dst = room();
    // SAFETY: the caller's room; for `len` 1 to 3 the stores at `1`, `1 + middle` and `1 + last` cover
    // `1 .. 1 + len`, and none is past it.
    unsafe {
        dst.write(header);
        if len != 0 {
            dst.add(1).write(c0);
            dst.add(1).add(middle).write(c1);
            dst.add(1).add(last).write(c2);
        }
    }
    true
}

/// Appends `head` (a collection's length, or nothing), then the `N` bytes `encode` gives for each of `values` (a block
/// of fixed-size elements): one capacity check and the stores, where `Vec::resize` would first fill the room with
/// zeros (a call to `memset` for each block).
#[inline(always)]
pub(crate) fn extend_encoded<T, const H: usize, const N: usize>(
    out: &mut Vec<u8>,
    head: [u8; H],
    values: &[T],
    encode: impl Fn(&T) -> [u8; N],
) {
    // Saturated, a size `reserve` refuses (it panics, as `resize` did).
    let size = values.len().saturating_mul(N).saturating_add(H);
    out.reserve(size);
    let len = out.len();
    let start = out.spare_capacity_mut().as_mut_ptr().cast::<[u8; H]>();
    // SAFETY: the room reserved holds `H` bytes from `len`, then `N` for each value; `at` has passed `N` for each
    // value written so far. `[u8; H]` and `[u8; N]` have alignment 1.
    unsafe {
        start.write(head);
        let mut at = start.add(1).cast::<[u8; N]>();
        for v in values {
            at.write(encode(v));
            at = at.add(1);
        }
    }
    // SAFETY: the `size` bytes from `len` are written (`H`, then `N` for each value), within the room reserved.
    unsafe { out.set_len(len.wrapping_add(size)) };
}

/// The largest element [`extend_fixed`] encodes through a buffer on the stack.
pub(crate) const FIXED_STACK: usize = 64;

/// Appends `head`, then the `size` bytes (at most [`FIXED_STACK`]) `encode` writes for each of `values` (a block of
/// a fixed-size type that encodes into a slice): each into a zeroed buffer on the stack, copied to the room reserved
/// once. When `size` is a constant (it is, inlined), the compiler drops the buffer and stores straight into the room,
/// where zeroing the room itself first (`Vec::resize`) would cost a pass over it.
#[inline(always)]
pub(crate) fn extend_fixed<T, const H: usize>(
    out: &mut Vec<u8>,
    head: [u8; H],
    values: &[T],
    size: usize,
    encode: impl Fn(&T, &mut [u8]),
) {
    let size = size.min(FIXED_STACK);
    // Saturated, a size `reserve` refuses (it panics, as `resize` does).
    let total = values.len().saturating_mul(size).saturating_add(H);
    out.reserve(total);
    let len = out.len();
    let start = out.spare_capacity_mut().as_mut_ptr().cast::<u8>();
    // SAFETY: the room reserved holds `H` bytes from `len`, then `size` for each value; `at` has passed `size` for each
    // value written so far, and `buffer` holds `size <= FIXED_STACK` bytes. `u8` has alignment 1.
    unsafe {
        start.cast::<[u8; H]>().write(head);
        let mut at = start.add(H);
        for v in values {
            let mut buffer = [0u8; FIXED_STACK];
            encode(v, buffer.get_mut(..size).unwrap_or_default());
            core::ptr::copy_nonoverlapping(buffer.as_ptr(), at, size);
            at = at.add(size);
        }
    }
    // SAFETY: the `total` bytes from `len` are written (`H`, then `size` for each value), within the room reserved.
    unsafe { out.set_len(len.wrapping_add(total)) };
}

/// [`extend_fixed`] over an iterator (a map's entries): appends `head`, then the `size` bytes (at most
/// [`FIXED_STACK`]) `encode` writes for each of at most `values.len()` items, with one reservation. Writes and keeps
/// only what the iterator yields, so an iterator that yields fewer items than its `len` said leaves no byte
/// unwritten.
#[inline(always)]
pub(crate) fn extend_fixed_iter<I, const H: usize>(
    out: &mut Vec<u8>,
    head: [u8; H],
    values: I,
    size: usize,
    encode: impl Fn(I::Item, &mut [u8]),
) where
    I: ExactSizeIterator,
{
    let size = size.min(FIXED_STACK);
    let count = values.len();
    // Saturated, a size `reserve` refuses (it panics, as `resize` does).
    out.reserve(count.saturating_mul(size).saturating_add(H));
    let len = out.len();
    let start = out.spare_capacity_mut().as_mut_ptr().cast::<u8>();
    let mut written = 0usize;
    // SAFETY: the room reserved holds `H` bytes from `len`, then `size` for each of at most `count` items (`take`);
    // `at` has passed `size` for each item written so far, and `buffer` holds `size <= FIXED_STACK` bytes. `u8` has
    // alignment 1.
    unsafe {
        start.cast::<[u8; H]>().write(head);
        let mut at = start.add(H);
        for v in values.take(count) {
            let mut buffer = [0u8; FIXED_STACK];
            encode(v, buffer.get_mut(..size).unwrap_or_default());
            core::ptr::copy_nonoverlapping(buffer.as_ptr(), at, size);
            at = at.add(size);
            // At most `count`: one an item.
            written = written.wrapping_add(1);
        }
    }
    // SAFETY: `H` bytes, then `size` for each of the `written <= count` items, are written from `len`, within the
    // room reserved (so the product does not wrap).
    unsafe { out.set_len(len.wrapping_add(H).wrapping_add(written.wrapping_mul(size))) };
}

/// Decodes each of `chunks` (the elements of a block of fixed-size values, its bytes already taken from the input)
/// into a `Vec` allocated once at its exact length: `collect` into a `Result` does not know the length and grows the
/// `Vec` as it goes. Stops at the first error, dropping the values decoded so far.
#[inline(always)]
pub(crate) fn decode_block<C, T, I, F>(chunks: I, decode: F) -> crate::error::Result<Vec<T>>
where
    I: ExactSizeIterator<Item = C>,
    F: Fn(C) -> crate::error::Result<T>,
{
    let len = chunks.len();
    let mut out = Vec::with_capacity(len);
    let room = out.spare_capacity_mut();
    let mut done = 0usize;
    for (slot, bytes) in room.iter_mut().zip(chunks) {
        match decode(bytes) {
            Ok(value) => {
                slot.write(value);
                // At most `len`: one a slot.
                done = done.wrapping_add(1);
            }
            Err(e) => {
                // SAFETY: the first `done` slots are written.
                unsafe { out.set_len(done) };
                return Err(e);
            }
        }
    }
    // SAFETY: the capacity is at least `len` and `zip` wrote a slot for each of the `len` chunks: `done == len`.
    unsafe { out.set_len(done) };
    Ok(out)
}

/// The most strings [`ascii_strings`] writes with one reservation.
const RUN: usize = 64;

/// Appends the header and bytes of each string from the start of `values` while it is ASCII of at most [`SHORT`]
/// bytes, up to [`RUN`] of them with one reservation (room for the longest such strings), and returns how many it
/// wrote and how many it had room for: fewer written means the next is not short ASCII. The end of the output is
/// kept in a register, not in the `Vec`, from one string to the next (a list of strings,
/// [`crate::strings::write_strings`]).
#[inline]
pub(crate) fn ascii_strings(out: &mut Vec<u8>, values: &[String]) -> (usize, usize) {
    let run = values.len().min(RUN);
    // At most `RUN * (SHORT + 1)`: no wrap.
    out.reserve(run.wrapping_mul(SHORT.wrapping_add(1)));
    let base = out.as_mut_ptr();
    let mut o = out.len();
    let mut done = 0usize;
    for v in values.iter().take(run) {
        // SAFETY: `v.len() <= SHORT`, and the room reserved holds `1 + SHORT` bytes from `o` for each string of the
        // run, of which `o` has passed at most that many for each string written.
        if v.len() > SHORT || !unsafe { ascii_into::<false>(|| base.add(o), v.as_bytes()) } {
            break;
        }
        o = o.wrapping_add(1).wrapping_add(v.len());
        done = done.wrapping_add(1);
    }
    // SAFETY: the bytes up to `o` are written (each string's `1 + len`), within the room reserved.
    unsafe { out.set_len(o) };
    (done, run)
}

/// The string of the Latin-1 text of `n` bytes (1 to 16) at the start of `block` when it is ASCII, else `None`: the
/// block read as one word and tested under a mask, stored as the first and last 8 (or 4) bytes, or as three bytes,
/// with no call to `memcpy` ([`crate::reader::Reader`]'s lists of strings).
#[inline(always)]
pub(crate) fn ascii_block(block: &[u8; 16], n: usize) -> Option<String> {
    if n == 0 || n > 16 {
        return None;
    }
    let w = u128::from_le_bytes(*block);
    // The bytes of the text: `1 <= n <= 16`, so the shift is 8 to 120 below 16.
    let mask = if n == 16 { u128::MAX } else { (1u128 << (n.wrapping_mul(8) as u32)).wrapping_sub(1) };
    if w & mask & 0x8080_8080_8080_8080_8080_8080_8080_8080 != 0 {
        return None;
    }
    let mut text = Vec::<u8>::with_capacity(n);
    let dst = text.spare_capacity_mut().as_mut_ptr().cast::<u8>();
    let src = block.as_ptr();
    // SAFETY: `n` bytes of room at `dst`, and `n <= 16` bytes readable at `src`; the stores cover `0 .. n` exactly
    // (first and last 8 or 4 bytes, which may overlap, or the first, middle and last of 1 to 3); ASCII is valid
    // UTF-8.
    unsafe {
        if n >= 8 {
            dst.cast::<[u8; 8]>().write_unaligned(src.cast::<[u8; 8]>().read_unaligned());
            dst.add(n.wrapping_sub(8))
                .cast::<[u8; 8]>()
                .write_unaligned(src.add(n.wrapping_sub(8)).cast::<[u8; 8]>().read_unaligned());
        } else if n >= 4 {
            dst.cast::<[u8; 4]>().write_unaligned(src.cast::<[u8; 4]>().read_unaligned());
            dst.add(n.wrapping_sub(4))
                .cast::<[u8; 4]>()
                .write_unaligned(src.add(n.wrapping_sub(4)).cast::<[u8; 4]>().read_unaligned());
        } else {
            dst.write(src.read());
            dst.add(n / 2).write(src.add(n / 2).read());
            dst.add(n.wrapping_sub(1)).write(src.add(n.wrapping_sub(1)).read());
        }
        text.set_len(n);
        Some(String::from_utf8_unchecked(text))
    }
}

/// The longest text [`short_wide`] takes. Up to 64 bytes it is faster than [`transcode`] for every class of text but
/// the pairs of Latin-1 text (48 bytes or more on x86_64 with SSSE3, a few percent), with fewer branches per char and
/// none between strings (measured on x86_64).
pub(crate) const SHORT_WIDE: usize = 64;

/// Arm64: the longest text with a char above U+007F that [`short_wide`] writes; longer text takes [`neon::wide`].
#[cfg(all(target_arch = "aarch64", target_endian = "little"))]
const NEON_FROM: usize = 0;

/// Appends the header and chars of `text`, at most [`SHORT_WIDE`] bytes with a char above U+007F: as Latin-1 when every
/// char is at most U+00FF, else as UTF-16 LE code units. One reservation, the chars decoded one by one straight into
/// it, the header written last. Every target.
#[inline(never)]
pub(crate) fn short_wide(out: &mut Vec<u8>, text: &str) {
    short_wide_inline(out, text);
}

/// [`short_wide`], inlined (in [`strings_short`]'s loop).
#[inline(always)]
fn short_wide_inline(out: &mut Vec<u8>, text: &str) {
    let (src, len) = (text.as_ptr(), text.len());
    if len > SHORT_WIDE {
        return;
    }
    // UTF-16 takes at most two bytes per byte of UTF-8, Latin-1 one. `len <= 64`: no sum below can wrap. A char above
    // U+007F takes two bytes or more, so there are at most 63 chars or code units and the header is one byte.
    out.reserve(len.wrapping_mul(2).wrapping_add(1));
    let at = out.len();
    // SAFETY: the room reserved holds `1 + 2 * len` bytes from `at`. Invariants: `i <= len`, a char boundary of the
    // valid UTF-8 at `src`, and `o <= i` (Latin-1) or `o <= 2 * i` (UTF-16) bytes written after the header.
    unsafe {
        let base = out.as_mut_ptr().add(at);
        let dst = base.add(1);
        let (mut i, mut o) = (0usize, 0usize);
        while i < len {
            let b = src.add(i).read();
            if b < 0x80 {
                dst.add(o).write(b);
                i = i.wrapping_add(1);
            } else if b < 0xC4 {
                // U+0080..U+00FF: its lead byte's two low bits, then its continuation byte's six.
                dst.add(o).write((b << 6) | (src.add(i.wrapping_add(1)).read() & 0x3F));
                i = i.wrapping_add(2);
            } else {
                break;
            }
            o = o.wrapping_add(1);
        }
        if i == len {
            // `o` chars, at most 63: the header `2 * o + 1`.
            base.write((o as u8).wrapping_mul(2).wrapping_add(1));
            out.set_len(at.wrapping_add(1).wrapping_add(o));
            return;
        }
        // A char above U+00FF: the Latin-1 bytes become code units in place, last first, and the rest follows.
        let mut k = o;
        while k != 0 {
            k = k.wrapping_sub(1);
            let unit = u16::from(dst.add(k).read());
            dst.add(k.wrapping_mul(2)).cast::<[u8; 2]>().write_unaligned(unit.to_le_bytes());
        }
        let mut o = o.wrapping_mul(2);
        let unit = |o: usize, u: u32| dst.add(o).cast::<[u8; 2]>().write_unaligned((u as u16).to_le_bytes());
        while i < len {
            let byte = |k: usize| u32::from(src.add(i.wrapping_add(k)).read());
            let b = byte(0);
            if b < 0x80 {
                unit(o, b);
                i = i.wrapping_add(1);
            } else if b < 0xE0 {
                unit(o, ((b & 0x1F) << 6) | (byte(1) & 0x3F));
                i = i.wrapping_add(2);
            } else if b < 0xF0 {
                unit(o, ((b & 0x0F) << 12) | ((byte(1) & 0x3F) << 6) | (byte(2) & 0x3F));
                i = i.wrapping_add(3);
            } else {
                let c = ((b & 0x07) << 18) | ((byte(1) & 0x3F) << 12) | ((byte(2) & 0x3F) << 6) | (byte(3) & 0x3F);
                let v = c.wrapping_sub(0x1_0000);
                unit(o, 0xD800 | (v >> 10));
                o = o.wrapping_add(2);
                unit(o, 0xDC00 | (v & 0x3FF));
                i = i.wrapping_add(4);
            }
            o = o.wrapping_add(2);
        }
        // `o / 2` code units, at most 63: the header `2 * (o / 2)`, which is `o`.
        base.write(o as u8);
        out.set_len(at.wrapping_add(1).wrapping_add(o));
    }
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
    if text.len() >= PAIRS_FROM && x86::tier() >= x86::Tier::Ssse3 {
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

/// The longest text [`wide`] takes to a one-pass kernel: its header (a count of chars or code units, at most one per
/// byte) fits an `i32`.
#[cfg(any(target_arch = "x86_64", all(target_arch = "aarch64", target_endian = "little")))]
const WIDE_MAX: usize = i32::MAX as usize;

/// Appends the header and chars of `text`, which has a char above U+007F: as Latin-1 when every char is at most
/// U+00FF, else as UTF-16 LE code units. Returns false, having written nothing, when it does not take the text: on
/// x86_64 without SSE4.1, or on targets without a one-pass kernel, text longer than [`SHORT_WIDE`], which
/// [`transcode`] takes.
#[inline(always)]
pub(crate) fn wide(out: &mut Vec<u8>, text: &str) -> bool {
    #[cfg(target_arch = "x86_64")]
    if text.len() <= WIDE_MAX {
        // Text under 64 bytes has a one-byte header (at most 63 chars or code units): each kernel has a copy for it
        // with that room, and no branch on the header's size.
        let one = text.len() < 64;
        match x86::tier() {
            // SAFETY: the CPU has what each kernel needs (its tier), and the text is at most `WIDE_MAX` bytes (under
            // 64 for the copies with `ONE`).
            #[cfg(nino_avx512)]
            x86::Tier::Avx512 if one => unsafe { x86::wide_avx512::<true>(out, text) },
            // SAFETY: as above.
            #[cfg(nino_avx512)]
            x86::Tier::Avx512 => unsafe { x86::wide_avx512::<false>(out, text) },
            // SAFETY: as above.
            x86::Tier::Avx2 if one => unsafe { x86::wide_avx2::<true>(out, text) },
            // SAFETY: as above.
            x86::Tier::Avx2 => unsafe { x86::wide_avx2::<false>(out, text) },
            // SAFETY: as above (a compiler without AVX-512 never finds that tier).
            #[cfg(not(nino_avx512))]
            x86::Tier::Avx512 if one => unsafe { x86::wide_avx2::<true>(out, text) },
            // SAFETY: as above.
            #[cfg(not(nino_avx512))]
            x86::Tier::Avx512 => unsafe { x86::wide_avx2::<false>(out, text) },
            // SAFETY: as above.
            x86::Tier::Sse41 if one => unsafe { x86::wide_sse41::<true>(out, text) },
            // SAFETY: as above.
            x86::Tier::Sse41 => unsafe { x86::wide_sse41::<false>(out, text) },
            x86::Tier::Sse2 | x86::Tier::Ssse3 => return wide_short(out, text),
        }
        return true;
    }
    // Arm64: short text stays with `short_wide`, which beats the 32-byte windows' setup there (Neoverse-N2).
    #[cfg(all(target_arch = "aarch64", target_endian = "little"))]
    if text.len() > NEON_FROM && text.len() <= WIDE_MAX {
        neon::wide(out, text);
        return true;
    }
    wide_short(out, text)
}

/// The size of the last-level cache in bytes, read once: the largest `size` under
/// `/sys/devices/system/cpu/cpu0/cache` on Linux (the cache one core shares with its neighbors, an L3 slice on
/// EPYC), 32 MiB on other systems, under Miri and when the files cannot be read. A list whose data fits in it is
/// written without prefetching: on a CPU whose L3 held a 100 MB set (Xeon 6973P-C), prefetching ahead made the Go
/// runtime 13 to 38% slower, since nothing waited on memory and the extra loads still cost.
#[inline]
pub(crate) fn last_level_cache() -> usize {
    static SIZE: std::sync::OnceLock<usize> = std::sync::OnceLock::new();
    *SIZE.get_or_init(|| read_last_level_cache().unwrap_or(32 << 20))
}

#[cfg(all(not(miri), target_os = "linux"))]
fn read_last_level_cache() -> Option<usize> {
    let mut largest = None;
    for entry in std::fs::read_dir("/sys/devices/system/cpu/cpu0/cache").ok()?.flatten() {
        let Ok(text) = std::fs::read_to_string(entry.path().join("size")) else { continue };
        let text = text.trim();
        let (digits, unit) = match text.char_indices().find(|(_, c)| !c.is_ascii_digit()) {
            Some((at, _)) => text.split_at(at),
            None => (text, ""),
        };
        let Ok(n) = digits.parse::<usize>() else { continue };
        let scale = match unit {
            "" => 1,
            "K" => 1 << 10,
            "M" => 1 << 20,
            "G" => 1 << 30,
            _ => continue,
        };
        let size = n.saturating_mul(scale);
        largest = Some(largest.map_or(size, |l: usize| l.max(size)));
    }
    largest
}

#[cfg(not(all(not(miri), target_os = "linux")))]
fn read_last_level_cache() -> Option<usize> {
    None
}

/// Asks the CPU to bring the cache line at `p` into the cache closest to the core, without waiting for it: x86 and
/// x86_64 `prefetcht0` (SSE), Arm64 `prfm pldl1keep`; nothing on other targets and under Miri. A prefetch never
/// faults, whatever the address (a dangling pointer of an empty `String` included), and changes nothing the program
/// can observe.
#[inline(always)]
pub(crate) fn prefetch<T: ?Sized>(p: *const T) {
    #[cfg(all(not(miri), target_arch = "x86_64"))]
    // SAFETY: SSE is part of every x86_64 target; a prefetch reads nothing and never faults.
    unsafe {
        core::arch::x86_64::_mm_prefetch::<{ core::arch::x86_64::_MM_HINT_T0 }>(p.cast::<i8>());
    }
    #[cfg(all(not(miri), target_arch = "x86", target_feature = "sse"))]
    // SAFETY: the target has SSE (the cfg); a prefetch reads nothing and never faults.
    unsafe {
        core::arch::x86::_mm_prefetch::<{ core::arch::x86::_MM_HINT_T0 }>(p.cast::<i8>());
    }
    #[cfg(all(not(miri), target_arch = "aarch64"))]
    // SAFETY: a hint that touches no register but its address and never faults; no memory is read or written as
    // far as the program can tell, no stack, no flags.
    unsafe {
        core::arch::asm!("prfm pldl1keep, [{0}]", in(reg) p.cast::<u8>(), options(nostack, readonly, preserves_flags));
    }
    #[cfg(any(
        miri,
        not(any(target_arch = "x86_64", all(target_arch = "x86", target_feature = "sse"), target_arch = "aarch64"))
    ))]
    let _ = p;
}

/// Pads the code to the next multiple of 64 bytes (and aligns the function's section to 64), so that what follows
/// sits where it does whatever code comes before it: the loops over a list of strings took up to 18% longer,
/// depending on the CPU, from where the linker happened to place them (functions are aligned to 16 bytes only),
/// each time unrelated code grew. Only at the start of a function that runs once for a list: the padding (a few
/// multi-byte `nop`s) runs once a call. x86_64 and AArch64; not under Miri (no inline assembly there).
#[inline(always)]
pub(crate) fn align_code() {
    #[cfg(all(not(miri), any(target_arch = "x86_64", target_arch = "aarch64")))]
    // SAFETY: an assembler directive: padding that does nothing, touching no memory, stack or flags.
    unsafe {
        core::arch::asm!(".p2align 6", options(nomem, nostack, preserves_flags));
    }
}

/// Writes strings from the start of `values` (a list's) with this CPU's list loop while it takes them (each under 64
/// bytes on x86_64 with SSE4.1, else of up to [`SHORT`] bytes); returns how many it wrote.
#[inline]
pub(crate) fn strings(out: &mut Vec<u8>, values: &[String]) -> usize {
    #[cfg(target_arch = "x86_64")]
    // SAFETY: the CPU has what each kernel needs (its tier).
    unsafe {
        match x86::tier() {
            #[cfg(nino_avx512)]
            x86::Tier::Avx512 => x86::strings_avx512(out, values),
            x86::Tier::Avx2 => x86::strings_avx2(out, values),
            // A compiler without AVX-512 never finds that tier.
            #[cfg(not(nino_avx512))]
            x86::Tier::Avx512 => x86::strings_avx2(out, values),
            x86::Tier::Sse41 => x86::strings_sse41(out, values),
            x86::Tier::Sse2 | x86::Tier::Ssse3 => strings_short(out, values),
        }
    }
    #[cfg(not(target_arch = "x86_64"))]
    strings_short(out, values)
}

/// The list loop without a one-pass kernel (every target but x86_64 with SSE4.1): the strings from the start of
/// `values` while each is at most [`SHORT`] bytes, ASCII through [`ascii_short`] and other text through
/// [`short_wide`]'s body inlined; returns how many it wrote.
#[inline]
fn strings_short(out: &mut Vec<u8>, values: &[String]) -> usize {
    for (k, v) in values.iter().enumerate() {
        if v.len() > SHORT {
            return k;
        }
        if !ascii_short(out, v.as_bytes()) {
            #[cfg(all(target_arch = "aarch64", target_endian = "little"))]
            if v.len() > NEON_FROM {
                neon::wide(out, v);
                continue;
            }
            // Text of up to 32 bytes with a char above U+007F, which `short_wide` takes.
            short_wide_inline(out, v);
        }
    }
    values.len()
}

/// [`wide`] for text of up to [`SHORT`] bytes: [`short_wide`] directly on every target but x86_64 (where [`wide`]
/// takes such text to it), so the writer's inlined path is that call alone.
#[inline(always)]
pub(crate) fn wide_short_text(out: &mut Vec<u8>, text: &str) {
    #[cfg(all(target_arch = "aarch64", target_endian = "little"))]
    if text.len() > NEON_FROM {
        neon::wide(out, text);
        return;
    }
    #[cfg(not(target_arch = "x86_64"))]
    short_wide(out, text);
    #[cfg(target_arch = "x86_64")]
    wide(out, text);
}

/// [`wide`] without a one-pass kernel: [`short_wide`] for text of up to [`SHORT_WIDE`] bytes.
#[inline(always)]
fn wide_short(out: &mut Vec<u8>, text: &str) -> bool {
    if text.len() <= SHORT_WIDE {
        short_wide(out, text);
        return true;
    }
    false
}

/// The room a one-pass writer reserves for the header of `text` (with a char above U+007F): exact below 256 bytes,
/// where the header takes one byte or three by the text's UTF-16 code units (which `units` counts, for text of 64
/// bytes or more: fewer than 64 units in under 256 bytes is possible), so the chars are never moved there (a moved
/// block reloads bytes just stored, which stalls); else [`header_room`].
#[inline(always)]
pub(crate) fn text_room(text: &str, units: impl FnOnce(&[u8]) -> u64) -> usize {
    let len = text.len();
    if len < 64 {
        1
    } else if len < 256 {
        if units(text.as_bytes()) < 64 {
            1
        } else {
            3
        }
    } else {
        header_room(len)
    }
}

/// The room a one-pass writer reserves for the header of a string of `len` bytes of UTF-8: the size of the longest
/// header that length allows (at most `len` chars or code units, and `2 * n` and `2 * n + 1` take as many bytes).
/// [`finish`] moves the chars down when the header takes less.
#[inline(always)]
pub(crate) fn header_room(len: usize) -> usize {
    // `len <= i32::MAX` where it matters (`WIDE_MAX`, and the writer fails longer text); the cast saturates past it.
    crate::strings::header_size(u32::try_from(len.saturating_mul(2)).unwrap_or(u32::MAX))
}

/// The room a one-pass kernel reserves for the header of text of `len` bytes (with a char above U+007F), guessed from
/// the length alone: one byte below 128 bytes, which takes the header of text under 64 bytes and of most text up to
/// 127 (fewer than 64 chars or code units: two bytes or more a char, or a Latin-1 char in every 64 bytes or more);
/// else [`header_room`]. [`finish`] moves the chars when the header takes another size. Counting the chars first
/// costs as much as the move (its loads wait for the stores just made) on every string, where the move costs only
/// the strings that take it.
#[cfg(any(target_arch = "x86_64", all(target_arch = "aarch64", target_endian = "little")))]
#[inline(always)]
pub(crate) fn kernel_room(len: usize) -> usize {
    if len < 128 {
        1
    } else {
        header_room(len)
    }
}

/// Ends a one-pass writer's output at `at`: its header (of the count of chars or of code units) in the `room` bytes
/// reserved for it, the `o` bytes written after them moved when the header takes another size, and the length.
///
/// # Safety
/// The `room` bytes from `at` (from [`kernel_room`] of the text's length `len`) and the `o` after them are
/// initialized room of `out`, `o` at most twice `i32::MAX`, and `out` has room for `room + 2 * len + 32` bytes from
/// `at`; with `ONE`, `room` is 1 and the text under 64 bytes (so `o` is at most 126 and the header one byte).
#[cfg(any(target_arch = "x86_64", all(target_arch = "aarch64", target_endian = "little")))]
#[inline(always)]
unsafe fn finish<const ONE: bool>(out: &mut Vec<u8>, at: usize, room: usize, o: usize, latin1: bool) {
    // Latin-1: `2 * count + 1`; UTF-16: `2 * units`, which is `o`. Either fits a `u32` (the text is at most
    // `WIDE_MAX` bytes, so `o / 2` and `o` (Latin-1) are at most `i32::MAX`).
    let value = if latin1 { (o as u32).wrapping_mul(2).wrapping_add(1) } else { o as u32 };
    type Block = core::mem::MaybeUninit<[u8; 64]>;
    // SAFETY: the caller's contract. The header is stored as one byte when it takes one, else as its 3 or 5 bytes:
    // stores of a constant size, never a call to `memcpy`. The chars move within the room reserved, as two blocks
    // of 64 (both read before either is written; the bytes past `o` are spare capacity, copied as `MaybeUninit`)
    // where `o <= 128`, else with `ptr::copy` (which allows the overlap):
    // - down, to a one-byte header (`o <= 126`) from a room of 3 or 5 (text of 128 bytes or more): the blocks end
    //   at `room + 1 + 128 <= room + 2 * len + 32`;
    // - up, to a three-byte header from a room of 1 (text of 64 to 127 bytes: a header of 0x80 or more is `o >= 64`
    //   Latin-1 chars or `o >= 128` bytes of code units, from as many bytes of text or more): the blocks end at
    //   `3 + 128 <= 1 + 2 * len + 32`, and `3 + o <= 1 + 2 * len + 32`;
    // - down, to a header shorter than a room of 5 (text of 32768 bytes or more).
    unsafe {
        let base = out.as_mut_ptr().add(at);
        let shift = |from: usize, to: usize| {
            let (src, dst) = (base.add(from), base.add(to));
            if o <= 128 {
                let (a, b) = (src.cast::<Block>().read_unaligned(), src.add(64).cast::<Block>().read_unaligned());
                dst.cast::<Block>().write_unaligned(a);
                dst.add(64).cast::<Block>().write_unaligned(b);
            } else {
                core::ptr::copy(src, dst, o);
            }
        };
        if ONE || value < 0x80 {
            if !ONE && room > 1 {
                shift(room, 1);
            }
            base.write(value as u8);
            out.set_len(at.wrapping_add(1).wrapping_add(o));
            return;
        }
        let (header, n) = crate::strings::header(value);
        if n != room {
            shift(room, n);
        }
        if n == 3 {
            base.cast::<[u8; 3]>().write_unaligned([header[0], header[1], header[2]]);
        } else {
            base.cast::<[u8; 5]>().write_unaligned(header);
        }
        out.set_len(at.wrapping_add(n).wrapping_add(o));
    }
}

/// For a bit set of eight lanes of `WIDTH` bytes: the shuffle that moves the lanes in the set, in order, to the front
/// (and zeros after them).
// Every index is below the array's length (k < 8, m < 256, WIDTH <= 2).
#[cfg(any(target_arch = "x86_64", all(target_arch = "aarch64", target_endian = "little")))]
#[allow(clippy::indexing_slicing, clippy::arithmetic_side_effects)]
const fn keep_shuffles<const WIDTH: usize>() -> [[u8; 16]; 256] {
    let mut table = [[0x80u8; 16]; 256];
    let mut m = 0;
    while m < 256 {
        let mut o = 0;
        let mut k = 0;
        while k < 8 {
            if m & (1 << k) != 0 {
                let mut b = 0;
                while b < WIDTH {
                    table[m][o] = (WIDTH * k + b) as u8;
                    o += 1;
                    b += 1;
                }
            }
            k += 1;
        }
        m += 1;
    }
    table
}

/// [`keep_shuffles`] for bytes (Latin-1) and for 16-bit code units.
#[cfg(any(target_arch = "x86_64", all(target_arch = "aarch64", target_endian = "little")))]
static KEEP8: [[u8; 16]; 256] = keep_shuffles::<1>();
#[cfg(any(target_arch = "x86_64", all(target_arch = "aarch64", target_endian = "little")))]
static KEEP16: [[u8; 16]; 256] = keep_shuffles::<2>();

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

// ------------------------------------------------------------------------------------------------ the reader's text

/// A `String` as its pointer and its length and capacity in one word (the length in the low half), returned in two
/// registers. A `String` returned whole goes through memory, and the reader's caller reads it back in the pieces of
/// its `Result<Option<String>>` (an [`crate::Error`] is an `i32` and a byte), which span the three stores that
/// wrote it: a store forwarding stall for every string read. Only for text of at most [`PARTS_MAX`] bytes, whose
/// `String` has fewer than 2^32 bytes of capacity.
pub(crate) struct Parts {
    ptr: *mut u8,
    sizes: u64,
}

/// The longest text (in bytes of Latin-1 or of UTF-16) read through [`Parts`]: its UTF-8 takes at most three times
/// as many bytes, fewer than 2^32.
const PARTS_MAX: usize = (u32::MAX / 3) as usize;

impl Parts {
    /// `text`'s parts (its capacity below 2^32), its buffer now owned by them.
    #[inline(always)]
    fn of(text: String) -> Self {
        let mut bytes = core::mem::ManuallyDrop::new(text.into_bytes());
        let sizes = (bytes.len() as u64) | (bytes.capacity() as u64).wrapping_shl(32);
        Self { ptr: bytes.as_mut_ptr(), sizes }
    }

    /// The `String` again.
    #[inline(always)]
    fn string(self) -> String {
        // SAFETY: the parts of a `String` (`of`), whose length and capacity are below 2^32: taken back once.
        unsafe { String::from_raw_parts(self.ptr, self.sizes as u32 as usize, (self.sizes >> 32) as usize) }
    }
}

/// Latin-1 bytes (each byte is the code point U+0000..U+00FF) as a `String`: ASCII copied as it is, other bytes as
/// two bytes of UTF-8, written straight into the room of a `Vec` that becomes the `String` unchecked. Each Latin-1
/// byte is a Unicode scalar value, so what is written is valid UTF-8. Text of up to [`PARTS_MAX`] bytes comes back
/// through [`Parts`].
#[inline(always)]
pub(crate) fn latin1_string(bytes: &[u8]) -> String {
    if bytes.len() <= PARTS_MAX {
        latin1_parts(bytes).string()
    } else {
        latin1_long(bytes)
    }
}

#[inline(never)]
fn latin1_parts(bytes: &[u8]) -> Parts {
    Parts::of(latin1_text(bytes))
}

#[inline(never)]
fn latin1_long(bytes: &[u8]) -> String {
    latin1_text(bytes)
}

/// [`latin1_string`]'s work.
#[inline(always)]
fn latin1_text(bytes: &[u8]) -> String {
    // Short ASCII (the most common text): two words, or two pairs of words, that may overlap; no call to `memcpy`.
    let len = bytes.len();
    if let (Some(first), Some(last)) = (bytes.first_chunk::<8>(), bytes.last_chunk::<8>()) {
        if len <= 16 && (u64::from_le_bytes(*first) | u64::from_le_bytes(*last)) & HIGH == 0 {
            let mut text = Vec::<u8>::with_capacity(len);
            let dst = text.spare_capacity_mut().as_mut_ptr().cast::<u8>();
            // SAFETY: `8 <= len <= 16` bytes of room: the first eight and the last eight; ASCII is valid UTF-8.
            unsafe {
                dst.cast::<[u8; 8]>().write_unaligned(*first);
                dst.add(len.wrapping_sub(8)).cast::<[u8; 8]>().write_unaligned(*last);
                text.set_len(len);
                return String::from_utf8_unchecked(text);
            }
        }
    }
    if let (Some(first), Some(last)) = (bytes.first_chunk::<16>(), bytes.last_chunk::<16>()) {
        let high = u128::from(HIGH) | (u128::from(HIGH) << 64);
        if len <= 32 && (u128::from_le_bytes(*first) | u128::from_le_bytes(*last)) & high == 0 {
            let mut text = Vec::<u8>::with_capacity(len);
            let dst = text.spare_capacity_mut().as_mut_ptr().cast::<u8>();
            // SAFETY: `16 <= len <= 32` bytes of room: the first 16 and the last 16; ASCII is valid UTF-8.
            unsafe {
                dst.cast::<[u8; 16]>().write_unaligned(*first);
                dst.add(len.wrapping_sub(16)).cast::<[u8; 16]>().write_unaligned(*last);
                text.set_len(len);
                return String::from_utf8_unchecked(text);
            }
        }
    }
    // A byte above 0x7F takes two bytes of UTF-8, so the UTF-8 takes `len + high <= 2 * len`, which cannot wrap
    // (`len <= isize::MAX`). Short text gets the most it can take, with no pass to count the bytes above 0x7F first
    // (a capacity of at most twice the length, [`SHORT_ROOM`] bytes more); longer text what it takes.
    let room = if len <= SHORT_ROOM {
        len.wrapping_mul(2)
    } else {
        let high = bytes.iter().filter(|&&b| b >= 0x80).count();
        if high == 0 {
            // SAFETY: ASCII is valid UTF-8.
            return unsafe { String::from_utf8_unchecked(bytes.to_vec()) };
        }
        len.wrapping_add(high)
    };
    let mut text = Vec::<u8>::with_capacity(room);
    let dst = text.spare_capacity_mut().as_mut_ptr().cast::<u8>();
    // SAFETY: `room` bytes of room at `dst`, at least what the bytes' UTF-8 takes.
    let o = unsafe { latin1_utf8(bytes, dst, room) };
    // SAFETY: the `o` bytes from the start are written (`o <= room`), and they are valid UTF-8 (see above).
    unsafe {
        text.set_len(o);
        String::from_utf8_unchecked(text)
    }
}

/// Up to this many bytes of Latin-1 (half as many UTF-16 code units) the reader's `String` gets room for the longest
/// UTF-8 the text can take, so no pass counts it first: at most this many bytes of capacity more than the text needs
/// (on x86_64 the count cost more than the larger allocation: lists of mixed text read 14% faster without it).
const SHORT_ROOM: usize = 64;

/// Below this many bytes of input the 16-byte decoders (SSSE3, SSE4.1, NEON) lose to the plain one: their setup and
/// last partial vector cost more than they save. AVX-512's masked loads and stores have no last partial vector.
#[cfg(any(target_arch = "x86_64", all(target_arch = "aarch64", target_endian = "little")))]
const SHORT_DECODE: usize = 32;

/// [`SHORT_DECODE`] for UTF-16, in code units (measured on x86_64 with SSE4.1 and on Neoverse-N2).
#[cfg(any(target_arch = "x86_64", all(target_arch = "aarch64", target_endian = "little")))]
const SHORT_UTF16: usize = 24;

/// Writes the UTF-8 of the Latin-1 `bytes` at `dst` with the kernel this target has; returns the bytes written.
///
/// # Safety
/// `room` bytes are writable at `dst`, at least the UTF-8's length (one byte per byte, two above 0x7F).
#[inline(always)]
#[cfg_attr(
    not(any(target_arch = "x86_64", all(target_arch = "aarch64", target_endian = "little"))),
    allow(unused_variables) // `room` is for the vector kernels.
)]
unsafe fn latin1_utf8(bytes: &[u8], dst: *mut u8, room: usize) -> usize {
    // SAFETY: the caller's contract, and the CPU has what each kernel needs (its tier).
    unsafe {
        #[cfg(target_arch = "x86_64")]
        match x86::tier() {
            #[cfg(nino_avx512)]
            x86::Tier::Avx512 => x86::latin1_avx512(bytes, dst),
            _ if bytes.len() < SHORT_DECODE => latin1_words(bytes, 0, dst, 0),
            x86::Tier::Sse2 => latin1_words(bytes, 0, dst, 0),
            _ => x86::latin1_ssse3(bytes, dst, room),
        }
        #[cfg(all(target_arch = "aarch64", target_endian = "little"))]
        return if bytes.len() < SHORT_DECODE {
            latin1_words(bytes, 0, dst, 0)
        } else {
            neon::latin1(bytes, dst, room)
        };
        #[cfg(not(any(target_arch = "x86_64", all(target_arch = "aarch64", target_endian = "little"))))]
        return latin1_words(bytes, 0, dst, 0);
    }
}

/// Writes the UTF-8 of the Latin-1 `bytes[k..]` at `dst + o`, eight bytes a word while they are ASCII; returns the
/// offset after it. The plain decoder, and the vector kernels' last bytes.
///
/// # Safety
/// The room from `dst + o` holds the UTF-8 of `bytes[k..]` (one byte per byte, two above 0x7F); `k <= bytes.len()`.
#[inline(always)]
unsafe fn latin1_words(bytes: &[u8], k: usize, dst: *mut u8, mut o: usize) -> usize {
    // SAFETY: `k <= len`.
    let bytes = unsafe { bytes.get_unchecked(k..) };
    // Invariant: the room from `dst + o` holds the UTF-8 of what is left (at least one byte a byte), so each store
    // below fits.
    let (words, tail) = bytes.as_chunks::<8>();
    for w in words {
        let word = u64::from_le_bytes(*w);
        if word & HIGH == 0 {
            // SAFETY: eight bytes of input left from here, so at least eight bytes of room (the invariant).
            unsafe { dst.add(o).cast::<[u8; 8]>().write_unaligned(*w) };
            o = o.wrapping_add(8);
            continue;
        }
        for &b in w {
            // SAFETY: the invariant.
            o = unsafe { latin1_char(dst, o, b) };
        }
    }
    for &b in tail {
        // SAFETY: the invariant.
        o = unsafe { latin1_char(dst, o, b) };
    }
    o
}

/// Writes the UTF-8 of the Latin-1 byte `b` at `dst + o`; returns the offset after it.
///
/// # Safety
/// One byte (two for `b >= 0x80`) is writable at `dst + o`.
#[inline(always)]
unsafe fn latin1_char(dst: *mut u8, o: usize, b: u8) -> usize {
    // SAFETY: the caller's contract. `o + 2` cannot wrap: it is within an allocation.
    unsafe {
        if b < 0x80 {
            dst.add(o).write(b);
            return o.wrapping_add(1);
        }
        dst.add(o).write(0xC0 | (b >> 6));
        dst.add(o).add(1).write(0x80 | (b & 0x3F));
    }
    o.wrapping_add(2)
}

/// UTF-16 LE code units as a `String`, each unpaired surrogate as U+FFFD (as `char::decode_utf16` with the
/// replacement char): written straight into the room of a `Vec` that becomes the `String` unchecked. Every sequence
/// written is the UTF-8 of a Unicode scalar value (a code unit outside the surrogates, a valid surrogate pair or
/// U+FFFD), so the whole is valid UTF-8.
///
/// Text of up to [`PARTS_MAX`] bytes (half as many units) comes back through [`Parts`].
#[inline(always)]
pub(crate) fn utf16_string(units: &[[u8; 2]]) -> String {
    if units.len() <= PARTS_MAX / 2 {
        utf16_parts(units).string()
    } else {
        utf16_long(units)
    }
}

#[inline(never)]
fn utf16_parts(units: &[[u8; 2]]) -> Parts {
    Parts::of(utf16_text(units))
}

#[inline(never)]
fn utf16_long(units: &[[u8; 2]]) -> String {
    utf16_text(units)
}

/// [`utf16_string`]'s work.
#[inline(always)]
fn utf16_text(units: &[[u8; 2]]) -> String {
    // Room: one byte for a unit below U+0080, two below U+0800, three for the rest. A surrogate pair takes four
    // bytes (six counted), an unpaired surrogate three (U+FFFD). At most `3 * len`, which cannot wrap (`2 * len`
    // bytes exist): what short text gets, with no pass to count first (a capacity of at most three times the units,
    // [`SHORT_ROOM`] bytes more than two bytes a unit); longer text what its units count.
    let room = if units.len() <= SHORT_ROOM / 2 {
        units.len().wrapping_mul(3)
    } else {
        units.iter().fold(0usize, |n, u| {
            let u = u16::from_le_bytes(*u);
            n.wrapping_add(1).wrapping_add(usize::from(u >= 0x80)).wrapping_add(usize::from(u >= 0x800))
        })
    };
    let mut text = Vec::<u8>::with_capacity(room);
    let dst = text.spare_capacity_mut().as_mut_ptr().cast::<u8>();
    // SAFETY: `room` bytes of room at `dst`, what the units count.
    let o = unsafe { utf16_utf8(units, dst, room) };
    // SAFETY: the `o` bytes from the start are written (`o <= room`), and they are valid UTF-8 (see above).
    unsafe {
        text.set_len(o);
        String::from_utf8_unchecked(text)
    }
}

/// Writes the UTF-8 of the UTF-16 `units` at `dst` with the kernel this target has; returns the bytes written.
///
/// # Safety
/// `room` bytes are writable at `dst`, at least what the units count (see [`utf16_string`]).
#[inline(always)]
#[cfg_attr(
    not(any(target_arch = "x86_64", all(target_arch = "aarch64", target_endian = "little"))),
    allow(unused_variables) // `room` is for the vector kernels.
)]
unsafe fn utf16_utf8(units: &[[u8; 2]], dst: *mut u8, room: usize) -> usize {
    // SAFETY: the caller's contract, and the CPU has what each kernel needs (its tier).
    unsafe {
        #[cfg(target_arch = "x86_64")]
        match x86::tier() {
            #[cfg(nino_avx512)]
            x86::Tier::Avx512 => x86::utf16_avx512(units, dst),
            _ if units.len() < SHORT_UTF16 => utf16_words(units, 0, units.len(), dst, 0).1,
            x86::Tier::Sse2 | x86::Tier::Ssse3 => utf16_words(units, 0, units.len(), dst, 0).1,
            _ => x86::utf16_sse41(units, dst, room),
        }
        #[cfg(all(target_arch = "aarch64", target_endian = "little"))]
        return if units.len() < SHORT_UTF16 {
            utf16_words(units, 0, units.len(), dst, 0).1
        } else {
            neon::utf16(units, dst, room)
        };
        #[cfg(not(any(target_arch = "x86_64", all(target_arch = "aarch64", target_endian = "little"))))]
        return utf16_words(units, 0, units.len(), dst, 0).1;
    }
}

/// Writes the UTF-8 of `units[k..]` at `dst + o` until `end` (a surrogate pair at `end - 1` is taken whole), four
/// units a word while they are ASCII or two-byte; returns the unit and the offset after them. The plain decoder, and
/// the vector kernels' last units and units with surrogates.
///
/// # Safety
/// The room from `dst + o` holds what `units[k..]` count (see [`utf16_string`]).
#[inline(always)]
unsafe fn utf16_words(units: &[[u8; 2]], mut k: usize, end: usize, dst: *mut u8, mut o: usize) -> (usize, usize) {
    // Invariant: the room left is at least what the units from `k` count, so each write below fits.
    while k < end {
        let Some(&[a, b]) = units.get(k) else { break };
        // Four units of ASCII at a time, while four are left.
        if let Some(four) = units.get(k..k.wrapping_add(4)).and_then(|q| q.as_flattened().first_chunk::<8>()) {
            let w = u64::from_le_bytes(*four);
            if w & 0xFF80_FF80_FF80_FF80 == 0 {
                let packed = (w & 0xFF) | ((w >> 8) & 0xFF00) | ((w >> 16) & 0xFF_0000) | ((w >> 24) & 0xFF00_0000);
                // SAFETY: four units below U+0080 count four bytes of room.
                unsafe { dst.add(o).cast::<[u8; 4]>().write_unaligned((packed as u32).to_le_bytes()) };
                k = k.wrapping_add(4);
                o = o.wrapping_add(4);
                continue;
            }
            // Four units of U+0080..U+07FF (Greek, Cyrillic, Hebrew, Arabic, ...): each `0x80 <= u < 0x800`, so
            // bits 11..15 clear and some of bits 7..10 set.
            let lanes = w & 0x0780_0780_0780_0780;
            if w & 0xF800_F800_F800_F800 == 0
                && lanes.wrapping_add(0x7FFF_7FFF_7FFF_7FFF) & 0x8000_8000_8000_8000 == HIGH16
            {
                // Each unit as a lead byte (its five high bits) then a continuation byte (its six low bits).
                let lead = 0x00C0_00C0_00C0_00C0 | ((w >> 6) & 0x001F_001F_001F_001F);
                let cont = 0x0080_0080_0080_0080 | (w & 0x003F_003F_003F_003F);
                let pairs = (lead | (cont << 8)).to_le_bytes();
                // SAFETY: four units of two bytes each count eight bytes of room.
                unsafe { dst.add(o).cast::<[u8; 8]>().write_unaligned(pairs) };
                k = k.wrapping_add(4);
                o = o.wrapping_add(8);
                continue;
            }
        }
        let u = u32::from(u16::from_le_bytes([a, b]));
        k = k.wrapping_add(1);
        // SAFETY: each write is within the room the unit (and for a pair, the next one) counts, as said beside it.
        unsafe {
            if u < 0x80 {
                // One byte counted.
                dst.add(o).write(u as u8);
                o = o.wrapping_add(1);
            } else if u < 0x800 {
                // Two bytes counted.
                dst.add(o).cast::<[u8; 2]>().write_unaligned([0xC0 | (u >> 6) as u8, 0x80 | (u & 0x3F) as u8]);
                o = o.wrapping_add(2);
            } else if u & 0xF800 != 0xD800 {
                // Three bytes counted.
                let bytes = [0xE0 | (u >> 12) as u8, 0x80 | ((u >> 6) & 0x3F) as u8, 0x80 | (u & 0x3F) as u8];
                dst.add(o).cast::<[u8; 3]>().write_unaligned(bytes);
                o = o.wrapping_add(3);
            } else {
                let low = units.get(k).map_or(0, |&n| u32::from(u16::from_le_bytes(n)));
                if u < 0xDC00 && low & 0xFC00 == 0xDC00 {
                    // A pair: six bytes counted, four written.
                    let c = (((u & 0x3FF) << 10) | (low & 0x3FF)).wrapping_add(0x1_0000);
                    let bytes = [
                        0xF0 | (c >> 18) as u8,
                        0x80 | ((c >> 12) & 0x3F) as u8,
                        0x80 | ((c >> 6) & 0x3F) as u8,
                        0x80 | (c & 0x3F) as u8,
                    ];
                    dst.add(o).cast::<[u8; 4]>().write_unaligned(bytes);
                    k = k.wrapping_add(1);
                    o = o.wrapping_add(4);
                } else {
                    // Unpaired: U+FFFD, three bytes counted.
                    dst.add(o).cast::<[u8; 3]>().write_unaligned([0xEF, 0xBF, 0xBD]);
                    o = o.wrapping_add(3);
                }
            }
        }
    }
    (k, o)
}

/// For each bit set of eight bytes (bit `k` for byte `k` above 0x7F), given 16-bit lanes of a lead byte (low) and
/// a continuation byte (high): the shuffle that keeps each lane's lead byte and, for the bytes in the set, its
/// continuation byte, in order; and the bytes kept. Latin-1 and UTF-16 below U+0800 to UTF-8.
#[cfg(any(target_arch = "x86_64", all(target_arch = "aarch64", target_endian = "little")))]
static EXPAND: [[u8; 16]; 256] = expand_shuffles();

// Every index is below the array's length (o < 16, m < 256).
#[cfg(any(target_arch = "x86_64", all(target_arch = "aarch64", target_endian = "little")))]
#[allow(clippy::indexing_slicing, clippy::arithmetic_side_effects)]
const fn expand_shuffles() -> [[u8; 16]; 256] {
    let mut table = [[0x80u8; 16]; 256];
    let mut m = 0;
    while m < 256 {
        let (mut o, mut k) = (0, 0);
        while k < 8 {
            table[m][o] = 2 * k as u8;
            o += 1;
            if m & (1 << k) != 0 {
                table[m][o] = 2 * k as u8 + 1;
                o += 1;
            }
            k += 1;
        }
        m += 1;
    }
    table
}

/// For four 32-bit lanes of a code unit's UTF-8 (one, two or three bytes from the lane's lowest), indexed by two bits
/// a unit (bit `2k` for unit `k` at least U+0080, bit `2k + 1` at least U+0800): the shuffle that keeps each lane's
/// bytes, in order, and how many it keeps.
#[cfg(any(target_arch = "x86_64", all(target_arch = "aarch64", target_endian = "little")))]
static PACK3: [([u8; 16], u8); 256] = pack3_shuffles();

// Every index is below the array's length (o < 16, m < 256).
#[cfg(any(target_arch = "x86_64", all(target_arch = "aarch64", target_endian = "little")))]
#[allow(clippy::indexing_slicing, clippy::arithmetic_side_effects)]
const fn pack3_shuffles() -> [([u8; 16], u8); 256] {
    let mut table = [([0x80u8; 16], 0u8); 256];
    let mut m = 0;
    while m < 256 {
        let (mut o, mut k) = (0, 0);
        while k < 4 {
            let bytes = 1 + ((m >> (2 * k)) & 1) + ((m >> (2 * k + 1)) & 1);
            let mut b = 0;
            while b < bytes {
                table[m].0[o] = (4 * k + b) as u8;
                o += 1;
                b += 1;
            }
            k += 1;
        }
        table[m].1 = o as u8;
        m += 1;
    }
    table
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
            if x86::tier() >= x86::Tier::Ssse3 {
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

    /// Every one-pass writer this CPU can run (each tier's, not only the best).
    #[allow(clippy::vec_init_then_push)] // Which pushes run depends on the target.
    fn one_pass() -> Vec<(&'static str, Writer)> {
        #[allow(unused_mut)]
        let mut kernels: Vec<(&'static str, Writer)> = vec![];
        #[cfg(target_arch = "x86_64")]
        {
            if x86::tier() >= x86::Tier::Sse41 {
                // SAFETY: the CPU has SSE4.1 (its tier), and the text is short.
                kernels.push(("sse4.1", |out, text| unsafe {
                    if text.len() < 64 {
                        x86::wide_sse41::<true>(out, text)
                    } else {
                        x86::wide_sse41::<false>(out, text)
                    }
                }));
                // SAFETY: as above; the copy for any length, on short text too.
                kernels.push(("sse4.1 (any length)", |out, text| unsafe { x86::wide_sse41::<false>(out, text) }));
            }
            if x86::tier() >= x86::Tier::Avx2 {
                // SAFETY: the CPU has AVX2 (its tier), and the text is short.
                kernels.push(("avx2", |out, text| unsafe {
                    if text.len() < 64 {
                        x86::wide_avx2::<true>(out, text)
                    } else {
                        x86::wide_avx2::<false>(out, text)
                    }
                }));
                // SAFETY: as above; the copy for any length, on short text too.
                kernels.push(("avx2 (any length)", |out, text| unsafe { x86::wide_avx2::<false>(out, text) }));
            }
            #[cfg(nino_avx512)]
            if x86::tier() >= x86::Tier::Avx512 {
                // SAFETY: the CPU has AVX-512 (its tier), and the text is short.
                kernels.push(("avx-512", |out, text| unsafe {
                    if text.len() < 64 {
                        x86::wide_avx512::<true>(out, text)
                    } else {
                        x86::wide_avx512::<false>(out, text)
                    }
                }));
                // SAFETY: as above; the copy for any length, on short text too.
                kernels.push(("avx-512 (any length)", |out, text| unsafe { x86::wide_avx512::<false>(out, text) }));
            }
        }
        #[cfg(all(target_arch = "aarch64", target_endian = "little"))]
        kernels.push(("neon", neon::wide));
        kernels
    }

    /// A one-pass writer.
    type Writer = fn(&mut Vec<u8>, &str);
    type Latin1Way = unsafe fn(&[u8], *mut u8, usize) -> usize;
    type Ways<T> = Vec<(&'static str, T)>;
    type Utf16Way = unsafe fn(&[[u8; 2]], *mut u8, usize) -> usize;

    /// Every reader's decoder this CPU can run: the plain ones and each tier's kernels.
    fn decoder_ways() -> (Ways<Latin1Way>, Ways<Utf16Way>) {
        #[allow(unused_mut)]
        let mut latin1: Ways<Latin1Way> = vec![(
            "words",
            // SAFETY: the caller's room is the UTF-8's length.
            |b, d, _| unsafe { latin1_words(b, 0, d, 0) },
        )];
        #[allow(unused_mut)]
        let mut utf16: Ways<Utf16Way> = vec![(
            "words",
            // SAFETY: the caller's room is what the units count.
            |u, d, _| unsafe { utf16_words(u, 0, u.len(), d, 0).1 },
        )];
        #[cfg(target_arch = "x86_64")]
        {
            if x86::tier() >= x86::Tier::Ssse3 {
                // SAFETY: the CPU has SSSE3 (its tier); the caller's room.
                latin1.push(("ssse3", |b, d, r| unsafe { x86::latin1_ssse3(b, d, r) }));
            }
            if x86::tier() >= x86::Tier::Sse41 {
                // SAFETY: the CPU has SSE4.1 (its tier); the caller's room.
                utf16.push(("sse4.1", |u, d, r| unsafe { x86::utf16_sse41(u, d, r) }));
            }
            #[cfg(nino_avx512)]
            if x86::tier() >= x86::Tier::Avx512 {
                // SAFETY: the CPU has AVX-512 (its tier); the caller's room.
                latin1.push(("avx-512", |b, d, _| unsafe { x86::latin1_avx512(b, d) }));
                // SAFETY: as above.
                utf16.push(("avx-512", |u, d, _| unsafe { x86::utf16_avx512(u, d) }));
            }
        }
        #[cfg(all(target_arch = "aarch64", target_endian = "little"))]
        {
            latin1.push(("neon", neon::latin1));
            utf16.push(("neon", neon::utf16));
        }
        (latin1, utf16)
    }

    fn check(text: &str) {
        let expected = reference(text);
        // The ways that write the header too: the header, then the same bytes.
        let count = if expected.1 { expected.0.len() } else { expected.0.len() / 2 };
        let mut want = vec![0xAA; 3];
        let (header, n) = crate::strings::header((count as u32).wrapping_mul(2).wrapping_add(u32::from(expected.1)));
        want.extend_from_slice(header.get(..n).unwrap_or_default());
        want.extend_from_slice(&expected.0);
        if text.len() <= SHORT_WIDE && !text.is_ascii() {
            let mut out = vec![0xAA; 3];
            short_wide(&mut out, text);
            assert_eq!(out, want, "short_wide: {text:?}");
        }
        if !text.is_ascii() {
            let mut out = vec![0xAA; 3];
            if wide(&mut out, text) {
                assert_eq!(out, want, "wide: {text:?}");
            } else {
                assert_eq!(out, [0xAA; 3], "wide: {text:?}");
            }
        }
        for (name, kernel) in one_pass() {
            // ASCII text too, which `wide` is not given.
            let mut out = vec![0xAA; 3];
            kernel(&mut out, text);
            assert_eq!(out, want, "{name}: {text:?}");
        }
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

    /// Every length up to 256 chars with every char class at every position. Under Miri, which is slow, the lengths
    /// around a block's and a word's edges and the pair steps' threshold, at the first, a middle and the last
    /// position.
    fn shapes() -> Vec<(usize, Vec<usize>)> {
        if cfg!(miri) {
            [0, 1, 2, 7, 8, 9, 15, 16, 17, 23, 24, 31, 32, 33, 47, 48]
                .into_iter()
                .map(|len: usize| (len, if len == 0 { vec![] } else { vec![0, len / 2, len.saturating_sub(1)] }))
                .collect()
        } else {
            (0..=256).map(|len| (len, (0..len).collect())).collect()
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

    /// Every way `finish` places a one-pass writer's chars: below 64 bytes, from 64 to 127 with a one-byte header and
    /// moved up for a three-byte one (two blocks, or a copy past 128 bytes of output), and from 128 bytes left in
    /// place or moved down for a one-byte header (under 64 code units in 128 to 255 bytes). Lengths at each edge.
    #[test]
    fn header_moves() {
        let mixed = |ascii: usize, c: char, n: usize| -> String {
            core::iter::repeat_n('a', ascii).chain(core::iter::repeat_n(c, n)).collect()
        };
        for text in [
            mixed(62, 'é', 1),   // 63 chars in 64 bytes: one-byte header
            mixed(62, 'é', 2),   // 64 chars in 66 bytes: Latin-1, moved up
            mixed(125, 'é', 1),  // 126 chars in 127 bytes: moved up
            mixed(30, 'é', 34),  // 64 chars in 98 bytes
            mixed(63, '字', 1),  // 64 code units in 66 bytes: UTF-16, moved up (128 bytes)
            mixed(64, '字', 1),  // 65 code units: moved up as a copy (130 bytes)
            mixed(120, '字', 2), // 122 code units in 126 bytes
            mixed(0, '字', 42),  // 42 code units in 126 bytes: in place
            mixed(0, '字', 43),  // 43 code units in 129 bytes: moved down
            mixed(1, '字', 63),  // 64 code units in 190 bytes: in place
            mixed(0, '😀', 31),  // 62 code units in 124 bytes: in place
            mixed(0, '😀', 32),  // 64 code units in 128 bytes: in place
            mixed(0, '字', 85),  // 255 bytes
            mixed(0, 'é', 64),   // 128 bytes
        ] {
            check(&text);
        }
    }

    /// The reader's decoders against the plain ones, for every length up to 256 code units (or bytes) with each class
    /// at each position (fewer under Miri), and random input: unpaired, reversed and split surrogates included.
    #[test]
    fn decoders() {
        let (latin1_ways, utf16_ways) = decoder_ways();
        let latin1 = |bytes: &[u8]| {
            let want = bytes.iter().map(|&b| char::from(b)).collect::<String>();
            assert_eq!(latin1_string(bytes), want, "{bytes:?}");
            let room = bytes.len() + bytes.iter().filter(|&&b| b >= 0x80).count();
            for (name, way) in &latin1_ways {
                let mut text = Vec::<u8>::with_capacity(room);
                // SAFETY: `room` bytes of room, the UTF-8's length.
                let o = unsafe { way(bytes, text.as_mut_ptr(), room) };
                assert!(o <= room, "{name}: {bytes:?}");
                // SAFETY: `o` bytes written.
                unsafe { text.set_len(o) };
                assert_eq!(text, want.as_bytes(), "{name}: {bytes:?}");
            }
        };
        let utf16 = |units: &[u16]| {
            let le: Vec<[u8; 2]> = units.iter().map(|u| u.to_le_bytes()).collect();
            let want = String::from_utf16_lossy(units);
            assert_eq!(utf16_string(&le), want, "{units:?}");
            let room: usize = units.iter().map(|&u| 1 + usize::from(u >= 0x80) + usize::from(u >= 0x800)).sum();
            for (name, way) in &utf16_ways {
                let mut text = Vec::<u8>::with_capacity(room);
                // SAFETY: `room` bytes of room, what the units count.
                let o = unsafe { way(&le, text.as_mut_ptr(), room) };
                assert!(o <= room, "{name}: {units:?}");
                // SAFETY: `o` bytes written.
                unsafe { text.set_len(o) };
                assert_eq!(text, want.as_bytes(), "{name}: {units:?}");
            }
        };
        let bytes: [u8; 6] = [b'a', 0x7F, 0x80, 0xBF, 0xE9, 0xFF];
        let units: [u16; 12] = [0x61, 0x7F, 0x80, 0x7FF, 0x800, 0xD7FF, 0xD800, 0xDBFF, 0xDC00, 0xDFFF, 0xE000, 0xFFFF];
        let lengths: Vec<usize> = if cfg!(miri) { vec![0, 1, 3, 4, 5, 7, 8, 9, 16, 17] } else { (0..=256).collect() };
        for &len in &lengths {
            let positions: Vec<usize> =
                if cfg!(miri) { [0, len / 2, len.saturating_sub(1)].into() } else { (0..len).collect() };
            for &filler in &bytes {
                latin1(&vec![filler; len]);
                for &at in &positions {
                    for &b in &bytes {
                        latin1(&(0..len).map(|i| if i == at { b } else { filler }).collect::<Vec<_>>());
                    }
                }
            }
            for &filler in &[0x61u16, 0xE9, 0x416, 0x5B57] {
                utf16(&vec![filler; len]);
                for &at in &positions {
                    for &u in &units {
                        utf16(&(0..len).map(|i| if i == at { u } else { filler }).collect::<Vec<_>>());
                        // A pair or a reversed one at `at`.
                        let mut v: Vec<u16> = vec![filler; len.max(2)];
                        let pair = [u, if u < 0xDC00 { 0xDC00 | (u & 0x3FF) } else { 0xD800 | (u & 0x3FF) }];
                        if let Some(two) = v.get_mut(at.min(len.max(2) - 2)..).and_then(|t| t.first_chunk_mut::<2>()) {
                            *two = pair;
                        }
                        utf16(&v);
                    }
                }
            }
        }
        let mut seed = 0x2545_F491_4F6C_DD1Du64;
        let mut next = move || {
            seed ^= seed << 13;
            seed ^= seed >> 7;
            seed ^= seed << 17;
            seed
        };
        for _ in 0..if cfg!(miri) { 20 } else { 20_000 } {
            let len = (next() % 300) as usize;
            let raw: Vec<u8> = (0..len).map(|_| next() as u8 & if next() & 1 == 0 { 0x7F } else { 0xFF }).collect();
            latin1(&raw);
            let wide: Vec<u16> = (0..len)
                .map(|_| {
                    let r = next();
                    let class = units.get((r % 13) as usize).copied().unwrap_or(0x41);
                    class ^ ((r >> 8) as u16 & if class < 0x80 { 0x1F } else { 0x3FF })
                })
                .collect();
            utf16(&wide);
        }
    }

    #[test]
    fn two_and_short_bytes() {
        // Every length up to SHORT and past it, after a prefix: the head, then the bytes, or nothing.
        for len in 0..=SHORT + 2 {
            let bytes: Vec<u8> = (0..len).map(|i| 0x80 | i as u8).collect();
            let mut out = vec![0xAA; 3];
            let done = two_and_short(&mut out, [7, 9], &bytes);
            if len <= SHORT {
                let mut expected = vec![0xAA, 0xAA, 0xAA, 7, 9];
                expected.extend_from_slice(&bytes);
                assert!(done, "{len}");
                assert_eq!(out, expected, "{len}");
            } else {
                assert!(!done, "{len}");
                assert_eq!(out, [0xAA; 3], "{len}");
            }
        }
    }

    #[test]
    fn encoded_blocks() {
        // Every count up to 40, after output of every length up to 9 with and without spare room: what the output
        // held, the head, then the bytes of each value in order.
        for count in 0..=40u32 {
            let values: Vec<u32> = (0..count).map(|i| i.wrapping_mul(0x0102_0304) ^ 0xA5).collect();
            for held in 0..=9 {
                for room in [0, 7] {
                    let mut out = Vec::with_capacity(held + room);
                    out.extend((0..held).map(|i| i as u8));
                    let mut expected = out.clone();
                    expected.extend([0xEE; 3]);
                    expected.extend(values.iter().flat_map(|v| v.to_le_bytes()));
                    extend_encoded(&mut out, [0xEE; 3], &values, |v| v.to_le_bytes());
                    assert_eq!(out, expected, "{count} {held} {room}");
                }
            }
        }
    }

    #[test]
    fn short_ascii() {
        // Every length up to SHORT and past it, all ASCII and with a non-ASCII char at each position, after a prefix.
        for len in 0..=SHORT + 2 {
            for at in (0..len).map(Some).chain([None]) {
                let text: String =
                    (0..len).map(|i| if Some(i) == at { 'é' } else { char::from(b'a' + i as u8) }).collect();
                let mut out = vec![0xAA; 3];
                let done = ascii_short(&mut out, text.as_bytes());
                if text.len() <= SHORT && text.is_ascii() {
                    let mut expected = vec![0xAA; 3];
                    expected.push((2 * len + 1) as u8);
                    expected.extend_from_slice(text.as_bytes());
                    assert!(done, "{text:?}");
                    assert_eq!(out, expected, "{text:?}");
                } else {
                    assert!(!done, "{text:?}");
                    assert_eq!(out, [0xAA; 3], "{text:?}");
                }
            }
        }
    }

    #[test]
    fn ascii_lists() {
        // Lists of strings of every length up to past SHORT, ASCII or not, longer than a run: what each written on
        // its own gives.
        let texts: Vec<String> = (0..=SHORT + 2)
            .flat_map(|len| {
                let ascii: String = (0..len).map(|i| char::from(b'a' + (i % 26) as u8)).collect();
                let mut latin1 = ascii.clone();
                latin1.push('é');
                [ascii, latin1]
            })
            .collect();
        let rounds = if cfg!(miri) { 3 } else { 40 };
        for round in 0..rounds {
            let values: Vec<String> = texts.iter().cycle().skip(round * 7).take(RUN * 2 + round).cloned().collect();
            let mut out = vec![0xAA; 3];
            let mut rest = values.as_slice();
            while !rest.is_empty() {
                let (done, run) = ascii_strings(&mut out, rest);
                assert!(done <= run && run <= RUN);
                rest = rest.get(done..).unwrap_or_default();
                if done < run {
                    let Some(first) = rest.first() else { break };
                    let mut expected = vec![(2 * first.chars().count() + 1) as u8];
                    expected.extend(first.chars().map(|c| c as u32 as u8));
                    assert!(!(first.len() <= SHORT && first.is_ascii()), "{first:?}");
                    out.extend_from_slice(&expected);
                    rest = rest.get(1..).unwrap_or_default();
                }
            }
            let mut expected = vec![0xAA; 3];
            for v in &values {
                expected.push((2 * v.chars().count() + 1) as u8);
                expected.extend(v.chars().map(|c| c as u32 as u8));
            }
            assert_eq!(out, expected);
        }
    }

    /// A string's bytes in the default encoding, header first, from the plain decoder.
    fn encoded(text: &str) -> Vec<u8> {
        let (bytes, latin1) = reference(text);
        let count = if latin1 { bytes.len() } else { bytes.len() / 2 };
        let (header, n) = crate::strings::header((count as u32).wrapping_mul(2).wrapping_add(u32::from(latin1)));
        let mut out = header.get(..n).unwrap_or_default().to_vec();
        out.extend_from_slice(&bytes);
        out
    }

    /// A list loop: its name, the loop, and the longest string it must take.
    type ListWay = (&'static str, fn(&mut Vec<u8>, &[String]) -> usize, usize);

    /// Every list loop this CPU can run, with the longest string it must take: the plain one, each x86_64 tier's
    /// (which [`strings`] picks from), and [`strings`] itself.
    #[allow(clippy::vec_init_then_push)] // Which pushes run depends on the target.
    fn list_ways() -> Vec<ListWay> {
        #[allow(unused_mut)]
        let mut ways: Vec<ListWay> = vec![("short", strings_short, SHORT), ("default", strings, SHORT)];
        #[cfg(target_arch = "x86_64")]
        {
            if x86::tier() >= x86::Tier::Sse41 {
                // SAFETY: the CPU has SSE4.1 (its tier).
                ways.push(("sse4.1", |out, values| unsafe { x86::strings_sse41(out, values) }, 63));
            }
            if x86::tier() >= x86::Tier::Avx2 {
                // SAFETY: the CPU has AVX2 (its tier).
                ways.push(("avx2", |out, values| unsafe { x86::strings_avx2(out, values) }, 63));
            }
            #[cfg(nino_avx512)]
            if x86::tier() >= x86::Tier::Avx512 {
                // SAFETY: the CPU has AVX-512 (its tier).
                ways.push(("avx-512", |out, values| unsafe { x86::strings_avx512(out, values) }, 63));
            }
        }
        ways
    }

    #[test]
    fn list_kernels() {
        // Lists of every class of text at every length up to past 64 bytes (fewer under Miri), mixed: what each string
        // written on its own gives, every list loop stopping only at a string longer than it must take.
        let lengths = if cfg!(miri) { vec![0, 1, 7, 15, 16, 31, 32, 33, 63, 64, 70] } else { (0..=70).collect() };
        let texts: Vec<String> = lengths
            .iter()
            .flat_map(|&len| {
                ['a', 'é', 'Ж', '字', '😀'].map(|c| {
                    let mut text: String = (0..len).map(|i| char::from(b'a' + (i % 26) as u8)).collect();
                    if c != 'a' {
                        // The char last, the text cut back to about `len` bytes.
                        while !text.is_empty() && text.len() + c.len_utf8() > len.max(c.len_utf8()) {
                            text.pop();
                        }
                        text.push(c);
                    }
                    text
                })
            })
            .collect();
        let rounds = if cfg!(miri) { 2 } else { 30 };
        for (name, way, most) in list_ways() {
            for round in 0..rounds {
                let values: Vec<String> =
                    texts.iter().cycle().skip(round * 11).take(texts.len() + round).cloned().collect();
                let mut out = vec![0xAA; 3];
                let mut rest = values.as_slice();
                while !rest.is_empty() {
                    let done = way(&mut out, rest);
                    assert!(done <= rest.len(), "{name}");
                    rest = rest.get(done..).unwrap_or_default();
                    let Some((first, tail)) = rest.split_first() else { break };
                    assert!(first.len() > most, "{name}: {first:?}");
                    out.extend_from_slice(&encoded(first));
                    rest = tail;
                }
                let mut expected = vec![0xAA; 3];
                for v in &values {
                    expected.extend_from_slice(&encoded(v));
                }
                assert_eq!(out, expected, "{name}");
            }
        }
    }

    #[test]
    fn ascii_blocks() {
        // Every length, all ASCII and with a byte of 0x80 or above at each position, the bytes past the text either.
        for n in 0..=17 {
            for at in (0..16).map(Some).chain([None]) {
                let mut block = [0u8; 16];
                for (i, b) in block.iter_mut().enumerate() {
                    *b = if Some(i) == at { 0xE9 } else { b'a' + i as u8 };
                }
                let got = ascii_block(&block, n);
                let text = block.get(..n.min(16)).unwrap_or_default();
                if (1..=16).contains(&n) && text.is_ascii() {
                    assert_eq!(got.as_deref().map(str::as_bytes), Some(text), "{n} {at:?}");
                } else {
                    assert_eq!(got, None, "{n} {at:?}");
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

    /// Bytes from a xorshift: `len` of them, a share of them set to `bad` (0 for none) so decoders fail at random
    #[allow(clippy::indexing_slicing, clippy::arithmetic_side_effects)]
    /// places.
    fn random_bytes(seed: &mut u64, len: usize, bad: u8) -> Vec<u8> {
        (0..len)
            .map(|_| {
                *seed ^= *seed << 13;
                *seed ^= *seed >> 7;
                *seed ^= *seed << 17;
                if bad != 0 && seed.is_multiple_of(97) {
                    bad
                } else {
                    (*seed >> 24) as u8
                }
            })
            .collect()
    }

    /// `decode_block` against `collect` into a `Result` for every count to 256, values that own memory (Miri checks
    #[allow(clippy::indexing_slicing, clippy::arithmetic_side_effects)]
    /// none leaks or is dropped twice when a decode fails partway), random input and failures at random places.
    #[test]
    fn decode_block_matches_plain() {
        let decode = |c: &[u8; 3]| -> crate::error::Result<String> {
            if c.first() == Some(&0xFF) {
                return Err(crate::error::Error::new(crate::error::ErrorKind::InvalidBool, c.len() as i32));
            }
            Ok(c.iter().map(|&b| char::from(b)).collect())
        };
        let mut seed = 0x2545_F491_4F6C_DD1Du64;
        let counts: Vec<usize> = if cfg!(miri) { vec![0, 1, 2, 7, 33] } else { (0..=256).collect() };
        for n in counts {
            for bad in [0, 0xFF] {
                let bytes = random_bytes(&mut seed, n * 3, bad);
                let plain: crate::error::Result<Vec<String>> = bytes.as_chunks::<3>().0.iter().map(decode).collect();
                assert_eq!(decode_block(bytes.as_chunks::<3>().0.iter(), decode), plain, "{n}");
            }
        }
    }

    /// `extend_fixed` against encoding each value into a zeroed buffer and appending it, for every count to 256,
    #[allow(clippy::indexing_slicing, clippy::arithmetic_side_effects)]
    /// sizes of 1 to `FIXED_STACK` bytes and a head of none or four bytes, after what the `Vec` held.
    #[test]
    fn extend_fixed_matches_plain() {
        let mut seed = 0x9E37_79B9_7F4A_7C15u64;
        let counts: Vec<usize> = if cfg!(miri) { vec![0, 1, 5, 17] } else { (0..=256).collect() };
        for size in [1usize, 2, 3, 8, 10, 16, 33, FIXED_STACK] {
            for &n in &counts {
                let values: Vec<Vec<u8>> = (0..n).map(|_| random_bytes(&mut seed, size, 0)).collect();
                // Writes all but the last byte, which stays zero.
                let encode = |v: &Vec<u8>, out: &mut [u8]| {
                    for (o, &b) in out.iter_mut().zip(v.iter().take(size - 1)) {
                        *o = b;
                    }
                };
                let mut want = vec![7u8; 3];
                want.extend_from_slice(&[1, 2, 3, 4]);
                for v in &values {
                    let mut b = vec![0u8; size];
                    encode(v, &mut b);
                    want.extend_from_slice(&b);
                }
                let mut got = vec![7u8; 3];
                extend_fixed(&mut got, [1, 2, 3, 4], &values, size, encode);
                assert_eq!(got, want, "{size} {n}");
                let mut got = vec![7u8; 3];
                extend_fixed(&mut got, [], &values, size, encode);
                assert_eq!(got.get(3..), want.get(7..), "{size} {n}");
            }
        }
    }

    /// `extend_fixed_iter` against encoding each item in turn, for every count to 256 and sizes of 1 to `FIXED_STACK`
    /// bytes, and with an iterator that yields fewer items than its `len` says (only those are kept).
    #[allow(clippy::indexing_slicing, clippy::arithmetic_side_effects)]
    #[test]
    fn extend_fixed_iter_matches_plain() {
        /// Yields the items of `inner`, while its `len` claims `extra` more.
        struct Short<I> {
            inner: I,
            extra: usize,
        }
        impl<I: ExactSizeIterator> Iterator for Short<I> {
            type Item = I::Item;
            fn next(&mut self) -> Option<I::Item> {
                self.inner.next()
            }
            fn size_hint(&self) -> (usize, Option<usize>) {
                let n = self.inner.len() + self.extra;
                (n, Some(n))
            }
        }
        impl<I: ExactSizeIterator> ExactSizeIterator for Short<I> {}
        let mut seed = 0xC2B2_AE3D_27D4_EB4Fu64;
        let counts: Vec<usize> = if cfg!(miri) { vec![0, 1, 5, 17] } else { (0..=256).collect() };
        for size in [1usize, 2, 3, 8, 10, 16, 33, FIXED_STACK] {
            for &n in &counts {
                let values: Vec<Vec<u8>> = (0..n).map(|_| random_bytes(&mut seed, size, 0)).collect();
                // Writes all but the last byte, which stays zero.
                let encode = |v: &Vec<u8>, out: &mut [u8]| {
                    for (o, &b) in out.iter_mut().zip(v.iter().take(size - 1)) {
                        *o = b;
                    }
                };
                let mut want = vec![7u8; 3];
                want.extend_from_slice(&[1, 2, 3, 4]);
                for v in &values {
                    let mut b = vec![0u8; size];
                    encode(v, &mut b);
                    want.extend_from_slice(&b);
                }
                let mut got = vec![7u8; 3];
                extend_fixed_iter(&mut got, [1, 2, 3, 4], values.iter(), size, encode);
                assert_eq!(got, want, "{size} {n}");
                let mut got = vec![7u8; 3];
                extend_fixed_iter(&mut got, [1, 2, 3, 4], Short { inner: values.iter(), extra: 3 }, size, encode);
                assert_eq!(got, want, "{size} {n} short");
                let mut got = vec![7u8; 3];
                extend_fixed_iter(&mut got, [], values.iter(), size, encode);
                assert_eq!(got.get(3..), want.get(7..), "{size} {n}");
            }
        }
    }

    /// A map of fixed-size keys and values, written as one block and read with one bounds check an entry (a slice on
    /// x86; off x86 an 8-byte array for `(i32, i32)` and the generic loop for the others), against reading and
    /// writing its entries one by one: every count to 256 (fewer under Miri), random bytes with bad bools and
    /// repeated keys, whole and cut short; the same map or the same error, and the same bytes.
    #[allow(clippy::indexing_slicing, clippy::arithmetic_side_effects, clippy::unwrap_used)]
    #[test]
    fn maps_match_entry_by_entry() {
        use crate::error::{Error, ErrorKind, Result};
        use crate::reader::Reader;
        use crate::traits::{NinoRead, NinoWrite};
        use crate::types::Guid;
        use std::collections::{BTreeMap, HashMap};
        /// The entries one by one, as maps of other keys and values are read.
        fn each<K: NinoRead, V: NinoRead>(bytes: &[u8], mut insert: impl FnMut(K, V) -> bool) -> Result<()> {
            let mut r = Reader::new(bytes);
            // The length's own checks, as every map's read makes them.
            let count = r.length(K::MIN_SIZE + V::MIN_SIZE)?.ok_or_else(Error::null)?;
            for _ in 0..count {
                let key = K::read(&mut r)?;
                let value = V::read(&mut r)?;
                if !insert(key, value) {
                    return Err(Error::of(ErrorKind::DuplicateKey));
                }
            }
            Ok(())
        }
        fn check<K, V>(seed: &mut u64, n: usize)
        where
            K: NinoRead + NinoWrite + Ord + core::hash::Hash + Clone + core::fmt::Debug,
            V: NinoRead + NinoWrite + PartialEq + core::fmt::Debug,
        {
            let name = core::any::type_name::<(K, V)>();
            let size = <K as NinoRead>::FIXED_SIZE + <V as NinoRead>::FIXED_SIZE;
            for bad in [0u8, 0xFF] {
                let mut bytes = (n as i32).to_le_bytes().to_vec();
                bytes.extend(random_bytes(seed, n * size, bad));
                // Mostly small keys and values (valid bools, repeated keys), some random.
                if !seed.is_multiple_of(3) {
                    for (k, b) in bytes.iter_mut().enumerate().skip(4) {
                        if (k - 4) % size != 0 && *b != bad {
                            *b = 0;
                        } else if *b != bad {
                            *b &= 1;
                        }
                    }
                }
                for cut in [0, 1, size] {
                    let bytes = &bytes[..bytes.len().saturating_sub(cut).max(4)];
                    let mut want = HashMap::new();
                    let plain = each::<K, V>(bytes, |k, v| want.insert(k, v).is_none()).map(|()| want);
                    let got = HashMap::<K, V>::read(&mut Reader::new(bytes));
                    assert_eq!(got, plain, "{name} {n} {cut} hash");
                    let mut want = BTreeMap::new();
                    let plain = each::<K, V>(bytes, |k, v| want.insert(k, v).is_none()).map(|()| want);
                    let got = BTreeMap::<K, V>::read(&mut Reader::new(bytes));
                    assert_eq!(got, plain, "{name} {n} {cut} tree");
                    // Written back: the length, then each entry in the map's order.
                    if let Ok(map) = got {
                        let mut w = crate::writer::Writer::new();
                        w.length(map.len()).unwrap();
                        for (k, v) in &map {
                            k.write(&mut w).unwrap();
                            v.write(&mut w).unwrap();
                        }
                        assert_eq!(crate::serialize(&map).unwrap(), w.into_vec(), "{name} {n} tree write");
                        let hash: HashMap<K, V> = map.into_iter().collect();
                        let mut w = crate::writer::Writer::new();
                        w.length(hash.len()).unwrap();
                        for (k, v) in &hash {
                            k.write(&mut w).unwrap();
                            v.write(&mut w).unwrap();
                        }
                        assert_eq!(crate::serialize(&hash).unwrap(), w.into_vec(), "{name} {n} hash write");
                    }
                }
            }
        }
        let mut seed = 0x6A09_E667_F3BC_C908u64;
        let counts: Vec<usize> = if cfg!(miri) { vec![0, 1, 7] } else { (0..=256).collect() };
        for n in counts {
            check::<i32, i32>(&mut seed, n);
            check::<u8, bool>(&mut seed, n);
            check::<bool, u64>(&mut seed, n);
            check::<(i16, bool), Guid>(&mut seed, n);
        }
    }

    /// Each fixed-size type's block read (`read_many`) against reading its elements one by one, for every count to
    #[allow(clippy::indexing_slicing, clippy::arithmetic_side_effects)]
    /// 256 and random bytes, valid or not: the same values, or the same error.
    #[test]
    fn blocks_match_one_by_one() {
        use crate::reader::Reader;
        use crate::traits::NinoRead;
        use crate::types::{DateOnly, DateTime, DateTimeOffset, Decimal, Guid, Half, Rune, TimeOnly, TimeSpan};
        fn check<T: NinoRead + PartialEq + core::fmt::Debug>(seed: &mut u64, n: usize) {
            let size = T::FIXED_SIZE;
            for bad in [0u8, 0xFF] {
                let mut bytes = random_bytes(seed, n * size, bad);
                // Mostly small values (valid dates, decimals and runes), some random.
                if !seed.is_multiple_of(4) {
                    for (k, b) in bytes.iter_mut().enumerate() {
                        if k % size > size.min(4) / 2 && *b != bad {
                            *b = 0;
                        }
                    }
                }
                let block = T::read_many(&mut Reader::new(&bytes), n);
                let mut r = Reader::new(&bytes);
                let each: crate::error::Result<Vec<T>> = (0..n).map(|_| T::read(&mut r)).collect();
                assert_eq!(block, each, "{} {n}", core::any::type_name::<T>());
                // With the allocation budget spent: the `Vec` grown as it goes.
                let mut spent = Reader::new(&bytes);
                spent.capacity::<u8>(usize::MAX);
                assert_eq!(T::read_many(&mut spent, n), each, "{} {n} spent", core::any::type_name::<T>());
            }
        }
        let mut seed = 0x1234_5678_9ABC_DEF1u64;
        let counts: Vec<usize> = if cfg!(miri) { vec![0, 1, 9] } else { (0..=256).collect() };
        for n in counts {
            for _ in 0..if cfg!(miri) { 1 } else { 4 } {
                check::<bool>(&mut seed, n);
                check::<i8>(&mut seed, n);
                check::<u16>(&mut seed, n);
                check::<u64>(&mut seed, n);
                check::<Decimal>(&mut seed, n);
                check::<Guid>(&mut seed, n);
                check::<DateTime>(&mut seed, n);
                check::<TimeSpan>(&mut seed, n);
                check::<DateOnly>(&mut seed, n);
                check::<TimeOnly>(&mut seed, n);
                check::<DateTimeOffset>(&mut seed, n);
                check::<Half>(&mut seed, n);
                check::<Rune>(&mut seed, n);
                check::<(i32, i32)>(&mut seed, n);
                check::<(i64, bool, u8)>(&mut seed, n);
                check::<(DateOnly, Decimal)>(&mut seed, n);
            }
        }
        // Values at the edges of the ranges, which the quick test sends one by one: read the same either way.
        let max = crate::types::MAX_TICKS;
        let edges = [
            (max - (1 << 32)).to_le_bytes(),
            max.to_le_bytes(),
            (max + 1).to_le_bytes(),
            (max | 3 << 62).to_le_bytes(),
            (crate::types::TICKS_PER_DAY - 1).to_le_bytes(),
            crate::types::TICKS_PER_DAY.to_le_bytes(),
        ];
        for k in 0..edges.len() {
            let bytes: Vec<u8> = edges.iter().take(k + 1).flatten().copied().collect();
            let n = k + 1;
            for (block, each) in [
                (
                    format!("{:?}", DateTime::read_many(&mut Reader::new(&bytes), n)),
                    format!(
                        "{:?}",
                        (0..n)
                            .map(|i| DateTime::read(&mut Reader::new(&bytes[i * 8..])))
                            .collect::<crate::error::Result<Vec<_>>>()
                    ),
                ),
                (
                    format!("{:?}", TimeOnly::read_many(&mut Reader::new(&bytes), n)),
                    format!(
                        "{:?}",
                        (0..n)
                            .map(|i| TimeOnly::read(&mut Reader::new(&bytes[i * 8..])))
                            .collect::<crate::error::Result<Vec<_>>>()
                    ),
                ),
            ] {
                assert_eq!(block, each, "{k}");
            }
        }
    }
}
