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

/// The number of UTF-16 code units of valid UTF-8 `bytes`: one per scalar value, two for those above U+FFFF (a
/// lead byte 0xF0 or above). Counted over the bytes, a loop the compiler vectorizes.
#[inline]
pub(crate) fn utf16_len(bytes: &[u8]) -> u64 {
    let mut units = 0u64;
    for &b in bytes {
        // Continuation bytes (0x80..0xBF) start no unit; 4-byte leads start two.
        units = units.wrapping_add(u64::from((b as i8) >= -0x40)).wrapping_add(u64::from(b >= 0xF0));
    }
    units
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
