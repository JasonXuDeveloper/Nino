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

mod decode;
#[cfg(all(target_arch = "aarch64", target_endian = "little"))]
mod neon;
#[cfg(target_arch = "x86_64")]
mod x86;
pub(crate) use decode::{latin1_string, utf16_string};

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
    let mut out = Vec::with_capacity(chunks.len());
    fill_block(&mut out, chunks, decode)?;
    Ok(out)
}

/// [`decode_block`] into `out` (reading into an existing `Vec`): emptied, then filled, keeping its allocation when it
/// has room for every value. On an error `out` holds the values decoded before it.
#[inline(always)]
pub(crate) fn decode_block_into<C, T, I, F>(out: &mut Vec<T>, chunks: I, decode: F) -> crate::error::Result<()>
where
    I: ExactSizeIterator<Item = C>,
    F: Fn(C) -> crate::error::Result<T>,
{
    out.clear();
    out.reserve_exact(chunks.len());
    fill_block(out, chunks, decode)
}

/// Decodes `chunks` into the spare capacity of `out`, which is empty with room for all of them.
#[inline(always)]
fn fill_block<C, T, I, F>(out: &mut Vec<T>, chunks: I, decode: F) -> crate::error::Result<()>
where
    I: ExactSizeIterator<Item = C>,
    F: Fn(C) -> crate::error::Result<T>,
{
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
                // SAFETY: `out` was empty, and the first `done` slots are written.
                unsafe { out.set_len(done) };
                return Err(e);
            }
        }
    }
    // SAFETY: `out` was empty with room for every chunk, and `zip` wrote a slot for each (it stops at the shorter of
    // the spare capacity and the chunks, and the capacity is at least their count): the first `done` are written.
    unsafe { out.set_len(done) };
    Ok(())
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

/// Appends the header and chars of `text`, at most [`SHORT_WIDE`] bytes with a char above U+007F: as Latin-1 when every
/// char is at most U+00FF, else as UTF-16 LE code units. One reservation, the chars decoded one by one straight into
/// it, the header written last. Every target.
#[inline(never)]
pub(crate) fn short_wide(out: &mut Vec<u8>, text: &str) {
    short_wide_inline(out, text);
}

/// [`short_wide`], inlined (in [`strings_short`]'s loop).
///
/// Branchy on purpose: a branch a char on its class, which text of one shape after another predicts, keeps the next
/// char's address an add away, where a branch-free decoder (a table of lengths, or the length of a run of ASCII from
/// its bit mask) waits for a load every char. Each class has its own back edge, and the bytes are read sign-extended
/// (ASCII is `>= 0`, and its unit is the byte itself), so a char takes a few instructions: on Arm64 six for ASCII and
/// eleven for a two-byte char, where one loop with a shared tail took nine and fifteen (`optimize-runtimes`).
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
    // SAFETY: the room reserved holds `1 + 2 * len` bytes from `at`. Invariants: `p` is a char boundary of the valid
    // UTF-8 from `src` to `end` (so a lead byte's continuation bytes are there to read, and `p` is read only while it
    // is not `end`), and `d` is at most `p - src` bytes (Latin-1) or `2 * (p - src)` (UTF-16) past `dst`.
    unsafe {
        let base = out.as_mut_ptr().add(at);
        let dst = base.add(1);
        let end = src.add(len);
        let (mut p, mut d) = (src, dst);
        let lead = |p: *const u8| i32::from(p.read() as i8);
        let next = |p: *const u8, k: usize| i32::from(p.add(k).read() & 0x3F);
        // A byte read as `i8`: ASCII is `0..=127`, a lead byte of 0xC0 and above `-64..=-1`.
        let mut b = 0;
        let wide = p != end && {
            b = lead(p);
            loop {
                if b >= 0 {
                    d.write(b as u8);
                    p = p.add(1);
                    d = d.add(1);
                    if p == end {
                        break false;
                    }
                    b = lead(p);
                    continue;
                }
                // 0xC4 and above: a char above U+00FF.
                if b >= -0x3C {
                    break true;
                }
                // U+0080..U+00FF: its lead byte's two low bits, then its continuation byte's six.
                d.write(((b << 6) | next(p, 1)) as u8);
                p = p.add(2);
                d = d.add(1);
                if p == end {
                    break false;
                }
                b = lead(p);
            }
        };
        // `d >= dst`, and at most 63 bytes past it.
        let o = d.offset_from(dst) as usize;
        if !wide {
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
        let mut d = dst.add(o.wrapping_mul(2));
        // A unit's low 16 bits: the bits above them, from a sign-extended byte, are not stored.
        let unit = |d: *mut u8, c: i32| d.cast::<[u8; 2]>().write_unaligned((c as u16).to_le_bytes());
        loop {
            if b >= 0 {
                unit(d, b);
                p = p.add(1);
                d = d.add(2);
                if p == end {
                    break;
                }
                b = lead(p);
                continue;
            }
            if b < -0x20 {
                // 0xC0..0xDF: two bytes.
                unit(d, ((b & 0x1F) << 6) | next(p, 1));
                p = p.add(2);
                d = d.add(2);
                if p == end {
                    break;
                }
                b = lead(p);
                continue;
            }
            if b < -0x10 {
                // 0xE0..0xEF: three bytes.
                unit(d, ((b & 0x0F) << 12) | (next(p, 1) << 6) | next(p, 2));
                p = p.add(3);
                d = d.add(2);
                if p == end {
                    break;
                }
                b = lead(p);
                continue;
            }
            // Four bytes: a surrogate pair, stored as one word.
            let c = ((b & 0x07) << 18) | (next(p, 1) << 12) | (next(p, 2) << 6) | next(p, 3);
            let v = c.wrapping_sub(0x1_0000);
            let pair = (0xD800 | (v >> 10)) | ((0xDC00 | (v & 0x3FF)) << 16);
            d.cast::<[u8; 4]>().write_unaligned(pair.to_le_bytes());
            p = p.add(4);
            d = d.add(4);
            if p == end {
                break;
            }
            b = lead(p);
        }
        // `o` bytes of code units, at most 126: the header `2 * (o / 2)`, which is `o`.
        let o = d.offset_from(dst) as usize;
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
    if text.len() > SHORT_WIDE && text.len() <= WIDE_MAX {
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

#[cfg(test)]
mod tests;
