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
            let mark = r.mark();
            values.push(T::read(r)?);
            r.trail(mark, Step::Index(index, i));
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
/// element's size and the bytes it points to, [`NinoWrite::PREFETCH`]) than the last-level cache holds
/// ([`crate::simd::last_level_cache`]). Data that fits is likely in the cache already, where a prefetch per element
/// costs more than it saves.
#[inline]
pub(crate) fn prefetches<T: NinoWrite>(len: usize) -> bool {
    len >= 512 && outgrows_cache(len, core::mem::size_of::<T>().saturating_add(T::PREFETCH))
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
    let mark = r.mark();
    let value = T::read(r)?;
    if r.mark() != mark {
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

// ---------------------------------------------------------------------- numbers

macro_rules! number {
    ($($t:ty, $n:literal, $method:ident;)*) => {$(
        impl NinoRead for $t {
            const MIN_SIZE: usize = $n;
            const FIXED_SIZE: usize = $n;

            #[inline]
            fn decode_fixed(bytes: &[u8]) -> Result<Self> {
                bytes.first_chunk::<$n>().map(|b| <$t>::from_le_bytes(*b)).ok_or_else(Error::end)
            }

            #[inline]
            fn read(r: &mut Reader<'_>) -> Result<Self> {
                r.$method()
            }

            #[inline]
            fn read_many(r: &mut Reader<'_>, count: usize) -> Result<Vec<Self>> {
                let size = count.checked_mul($n).ok_or_else(Error::too_large)?;
                let bytes = r.take(size)?;
                Ok(bytes.as_chunks::<$n>().0.iter().map(|c| <$t>::from_le_bytes(*c)).collect())
            }
        }

        impl NinoWrite for $t {
            const FIXED_SIZE: usize = $n;

            #[inline]
            fn encode_fixed(&self, out: &mut [u8]) {
                if let Some(dst) = out.first_chunk_mut::<$n>() {
                    *dst = self.to_le_bytes();
                }
            }

            #[inline]
            fn write<'a>(&'a self, w: &mut Writer<'a>) -> Result<()> {
                w.$method(*self);
                Ok(())
            }

            #[inline]
            fn write_many<'a>(values: &'a [Self], w: &mut Writer<'a>) -> Result<()> {
                w.encoded(values, |v| v.to_le_bytes());
                Ok(())
            }

            #[inline]
            fn write_counted<'a>(values: &'a [Self], w: &mut Writer<'a>) -> Result<()> {
                w.counted(values, |v| v.to_le_bytes())
            }
        }
    )*};
}

number! {
    i16, 2, i16;
    u16, 2, u16;
    i32, 4, i32;
    u32, 4, u32;
    i64, 8, i64;
    u64, 8, u64;
    i128, 16, i128;
    u128, 16, u128;
    f32, 4, f32;
    f64, 8, f64;
}

impl NinoRead for u8 {
    const MIN_SIZE: usize = 1;
    const FIXED_SIZE: usize = 1;

    #[inline]
    fn decode_fixed(bytes: &[u8]) -> Result<Self> {
        bytes.first().copied().ok_or_else(Error::end)
    }

    #[inline]
    fn read(r: &mut Reader<'_>) -> Result<Self> {
        r.u8()
    }

    /// A `byte[]`: one copy.
    #[inline]
    fn read_many(r: &mut Reader<'_>, count: usize) -> Result<Vec<Self>> {
        r.take(count).map(<[u8]>::to_vec)
    }
}

impl NinoWrite for u8 {
    const FIXED_SIZE: usize = 1;

    #[inline]
    fn encode_fixed(&self, out: &mut [u8]) {
        if let Some(b) = out.first_mut() {
            *b = *self;
        }
    }

    #[inline]
    fn write<'a>(&'a self, w: &mut Writer<'a>) -> Result<()> {
        w.u8(*self);
        Ok(())
    }

    #[inline]
    fn write_many<'a>(values: &'a [Self], w: &mut Writer<'a>) -> Result<()> {
        w.bytes(values);
        Ok(())
    }
}

impl NinoRead for i8 {
    const MIN_SIZE: usize = 1;
    const FIXED_SIZE: usize = 1;

    #[inline]
    fn decode_fixed(bytes: &[u8]) -> Result<Self> {
        bytes.first().map(|&b| b as i8).ok_or_else(Error::end)
    }

    #[inline]
    fn read(r: &mut Reader<'_>) -> Result<Self> {
        r.i8()
    }

    #[inline]
    fn read_many(r: &mut Reader<'_>, count: usize) -> Result<Vec<Self>> {
        Ok(r.take(count)?.iter().map(|&b| b as i8).collect())
    }
}

impl NinoWrite for i8 {
    const FIXED_SIZE: usize = 1;

    #[inline]
    fn encode_fixed(&self, out: &mut [u8]) {
        if let Some(b) = out.first_mut() {
            *b = *self as u8;
        }
    }

    #[inline]
    fn write<'a>(&'a self, w: &mut Writer<'a>) -> Result<()> {
        w.i8(*self);
        Ok(())
    }

    #[inline]
    fn write_many<'a>(values: &'a [Self], w: &mut Writer<'a>) -> Result<()> {
        w.encoded(values, |&v| [v as u8]);
        Ok(())
    }

    #[inline]
    fn write_counted<'a>(values: &'a [Self], w: &mut Writer<'a>) -> Result<()> {
        w.counted(values, |&v| [v as u8])
    }
}

impl NinoRead for bool {
    const MIN_SIZE: usize = 1;
    const FIXED_SIZE: usize = 1;

    #[inline]
    fn decode_fixed(bytes: &[u8]) -> Result<Self> {
        match bytes.first() {
            Some(0) => Ok(false),
            Some(1) => Ok(true),
            Some(&b) => Err(Error::new(ErrorKind::InvalidBool, i32::from(b))),
            None => Err(Error::end()),
        }
    }

    #[inline]
    fn read(r: &mut Reader<'_>) -> Result<Self> {
        r.bool()
    }

    /// One pass that ORs every byte (no early exit, so it runs a vector at a time): a byte other than 0 and 1 sets
    /// a bit above the lowest. Only then is the bad byte looked for.
    #[inline]
    fn read_many(r: &mut Reader<'_>, count: usize) -> Result<Vec<Self>> {
        let bytes = r.take(count)?;
        if bytes.iter().fold(0u8, |a, &b| a | b) > 1 {
            return Err(invalid_bool(bytes));
        }
        Ok(bytes.iter().map(|&b| b != 0).collect())
    }
}

/// The error for the first byte of `bytes` that is not 0 or 1.
#[cold]
#[inline(never)]
fn invalid_bool(bytes: &[u8]) -> Error {
    let b = bytes.iter().copied().find(|&b| b > 1).unwrap_or(2);
    Error::new(ErrorKind::InvalidBool, i32::from(b))
}

impl NinoWrite for bool {
    const FIXED_SIZE: usize = 1;

    #[inline]
    fn encode_fixed(&self, out: &mut [u8]) {
        if let Some(b) = out.first_mut() {
            *b = u8::from(*self);
        }
    }

    #[inline]
    fn write<'a>(&'a self, w: &mut Writer<'a>) -> Result<()> {
        w.bool(*self);
        Ok(())
    }

    #[inline]
    fn write_many<'a>(values: &'a [Self], w: &mut Writer<'a>) -> Result<()> {
        w.encoded(values, |&v| [u8::from(v)]);
        Ok(())
    }

    #[inline]
    fn write_counted<'a>(values: &'a [Self], w: &mut Writer<'a>) -> Result<()> {
        w.counted(values, |&v| [u8::from(v)])
    }
}

// ---------------------------------------------------------------------- strings

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
}

impl NinoWrite for String {
    const PREFETCH: usize = 64;

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

// ---------------------------------------------------------------------- Option, Box

fn option_inner<T: 'static>(v: &mut dyn Any) -> Option<&mut dyn Any> {
    v.downcast_mut::<Option<T>>()?.as_mut().map(|x| x as &mut dyn Any)
}

fn box_inner<T: 'static>(v: &mut dyn Any) -> Option<&mut dyn Any> {
    Some(&mut **v.downcast_mut::<Box<T>>()? as &mut dyn Any)
}

/// A value that may be null, in `T`'s null encoding (see [`NinoRead::read_option`]).
impl<T: NinoRead> NinoRead for Option<T> {
    const MAY_DEFER: bool = T::MAY_DEFER;
    const MIN_SIZE: usize = 1;

    #[inline]
    fn read(r: &mut Reader<'_>) -> Result<Self> {
        if !T::MAY_DEFER {
            return T::read_option(r);
        }
        let mark = r.mark();
        let value = T::read_option(r)?;
        r.trail(mark, Step::Field(option_inner::<T>));
        Ok(value)
    }
}

impl<T: NinoWrite> NinoWrite for Option<T> {
    const PREFETCH: usize = T::PREFETCH;

    #[inline(always)]
    fn prefetch(&self) {
        if let Some(v) = self {
            v.prefetch();
        }
    }

    #[inline(always)]
    fn prefetch_members(&self) {
        if let Some(v) = self {
            v.prefetch_members();
        }
    }

    #[inline]
    fn write<'a>(&'a self, w: &mut Writer<'a>) -> Result<()> {
        T::write_option(self.as_ref(), w)
    }
}

/// `T` on the heap (a class that holds itself): the same bytes as `T`.
impl<T: NinoRead> NinoRead for Box<T> {
    const MAY_DEFER: bool = T::MAY_DEFER;
    const MIN_SIZE: usize = T::MIN_SIZE;

    #[inline]
    fn read(r: &mut Reader<'_>) -> Result<Self> {
        if !T::MAY_DEFER {
            return T::read_boxed(r);
        }
        let mark = r.mark();
        let value = T::read_boxed(r)?;
        r.trail(mark, Step::Field(box_inner::<T>));
        Ok(value)
    }

    #[inline]
    fn read_option(r: &mut Reader<'_>) -> Result<Option<Self>> {
        if !T::MAY_DEFER {
            return T::read_boxed_option(r);
        }
        let mark = r.mark();
        let value = T::read_boxed_option(r)?;
        r.trail(mark, Step::Field(box_inner::<T>));
        Ok(value)
    }
}

impl<T: NinoWrite> NinoWrite for Box<T> {
    // Its target's size and 64 for the target's own targets (not `T::PREFETCH`: a type that holds itself through a
    // `Box` would define the constant through itself).
    const PREFETCH: usize = core::mem::size_of::<T>().saturating_add(64);

    #[inline(always)]
    fn prefetch(&self) {
        crate::simd::prefetch(&**self);
    }

    #[inline(always)]
    fn prefetch_members(&self) {
        (**self).prefetch();
    }

    #[inline]
    fn write<'a>(&'a self, w: &mut Writer<'a>) -> Result<()> {
        (**self).write(w)
    }

    #[inline]
    fn write_option<'a>(value: Option<&'a Self>, w: &mut Writer<'a>) -> Result<()> {
        T::write_option(value.map(|b| &**b), w)
    }
}

// ---------------------------------------------------------------------- sequences

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
}

impl<T: NinoWrite> NinoWrite for Vec<T> {
    const PREFETCH: usize = 64;

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
}

impl<T: NinoWrite> NinoWrite for VecDeque<T> {
    fn write<'a>(&'a self, w: &mut Writer<'a>) -> Result<()> {
        w.length(self.len())?;
        let (front, back) = self.as_slices();
        T::write_many(front, w)?;
        T::write_many(back, w)
    }

    fn write_option<'a>(value: Option<&'a Self>, w: &mut Writer<'a>) -> Result<()> {
        match value {
            None => {
                w.null_length();
                Ok(())
            }
            Some(v) => v.write(w),
        }
    }
}

macro_rules! set {
    ($set:ident, $($bound:tt)*) => {
        /// A set: its length, then each element. A repeated element is rejected.
        impl<T: NinoRead + $($bound)*> NinoRead for $set<T> {
            const MIN_SIZE: usize = 4;

            fn read(r: &mut Reader<'_>) -> Result<Self> {
                Self::read_option(r)?.ok_or_else(Error::null)
            }

            fn read_option(r: &mut Reader<'_>) -> Result<Option<Self>> {
                let Some(count) = r.length(T::MIN_SIZE)? else { return Ok(None) };
                let mut set = set!(@new $set, r.capacity::<T>(count));
                for _ in 0..count {
                    if !set.insert(read_key::<T>(r)?) {
                        return Err(Error::of(ErrorKind::DuplicateKey));
                    }
                }
                Ok(Some(set))
            }
        }

        impl<T: NinoWrite> NinoWrite for $set<T> {
            fn write<'a>(&'a self, w: &mut Writer<'a>) -> Result<()> {
                w.length(self.len())?;
                for v in self {
                    v.write(w)?;
                }
                Ok(())
            }

            fn write_option<'a>(value: Option<&'a Self>, w: &mut Writer<'a>) -> Result<()> {
                match value {
                    None => {
                        w.null_length();
                        Ok(())
                    }
                    Some(v) => v.write(w),
                }
            }
        }
    };
    (@new HashSet, $n:expr) => { HashSet::with_capacity($n) };
    (@new BTreeSet, $n:expr) => { { let _ = $n; BTreeSet::new() } };
}

set!(HashSet, Eq + Hash);
set!(BTreeSet, Ord);

/// A dictionary: its length, then each key and value. A repeated key is rejected.
impl<K, V, S> NinoRead for HashMap<K, V, S>
where
    K: NinoRead + Eq + Hash + Clone,
    V: NinoRead,
    S: BuildHasher + Default + 'static,
{
    const MAY_DEFER: bool = V::MAY_DEFER;
    const MIN_SIZE: usize = 4;

    fn read(r: &mut Reader<'_>) -> Result<Self> {
        Self::read_option(r)?.ok_or_else(Error::null)
    }

    fn read_option(r: &mut Reader<'_>) -> Result<Option<Self>> {
        let Some(count) = r.length(K::MIN_SIZE.saturating_add(V::MIN_SIZE))? else { return Ok(None) };
        let mut map = HashMap::with_capacity_and_hasher(r.capacity::<(K, V)>(count), S::default());
        if entries_fixed_read::<K, V>() {
            if ENTRY_SLICES {
                read_entries_fixed(r, count, |key, value| match map.entry(key) {
                    hash_map::Entry::Occupied(_) => Err(Error::of(ErrorKind::DuplicateKey)),
                    hash_map::Entry::Vacant(e) => {
                        e.insert(value);
                        Ok(())
                    }
                })?;
            } else {
                // A plain insert, as measured on Neoverse-N2. The map is dropped on the error, so a repeated key's
                // replaced value is never seen.
                read_entries_fixed(r, count, |key, value| match map.insert(key, value) {
                    Some(_) => Err(Error::of(ErrorKind::DuplicateKey)),
                    None => Ok(()),
                })?;
            }
            return Ok(Some(map));
        }
        for _ in 0..count {
            let key = read_key::<K>(r)?;
            let value = if V::MAY_DEFER {
                let mark = r.mark();
                let value = V::read(r)?;
                if r.mark() != mark {
                    let at = key.clone();
                    r.trail(mark, dyn_step(move |m| Some(m.downcast_mut::<Self>()?.get_mut(&at)? as &mut dyn Any)));
                }
                value
            } else {
                V::read(r)?
            };
            match map.entry(key) {
                hash_map::Entry::Occupied(_) => return Err(Error::of(ErrorKind::DuplicateKey)),
                hash_map::Entry::Vacant(e) => {
                    e.insert(value);
                }
            }
        }
        Ok(Some(map))
    }
}

/// A sorted dictionary: its length, then each key and value. A repeated key is rejected.
impl<K, V> NinoRead for BTreeMap<K, V>
where
    K: NinoRead + Ord + Clone,
    V: NinoRead,
{
    const MAY_DEFER: bool = V::MAY_DEFER;
    const MIN_SIZE: usize = 4;

    fn read(r: &mut Reader<'_>) -> Result<Self> {
        Self::read_option(r)?.ok_or_else(Error::null)
    }

    fn read_option(r: &mut Reader<'_>) -> Result<Option<Self>> {
        let Some(count) = r.length(K::MIN_SIZE.saturating_add(V::MIN_SIZE))? else { return Ok(None) };
        let mut map = BTreeMap::new();
        if entries_fixed_read::<K, V>() {
            read_entries_fixed(r, count, |key, value| match map.insert(key, value) {
                Some(_) => Err(Error::of(ErrorKind::DuplicateKey)),
                None => Ok(()),
            })?;
            return Ok(Some(map));
        }
        for _ in 0..count {
            let key = read_key::<K>(r)?;
            let value = if V::MAY_DEFER {
                let mark = r.mark();
                let value = V::read(r)?;
                if r.mark() != mark {
                    let at = key.clone();
                    r.trail(mark, dyn_step(move |m| Some(m.downcast_mut::<Self>()?.get_mut(&at)? as &mut dyn Any)));
                }
                value
            } else {
                V::read(r)?
            };
            if map.insert(key, value).is_some() {
                return Err(Error::of(ErrorKind::DuplicateKey));
            }
        }
        Ok(Some(map))
    }
}

impl<K: NinoWrite, V: NinoWrite, S> NinoWrite for HashMap<K, V, S> {
    fn write<'a>(&'a self, w: &mut Writer<'a>) -> Result<()> {
        if entries_fixed(K::FIXED_SIZE, V::FIXED_SIZE) {
            return write_entries_fixed(self.iter(), w);
        }
        w.length(self.len())?;
        for (k, v) in self {
            k.write(w)?;
            v.write(w)?;
        }
        Ok(())
    }

    fn write_option<'a>(value: Option<&'a Self>, w: &mut Writer<'a>) -> Result<()> {
        match value {
            None => {
                w.null_length();
                Ok(())
            }
            Some(v) => v.write(w),
        }
    }
}

impl<K: NinoWrite, V: NinoWrite> NinoWrite for BTreeMap<K, V> {
    fn write<'a>(&'a self, w: &mut Writer<'a>) -> Result<()> {
        if entries_fixed(K::FIXED_SIZE, V::FIXED_SIZE) {
            return write_entries_fixed(self.iter(), w);
        }
        w.length(self.len())?;
        for (k, v) in self {
            k.write(w)?;
            v.write(w)?;
        }
        Ok(())
    }

    fn write_option<'a>(value: Option<&'a Self>, w: &mut Writer<'a>) -> Result<()> {
        match value {
            None => {
                w.null_length();
                Ok(())
            }
            Some(v) => v.write(w),
        }
    }
}

// ---------------------------------------------------------------------- tuples

macro_rules! tuple {
    ($($name:ident $index:tt),+) => {
        /// `ValueTuple`, `KeyValuePair` (and, inside an `Option`, `Tuple`): the items in order, no header.
        impl<$($name: NinoRead),+> NinoRead for ($($name,)+) {
            const MAY_DEFER: bool = false $(|| $name::MAY_DEFER)+;
            const MIN_SIZE: usize = 0usize $(.saturating_add($name::MIN_SIZE))+;
            /// The items' sizes added up when every item is fixed-size.
            const FIXED_SIZE: usize =
                if false $(|| <$name as NinoRead>::FIXED_SIZE == 0)+ { 0 } else { 0usize $(.saturating_add(<$name as NinoRead>::FIXED_SIZE))+ };

            // The last item's tail is not read.
            #[allow(unused_assignments)]
            #[inline]
            fn decode_fixed(bytes: &[u8]) -> Result<Self> {
                let mut rest = bytes;
                Ok(($({
                    let (item, tail) = rest.split_at_checked(<$name as NinoRead>::FIXED_SIZE).ok_or_else(Error::end)?;
                    rest = tail;
                    $name::decode_fixed(item)?
                },)+))
            }

            #[inline]
            fn read(r: &mut Reader<'_>) -> Result<Self> {
                Ok(($(
                    r.read_in::<$name>(|p| Some(&mut p.downcast_mut::<Self>()?.$index as &mut dyn Any))?,
                )+))
            }
        }

        impl<$($name: NinoWrite),+> NinoWrite for ($($name,)+) {
            const FIXED_SIZE: usize =
                if false $(|| <$name as NinoWrite>::FIXED_SIZE == 0)+ { 0 } else { 0usize $(.saturating_add(<$name as NinoWrite>::FIXED_SIZE))+ };

            // The last item's tail is not written.
            #[allow(unused_assignments)]
            #[inline]
            fn encode_fixed(&self, out: &mut [u8]) {
                let mut rest = out;
                $(
                    let Some((item, tail)) = rest.split_at_mut_checked(<$name as NinoWrite>::FIXED_SIZE) else { return };
                    rest = tail;
                    self.$index.encode_fixed(item);
                )+
            }

            #[inline]
            fn write<'a>(&'a self, w: &mut Writer<'a>) -> Result<()> {
                $(self.$index.write(w)?;)+
                Ok(())
            }
        }
    };
}

tuple!(A 0);
tuple!(A 0, B 1);
tuple!(A 0, B 1, C 2);
tuple!(A 0, B 1, C 2, D 3);
tuple!(A 0, B 1, C 2, D 3, E 4);
tuple!(A 0, B 1, C 2, D 3, E 4, F 5);
tuple!(A 0, B 1, C 2, D 3, E 4, F 5, G 6);

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
