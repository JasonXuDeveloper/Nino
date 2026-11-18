use core::any::Any;
use std::collections::VecDeque;

use crate::defer::Step;
use crate::error::{Error, Result};
use crate::reader::Reader;
use crate::traits::{NinoRead, NinoWrite};
use crate::writer::Writer;

fn vec_item<T: 'static>(v: &mut dyn Any, i: usize) -> Option<&mut dyn Any> {
    v.downcast_mut::<Vec<T>>()?.get_mut(i).map(|x| x as &mut dyn Any)
}

fn deque_item<T: 'static>(v: &mut dyn Any, i: usize) -> Option<&mut dyn Any> {
    v.downcast_mut::<VecDeque<T>>()?.get_mut(i).map(|x| x as &mut dyn Any)
}

/// Reads `count` elements one by one into a `Vec` (deferred bodies in them reached by `index`).
#[inline]
fn read_into_vec<T: NinoRead>(
    r: &mut Reader<'_>,
    count: usize,
    index: fn(&mut dyn Any, usize) -> Option<&mut dyn Any>,
) -> Result<Vec<T>> {
    let mut values = Vec::with_capacity(r.capacity::<T>(count));
    for i in 0..count {
        if T::MAY_DEFER {
            let mark = r.defer_mark();
            values.push(T::read(r)?);
            r.defer_trail(mark, Step::Index(index, i));
        } else {
            values.push(T::read(r)?);
        }
    }
    Ok(values)
}

/// [`NinoRead::read_many`]'s default: each element in turn.
#[inline]
pub(crate) fn read_each<T: NinoRead>(r: &mut Reader<'_>, count: usize) -> Result<Vec<T>> {
    read_into_vec(r, count, vec_item::<T>)
}

/// [`NinoRead::read_many_into`]'s default: the first elements read into, by index, the rest read new.
#[inline]
pub(crate) fn read_each_into<T: NinoRead>(r: &mut Reader<'_>, count: usize, values: &mut Vec<T>) -> Result<()> {
    if values.is_empty() {
        // Nothing to read into: the plain read, its allocation budget included.
        *values = T::read_many(r, count)?;
        return Ok(());
    }
    values.truncate(count);
    for (i, v) in values.iter_mut().enumerate() {
        if T::MAY_DEFER {
            let mark = r.defer_mark();
            v.read_into(r)?;
            r.defer_trail(mark, Step::Index(vec_item::<T>, i));
        } else {
            v.read_into(r)?;
        }
    }
    let have = values.len();
    // `have <= count`: no wrap.
    values.reserve(r.capacity::<T>(count.wrapping_sub(have)));
    for i in have..count {
        if T::MAY_DEFER {
            let mark = r.defer_mark();
            values.push(T::read(r)?);
            r.defer_trail(mark, Step::Index(vec_item::<T>, i));
        } else {
            values.push(T::read(r)?);
        }
    }
    Ok(())
}

/// [`NinoRead::read_many_into`]'s default for a fixed-size type: [`read_fixed`] into the `Vec`'s buffer.
#[inline]
pub(crate) fn read_fixed_into<T: NinoRead>(r: &mut Reader<'_>, count: usize, values: &mut Vec<T>) -> Result<()> {
    if values.capacity() < count {
        // Not enough room: the plain read, its allocation budget included.
        *values = read_fixed(r, count)?;
        return Ok(());
    }
    let size = count.checked_mul(T::FIXED_SIZE).ok_or_else(Error::too_large)?;
    let bytes = r.take(size)?;
    crate::simd::decode_block_into(values, bytes.chunks_exact(T::FIXED_SIZE), T::decode_fixed)
}

/// [`NinoRead::read_many`]'s default for a fixed-size type: the elements' bytes with one length check, then
/// [`NinoRead::decode_fixed`] on each.
#[inline]
pub(crate) fn read_fixed<T: NinoRead>(r: &mut Reader<'_>, count: usize) -> Result<Vec<T>> {
    let size = count.checked_mul(T::FIXED_SIZE).ok_or_else(Error::too_large)?;
    let capacity = r.capacity::<T>(count);
    let bytes = r.take(size)?;
    if capacity < count {
        return grow_fixed(bytes.chunks_exact(T::FIXED_SIZE), capacity, T::decode_fixed);
    }
    crate::simd::decode_block(bytes.chunks_exact(T::FIXED_SIZE), T::decode_fixed)
}

/// A block of fixed-size elements past the allocation budget ([`Reader::capacity`]): `capacity` of them allocated up
/// front, the `Vec` grown for the rest.
#[cold]
#[inline(never)]
pub(crate) fn grow_fixed<C, T>(
    chunks: impl Iterator<Item = C>,
    capacity: usize,
    decode: impl Fn(C) -> Result<T>,
) -> Result<Vec<T>> {
    let mut values = Vec::with_capacity(capacity);
    for c in chunks {
        values.push(decode(c)?);
    }
    Ok(values)
}

/// The elements a prefetching loop runs ahead: at element `i`, element `i + 2 * PREFETCH_AHEAD`'s targets are asked
/// for ([`NinoWrite::prefetch`]), and element `i + PREFETCH_AHEAD`'s targets' own ([`NinoWrite::prefetch_members`],
/// their lines asked for a batch earlier). The same as the Go runtime's.
const PREFETCH_AHEAD: usize = 8;

/// Whether a slice of `len` elements is written prefetching ahead: at least 512 elements, and more estimated data (an
/// element's size and the bytes it points to, [`NinoWrite::PREFETCH_BYTES`]) than the last-level cache holds
/// ([`crate::simd::last_level_cache`]). Data that fits is likely in the cache already, where a prefetch per element
/// costs more than it saves.
#[inline]
pub(crate) fn prefetches<T: NinoWrite>(len: usize) -> bool {
    len >= 512 && outgrows_cache(len, core::mem::size_of::<T>().saturating_add(T::PREFETCH_BYTES))
}

/// Whether `len` elements of `bytes` each are more than the last-level cache holds: out of line, so that a short
/// list's code is one compare.
#[cold]
#[inline(never)]
fn outgrows_cache(len: usize, bytes: usize) -> bool {
    len.saturating_mul(bytes) > crate::simd::last_level_cache()
}

/// [`NinoWrite::write_many`] for a long slice of elements that point elsewhere ([`prefetches`]): the same bytes,
/// each element's targets asked for [`PREFETCH_AHEAD`] elements before they are written, in two stages. Out of line,
/// so that the loop of every other slice stays as it was.
#[inline(never)]
pub(crate) fn write_prefetched<'a, T: NinoWrite>(values: &'a [T], w: &mut Writer<'a>) -> Result<()> {
    for (i, v) in values.iter().enumerate() {
        // `i < values.len() <= isize::MAX`: the sums cannot wrap.
        if let Some(ahead) = values.get(i.wrapping_add(2 * PREFETCH_AHEAD)) {
            ahead.prefetch();
        }
        if let Some(next) = values.get(i.wrapping_add(PREFETCH_AHEAD)) {
            next.prefetch_members();
        }
        v.write(w)?;
    }
    Ok(())
}

/// [`NinoWrite::write_many`]'s default for a fixed-size type: room for every element, then
/// [`NinoWrite::encode_fixed`] into each element's bytes.
#[inline]
pub(crate) fn write_fixed<T: NinoWrite>(values: &[T], w: &mut Writer<'_>) {
    if T::FIXED_SIZE <= crate::simd::FIXED_STACK {
        crate::simd::extend_fixed(w.buffer(), [], values, T::FIXED_SIZE, T::encode_fixed);
        return;
    }
    let out = w.reserve(values.len().saturating_mul(T::FIXED_SIZE));
    for (v, o) in values.iter().zip(out.chunks_exact_mut(T::FIXED_SIZE)) {
        v.encode_fixed(o);
    }
}

/// [`NinoWrite::write_counted`]'s default for a fixed-size type: the length and the elements in one block.
#[inline]
pub(crate) fn write_counted_fixed<T: NinoWrite>(values: &[T], w: &mut Writer<'_>) -> Result<()> {
    let length = i32::try_from(values.len()).map_err(|_| Error::too_large())?;
    if T::FIXED_SIZE <= crate::simd::FIXED_STACK {
        crate::simd::extend_fixed(w.buffer(), length.to_le_bytes(), values, T::FIXED_SIZE, T::encode_fixed);
        return Ok(());
    }
    let out = w.reserve(values.len().saturating_mul(T::FIXED_SIZE).saturating_add(4));
    let Some((head, out)) = out.split_first_chunk_mut::<4>() else { return Ok(()) };
    *head = length.to_le_bytes();
    for (v, o) in values.iter().zip(out.chunks_exact_mut(T::FIXED_SIZE)) {
        v.encode_fixed(o);
    }
    Ok(())
}

/// Writes a length and the elements; `None` is null.
#[inline]
fn write_seq<'a, T: NinoWrite>(values: Option<&'a [T]>, w: &mut Writer<'a>) -> Result<()> {
    match values {
        None => {
            w.null_length();
            Ok(())
        }
        Some(values) => T::write_counted(values, w),
    }
}

/// An array, list or other sequence: its length (-1 null), then each element.
impl<T: NinoRead> NinoRead for Vec<T> {
    const MAY_DEFER: bool = T::MAY_DEFER;
    const MIN_SIZE: usize = 4;

    #[inline]
    fn read(r: &mut Reader<'_>) -> Result<Self> {
        let count = r.length_not_null(T::MIN_SIZE)?;
        T::read_many(r, count)
    }

    #[inline]
    fn read_option(r: &mut Reader<'_>) -> Result<Option<Self>> {
        match r.length(T::MIN_SIZE)? {
            None => Ok(None),
            Some(count) => T::read_many(r, count).map(Some),
        }
    }

    #[inline]
    fn read_into(&mut self, r: &mut Reader<'_>) -> Result<()> {
        let count = r.length_not_null(T::MIN_SIZE)?;
        T::read_many_into(r, count, self)
    }

    #[inline]
    fn read_option_into(slot: &mut Option<Self>, r: &mut Reader<'_>) -> Result<()> {
        match (r.length(T::MIN_SIZE)?, slot) {
            (None, slot) => *slot = None,
            (Some(count), Some(values)) => T::read_many_into(r, count, values)?,
            (Some(count), slot) => *slot = Some(T::read_many(r, count)?),
        }
        Ok(())
    }
}

impl<T: NinoWrite> NinoWrite for Vec<T> {
    const PREFETCH_BYTES: usize = 64;

    #[inline(always)]
    fn prefetch(&self) {
        crate::simd::prefetch(self.as_ptr());
    }

    #[inline]
    fn write<'a>(&'a self, w: &mut Writer<'a>) -> Result<()> {
        write_seq(Some(self.as_slice()), w)
    }

    #[inline]
    fn write_option<'a>(value: Option<&'a Self>, w: &mut Writer<'a>) -> Result<()> {
        write_seq(value.map(Vec::as_slice), w)
    }
}

impl<T: NinoWrite> NinoWrite for [T] {
    #[inline]
    fn write<'a>(&'a self, w: &mut Writer<'a>) -> Result<()> {
        write_seq(Some(self), w)
    }
}

/// A queue (`Queue<T>`, `LinkedList<T>`): its length, then each element from the front.
impl<T: NinoRead> NinoRead for VecDeque<T> {
    const MAY_DEFER: bool = T::MAY_DEFER;
    const MIN_SIZE: usize = 4;

    fn read(r: &mut Reader<'_>) -> Result<Self> {
        let count = r.length_not_null(T::MIN_SIZE)?;
        if T::MAY_DEFER {
            read_into_vec(r, count, deque_item::<T>).map(VecDeque::from)
        } else {
            T::read_many(r, count).map(VecDeque::from)
        }
    }

    fn read_option(r: &mut Reader<'_>) -> Result<Option<Self>> {
        match r.length(T::MIN_SIZE)? {
            None => Ok(None),
            Some(count) if T::MAY_DEFER => read_into_vec(r, count, deque_item::<T>).map(|v| Some(v.into())),
            Some(count) => T::read_many(r, count).map(|v| Some(v.into())),
        }
    }

    /// Cleared and refilled, keeping its buffer.
    fn read_into(&mut self, r: &mut Reader<'_>) -> Result<()> {
        let count = r.length_not_null(T::MIN_SIZE)?;
        refill_deque(self, r, count)
    }

    fn read_option_into(slot: &mut Option<Self>, r: &mut Reader<'_>) -> Result<()> {
        let Some(queue) = slot else {
            *slot = Self::read_option(r)?;
            return Ok(());
        };
        match r.length(T::MIN_SIZE)? {
            None => *slot = None,
            Some(count) => refill_deque(queue, r, count)?,
        }
        Ok(())
    }
}

/// Empties `queue` and reads `count` elements into it, keeping its buffer when it has room.
fn refill_deque<T: NinoRead>(queue: &mut VecDeque<T>, r: &mut Reader<'_>, count: usize) -> Result<()> {
    queue.clear();
    if queue.capacity() < count {
        queue.reserve(r.capacity::<T>(count));
    }
    for i in 0..count {
        if T::MAY_DEFER {
            let mark = r.defer_mark();
            queue.push_back(T::read(r)?);
            r.defer_trail(mark, Step::Index(deque_item::<T>, i));
        } else {
            queue.push_back(T::read(r)?);
        }
    }
    Ok(())
}

impl<T: NinoWrite> NinoWrite for VecDeque<T> {
    fn write<'a>(&'a self, w: &mut Writer<'a>) -> Result<()> {
        w.length(self.len())?;
        let (front, back) = self.as_slices();
        T::write_many(front, w)?;
        T::write_many(back, w)
    }

    fn write_option<'a>(value: Option<&'a Self>, w: &mut Writer<'a>) -> Result<()> {
        w.or_null_length(value)
    }
}

#[cfg(test)]
#[allow(clippy::indexing_slicing, clippy::arithmetic_side_effects, clippy::unwrap_used)]
mod tests {
    use crate::{serialize, NinoWrite, Writer};

    /// The prefetching loops write the same bytes as their elements written one by one: strings (in batches),
    /// optional strings and boxed strings (two stages), with lengths that end the batches and the look-ahead
    /// anywhere, and lists too short to look ahead at all.
    #[test]
    fn prefetched_lists() {
        let kinds = ["", "a", "é", "字", "🎉 строка", "crème brûlée"];
        let text = |i: usize| format!("{}{}", kinds[i % kinds.len()], i % 97);
        fn check<T: NinoWrite>(values: &[T], prefetched: impl for<'a> Fn(&'a [T], &mut Writer<'a>)) {
            let mut expected = Vec::new();
            values.iter().for_each(|v| expected.extend(serialize(v).unwrap()));
            let mut w = Writer::new();
            prefetched(values, &mut w);
            assert_eq!(w.into_vec(), expected, "{} elements", values.len());
        }
        for n in [0usize, 1, 7, 8, 9, 15, 16, 17, 23, 24, 25, 1000, 1007, 1016, 1023] {
            let strings: Vec<String> = (0..n).map(text).collect();
            check(&strings, |v: &[String], w: &mut Writer<'_>| crate::strings::write_strings_prefetched(w, v).unwrap());
            let options: Vec<Option<String>> = (0..n).map(|i| (i % 5 != 0).then(|| text(i))).collect();
            check(&options, |v: &[Option<String>], w: &mut Writer<'_>| super::write_prefetched(v, w).unwrap());
            let boxes: Vec<Option<Box<String>>> = (0..n).map(|i| (i % 7 != 0).then(|| Box::new(text(i)))).collect();
            check(&boxes, |v: &[Option<Box<String>>], w: &mut Writer<'_>| super::write_prefetched(v, w).unwrap());
        }
    }

    /// The last-level cache size is read (on Linux) or assumed, never zero.
    #[test]
    fn last_level_cache_is_known() {
        assert!(crate::simd::last_level_cache() >= 1 << 20);
    }
}
