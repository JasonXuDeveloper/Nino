//! The traits generated code implements and calls.

use crate::error::Result;
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

    /// Reads `count` values (the length was checked against [`NinoRead::MIN_SIZE`]). Fixed-size types read them
    /// all with one length check.
    #[inline]
    fn read_many(r: &mut Reader<'_>, count: usize) -> Result<Vec<Self>> {
        crate::impls::read_each(r, count)
    }
}

/// A value that can be written in Nino's format.
pub trait NinoWrite {
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
        for v in values {
            v.write(w)?;
        }
        Ok(())
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
