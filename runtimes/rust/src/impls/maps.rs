use core::any::Any;
use core::hash::{BuildHasher, Hash};
use std::collections::{hash_map, BTreeMap, HashMap};

use crate::error::{Error, ErrorKind, Result};
use crate::reader::Reader;
use crate::traits::{NinoRead, NinoWrite};
use crate::writer::Writer;

use super::{dyn_step, read_key};

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

/// `NinoRead` for a hash map type `$map<K, V, S>` whose keys have `$bound`: its length, then each key and value, read
/// into an empty map by `$fill(map, r, count)` (which rejects a repeated key); read into an existing map, it is cleared
/// and refilled, keeping its buffer. One body for `HashMap` and `IndexMap` (convert.rs).
macro_rules! map {
    ($(#[$doc:meta])* $map:ident, $fill:ident, $($bound:tt)*) => {
        $(#[$doc])*
        impl<K, V, S> NinoRead for $map<K, V, S>
        where
            K: NinoRead + $($bound)*,
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
                let mut map = $map::with_capacity_and_hasher(r.capacity::<(K, V)>(count), S::default());
                $fill(&mut map, r, count)?;
                Ok(Some(map))
            }

            /// Cleared and refilled, keeping its buffer.
            fn read_into(&mut self, r: &mut Reader<'_>) -> Result<()> {
                let count = r.length_not_null(K::MIN_SIZE.saturating_add(V::MIN_SIZE))?;
                $crate::impls::map!(@refill self, r, count, $fill)
            }

            fn read_option_into(slot: &mut Option<Self>, r: &mut Reader<'_>) -> Result<()> {
                let Some(map) = slot else {
                    *slot = Self::read_option(r)?;
                    return Ok(());
                };
                match r.length(K::MIN_SIZE.saturating_add(V::MIN_SIZE))? {
                    None => *slot = None,
                    Some(count) => $crate::impls::map!(@refill map, r, count, $fill)?,
                }
                Ok(())
            }
        }
    };
    (@refill $map:expr, $r:expr, $count:expr, $fill:ident) => {{
        $map.clear();
        if $map.capacity() < $count {
            $map.reserve($r.capacity::<(K, V)>($count));
        }
        $fill($map, $r, $count)
    }};
}
pub(crate) use map;

map!(
    /// A dictionary: its length, then each key and value. A repeated key is rejected.
    HashMap,
    fill_hash_map,
    Eq + Hash + Clone
);

/// Reads `count` entries into `map` (empty); a key read twice is an error.
#[inline]
fn fill_hash_map<K, V, S>(map: &mut HashMap<K, V, S>, r: &mut Reader<'_>, count: usize) -> Result<()>
where
    K: NinoRead + Eq + Hash + Clone,
    V: NinoRead,
    S: BuildHasher + Default + 'static,
{
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
            // A plain insert, as measured on Neoverse-N2. On the error, a repeated key holds its second value.
            read_entries_fixed(r, count, |key, value| match map.insert(key, value) {
                Some(_) => Err(Error::of(ErrorKind::DuplicateKey)),
                None => Ok(()),
            })?;
        }
        return Ok(());
    }
    for _ in 0..count {
        let key = read_key::<K>(r)?;
        let value = if V::MAY_DEFER {
            let mark = r.defer_mark();
            let value = V::read(r)?;
            if r.defer_mark() != mark {
                let at = key.clone();
                r.defer_trail(
                    mark,
                    dyn_step(move |m| Some(m.downcast_mut::<HashMap<K, V, S>>()?.get_mut(&at)? as &mut dyn Any)),
                );
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
    Ok(())
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
        fill_btree_map(&mut map, r, count)?;
        Ok(Some(map))
    }

    /// Cleared and refilled.
    fn read_into(&mut self, r: &mut Reader<'_>) -> Result<()> {
        let count = r.length_not_null(K::MIN_SIZE.saturating_add(V::MIN_SIZE))?;
        self.clear();
        fill_btree_map(self, r, count)
    }

    fn read_option_into(slot: &mut Option<Self>, r: &mut Reader<'_>) -> Result<()> {
        let Some(map) = slot else {
            *slot = Self::read_option(r)?;
            return Ok(());
        };
        match r.length(K::MIN_SIZE.saturating_add(V::MIN_SIZE))? {
            None => *slot = None,
            Some(count) => {
                map.clear();
                fill_btree_map(map, r, count)?;
            }
        }
        Ok(())
    }
}

/// Reads `count` entries into `map` (empty); a key read twice is an error.
#[inline]
fn fill_btree_map<K, V>(map: &mut BTreeMap<K, V>, r: &mut Reader<'_>, count: usize) -> Result<()>
where
    K: NinoRead + Ord + Clone,
    V: NinoRead,
{
    if entries_fixed_read::<K, V>() {
        return read_entries_fixed(r, count, |key, value| match map.insert(key, value) {
            Some(_) => Err(Error::of(ErrorKind::DuplicateKey)),
            None => Ok(()),
        });
    }
    for _ in 0..count {
        let key = read_key::<K>(r)?;
        let value = if V::MAY_DEFER {
            let mark = r.defer_mark();
            let value = V::read(r)?;
            if r.defer_mark() != mark {
                let at = key.clone();
                r.defer_trail(
                    mark,
                    dyn_step(move |m| Some(m.downcast_mut::<BTreeMap<K, V>>()?.get_mut(&at)? as &mut dyn Any)),
                );
            }
            value
        } else {
            V::read(r)?
        };
        if map.insert(key, value).is_some() {
            return Err(Error::of(ErrorKind::DuplicateKey));
        }
    }
    Ok(())
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
        w.or_null_length(value)
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
        w.or_null_length(value)
    }
}
