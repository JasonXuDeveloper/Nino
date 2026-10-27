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
    #[inline]
    fn write<'a>(&'a self, w: &mut Writer<'a>) -> Result<()> {
        w.i8(*self);
        Ok(())
    }

    #[inline]
    fn write_many<'a>(values: &'a [Self], w: &mut Writer<'a>) -> Result<()> {
        w.reserve_capacity(values.len());
        for &v in values {
            w.i8(v);
        }
        Ok(())
    }
}

impl NinoRead for bool {
    const MIN_SIZE: usize = 1;

    #[inline]
    fn read(r: &mut Reader<'_>) -> Result<Self> {
        r.bool()
    }

    #[inline]
    fn read_many(r: &mut Reader<'_>, count: usize) -> Result<Vec<Self>> {
        let bytes = r.take(count)?;
        if let Some(&b) = bytes.iter().find(|&&b| b > 1) {
            return Err(Error::new(ErrorKind::InvalidBool, i32::from(b)));
        }
        Ok(bytes.iter().map(|&b| b != 0).collect())
    }
}

impl NinoWrite for bool {
    #[inline]
    fn write<'a>(&'a self, w: &mut Writer<'a>) -> Result<()> {
        w.bool(*self);
        Ok(())
    }

    #[inline]
    fn write_many<'a>(values: &'a [Self], w: &mut Writer<'a>) -> Result<()> {
        w.reserve_capacity(values.len());
        for &v in values {
            w.bool(v);
        }
        Ok(())
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
    #[inline]
    fn write<'a>(&'a self, w: &mut Writer<'a>) -> Result<()> {
        w.string(Some(self))
    }

    #[inline]
    fn write_many<'a>(values: &'a [Self], w: &mut Writer<'a>) -> Result<()> {
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
        Some(values) => {
            w.length(values.len())?;
            T::write_many(values, w)
        }
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

            #[inline]
            fn read(r: &mut Reader<'_>) -> Result<Self> {
                Ok(($(
                    r.read_in::<$name>(|p| Some(&mut p.downcast_mut::<Self>()?.$index as &mut dyn Any))?,
                )+))
            }
        }

        impl<$($name: NinoWrite),+> NinoWrite for ($($name,)+) {
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
