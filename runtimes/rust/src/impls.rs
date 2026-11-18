//! Trait implementations for primitives, strings, wrappers, tuples and the standard collections.

use core::any::Any;

use crate::defer::Step;
use crate::error::{Error, ErrorKind, Result};
use crate::reader::Reader;
use crate::traits::NinoRead;

mod maps;
mod numbers;
mod sequences;
mod sets;
mod strings;
mod tuples;
mod wrappers;

pub(crate) use maps::map;
pub(crate) use sequences::{
    grow_fixed, prefetches, read_each, read_each_into, read_fixed, read_fixed_into, write_counted_fixed, write_fixed,
    write_prefetched,
};
#[cfg(feature = "indexmap")]
pub(crate) use sets::{fill_set, FillSet};

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

/// A [`Step::Dyn`] from a closure.
pub fn dyn_step<F>(f: F) -> Step
where
    F: Fn(&mut dyn Any) -> Option<&mut dyn Any> + 'static,
{
    Step::Dyn(std::rc::Rc::new(f))
}
