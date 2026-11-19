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
