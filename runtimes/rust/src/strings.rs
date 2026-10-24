//! Text helpers: Latin-1 and UTF-16 decoding, and the default string encoding's writer.

use crate::error::{Error, Result};
use crate::writer::Writer;

/// Whether every byte is below 0x80. Up to 16 bytes, as one or two words that may overlap (no loop); longer
/// text out of line.
#[inline]
pub(crate) fn is_ascii(bytes: &[u8]) -> bool {
    if bytes.len() > 16 {
        return is_ascii_long(bytes);
    }
    if let (Some(first), Some(last)) = (bytes.first_chunk::<8>(), bytes.last_chunk::<8>()) {
        return (u64::from_ne_bytes(*first) | u64::from_ne_bytes(*last)) & 0x8080_8080_8080_8080 == 0;
    }
    if let (Some(first), Some(last)) = (bytes.first_chunk::<4>(), bytes.last_chunk::<4>()) {
        return (u32::from_ne_bytes(*first) | u32::from_ne_bytes(*last)) & 0x8080_8080 == 0;
    }
    bytes.iter().all(|&b| b < 0x80)
}

/// [`is_ascii`] past 16 bytes: eight at a time, a loop the compiler vectorizes, and the last eight as one word.
#[inline(never)]
fn is_ascii_long(bytes: &[u8]) -> bool {
    let mut high = bytes.last_chunk::<8>().map_or(0, |w| u64::from_ne_bytes(*w));
    for w in bytes.as_chunks::<8>().0 {
        high |= u64::from_ne_bytes(*w);
    }
    high & 0x8080_8080_8080_8080 == 0
}

/// Latin-1 bytes (each byte is the code point U+0000..U+00FF) as a `String`. Out of line, so that
/// [`crate::Reader::string`] stays small enough to inline into the codec that reads it: only the `String` comes back
/// through memory.
#[inline(never)]
pub(crate) fn latin1(bytes: &[u8]) -> String {
    if is_ascii(bytes) {
        // ASCII is UTF-8 as it is.
        return match core::str::from_utf8(bytes) {
            Ok(text) => text.to_owned(),
            Err(_) => String::new(),
        };
    }
    latin1_wide(bytes)
}

#[inline(never)]
fn latin1_wide(bytes: &[u8]) -> String {
    let wide = bytes.iter().filter(|&&b| b >= 0x80).count();
    let mut text = String::with_capacity(bytes.len().saturating_add(wide));
    text.extend(bytes.iter().map(|&b| char::from(b)));
    text
}

/// UTF-16 code units as a `String`; each unpaired surrogate becomes U+FFFD.
#[inline(never)]
pub(crate) fn utf16_lossy(units: &[[u8; 2]]) -> String {
    let units = units.iter().map(|u| u16::from_le_bytes(*u));
    let mut text = String::with_capacity(units.len().saturating_mul(3));
    text.extend(char::decode_utf16(units).map(|c| c.unwrap_or(char::REPLACEMENT_CHARACTER)));
    text
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

/// Writes `text` in the default encoding (not null).
#[inline]
pub(crate) fn write(w: &mut Writer<'_>, text: &str) -> Result<()> {
    let bytes = text.as_bytes();
    if is_ascii(bytes) {
        let count = i32::try_from(bytes.len()).map_err(|_| Error::too_large())?;
        w.header_bytes(if count == 0 { 0 } else { !count }, bytes);
        return Ok(());
    }
    write_wide(w, bytes)
}

/// Text with a char above U+007F, written as Latin-1 when every char is at most U+00FF, else as UTF-16. Each takes
/// at most one byte or one code unit per byte of UTF-8, so room for that is reserved, filled in one pass over the
/// bytes and cut to what was written; the header is written last. In UTF-8 a char of U+0080..U+00FF is the lead byte
/// 0xC2 or 0xC3 and a continuation byte, and every lead byte from 0xC4 up starts a char above U+00FF: the first such
/// byte turns the Latin-1 pass into the UTF-16 one.
#[inline(never)]
fn write_wide(w: &mut Writer<'_>, bytes: &[u8]) -> Result<()> {
    let at = w.position();
    let buf = w.buffer();
    buf.reserve(bytes.len().saturating_add(4));
    buf.extend_from_slice(&[0; 4]);
    if latin1_into(buf, bytes) {
        return w.end_header(at, true);
    }
    buf.truncate(at);
    buf.reserve(bytes.len().saturating_mul(2).saturating_add(4));
    buf.extend_from_slice(&[0; 4]);
    utf16_into(buf, bytes);
    w.end_header(at, false)
}

/// Appends the Latin-1 bytes of `bytes` (valid UTF-8) to `out`; false at a char above U+00FF.
#[inline]
fn latin1_into(out: &mut Vec<u8>, bytes: &[u8]) -> bool {
    let mut src = bytes.iter();
    while let Some(&b) = src.next() {
        if b < 0x80 {
            out.push(b);
        } else if b < 0xC4 {
            // A lead byte's two low bits, then its continuation byte's six.
            out.push((b << 6) | (src.next().copied().unwrap_or(0) & 0x3F));
        } else {
            return false;
        }
    }
    true
}

/// Appends the UTF-16 code units of `bytes` (valid UTF-8) to `out`, two bytes each.
#[inline]
fn utf16_into(out: &mut Vec<u8>, bytes: &[u8]) {
    let mut src = bytes.iter();
    while let Some(&b0) = src.next() {
        let mut next = || u32::from(src.next().copied().unwrap_or(0) & 0x3F);
        let b = u32::from(b0);
        let unit = if b < 0x80 {
            b
        } else if b < 0xE0 {
            ((b & 0x1F) << 6) | next()
        } else if b < 0xF0 {
            ((b & 0x0F) << 12) | (next() << 6) | next()
        } else {
            // Above U+FFFF: a surrogate pair.
            let c = ((b & 0x07) << 18) | (next() << 12) | (next() << 6) | next();
            let v = c.wrapping_sub(0x1_0000);
            out.extend_from_slice(&((0xD800 | ((v >> 10) & 0x3FF)) as u16).to_le_bytes());
            0xDC00 | (v & 0x3FF)
        };
        out.extend_from_slice(&(unit as u16).to_le_bytes());
    }
}
