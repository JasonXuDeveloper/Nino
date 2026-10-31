//! Reads and writes [Nino](https://github.com/JasonXuDeveloper/Nino)'s binary wire format: the bytes Nino v4 writes in
//! .NET, specified in `docs/spec/wire-format.md`.
//!
//! The runtime is what generated code calls: [`Reader`] and [`Writer`] for the primitives, strings and regions,
//! the [`NinoRead`]/[`NinoWrite`] traits (implemented here for primitives, strings, `Option`, `Box`, tuples and the
//! std collections, and by generated code for each `[NinoType]`), and [`NinoReadBody`]/[`NinoWriteBody`] for classes.
//!
//! ```
//! let bytes = nino::serialize(&vec![1i32, 2]).unwrap();
//! assert_eq!(bytes, [2, 0, 0, 0, 1, 0, 0, 0, 2, 0, 0, 0]);
//! let back: Vec<i32> = nino::deserialize(&bytes).unwrap();
//! assert_eq!(back, [1, 2]);
//! ```
//!
//! Guarantees: no input makes the reader panic, read out of bounds, allocate more than the data could fill, or
//! recurse without bound; a read either returns the whole value or an [`Error`]. The only `unsafe` code is `simd.rs`
//! (checked under Miri): the string writer's transcoding, and the reader's decoding of text into a `String` it
//! builds, from bytes the reader's safe code has already bounds-checked.

#![deny(unsafe_code)]
#![deny(
    clippy::indexing_slicing,
    clippy::unwrap_used,
    clippy::expect_used,
    clippy::panic,
    clippy::arithmetic_side_effects,
    clippy::unreachable,
    clippy::todo
)]
#![warn(missing_debug_implementations)]

mod convert;
mod defer;
mod error;
mod impls;
mod reader;
#[cfg(feature = "serde")]
mod serde_impls;
mod shared;
mod simd;
mod strings;
mod traits;
mod types;
mod writer;

pub use defer::Step;
pub use error::{Error, ErrorKind, Result};
pub use impls::dyn_step;
pub use reader::{Reader, Region, INLINE_DEPTH, MAX_DEPTH};
pub use shared::Shared;
pub use traits::{decode_member, encode_member, sum_fixed_sizes, NinoRead, NinoReadBody, NinoWrite, NinoWriteBody};
pub use types::{
    BigInteger, BitArray, DateOnly, DateTime, DateTimeKind, DateTimeOffset, Decimal, Grid, Guid, Half, Raw,
    RawNullables, Rune, TimeOnly, TimeSpan, Utf8, Version, WideString, MAX_TICKS, TICKS_PER_DAY, UNIX_EPOCH_TICKS,
};
pub use writer::Writer;

/// `#[derive(NinoRead, NinoWrite)]` (feature `derive`): see the README for the attributes.
#[cfg(feature = "derive")]
pub use nino_derive::{NinoRead, NinoWrite};

/// Serializes `value` under its declared type (what `NinoSerializer.Serialize<T>` writes in .NET).
pub fn serialize<T: NinoWrite + ?Sized>(value: &T) -> Result<Vec<u8>> {
    let mut out = Vec::new();
    serialize_into(value, &mut out)?;
    Ok(out)
}

/// Appends the bytes of `value` to `out` (reusing its capacity: no allocation once it is large enough). On error,
/// `out` is left as it was.
pub fn serialize_into<T: NinoWrite + ?Sized>(value: &T, out: &mut Vec<u8>) -> Result<()> {
    let start = out.len();
    let mut w = Writer::from_vec(core::mem::take(out));
    let result = value.write(&mut w);
    *out = w.into_vec();
    if result.is_err() {
        out.truncate(start);
    }
    result
}

/// Deserializes one value written by `NinoSerializer.Serialize<T>` (or [`serialize`]). Bytes after the value are
/// ignored, as .NET ignores them; [`deserialize_exact`] rejects them.
pub fn deserialize<T: NinoRead>(data: &[u8]) -> Result<T> {
    T::read(&mut Reader::new(data))
}

/// Deserializes one value into `value`, reusing what it holds ([`NinoRead::read_into`]: the members of a class or
/// struct, the buffers and elements of a `Vec`, the buffers of maps and sets, a polymorphic value of the same type),
/// as `NinoDeserializer.Deserialize(data, ref value)` does in .NET. Bytes after the value are ignored. On an error,
/// `value` is a valid value holding what was read before it: read into it again, or drop it.
pub fn deserialize_into<T: NinoRead>(data: &[u8], value: &mut T) -> Result<()> {
    value.read_into(&mut Reader::new(data))
}

/// [`deserialize`], failing when bytes are left after the value.
pub fn deserialize_exact<T: NinoRead>(data: &[u8]) -> Result<T> {
    let mut r = Reader::new(data);
    let value = T::read(&mut r)?;
    match r.remaining() {
        0 => Ok(value),
        n => Err(Error::new(ErrorKind::TrailingData, i32::try_from(n).unwrap_or(i32::MAX))),
    }
}

/// The type id of a type (or the key of a keyed member) from its name: FNV-1a over the UTF-16 code units of
/// `name`, low byte first, with 0 replaced by 1 (the specification, 7.2). Usable in constants.
// Indexes a valid `str` by its own UTF-8 sequence lengths: always in bounds.
#[allow(clippy::indexing_slicing, clippy::arithmetic_side_effects)]
pub const fn type_id(name: &str) -> i32 {
    let bytes = name.as_bytes();
    let mut hash: u32 = 0x811C_9DC5;
    let mut i = 0usize;
    while i < bytes.len() {
        // Decode one UTF-8 scalar (the input is a valid str).
        let b0 = bytes[i] as u32;
        let (c, n) = if b0 < 0x80 {
            (b0, 1)
        } else if b0 < 0xE0 {
            (((b0 & 0x1F) << 6) | (bytes[i + 1] as u32 & 0x3F), 2)
        } else if b0 < 0xF0 {
            (((b0 & 0x0F) << 12) | ((bytes[i + 1] as u32 & 0x3F) << 6) | (bytes[i + 2] as u32 & 0x3F), 3)
        } else {
            (
                ((b0 & 0x07) << 18)
                    | ((bytes[i + 1] as u32 & 0x3F) << 12)
                    | ((bytes[i + 2] as u32 & 0x3F) << 6)
                    | (bytes[i + 3] as u32 & 0x3F),
                4,
            )
        };
        i += n;
        if c >= 0x10000 {
            let v = c - 0x10000;
            hash = fnv_unit(hash, 0xD800 | (v >> 10));
            hash = fnv_unit(hash, 0xDC00 | (v & 0x3FF));
        } else {
            hash = fnv_unit(hash, c);
        }
    }
    if hash == 0 {
        1
    } else {
        hash as i32
    }
}

const fn fnv_unit(hash: u32, unit: u32) -> u32 {
    let hash = (hash ^ (unit & 0xFF)).wrapping_mul(0x0100_0193);
    (hash ^ (unit >> 8)).wrapping_mul(0x0100_0193)
}

/// A [`Step::Field`] from a type to one of its members, for [`Reader::read_member`]:
/// `self.next = r.read_member(nino::field!(Self, next))?`.
#[macro_export]
macro_rules! field {
    ($t:ty, $($member:tt)+) => {
        |p: &mut dyn ::core::any::Any| -> ::core::option::Option<&mut dyn ::core::any::Any> {
            ::core::option::Option::Some(&mut p.downcast_mut::<$t>()?.$($member)+ as &mut dyn ::core::any::Any)
        }
    };
}

/// The read methods of a sealed class's [`NinoRead`] impl (a class written behind a presence byte, never a type id),
/// each forwarding to the [`Reader`]'s sealed-class read (`read_sealed_value`, `read_sealed`, ...). Generated code
/// and the derive write it in the impl, after the constants:
/// `impl NinoRead for Record { const MAY_DEFER: bool = true; nino::sealed_read!(); }`.
#[macro_export]
macro_rules! sealed_read {
    () => {
        #[inline]
        fn read(r: &mut $crate::Reader<'_>) -> $crate::Result<Self> {
            r.read_sealed_value()
        }

        #[inline]
        fn read_option(r: &mut $crate::Reader<'_>) -> $crate::Result<::core::option::Option<Self>> {
            r.read_sealed()
        }

        #[inline]
        fn read_boxed(r: &mut $crate::Reader<'_>) -> $crate::Result<::std::boxed::Box<Self>> {
            r.read_sealed_boxed_value()
        }

        #[inline]
        fn read_boxed_option(
            r: &mut $crate::Reader<'_>,
        ) -> $crate::Result<::core::option::Option<::std::boxed::Box<Self>>> {
            r.read_sealed_boxed()
        }

        #[inline]
        fn read_into(&mut self, r: &mut $crate::Reader<'_>) -> $crate::Result<()> {
            r.read_sealed_into(self)
        }

        #[inline]
        fn read_option_into(slot: &mut ::core::option::Option<Self>, r: &mut $crate::Reader<'_>) -> $crate::Result<()> {
            r.read_sealed_option_into(slot)
        }

        #[inline]
        fn read_boxed_option_into(
            slot: &mut ::core::option::Option<::std::boxed::Box<Self>>,
            r: &mut $crate::Reader<'_>,
        ) -> $crate::Result<()> {
            r.read_sealed_boxed_option_into(slot)
        }
    };
}

/// The write methods of a sealed class's [`NinoWrite`] impl ([`sealed_read!`]'s counterpart), forwarding to
/// [`Writer::write_sealed`]: `impl NinoWrite for Record { nino::sealed_write!(); }`.
#[macro_export]
macro_rules! sealed_write {
    () => {
        #[inline]
        fn write<'a>(&'a self, w: &mut $crate::Writer<'a>) -> $crate::Result<()> {
            w.write_sealed(::core::option::Option::Some(self))
        }

        #[inline]
        fn write_option<'a>(value: ::core::option::Option<&'a Self>, w: &mut $crate::Writer<'a>) -> $crate::Result<()> {
            w.write_sealed(value)
        }
    };
}
