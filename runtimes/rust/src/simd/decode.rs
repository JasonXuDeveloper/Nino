//! The reader's text: Latin-1 and UTF-16 decoded into a `String` it builds (from bytes the reader has bounds-checked;
//! unchecked only over the UTF-8 the decoders write themselves). See the module root for the kernels and the tests.

use super::*;

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
pub(super) unsafe fn latin1_words(bytes: &[u8], k: usize, dst: *mut u8, mut o: usize) -> usize {
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
pub(super) unsafe fn utf16_words(
    units: &[[u8; 2]],
    mut k: usize,
    end: usize,
    dst: *mut u8,
    mut o: usize,
) -> (usize, usize) {
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
pub(super) static EXPAND: [[u8; 16]; 256] = expand_shuffles();

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
pub(super) static PACK3: [([u8; 16], u8); 256] = pack3_shuffles();

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
