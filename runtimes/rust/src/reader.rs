//! [`Reader`]: bounds-checked, little-endian reads over a byte slice.
//!
//! Every read takes its bytes with one length check against what is left (of the data, or of the region or entry
//! being read) and fails with [`ErrorKind::UnexpectedEnd`] otherwise. Lengths are checked against the bytes left
//! before anything is allocated. Nothing here indexes, unwraps or does unchecked arithmetic (the lints below make
//! that a compile error), so no input can make it panic.

use core::any::Any;

use crate::defer::{ReadDeferrals, Step};
use crate::error::{Error, ErrorKind, Result};
use crate::traits::{NinoRead, NinoReadBody};

/// Open class bodies after which the next deferrable one is deferred (`NinoDepth.Inline`).
pub const DEPTH_INLINE: u32 = 128;
/// Open bodies after which reading or writing fails, for types that cannot be deferred (`NinoDepth.Max`).
pub const DEPTH_MAX: u32 = 512;
/// Bytes of memory collections may allocate up front per byte of input.
const PREALLOCATION: usize = 16;
/// Set in the length of a region followed by a section of deferred bodies.
pub(crate) const SECTION_FLAG: u32 = 0x8000_0000;

/// Reads values from bytes written by Nino.
///
/// Generated code calls the primitive methods (`i32`, `string`, ...), [`Reader::read`] for any [`NinoRead`] type,
/// [`Reader::read_class`] for class bodies and the region methods for version-tolerant and keyed bodies.
pub struct Reader<'d> {
    /// The bytes left: from the position to the end of the data, or of the region or entry being read.
    rest: &'d [u8],
    /// The absolute offset where `rest` ends (the position is `end - rest.len()`).
    end: usize,
    /// Open class bodies (see `docs/spec/wire-format.md`, 7.10).
    pub(crate) depth: u32,
    /// Open regions.
    pub(crate) regions: u32,
    /// Open bodies of structs that can hold themselves ([`Reader::enter_struct`]).
    structs: u32,
    /// Deferred bodies, created on the first deferral.
    pub(crate) defer: Option<Box<ReadDeferrals>>,
    /// Bytes of memory collections may still allocate before reading their elements ([`Reader::capacity`]).
    budget: usize,
}

/// A region opened by [`Reader::begin_region`]; pass it back to [`Reader::end_region`].
#[must_use = "a region must be ended with Reader::end_region"]
pub struct Region<'d> {
    /// The data after the region (its members, section and section offset).
    after: &'d [u8],
    /// The absolute end of `after`.
    after_end: usize,
    /// The section of deferred bodies (empty when the region has none) and its absolute start.
    section: &'d [u8],
    section_at: usize,
    /// The absolute start of the members: section keys count from there.
    members_at: usize,
    /// Deferral state when the region opened.
    pub(crate) pending_start: usize,
    pub(crate) log_start: usize,
}

impl<'d> Reader<'d> {
    /// A reader over `data`, at its start.
    pub fn new(data: &'d [u8]) -> Self {
        let budget = data.len().saturating_mul(PREALLOCATION).saturating_add(4096);
        Self { rest: data, end: data.len(), depth: 0, regions: 0, structs: 0, defer: None, budget }
    }

    /// How many of `count` elements of `T` (the length was checked against the bytes left) a collection allocates
    /// before reading them; it grows as it reads the rest. Up front allocation is limited to 16 bytes of memory per
    /// byte of input for the whole read, so collections nested in collections that claim more elements than they
    /// hold cannot multiply it by their depth.
    #[inline]
    pub fn capacity<T>(&mut self, count: usize) -> usize {
        let size = core::mem::size_of::<T>().max(1);
        let n = count.min(self.budget.checked_div(size).unwrap_or(0));
        self.budget = self.budget.saturating_sub(n.saturating_mul(size));
        n
    }

    /// The position: bytes from the start of the data.
    #[inline]
    pub fn position(&self) -> usize {
        self.end.saturating_sub(self.rest.len())
    }

    /// The bytes left (in the current region, if one is open).
    #[inline]
    pub fn remaining(&self) -> usize {
        self.rest.len()
    }

    /// Whether bytes are left in the current region (or the data): a version-tolerant body reads its next member
    /// only then, and a keyed body its next entry.
    #[inline]
    pub fn has_more(&self) -> bool {
        !self.rest.is_empty()
    }

    /// Moves past `n` bytes and returns them.
    #[inline]
    pub fn take(&mut self, n: usize) -> Result<&'d [u8]> {
        match self.rest.split_at_checked(n) {
            Some((head, tail)) => {
                self.rest = tail;
                Ok(head)
            }
            None => Err(Error::end()),
        }
    }

    /// Moves past `N` bytes and returns them as an array: one length check for a run of fixed-size members.
    #[inline]
    pub fn array<const N: usize>(&mut self) -> Result<[u8; N]> {
        match self.rest.split_first_chunk::<N>() {
            Some((head, tail)) => {
                self.rest = tail;
                Ok(*head)
            }
            None => Err(Error::end()),
        }
    }

    /// Skips `n` bytes.
    #[inline]
    pub fn skip(&mut self, n: usize) -> Result<()> {
        self.take(n).map(|_| ())
    }

    #[inline]
    pub fn u8(&mut self) -> Result<u8> {
        match self.rest.split_first() {
            Some((&b, tail)) => {
                self.rest = tail;
                Ok(b)
            }
            None => Err(Error::end()),
        }
    }

    #[inline]
    pub fn i8(&mut self) -> Result<i8> {
        self.u8().map(|b| b as i8)
    }

    /// A `bool`: 0 or 1. Any other byte is rejected (.NET reads it as true; see the README).
    #[inline]
    pub fn bool(&mut self) -> Result<bool> {
        match self.u8()? {
            0 => Ok(false),
            1 => Ok(true),
            b => Err(Error::new(ErrorKind::InvalidBool, i32::from(b))),
        }
    }

    #[inline]
    pub fn u16(&mut self) -> Result<u16> {
        self.array().map(u16::from_le_bytes)
    }
    #[inline]
    pub fn i16(&mut self) -> Result<i16> {
        self.array().map(i16::from_le_bytes)
    }
    #[inline]
    pub fn u32(&mut self) -> Result<u32> {
        self.array().map(u32::from_le_bytes)
    }
    #[inline]
    pub fn i32(&mut self) -> Result<i32> {
        self.array().map(i32::from_le_bytes)
    }
    #[inline]
    pub fn u64(&mut self) -> Result<u64> {
        self.array().map(u64::from_le_bytes)
    }
    #[inline]
    pub fn i64(&mut self) -> Result<i64> {
        self.array().map(i64::from_le_bytes)
    }
    /// `UInt128`: the low 64 bits, then the high 64 bits (little-endian `u128`).
    #[inline]
    pub fn u128(&mut self) -> Result<u128> {
        self.array().map(u128::from_le_bytes)
    }
    /// `Int128`: the low 64 bits, then the high 64 bits (little-endian `i128`).
    #[inline]
    pub fn i128(&mut self) -> Result<i128> {
        self.array().map(i128::from_le_bytes)
    }
    #[inline]
    pub fn f32(&mut self) -> Result<f32> {
        self.array().map(f32::from_le_bytes)
    }
    #[inline]
    pub fn f64(&mut self) -> Result<f64> {
        self.array().map(f64::from_le_bytes)
    }

    /// A presence byte (sealed classes, `T?`, `Lazy<T>`, `Tuple`): true when a value follows. Bytes other than 0 and
    /// 1 are rejected (.NET reads them as 1; see the README).
    #[inline]
    pub fn present(&mut self) -> Result<bool> {
        match self.u8()? {
            0 => Ok(false),
            1 => Ok(true),
            b => Err(Error::new(ErrorKind::InvalidPresence, i32::from(b))),
        }
    }

    /// A collection length: `None` for null (-1). Fails for any other negative length, and for a length whose
    /// elements, at `min_size` bytes each (at least 1), cannot fit in the bytes left: so a length never allocates
    /// more than the data could fill.
    #[inline]
    pub fn length(&mut self, min_size: usize) -> Result<Option<usize>> {
        let length = self.i32()?;
        if length < 0 {
            return if length == -1 { Ok(None) } else { Err(Error::length(length)) };
        }
        let count = length as u32 as u64;
        let size = count.saturating_mul(min_size.max(1) as u64);
        if size > self.rest.len() as u64 {
            return Err(Error::length(length));
        }
        Ok(Some(length as u32 as usize))
    }

    /// A length that must not be null (an `i32` count).
    #[inline]
    pub fn length_not_null(&mut self, min_size: usize) -> Result<usize> {
        self.length(min_size)?.ok_or_else(Error::null)
    }

    // ------------------------------------------------------------------ strings

    /// A string in the default encoding: null (-1), empty (0), `~n` then `n` Latin-1 bytes, or `n` then `n` UTF-16
    /// code units. Unpaired surrogates become U+FFFD; [`Reader::wide_string`] keeps them. Always inlined (the text
    /// is decoded out of line): returned through memory, `Result<Option<String>>` is read back in pieces that span
    /// the stores that wrote it, which stalls.
    #[inline(always)]
    pub fn string(&mut self) -> Result<Option<String>> {
        let header = self.i32()?;
        if header < 0 {
            if header == -1 {
                return Ok(None);
            }
            let bytes = self.latin1_bytes(header)?;
            return Ok(Some(crate::strings::latin1(bytes)));
        }
        if header == 0 {
            return Ok(Some(String::new()));
        }
        let units = self.utf16_units(header)?;
        Ok(Some(crate::strings::utf16_lossy(units)))
    }

    /// A string in the default encoding as its UTF-16 code units, unpaired surrogates included (lossless).
    pub fn wide_string(&mut self) -> Result<Option<Vec<u16>>> {
        let header = self.i32()?;
        if header < 0 {
            if header == -1 {
                return Ok(None);
            }
            let bytes = self.latin1_bytes(header)?;
            return Ok(Some(bytes.iter().map(|&b| u16::from(b)).collect()));
        }
        if header == 0 {
            return Ok(Some(Vec::new()));
        }
        let units = self.utf16_units(header)?;
        Ok(Some(units.iter().map(|u| u16::from_le_bytes(*u)).collect()))
    }

    /// The bytes of a Latin-1 string of header `header` (<= -2).
    #[inline]
    fn latin1_bytes(&mut self, header: i32) -> Result<&'d [u8]> {
        let size = !header as u32 as usize;
        self.take_or(size, header)
    }

    /// The bytes of `count` (> 0) UTF-16 code units.
    #[inline]
    fn utf16_units(&mut self, count: i32) -> Result<&'d [[u8; 2]]> {
        let size = (count as u32 as usize).checked_mul(2).ok_or_else(|| Error::length(count))?;
        Ok(self.take_or(size, count)?.as_chunks::<2>().0)
    }

    /// Moves past `n` bytes and returns them, or fails with [`ErrorKind::InvalidLength`] for `length` (the length
    /// the count came from) when fewer are left.
    #[inline]
    fn take_or(&mut self, n: usize, length: i32) -> Result<&'d [u8]> {
        match self.rest.split_at_checked(n) {
            Some((head, tail)) => {
                self.rest = tail;
                Ok(head)
            }
            None => Err(Error::length(length)),
        }
    }

    /// A string written as UTF-8 (`[NinoUtf8]`): the char count (-1 null, 0 empty), then the byte count and the bytes.
    /// The bytes must be strict UTF-8, with `count <= bytes <= 3 * count`, and decode to exactly `count` UTF-16 code
    /// units.
    pub fn utf8(&mut self) -> Result<Option<String>> {
        let chars = self.i32()?;
        if chars <= 0 {
            return match chars {
                0 => Ok(Some(String::new())),
                -1 => Ok(None),
                _ => Err(Error::length(chars)),
            };
        }
        let size = self.i32()?;
        let chars_u = chars as u32 as u64;
        if size < chars || size as u32 as u64 > chars_u.saturating_mul(3) || size as u32 as usize > self.rest.len() {
            return Err(Error::length(size));
        }
        let bytes = self.take(size as u32 as usize)?;
        let text = core::str::from_utf8(bytes).map_err(|_| Error::of(ErrorKind::InvalidUtf8))?;
        if crate::strings::utf16_len(bytes) != chars_u {
            return Err(Error::of(ErrorKind::InvalidUtf8));
        }
        Ok(Some(text.to_owned()))
    }

    // ------------------------------------------------------------------ values

    /// Reads any [`NinoRead`] value.
    #[inline]
    pub fn read<T: NinoRead>(&mut self) -> Result<T> {
        T::read(self)
    }

    /// Reads a member that may hold deferred bodies (generated code passes the step from its type to the member,
    /// see [`crate::field`]); for other members it is [`Reader::read`].
    #[inline]
    pub fn read_in<T: NinoRead>(&mut self, step: fn(&mut dyn Any) -> Option<&mut dyn Any>) -> Result<T> {
        if !T::MAY_DEFER {
            return T::read(self);
        }
        let mark = self.mark();
        let value = T::read(self)?;
        self.trail(mark, Step::Field(step));
        Ok(value)
    }

    /// The deferral mark before reading a value that may hold deferred bodies (see [`Reader::trail`]).
    #[inline]
    pub fn mark(&self) -> usize {
        match &self.defer {
            Some(d) => d.pending_len(),
            None => 0,
        }
    }

    /// After reading a value that may hold deferred bodies, from `mark`: records how to reach the value from the
    /// object or container it was read into, so the bodies can be put in place later. Cheap when nothing was
    /// deferred.
    #[inline]
    pub fn trail(&mut self, mark: usize, step: Step) {
        if let Some(d) = &mut self.defer {
            d.trail(mark, step);
        }
    }

    /// Reads the body of the class `T` (after its header), counting it as an open body: or, `128` bodies deep,
    /// creates the instance and reads its body later (see the specification, 7.10), when `T` can be deferred.
    #[inline]
    pub fn read_class<T: NinoReadBody>(&mut self) -> Result<T> {
        let depth = self.depth;
        if depth >= DEPTH_INLINE {
            return self.read_class_deep();
        }
        self.read_body_at(depth)
    }

    /// [`Reader::read_class`] 128 bodies deep or more (out of line: rare).
    #[inline(never)]
    fn read_class_deep<T: NinoReadBody>(&mut self) -> Result<T> {
        let depth = self.depth;
        if T::DEFERRABLE {
            let position = self.position();
            let regions = self.regions;
            self.deferrals().defer::<T>(position, regions);
            return Ok(T::default());
        }
        if depth.saturating_add(self.structs) >= DEPTH_MAX {
            return Err(Error::of(ErrorKind::TooDeep));
        }
        self.read_body_at(depth)
    }

    /// Reads the body of `T` as an open body at `depth` (the reader's), then the bodies deferred in it when it is
    /// the outermost.
    #[inline]
    fn read_body_at<T: NinoReadBody>(&mut self, depth: u32) -> Result<T> {
        self.depth = depth.saturating_add(1);
        let mut value = T::default();
        value.read_body(self)?;
        self.depth = depth;
        if depth == 0 && self.regions == 0 && self.defer.is_some() {
            crate::defer::flush_top(self, &mut value)?;
        }
        Ok(value)
    }

    /// Reads a sealed class: its presence byte (0 = null), then its body ([`Reader::read_class`]).
    #[inline]
    pub fn read_sealed<T: NinoReadBody>(&mut self) -> Result<Option<T>> {
        if self.present()? {
            self.read_class().map(Some)
        } else {
            Ok(None)
        }
    }

    /// Reads a sealed class that must not be null (an error when its presence byte is 0).
    #[inline]
    pub fn read_sealed_value<T: NinoReadBody>(&mut self) -> Result<T> {
        if self.present()? {
            self.read_class()
        } else {
            Err(Error::null())
        }
    }

    /// Generated code: opens the body of a struct that can hold itself (through a collection, an option or a
    /// polymorphic slot). Such nesting is counted with the class bodies, up to 512. Call [`Reader::exit_struct`]
    /// after the body.
    #[inline]
    pub fn enter_struct(&mut self) -> Result<()> {
        self.structs = self.structs.saturating_add(1);
        if self.structs.saturating_add(self.depth) > DEPTH_MAX {
            return Err(Error::of(ErrorKind::TooDeep));
        }
        Ok(())
    }

    /// Closes a body opened by [`Reader::enter_struct`].
    #[inline]
    pub fn exit_struct(&mut self) {
        self.structs = self.structs.saturating_sub(1);
    }

    pub(crate) fn deferrals(&mut self) -> &mut ReadDeferrals {
        self.defer.get_or_insert_with(Default::default)
    }

    // ------------------------------------------------------------------ regions

    /// Opens a length-prefixed region (a version-tolerant or keyed body, or a keyed member): until
    /// [`Reader::end_region`], reads stop at its members' end. A length with bit 31 set is a region followed by a
    /// section of deferred bodies (only the outermost region's is read).
    pub fn begin_region(&mut self) -> Result<Region<'d>> {
        let length = self.u32()?;
        let at = self.position();
        let (members, section, after) = if length & SECTION_FLAG == 0 {
            let (members, after) =
                self.rest.split_at_checked(length as usize).ok_or_else(|| Error::length(length as i32))?;
            (members, &[][..], after)
        } else {
            // members | section | i32 offset of the section (= length of the members)
            let total = (length & !SECTION_FLAG) as usize;
            let (region, after) = self.rest.split_at_checked(total).ok_or_else(|| Error::length(length as i32))?;
            let (body, offset) = region.split_last_chunk::<4>().ok_or_else(|| Error::length(length as i32))?;
            let offset = u32::from_le_bytes(*offset);
            let (members, section) = body
                .split_at_checked(offset as usize)
                .ok_or_else(|| Error::new(ErrorKind::InvalidSection, offset as i32))?;
            // Only the outermost region holds a section, and a writer flags one only when it holds a body.
            if self.regions != 0 {
                return Err(Error::new(ErrorKind::InvalidSection, length as i32));
            }
            check_section(section, members.len())?;
            (members, section, after)
        };
        let region = Region {
            after,
            after_end: self.end,
            section,
            section_at: at.saturating_add(members.len()),
            members_at: at,
            pending_start: self.mark(),
            log_start: self.defer.as_ref().map_or(0, |d| d.log_len()),
        };
        self.regions = self.regions.saturating_add(1);
        self.rest = members;
        self.end = at.saturating_add(members.len());
        Ok(region)
    }

    /// Ends a region opened by [`Reader::begin_region`]: skips what is left of it (members of a newer version,
    /// unknown keyed entries) and, at the end of the outermost region, reads the bodies deferred inside it from its
    /// section into `owner` (the object whose body the region is).
    pub fn end_region<O: Any>(&mut self, region: Region<'d>, owner: &mut O) -> Result<()> {
        self.rest = region.after;
        self.end = region.after_end;
        self.regions = self.regions.saturating_sub(1);
        if self.regions == 0 && self.defer.is_some() {
            crate::defer::end_outer_region(self, region, owner)?;
        }
        Ok(())
    }

    /// Ends the region of a keyed member that was read: like [`Reader::end_region`], but the value must have
    /// taken every byte of it (an entry longer than its value is not something a writer produces).
    pub fn end_member<O: Any>(&mut self, region: Region<'d>, owner: &mut O) -> Result<()> {
        if !self.rest.is_empty() {
            return Err(Error::length(i32::try_from(self.rest.len()).unwrap_or(i32::MAX)));
        }
        self.end_region(region, owner)
    }

    /// Runs `read` on the bytes of one section entry (`entry`, starting at absolute offset `at`), at `depth`, as
    /// inside a region; restores the reader's state afterwards.
    pub(crate) fn read_entry<R>(
        &mut self,
        entry: &'d [u8],
        at: usize,
        depth: u32,
        read: impl FnOnce(&mut Self) -> Result<R>,
    ) -> Result<R> {
        let saved = (self.rest, self.end, self.depth, self.regions);
        self.rest = entry;
        self.end = at.saturating_add(entry.len());
        self.depth = depth;
        self.regions = 1;
        let result = read(self);
        (self.rest, self.end, self.depth, self.regions) = saved;
        result
    }
}

impl<'d> Region<'d> {
    pub(crate) fn section(&self) -> (&'d [u8], usize, usize) {
        (self.section, self.section_at, self.members_at)
    }
}

/// Checks that a section is one or more entries `{key, len, body}` filling it exactly, each keyed by a position
/// up to the entry itself (right after its object's header, in the members or an earlier entry). The checks
/// catch a section offset that points into the members or at the section's end.
fn check_section(mut section: &[u8], members: usize) -> Result<()> {
    if section.is_empty() {
        return Err(Error::of(ErrorKind::InvalidSection));
    }
    let mut at = members;
    while !section.is_empty() {
        let key = take_i32(&mut section)?;
        let length = take_i32(&mut section)?;
        let size = usize::try_from(length).map_err(|_| Error::length(length))?;
        if usize::try_from(key).map_or(true, |k| k > at) {
            return Err(Error::new(ErrorKind::InvalidSection, key));
        }
        section = section.get(size..).ok_or_else(|| Error::length(length))?;
        at = at.saturating_add(8).saturating_add(size);
    }
    Ok(())
}

/// Takes an `i32` from the front of `bytes`.
#[inline]
pub(crate) fn take_i32(bytes: &mut &[u8]) -> Result<i32> {
    match bytes.split_first_chunk::<4>() {
        Some((head, tail)) => {
            *bytes = tail;
            Ok(i32::from_le_bytes(*head))
        }
        None => Err(Error::end()),
    }
}

impl core::fmt::Debug for Reader<'_> {
    fn fmt(&self, f: &mut core::fmt::Formatter<'_>) -> core::fmt::Result {
        f.debug_struct("Reader")
            .field("position", &self.position())
            .field("remaining", &self.rest.len())
            .field("depth", &self.depth)
            .field("regions", &self.regions)
            .finish_non_exhaustive()
    }
}

impl core::fmt::Debug for Region<'_> {
    fn fmt(&self, f: &mut core::fmt::Formatter<'_>) -> core::fmt::Result {
        f.debug_struct("Region").field("members_at", &self.members_at).field("section", &self.section.len()).finish()
    }
}
