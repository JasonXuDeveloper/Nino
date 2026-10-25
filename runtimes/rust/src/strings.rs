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
/// the byte count, less the continuation bytes (0x80..0xBF), plus the four-byte lead bytes (0xF0 and above), both
/// counted eight bytes a word: each byte's flag is its bit 0, summed in the word's byte lanes for up to 255 words,
/// then the lanes added. A loop the compiler vectorizes; a short text is a word or two.
#[inline]
pub(crate) fn utf16_len(bytes: &[u8]) -> u64 {
    const LOW: u64 = 0x0101_0101_0101_0101;
    // Bit 7 of each byte: set for a continuation byte (bits 7 and 6 are 1 and 0), and for a four-byte lead (bits 7
    // to 4 are all 1). A shift moves each byte's lower bits up to bit 7 of the same byte; what it moves across bytes
    // lands below bit 7 and is masked out. Both as bit 0 of each byte.
    let flags = |w: u64| ((w & !(w << 1)) >> 7 & LOW, (w & (w << 1) & (w << 2) & (w << 3)) >> 7 & LOW);
    // The sum of the eight byte lanes (each at most 255).
    let lanes = |x: u64| {
        let pairs = (x & 0x00FF_00FF_00FF_00FF).wrapping_add((x >> 8) & 0x00FF_00FF_00FF_00FF);
        pairs.wrapping_mul(0x0001_0001_0001_0001) >> 48
    };
    let (words, tail) = bytes.as_chunks::<8>();
    let mut last = [0u8; 8];
    if let Some(head) = last.get_mut(..tail.len()) {
        head.copy_from_slice(tail);
    }
    // Zero bytes past the text are neither continuation bytes nor leads.
    let (mut cont, mut four) = flags(u64::from_le_bytes(last));
    let (mut cont_sum, mut four_sum) = (0u64, 0u64);
    for block in words.chunks(254) {
        // At most 255 flags a lane, with the tail's: no lane overflows.
        for w in block {
            let (c, f) = flags(u64::from_le_bytes(*w));
            cont = cont.wrapping_add(c);
            four = four.wrapping_add(f);
        }
        cont_sum = cont_sum.wrapping_add(lanes(cont));
        four_sum = four_sum.wrapping_add(lanes(four));
        (cont, four) = (0, 0);
    }
    // No word: the tail's flags alone.
    cont_sum = cont_sum.wrapping_add(lanes(cont));
    four_sum = four_sum.wrapping_add(lanes(four));
    // Sums of counts of the bytes: the continuation bytes are at most all of them.
    (bytes.len() as u64).wrapping_sub(cont_sum).wrapping_add(four_sum)
}

/// Writes `text` in the default encoding (not null): ASCII text of up to 32 bytes through [`crate::simd::ascii_short`],
/// longer ASCII as one copy, other text through [`crate::simd::wide`] where it takes it, and the rest through
/// [`crate::simd::transcode`].
#[inline]
pub(crate) fn write(w: &mut Writer<'_>, text: &str) -> Result<()> {
    let bytes = text.as_bytes();
    if bytes.len() <= crate::simd::SHORT {
        if !crate::simd::ascii_short(w.buffer(), bytes) {
            // `wide` takes text of up to 32 bytes on every target.
            crate::simd::wide(w.buffer(), text);
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
