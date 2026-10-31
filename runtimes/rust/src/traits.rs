//! The traits generated code implements and calls.

use crate::error::{Error, Result};
use crate::reader::Reader;
use crate::writer::Writer;

/// A value that can be read from Nino's bytes under its declared type.
///
/// Implemented here for primitives, strings and the std collections, and by generated code for every
/// `[NinoType]`. `'static` because values with deferred bodies are moved into place through `Any`.
pub trait NinoRead: Sized + 'static {
    /// Whether a value of this type can hold a class body that is deferred (a class that can nest without bound,
    /// or a container of one). When false, containers skip the bookkeeping that puts deferred bodies in place.
    const MAY_DEFER: bool = false;

    /// The fewest bytes a value takes on the wire (at least 1 is assumed): a collection's length is rejected when
    /// that many bytes per element are not left, before anything is allocated.
    const MIN_SIZE: usize = 1;

    /// The size of every value on the wire when it is the same for all of them (numbers, `bool`, dates, raw
    /// structs, tuples of these), else 0. A list of a fixed-size type is read as one block: one length check, then
    /// [`NinoRead::decode_fixed`] on each element's bytes.
    const FIXED_SIZE: usize = 0;

    /// Reads a value from exactly [`NinoRead::FIXED_SIZE`] bytes (called only when that is not 0).
    #[inline]
    fn decode_fixed(bytes: &[u8]) -> Result<Self> {
        Self::read(&mut Reader::new(bytes))
    }

    /// Reads a value.
    fn read(r: &mut Reader<'_>) -> Result<Self>;

    /// Reads a value that may be null, in this type's null encoding: a presence byte by default (nullable values,
    /// sealed classes, `Lazy<T>`, `Tuple`); a length or header of -1 for strings and collections, a type id of 0
    /// for polymorphic types. Deferred bodies in it are reached from the value inside the `Some`.
    #[inline]
    fn read_option(r: &mut Reader<'_>) -> Result<Option<Self>> {
        if r.present()? {
            Self::read(r).map(Some)
        } else {
            Ok(None)
        }
    }

    /// [`NinoRead::read`] into a `Box`. Generated code for a class reads the body in place on the heap
    /// ([`Reader::read_sealed_boxed_value`]) instead of moving the value there.
    #[inline]
    fn read_boxed(r: &mut Reader<'_>) -> Result<Box<Self>> {
        Self::read(r).map(Box::new)
    }

    /// [`NinoRead::read_option`] into a `Box` ([`Reader::read_sealed_boxed`] for a class).
    #[inline]
    fn read_boxed_option(r: &mut Reader<'_>) -> Result<Option<Box<Self>>> {
        Ok(Self::read_option(r)?.map(Box::new))
    }

    /// Reads a value into `self`, reusing what it holds where it can: the members of a struct or class (each read
    /// into its own current value), the elements and the buffer of a `Vec` (by index), the buffers of maps and sets
    /// (cleared, then refilled), and the body of a polymorphic slot's value when the data holds the same type. What
    /// `NinoDeserializer.Deserialize(data, ref value)` does in .NET. Anything else (numbers, strings, tracked
    /// objects, a body deferred past 128 class bodies) is replaced, as this default does. On an error, `self` is a
    /// valid value holding what was read before it: read into it again, or drop it.
    #[inline]
    fn read_into(&mut self, r: &mut Reader<'_>) -> Result<()> {
        *self = Self::read(r)?;
        Ok(())
    }

    /// [`NinoRead::read_into`] for a value that may be null ([`NinoRead::read_option`]): an existing value is read
    /// into when the data holds one, created when the slot holds `None`.
    #[inline]
    fn read_option_into(slot: &mut Option<Self>, r: &mut Reader<'_>) -> Result<()> {
        *slot = Self::read_option(r)?;
        Ok(())
    }

    /// [`NinoRead::read_into`] for a `Box` (by default, into its content).
    #[inline]
    fn read_boxed_into(value: &mut Box<Self>, r: &mut Reader<'_>) -> Result<()> {
        (**value).read_into(r)
    }

    /// [`NinoRead::read_option_into`] for a `Box`.
    #[inline]
    fn read_boxed_option_into(slot: &mut Option<Box<Self>>, r: &mut Reader<'_>) -> Result<()> {
        *slot = Self::read_boxed_option(r)?;
        Ok(())
    }

    /// Reads `count` values into `values`, reusing its buffer and its first elements (each read into, by index); the
    /// rest are read new, and elements past `count` dropped.
    #[inline]
    fn read_many_into(r: &mut Reader<'_>, count: usize, values: &mut Vec<Self>) -> Result<()> {
        if Self::FIXED_SIZE != 0 {
            return crate::impls::read_fixed_into(r, count, values);
        }
        crate::impls::read_each_into(r, count, values)
    }

    /// Reads `count` values (the length was checked against [`NinoRead::MIN_SIZE`]). Fixed-size types read them
    /// all with one length check.
    #[inline]
    fn read_many(r: &mut Reader<'_>, count: usize) -> Result<Vec<Self>> {
        if Self::FIXED_SIZE != 0 {
            return crate::impls::read_fixed(r, count);
        }
        crate::impls::read_each(r, count)
    }
}

/// A value that can be written in Nino's format.
pub trait NinoWrite {
    /// As [`NinoRead::FIXED_SIZE`]: the size of every value on the wire when it is the same for all of them, else 0.
    /// A slice of a fixed-size type is written as one block, [`NinoWrite::encode_fixed`] on each element's bytes.
    const FIXED_SIZE: usize = 0;

    /// Writes the value into `out`, exactly [`NinoWrite::FIXED_SIZE`] bytes, zeroed (called only when that is not 0).
    #[inline]
    fn encode_fixed(&self, out: &mut [u8]) {
        let mut w = Writer::new();
        if self.write(&mut w).is_ok() {
            let bytes = w.as_slice();
            if let Some(dst) = out.get_mut(..bytes.len()) {
                dst.copy_from_slice(bytes);
            }
        }
    }

    /// The bytes a write of the value reads through its pointers, estimated (a `String`'s or `Vec`'s buffer counts
    /// 64, a `Box` its target's size and 64 for the target's own): 0 for a value with none, the default. A slice of
    /// at least 512 values with some, whose estimated data (the elements and these bytes) outgrows the last-level
    /// cache, is written prefetching ahead ([`NinoWrite::prefetch`], [`NinoWrite::prefetch_members`]). Set by
    /// `#[derive(NinoWrite)]` and nino-gen to the sum of the members'.
    const PREFETCH_BYTES: usize = 0;

    /// Prefetches the memory the value points to (a `String`'s text, a `Box`'s target, a struct's members' targets).
    /// Nothing by default.
    #[inline(always)]
    fn prefetch(&self) {}

    /// Prefetches the memory that what the value points to points to (a `Box`'s target's members' targets): the
    /// second stage, for an element whose first stage was prefetched a few elements earlier. Nothing by default.
    #[inline(always)]
    fn prefetch_members(&self) {}

    /// Writes the value. `'a` is the lifetime of the value: deferred bodies are written later in the same call.
    fn write<'a>(&'a self, w: &mut Writer<'a>) -> Result<()>;

    /// Writes a value that may be null, in this type's null encoding (see [`NinoRead::read_option`]).
    #[inline]
    fn write_option<'a>(value: Option<&'a Self>, w: &mut Writer<'a>) -> Result<()>
    where
        Self: Sized,
    {
        match value {
            Some(v) => {
                w.present(true);
                v.write(w)
            }
            None => {
                w.present(false);
                Ok(())
            }
        }
    }

    /// Writes the elements of a slice (no length). Fixed-size types write them as one block.
    #[inline]
    fn write_many<'a>(values: &'a [Self], w: &mut Writer<'a>) -> Result<()>
    where
        Self: Sized,
    {
        if Self::FIXED_SIZE != 0 {
            crate::impls::write_fixed(values, w);
            return Ok(());
        }
        if Self::PREFETCH_BYTES != 0 && crate::impls::prefetches::<Self>(values.len()) {
            return crate::impls::write_prefetched(values, w);
        }
        for v in values {
            v.write(w)?;
        }
        Ok(())
    }

    /// Writes the length of a slice (fails past `i32::MAX`), then its elements ([`NinoWrite::write_many`]).
    /// Fixed-size types write both as one block.
    #[inline]
    fn write_counted<'a>(values: &'a [Self], w: &mut Writer<'a>) -> Result<()>
    where
        Self: Sized,
    {
        if Self::FIXED_SIZE != 0 {
            return crate::impls::write_counted_fixed(values, w);
        }
        w.length(values.len())?;
        Self::write_many(values, w)
    }
}

/// The [`NinoRead::FIXED_SIZE`] (or [`NinoWrite::FIXED_SIZE`]) of a value made of items of these sizes one after
/// another, a tuple or a plain struct: their sum, or 0 (not of fixed size) when one of them is 0 or there are none.
/// Generated and derived code states a plain struct's size with it.
pub const fn sum_fixed_sizes(mut sizes: &[usize]) -> usize {
    let mut sum = 0usize;
    while let [size, rest @ ..] = sizes {
        if *size == 0 {
            return 0;
        }
        sum = sum.saturating_add(*size);
        sizes = rest;
    }
    sum
}

/// Decodes the next item of a fixed-size value ([`NinoRead::decode_fixed`] on its first [`NinoRead::FIXED_SIZE`]
/// bytes) and moves `bytes` past it: how generated and derived code reads a plain struct's members from its bytes.
#[inline]
pub fn decode_member<T: NinoRead>(bytes: &mut &[u8]) -> Result<T> {
    let (item, rest) = bytes.split_at_checked(T::FIXED_SIZE).ok_or_else(Error::end)?;
    *bytes = rest;
    T::decode_fixed(item)
}

/// Encodes the next item of a fixed-size value ([`NinoWrite::encode_fixed`] into the first [`NinoWrite::FIXED_SIZE`]
/// bytes of `out`) and moves `out` past it: how generated and derived code writes a plain struct's members.
#[inline]
pub fn encode_member<T: NinoWrite + ?Sized>(value: &T, out: &mut &mut [u8]) {
    if let Some((item, rest)) = core::mem::take(out).split_at_mut_checked(T::FIXED_SIZE) {
        value.encode_fixed(item);
        *out = rest;
    }
}

/// Reading the body of a `[NinoType]` class: what follows its header (presence byte or type id). Generated for every
/// class (`#[derive(NinoRead)]` or the templates); [`Reader::read_class`] counts the open bodies and defers the deep
/// ones.
pub trait NinoReadBody: Default + 'static {
    /// Whether .NET creates the object before reading its members, so a body nested 128 deep is written later
    /// (the specification, 7.10.1): false for types built by a constructor taking member values (records with a
    /// primary constructor, init-only or read-only members) and types compared by value (records, `GetHashCode`
    /// overrides, `IComparable`). It must be what .NET decides for the type, or deep data is read wrongly. The same
    /// value as [`NinoWriteBody::DEFERRABLE`].
    const DEFERRABLE: bool = true;

    /// The type id written before the body where the declared type is polymorphic (0 when the type has none).
    const TYPE_ID: i32 = 0;

    /// Reads the members into `self` (a new `Default` instance).
    fn read_body(&mut self, r: &mut Reader<'_>) -> Result<()>;
}

/// Writing the body of a `[NinoType]` class (see [`NinoReadBody`]); [`Writer::write_class`] counts the open bodies
/// and defers the deep ones.
pub trait NinoWriteBody: 'static {
    /// As [`NinoReadBody::DEFERRABLE`].
    const DEFERRABLE: bool = true;

    /// As [`NinoReadBody::TYPE_ID`].
    const TYPE_ID: i32 = 0;

    /// Writes the members.
    fn write_body<'a>(&'a self, w: &mut Writer<'a>) -> Result<()>;
}
