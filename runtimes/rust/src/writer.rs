//! [`Writer`]: appends Nino's bytes to a `Vec<u8>`.

use crate::defer::WriteDeferrals;
use crate::error::{Error, ErrorKind, Result};
use crate::reader::{INLINE_DEPTH, MAX_DEPTH};
use crate::traits::{NinoWrite, NinoWriteBody};

/// Writes values in Nino's format into a growing buffer (amortized doubling, as `Vec` grows), with no allocation
/// per value once the buffer is large enough.
///
/// The lifetime `'a` is that of the value being written: bodies nested too deep are written later, from a shallow
/// point (see the specification, 7.10), so the writer keeps references to them until the call ends.
pub struct Writer<'a> {
    buf: Vec<u8>,
    pub(crate) depth: u32,
    pub(crate) regions: u32,
    pub(crate) defer: Option<Box<WriteDeferrals<'a>>>,
}

impl Default for Writer<'_> {
    fn default() -> Self {
        Self::new()
    }
}

impl<'a> Writer<'a> {
    /// A writer with an empty buffer.
    pub fn new() -> Self {
        Self::from_vec(Vec::new())
    }

    /// A writer appending to `buf` (its capacity is reused).
    pub fn from_vec(buf: Vec<u8>) -> Self {
        Self { buf, depth: 0, regions: 0, defer: None }
    }

    /// The buffer, with everything written.
    pub fn into_vec(self) -> Vec<u8> {
        self.buf
    }

    /// The bytes written so far (including what the buffer held before).
    pub fn as_slice(&self) -> &[u8] {
        &self.buf
    }

    /// The position: the buffer's length.
    #[inline]
    pub fn position(&self) -> usize {
        self.buf.len()
    }

    /// Appends raw bytes.
    #[inline]
    pub fn bytes(&mut self, bytes: &[u8]) {
        self.buf.extend_from_slice(bytes);
    }

    /// Appends `N` bytes (a run of fixed-size members, written into an array first).
    #[inline]
    pub fn array<const N: usize>(&mut self, bytes: [u8; N]) {
        self.buf.extend_from_slice(&bytes);
    }

    /// Appends `size` zero bytes and returns them to be filled (a run of fixed-size elements).
    #[inline]
    pub fn reserve(&mut self, size: usize) -> &mut [u8] {
        let start = self.buf.len();
        self.buf.resize(start.saturating_add(size), 0);
        self.buf.get_mut(start..).unwrap_or_default()
    }

    /// Appends the `N` bytes `encode` gives for each of `values` (a block of fixed-size elements).
    #[inline]
    pub(crate) fn encoded<T, const N: usize>(&mut self, values: &[T], encode: impl Fn(&T) -> [u8; N]) {
        crate::simd::extend_encoded(&mut self.buf, [], values, encode);
    }

    /// Appends the length of `values` (fails past `i32::MAX`), then the `N` bytes `encode` gives for each: one
    /// capacity check for both (a collection of fixed-size elements).
    #[inline]
    pub(crate) fn counted<T, const N: usize>(&mut self, values: &[T], encode: impl Fn(&T) -> [u8; N]) -> Result<()> {
        let length = i32::try_from(values.len()).map_err(|_| Error::too_large())?;
        crate::simd::extend_encoded(&mut self.buf, length.to_le_bytes(), values, encode);
        Ok(())
    }

    /// A string header, then `bytes`: one capacity check for both.
    #[inline]
    pub(crate) fn header_bytes(&mut self, header: u32, bytes: &[u8]) {
        self.buf.reserve(bytes.len().saturating_add(crate::strings::header_size(header)));
        crate::strings::push_header(&mut self.buf, header);
        self.buf.extend_from_slice(bytes);
    }

    /// A string header alone.
    #[inline]
    pub(crate) fn header(&mut self, header: u32) {
        crate::strings::push_header(&mut self.buf, header);
    }

    /// Ends a string started at `at` with `room` zero bytes for its header and followed by its chars: writes the
    /// header (of the chars written: bytes for Latin-1, code units for UTF-16), moving the chars down when it takes
    /// fewer bytes than `room`.
    #[inline]
    pub(crate) fn end_header(&mut self, at: usize, room: usize, latin1: bool) -> Result<()> {
        let start = at.saturating_add(room);
        let size = self.buf.len().saturating_sub(start);
        let header = crate::strings::string_header(if latin1 { size } else { size / 2 }, latin1)?;
        let (h, n) = crate::strings::header(header);
        if n < room {
            self.buf.copy_within(start.., at.saturating_add(n));
            self.buf.truncate(at.saturating_add(n).saturating_add(size));
        }
        // A store of a constant size for each header size (not a copy of `n` bytes, a call to `memcpy`).
        match (n, self.buf.get_mut(at..)) {
            (1, Some([d0, ..])) => *d0 = h[0],
            (3, Some([d0, d1, d2, ..])) => [*d0, *d1, *d2] = [h[0], h[1], h[2]],
            (_, Some([d0, d1, d2, d3, d4, ..])) => [*d0, *d1, *d2, *d3, *d4] = h,
            _ => {}
        }
        Ok(())
    }

    /// The buffer, to append to directly.
    #[inline]
    pub(crate) fn buffer(&mut self) -> &mut Vec<u8> {
        &mut self.buf
    }

    /// Makes room for at least `additional` more bytes.
    #[inline]
    pub fn reserve_capacity(&mut self, additional: usize) {
        self.buf.reserve(additional);
    }

    #[inline]
    pub fn u8(&mut self, v: u8) {
        self.buf.push(v);
    }
    #[inline]
    pub fn i8(&mut self, v: i8) {
        self.buf.push(v as u8);
    }
    #[inline]
    pub fn bool(&mut self, v: bool) {
        self.buf.push(u8::from(v));
    }
    #[inline]
    pub fn u16(&mut self, v: u16) {
        self.array(v.to_le_bytes());
    }
    #[inline]
    pub fn i16(&mut self, v: i16) {
        self.array(v.to_le_bytes());
    }
    #[inline]
    pub fn u32(&mut self, v: u32) {
        self.array(v.to_le_bytes());
    }
    #[inline]
    pub fn i32(&mut self, v: i32) {
        self.array(v.to_le_bytes());
    }
    #[inline]
    pub fn u64(&mut self, v: u64) {
        self.array(v.to_le_bytes());
    }
    #[inline]
    pub fn i64(&mut self, v: i64) {
        self.array(v.to_le_bytes());
    }
    #[inline]
    pub fn u128(&mut self, v: u128) {
        self.array(v.to_le_bytes());
    }
    #[inline]
    pub fn i128(&mut self, v: i128) {
        self.array(v.to_le_bytes());
    }
    #[inline]
    pub fn f32(&mut self, v: f32) {
        self.array(v.to_le_bytes());
    }
    #[inline]
    pub fn f64(&mut self, v: f64) {
        self.array(v.to_le_bytes());
    }

    /// A presence byte: 1 when a value follows.
    #[inline]
    pub fn present(&mut self, present: bool) {
        self.buf.push(u8::from(present));
    }

    /// A collection length (fails past `i32::MAX`).
    #[inline]
    pub fn length(&mut self, length: usize) -> Result<()> {
        let length = i32::try_from(length).map_err(|_| Error::too_large())?;
        self.i32(length);
        Ok(())
    }

    /// The null length, -1.
    #[inline]
    pub fn null_length(&mut self) {
        self.i32(-1);
    }

    // ------------------------------------------------------------------ strings

    /// A string in the default encoding: one byte per char when every char is at most U+00FF (Latin-1), else
    /// UTF-16; `None` is null.
    #[inline]
    pub fn string(&mut self, value: Option<&str>) -> Result<()> {
        match value {
            None => {
                self.buf.push(crate::strings::NULL_HEADER);
                Ok(())
            }
            Some(text) => crate::strings::write(self, text),
        }
    }

    /// A string in the default encoding from its UTF-16 code units (unpaired surrogates included).
    pub fn utf16(&mut self, value: Option<&[u16]>) -> Result<()> {
        let Some(units) = value else {
            self.buf.push(crate::strings::NULL_HEADER);
            return Ok(());
        };
        let latin1 = units.iter().all(|&u| u <= 0xFF);
        self.header(crate::strings::string_header(units.len(), latin1)?);
        if latin1 {
            self.buf.extend(units.iter().map(|&u| u as u8));
        } else {
            let out = self.reserve(units.len().saturating_mul(2));
            for (dst, u) in out.as_chunks_mut::<2>().0.iter_mut().zip(units) {
                *dst = u.to_le_bytes();
            }
        }
        Ok(())
    }

    /// A string as UTF-8 (`[NinoUtf8]`): its length in UTF-16 code units plus one (0 null, 1 empty), then its byte
    /// count and bytes, the counts string headers.
    pub fn utf8(&mut self, value: Option<&str>) -> Result<()> {
        let Some(text) = value else {
            self.buf.push(crate::strings::NULL_HEADER);
            return Ok(());
        };
        if text.is_empty() {
            self.buf.push(crate::strings::EMPTY_HEADER);
            return Ok(());
        }
        let chars = crate::strings::utf16_len(text.as_bytes());
        let chars = u32::try_from(chars).ok().filter(|&c| c <= i32::MAX as u32).ok_or_else(Error::too_large)?;
        let size = u32::try_from(text.len()).ok().filter(|&c| c <= i32::MAX as u32).ok_or_else(Error::too_large)?;
        // `chars <= i32::MAX`: `chars + 1` cannot wrap. Two one-byte counts and up to 32 bytes: as one block.
        if chars < 0x7F && size < 0x80 {
            let head = [chars.wrapping_add(1) as u8, size as u8];
            if crate::simd::two_and_short(&mut self.buf, head, text.as_bytes()) {
                return Ok(());
            }
        }
        // One reservation: the two counts, then the bytes.
        self.buf.reserve(text.len().saturating_add(10));
        crate::strings::push_header(&mut self.buf, chars.wrapping_add(1));
        crate::strings::push_header(&mut self.buf, size);
        self.buf.extend_from_slice(text.as_bytes());
        Ok(())
    }

    // ------------------------------------------------------------------ values

    /// Writes any [`NinoWrite`] value.
    #[inline]
    pub fn write<T: NinoWrite + ?Sized>(&mut self, value: &'a T) -> Result<()> {
        value.write(self)
    }

    /// Writes the body of the class `T` (after its header), counting it as an open body: or, 128 bodies deep,
    /// queues it to be written later when `T` can be deferred (see the specification, 7.10).
    #[inline]
    pub fn write_class<T: NinoWriteBody>(&mut self, value: &'a T) -> Result<()> {
        let depth = self.depth;
        if depth >= INLINE_DEPTH {
            return self.write_class_deep(value);
        }
        self.write_body_at(value, depth)
    }

    /// [`Writer::write_class`] 128 bodies deep or more (out of line: rare).
    #[inline(never)]
    fn write_class_deep<T: NinoWriteBody>(&mut self, value: &'a T) -> Result<()> {
        let depth = self.depth;
        if T::DEFERRABLE {
            let (position, regions) = (self.position(), self.regions);
            return self.defer.get_or_insert_with(Default::default).defer(value, position, regions);
        }
        if depth >= MAX_DEPTH {
            return Err(Error::of(ErrorKind::TooDeep));
        }
        self.write_body_at(value, depth)
    }

    /// Writes the body of `value` as an open body at `depth` (the writer's), then the bodies deferred in it when it
    /// is the outermost.
    #[inline]
    fn write_body_at<T: NinoWriteBody>(&mut self, value: &'a T, depth: u32) -> Result<()> {
        self.depth = depth.saturating_add(1);
        value.write_body(self)?;
        self.depth = depth;
        if depth == 0 && self.defer.is_some() {
            crate::defer::write_top(self)?;
        }
        Ok(())
    }

    /// Writes a sealed class: its presence byte, then its body ([`Writer::write_class`]).
    #[inline]
    pub fn write_sealed<T: NinoWriteBody>(&mut self, value: Option<&'a T>) -> Result<()> {
        match value {
            Some(v) => {
                self.present(true);
                self.write_class(v)
            }
            None => {
                self.present(false);
                Ok(())
            }
        }
    }

    // ------------------------------------------------------------------ regions

    /// Starts a length-prefixed region (a version-tolerant or keyed body, or a keyed member); returns where its
    /// length goes, for [`Writer::end_region`].
    #[inline]
    pub fn begin_region(&mut self) -> usize {
        self.regions = self.regions.saturating_add(1);
        self.reserve_i32()
    }

    /// Ends the region started at `at`: writes its length, and at the end of the outermost region the bodies
    /// deferred inside it (a section).
    #[inline]
    pub fn end_region(&mut self, at: usize) -> Result<()> {
        self.regions = self.regions.saturating_sub(1);
        if self.regions == 0 && self.defer.is_some() && crate::defer::write_section(self, at)? {
            return Ok(());
        }
        self.patch_length(at)
    }

    /// Reserves an `i32` to patch later; returns its position.
    #[inline]
    pub fn reserve_i32(&mut self) -> usize {
        let at = self.buf.len();
        self.i32(0);
        at
    }

    /// Overwrites the `i32` at `at` (from [`Writer::reserve_i32`]).
    #[inline]
    pub fn patch_i32(&mut self, at: usize, value: i32) {
        if let Some(dst) = self.buf.get_mut(at..).and_then(|s| s.first_chunk_mut::<4>()) {
            *dst = value.to_le_bytes();
        }
    }

    /// Writes, at `at`, the number of bytes written after that `i32`.
    #[inline]
    pub fn patch_length(&mut self, at: usize) -> Result<()> {
        let length = self
            .buf
            .len()
            .checked_sub(at)
            .and_then(|n| n.checked_sub(4))
            .and_then(|n| i32::try_from(n).ok())
            .ok_or_else(Error::too_large)?;
        self.patch_i32(at, length);
        Ok(())
    }
}

impl core::fmt::Debug for Writer<'_> {
    fn fmt(&self, f: &mut core::fmt::Formatter<'_>) -> core::fmt::Result {
        f.debug_struct("Writer")
            .field("position", &self.buf.len())
            .field("depth", &self.depth)
            .field("regions", &self.regions)
            .finish_non_exhaustive()
    }
}
