use crate::error::{Error, Result};
use crate::reader::Reader;
use crate::traits::{NinoRead, NinoWrite};
use crate::writer::Writer;

use super::sequences::prefetches;

impl NinoRead for String {
    const MIN_SIZE: usize = 1;

    #[inline]
    fn read(r: &mut Reader<'_>) -> Result<Self> {
        r.string()?.ok_or_else(Error::null)
    }

    #[inline]
    fn read_option(r: &mut Reader<'_>) -> Result<Option<Self>> {
        r.string()
    }

    #[inline]
    fn read_many(r: &mut Reader<'_>, count: usize) -> Result<Vec<Self>> {
        r.strings(count)
    }

    /// Strings are read new (a list of them by the batched reader when the `Vec` is empty); the `Vec`'s buffer is
    /// kept.
    #[inline]
    fn read_many_into(r: &mut Reader<'_>, count: usize, values: &mut Vec<Self>) -> Result<()> {
        if values.capacity() < count {
            *values = r.strings(count)?;
            return Ok(());
        }
        crate::impls::read_each_into(r, count, values)
    }
}

impl NinoWrite for String {
    const PREFETCH_BYTES: usize = 64;

    #[inline(always)]
    fn prefetch(&self) {
        crate::simd::prefetch(self.as_ptr());
    }

    #[inline]
    fn write<'a>(&'a self, w: &mut Writer<'a>) -> Result<()> {
        w.string(Some(self))
    }

    #[inline]
    fn write_many<'a>(values: &'a [Self], w: &mut Writer<'a>) -> Result<()> {
        if prefetches::<Self>(values.len()) {
            return crate::strings::write_strings_prefetched(w, values);
        }
        crate::strings::write_strings(w, values)
    }

    #[inline]
    fn write_option<'a>(value: Option<&'a Self>, w: &mut Writer<'a>) -> Result<()> {
        w.string(value.map(String::as_str))
    }
}

impl NinoWrite for str {
    #[inline]
    fn write<'a>(&'a self, w: &mut Writer<'a>) -> Result<()> {
        w.string(Some(self))
    }
}
