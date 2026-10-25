//! Text helpers: the default string encoding's writer (its decoding is in [`crate::simd`]).

use crate::error::{Error, Result};
use crate::writer::Writer;

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
        let count = i32::try_from(bytes.len()).map_err(|_| Error::too_large())?;
        w.header_bytes(if count == 0 { 0 } else { !count }, bytes);
        return Ok(());
    }
    if crate::simd::wide(w.buffer(), text) {
        return Ok(());
    }
    write_wide(w, text)
}

/// Text with a char above U+007F, written as Latin-1 when every char is at most U+00FF, else as UTF-16: transcoded
/// straight into the buffer after a zero header ([`crate::simd::transcode`]), and the header written last.
#[inline(never)]
fn write_wide(w: &mut Writer<'_>, text: &str) -> Result<()> {
    let at = w.position();
    let buf = w.buffer();
    buf.extend_from_slice(&[0; 4]);
    let latin1 = crate::simd::transcode(buf, text);
    w.end_header(at, latin1)
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
