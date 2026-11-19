//! [`NinoRead`] and [`NinoWrite`] for primitives, strings, `Option`, `Box`, tuples and the std collections.

use core::any::Any;
use core::hash::{BuildHasher, Hash};
use std::collections::{hash_map, BTreeMap, BTreeSet, HashMap, HashSet, VecDeque};

use crate::defer::Step;
use crate::error::{Error, ErrorKind, Result};
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

/// Reads a value that is hashed or ordered (a dictionary key, a set element): a body deferred inside it could not
/// be put in place after the value is in the collection, so that is reported instead.
#[inline]
pub(crate) fn read_key<T: NinoRead>(r: &mut Reader<'_>) -> Result<T> {
    if !T::MAY_DEFER {
        return T::read(r);
    }
    let mark = r.defer_mark();
    let value = T::read(r)?;
    if r.defer_mark() != mark {
        return Err(Error::of(ErrorKind::Unsupported));
    }
    Ok(value)
}

/// Whether a map of `K` and `V` is written as one block of entries ([`write_entries_fixed`]): both fixed-size, an
/// entry small enough for [`crate::simd::extend_fixed_iter`]'s buffer.
const fn entries_fixed(key: usize, value: usize) -> bool {
    key != 0 && value != 0 && key.saturating_add(value) <= crate::simd::FIXED_STACK
}

/// A map's length and entries (fixed-size keys and values, [`entries_fixed`]) in one block: one reservation, each
/// key and value encoded into its bytes, no length check, `Result` or reservation an entry.
#[inline]
fn write_entries_fixed<'e, K, V, I>(entries: I, w: &mut Writer<'_>) -> Result<()>
where
    K: NinoWrite + 'e,
    V: NinoWrite + 'e,
    I: ExactSizeIterator<Item = (&'e K, &'e V)>,
{
    let length = i32::try_from(entries.len()).map_err(|_| Error::too_large())?;
    let size = K::FIXED_SIZE.saturating_add(V::FIXED_SIZE);
    crate::simd::extend_fixed_iter(w.buffer(), length.to_le_bytes(), entries, size, |(k, v), out| {
        if let Some((a, b)) = out.split_at_mut_checked(K::FIXED_SIZE) {
            k.encode_fixed(a);
            v.encode_fixed(b);
        }
    });
    Ok(())
}

/// Whether this target reads a map's fixed-size entries as a slice each, the key and value decoded from it and
/// inserted through the entry API: x86_64, where that measured faster, and 32-bit x86 (the same code). On Arm64 it was
/// 3–4% slower (Neoverse-N2), the entry held in a vector register across the hash call.
const ENTRY_SLICES: bool = cfg!(any(target_arch = "x86", target_arch = "x86_64"));

/// Whether a map's entries are read with one bounds check each ([`read_entries_fixed`]): keys and values of a fixed
/// size that is also their least, on x86 ([`ENTRY_SLICES`]) and, elsewhere, when an entry is 8 bytes (two `i32`s, an
/// `i64` and a `u32` and `u16`, ...), read as one array. Other entries off x86 keep the generic loop, which reads the
/// same entries in the same order.
const fn entries_fixed_read<K: NinoRead, V: NinoRead>() -> bool {
    (ENTRY_SLICES || K::FIXED_SIZE.saturating_add(V::FIXED_SIZE) == 8)
        && K::FIXED_SIZE != 0
        && V::FIXED_SIZE != 0
        && K::MIN_SIZE == K::FIXED_SIZE
        && V::MIN_SIZE == V::FIXED_SIZE
}

/// Reads `count` entries of fixed-size keys and values: one bounds check for each entry's bytes, its key and value
/// decoded from them and handed to `insert`, in order, so a cut entry, a bad value or a repeated key fails at the same
/// entry and with the same error as reading them one at a time. Off x86 an entry is 8 bytes, taken as one array: the
/// key and value stay in general-purpose registers (Neoverse-N2: 0.8% faster than the generic loop).
#[inline]
fn read_entries_fixed<K: NinoRead, V: NinoRead>(
    r: &mut Reader<'_>,
    count: usize,
    mut insert: impl FnMut(K, V) -> Result<()>,
) -> Result<()> {
    if !ENTRY_SLICES {
        for _ in 0..count {
            let entry = r.array::<8>()?;
            // Never `None`: `K::FIXED_SIZE + V::FIXED_SIZE` is 8 here ([`entries_fixed_read`]).
            let Some((k, v)) = entry.split_at_checked(K::FIXED_SIZE) else { break };
            insert(K::decode_fixed(k)?, V::decode_fixed(v)?)?;
        }
        return Ok(());
    }
    let size = K::FIXED_SIZE.saturating_add(V::FIXED_SIZE);
    for _ in 0..count {
        let entry = r.take(size)?;
        // Never `None`: an entry is `K::FIXED_SIZE + V::FIXED_SIZE` bytes.
        let Some((k, v)) = entry.split_at_checked(K::FIXED_SIZE) else { break };
        insert(K::decode_fixed(k)?, V::decode_fixed(v)?)?;
    }
    Ok(())
}

/// A [`Step::Dyn`] from a closure.
pub fn dyn_step<F>(f: F) -> Step
where
    F: Fn(&mut dyn Any) -> Option<&mut dyn Any> + 'static,
{
    Step::Dyn(std::rc::Rc::new(f))
}

// Keep these files in this module and in their original order. The map macro is shared with IndexMap.
// A module boundary also changes rustc's codegen unit partitioning, even for untouched functions (#248).
include!("impls/numbers.rs");
include!("impls/strings.rs");
include!("impls/wrappers.rs");
include!("impls/sequences.rs");
include!("impls/sets.rs");
include!("impls/maps.rs");
include!("impls/tuples.rs");

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
