//! Text helpers: string headers, and the default string encoding's writer (its decoding is in [`crate::simd`]).

use crate::error::{Error, Result};
use crate::writer::Writer;

/// The header of the null string (both encodings).
pub(crate) const NULL_HEADER: u8 = 0;

/// The header of the empty string (both encodings: Latin-1 of no chars, or a char count of 0, plus one).
pub(crate) const EMPTY_HEADER: u8 = 1;

/// The first byte of a three-byte string header: a little-endian `u16` follows.
pub(crate) const HEADER16: u8 = 0x80;

/// The first byte of a five-byte string header: a little-endian `u32` follows.
pub(crate) const HEADER32: u8 = 0x81;

/// The number of bytes of `value` as a string header (wire format 2.3.1): its first byte gives its size, as
/// MessagePack's string markers do. `value` itself up to 0x7F, else [`HEADER16`] and a `u16`, else [`HEADER32`] and
/// a `u32`: 1, 3 or 5 bytes. `2 * n` and `2 * n + 1` take the same number, so the size of a default string's header
/// follows from its length.
#[inline(always)]
pub(crate) const fn header_size(value: u32) -> usize {
    if value < 0x80 {
        1
    } else if value <= 0xFFFF {
        3
    } else {
        5
    }
}

/// `value` as a string header: its bytes (the first [`header_size`] of them used) and how many.
#[inline(always)]
pub(crate) fn header(value: u32) -> ([u8; 5], usize) {
    let v = value.to_le_bytes();
    if value < 0x80 {
        ([v[0], 0, 0, 0, 0], 1)
    } else if value <= 0xFFFF {
        ([HEADER16, v[0], v[1], 0, 0], 3)
    } else {
        ([HEADER32, v[0], v[1], v[2], v[3]], 5)
    }
}

/// The header of a default string of `count` chars: `2 * count + 1` for Latin-1, `2 * count` for UTF-16. Fails past
/// `i32::MAX` chars.
#[inline]
pub(crate) fn string_header(count: usize, latin1: bool) -> Result<u32> {
    let count = u32::try_from(count).ok().filter(|&c| c <= i32::MAX as u32).ok_or_else(Error::too_large)?;
    // `count <= 2^31 - 1`: `2 * count + 1 <= 2^32 - 1`.
    Ok(count.wrapping_mul(2).wrapping_add(u32::from(latin1)))
}

/// Whether every byte is below 0x80: eight at a time, a loop the compiler vectorizes, and the last eight as one word.
#[inline(never)]
fn is_ascii_long(bytes: &[u8]) -> bool {
    let mut high = bytes.last_chunk::<8>().map_or(0, |w| u64::from_ne_bytes(*w));
    for w in bytes.as_chunks::<8>().0 {
        high |= u64::from_ne_bytes(*w);
    }
    high & 0x8080_8080_8080_8080 == 0
}

/// The number of UTF-16 code units of valid UTF-8 `bytes`: one per scalar value, two for those above U+FFFF. That is
/// one per byte that starts a char (not 0x80..0xBF) plus one per four-byte lead (0xF0 and above). Blocks of 16 bytes
/// are counted in 16 byte lanes (a loop the compiler vectorizes: SSE2, NEON), and the last bytes as one word.
#[inline]
pub(crate) fn utf16_len(bytes: &[u8]) -> u64 {
    let (blocks, rest) = bytes.as_chunks::<16>();
    let mut units = 0u64;
    // At most 2 a byte: 127 blocks keep each lane below 256.
    for run in blocks.chunks(127) {
        let mut lanes = [0u8; 16];
        for block in run {
            for (lane, &b) in lanes.iter_mut().zip(block) {
                *lane = lane.wrapping_add(u8::from(b as i8 >= -0x40)).wrapping_add(u8::from(b >= 0xF0));
            }
        }
        units = units.wrapping_add(lanes.iter().map(|&n| u64::from(n)).sum::<u64>());
    }
    // The last 0 to 15 bytes: a word of the first eight when there are, then the rest as the high bytes of the
    // text's last eight (the bytes before them shifted out), or byte by byte in text of under eight bytes.
    let (head, tail) = match rest.split_first_chunk::<8>() {
        Some((word, tail)) => (word_units(u64::from_le_bytes(*word), 8), tail),
        None => (0, rest),
    };
    let tail_units = match bytes.last_chunk::<8>() {
        // `1 <= tail.len() < 8`: a shift of 8 to 56 bits.
        Some(last) if !tail.is_empty() => word_units(
            u64::from_le_bytes(*last) >> (8usize.wrapping_sub(tail.len()).wrapping_mul(8)),
            tail.len() as u64,
        ),
        _ => tail.iter().map(|&b| u64::from(b as i8 >= -0x40).wrapping_add(u64::from(b >= 0xF0))).sum(),
    };
    units.wrapping_add(head).wrapping_add(tail_units)
}

/// The UTF-16 code units of the `n` bytes of `w` (little-endian, zero above them; the bytes valid UTF-8 between char
/// boundaries): `n`, less the continuation bytes, plus the four-byte leads.
#[inline(always)]
fn word_units(w: u64, n: u64) -> u64 {
    const LOW: u64 = 0x0101_0101_0101_0101;
    // Bit 7 of each byte, as bit 0: set for a continuation byte (bits 7 and 6 are 1 and 0) and for a four-byte lead
    // (bits 7 to 4 are 1). A shift moves a byte's lower bits up to its bit 7; what crosses into the next byte lands
    // below bit 7 and is masked out. Zero bytes are neither.
    let cont = ((w & !(w << 1)) >> 7) & LOW;
    let four = ((w & (w << 1) & (w << 2) & (w << 3)) >> 7) & LOW;
    // Eight flags at most: the multiply sums them in the top byte. `cont <= n` (the bytes counted).
    n.wrapping_sub(cont.wrapping_mul(LOW) >> 56).wrapping_add(four.wrapping_mul(LOW) >> 56)
}

/// Writes `text` in the default encoding (not null): ASCII text of up to 32 bytes through [`crate::simd::ascii_short`],
/// longer ASCII as one copy, other text through [`crate::simd::wide`] where it takes it, and the rest through
/// [`crate::simd::transcode`].
#[inline]
pub(crate) fn write(w: &mut Writer<'_>, text: &str) -> Result<()> {
    let bytes = text.as_bytes();
    if bytes.len() <= crate::simd::SHORT {
        if !crate::simd::ascii_short(w.buffer(), bytes) {
            // Text of up to 32 bytes is taken on every target.
            crate::simd::wide_short_text(w.buffer(), text);
        }
        return Ok(());
    }
    if is_ascii_long(bytes) {
        w.header_bytes(string_header(bytes.len(), true)?, bytes);
        return Ok(());
    }
    if crate::simd::wide(w.buffer(), text) {
        return Ok(());
    }
    write_wide(w, text)
}

/// Writes each of `values` in the default encoding (a list's elements): runs of short ASCII strings through
/// [`crate::simd::ascii_strings`], with one reservation a run and the output's end in a register, and each other
/// string through [`write`].
#[inline]
pub(crate) fn write_strings(w: &mut Writer<'_>, values: &[String]) -> Result<()> {
    let mut rest = values;
    while !rest.is_empty() {
        let (done, run) = crate::simd::ascii_strings(w.buffer(), rest);
        rest = rest.get(done..).unwrap_or_default();
        if done < run {
            // Not short ASCII: the next string on its own.
            let Some((first, tail)) = rest.split_first() else { break };
            write(w, first)?;
            rest = tail;
        }
    }
    Ok(())
}

/// Text with a char above U+007F, written as Latin-1 when every char is at most U+00FF, else as UTF-16: transcoded
/// straight into the buffer after room for the longest header its length allows ([`crate::simd::transcode`]), and
/// the header written last (the chars moved down when it is shorter).
#[inline(never)]
fn write_wide(w: &mut Writer<'_>, text: &str) -> Result<()> {
    let at = w.position();
    let room = crate::simd::header_room(text.len());
    let buf = w.buffer();
    buf.resize(at.saturating_add(room), 0);
    let latin1 = crate::simd::transcode(buf, text);
    w.end_header(at, room, latin1)
}

#[cfg(test)]
#[allow(clippy::indexing_slicing, clippy::arithmetic_side_effects)]
mod tests {
    /// `utf16_len` against std's count for every length to 300 (every tail and block boundary near the start), each
    /// char class at each position, and long runs past a block of 254 words.
    #[test]
    fn utf16_len_counts_code_units() {
        let chars = ['a', 'é', 'ж', '字', '🎉', '\u{7FF}', '\u{800}', '\u{FFFF}', '\u{10000}'];
        for len in 0..300usize {
            for (k, &c) in chars.iter().enumerate() {
                let text: String =
                    (0..len).map(|i| if i % 7 == k % 7 { c } else { chars[(i + k) % chars.len()] }).collect();
                assert_eq!(super::utf16_len(text.as_bytes()), text.encode_utf16().count() as u64, "{text:?}");
            }
        }
        for c in chars {
            let text: String = core::iter::repeat_n(c, 5000).collect();
            assert_eq!(super::utf16_len(text.as_bytes()), text.encode_utf16().count() as u64);
        }
    }
}
