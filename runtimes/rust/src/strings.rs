//! Text helpers: Latin-1 and UTF-16 decoding, and the default string encoding's writer.

use crate::error::{Error, Result};
use crate::writer::Writer;

/// Latin-1 bytes (each byte is the code point U+0000..U+00FF) as a `String`.
#[inline]
pub(crate) fn latin1(bytes: &[u8]) -> String {
    if bytes.is_ascii() {
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
    if text.is_ascii() {
        let count = i32::try_from(text.len()).map_err(|_| Error::too_large())?;
        w.reserve_capacity(text.len().saturating_add(4));
        w.i32(if count == 0 { 0 } else { !count });
        w.bytes(text.as_bytes());
        return Ok(());
    }
    write_wide(w, text)
}

#[inline(never)]
fn write_wide(w: &mut Writer<'_>, text: &str) -> Result<()> {
    if text.chars().all(|c| u32::from(c) <= 0xFF) {
        // Latin-1: one byte per char.
        let count = text.chars().count();
        let header = i32::try_from(count).map_err(|_| Error::too_large())?;
        w.reserve_capacity(count.saturating_add(4));
        w.i32(!header);
        let out = w.reserve(count);
        for (dst, c) in out.iter_mut().zip(text.chars()) {
            *dst = u32::from(c) as u8;
        }
        return Ok(());
    }
    let count = usize::try_from(utf16_len(text.as_bytes())).map_err(|_| Error::too_large())?;
    let header = i32::try_from(count).map_err(|_| Error::too_large())?;
    w.reserve_capacity(count.saturating_mul(2).saturating_add(4));
    w.i32(header);
    let out = w.reserve(count.saturating_mul(2));
    for (dst, u) in out.as_chunks_mut::<2>().0.iter_mut().zip(text.encode_utf16()) {
        *dst = u.to_le_bytes();
    }
    Ok(())
}
