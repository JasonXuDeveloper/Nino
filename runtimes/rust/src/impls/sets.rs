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
                fill_set(&mut set, r, count)?;
                Ok(Some(set))
            }

            /// Cleared and refilled, keeping its buffer.
            fn read_into(&mut self, r: &mut Reader<'_>) -> Result<()> {
                let count = r.length_not_null(T::MIN_SIZE)?;
                self.clear();
                set!(@reserve $set, self, r, count);
                fill_set(self, r, count)
            }

            fn read_option_into(slot: &mut Option<Self>, r: &mut Reader<'_>) -> Result<()> {
                match slot {
                    Some(set) => {
                        let Some(count) = r.length(T::MIN_SIZE)? else {
                            *slot = None;
                            return Ok(());
                        };
                        set.clear();
                        set!(@reserve $set, set, r, count);
                        fill_set(set, r, count)
                    }
                    None => {
                        *slot = Self::read_option(r)?;
                        Ok(())
                    }
                }
            }
        }

        impl<T: NinoRead + $($bound)*> FillSet<T> for $set<T> {
            #[inline]
            fn insert_new(&mut self, value: T) -> bool {
                self.insert(value)
            }
        }

        impl<T: NinoWrite> NinoWrite for $set<T> {
            fn write<'a>(&'a self, w: &mut Writer<'a>) -> Result<()> {
                write_set(self.iter(), w)
            }

            fn write_option<'a>(value: Option<&'a Self>, w: &mut Writer<'a>) -> Result<()> {
                w.or_null_length(value)
            }
        }
    };
    (@new HashSet, $n:expr) => { HashSet::with_capacity($n) };
    (@new BTreeSet, $n:expr) => { { let _ = $n; BTreeSet::new() } };
    // Room for `count` elements when the set has less (within the read's allocation budget).
    (@reserve HashSet, $set:expr, $r:expr, $count:expr) => {
        if $set.capacity() < $count {
            $set.reserve($r.capacity::<T>($count));
        }
    };
    (@reserve BTreeSet, $set:expr, $r:expr, $count:expr) => {
        let _ = $count;
    };
}

/// A set read into ([`fill_set`]): inserts, saying whether the value was new.
pub(crate) trait FillSet<T> {
    fn insert_new(&mut self, value: T) -> bool;
}

/// Reads `count` elements into `set` (empty); an element read twice is an error.
#[inline]
pub(crate) fn fill_set<T: NinoRead, S: FillSet<T>>(set: &mut S, r: &mut Reader<'_>, count: usize) -> Result<()> {
    for _ in 0..count {
        if !set.insert_new(read_key::<T>(r)?) {
            return Err(Error::of(ErrorKind::DuplicateKey));
        }
    }
    Ok(())
}

set!(HashSet, Eq + Hash);
set!(BTreeSet, Ord);

/// Writes a set's length and elements in iteration order, taking one reservation for fixed-size elements.
#[inline]
pub(crate) fn write_set<'a, T: NinoWrite + 'a>(
    values: impl ExactSizeIterator<Item = &'a T>,
    w: &mut Writer<'a>,
) -> Result<()> {
    if T::FIXED_SIZE != 0 && T::FIXED_SIZE <= crate::simd::FIXED_STACK {
        let count = i32::try_from(values.len()).map_err(|_| Error::too_large())?;
        crate::simd::extend_fixed_iter(w.buffer(), count.to_le_bytes(), values, T::FIXED_SIZE, T::encode_fixed);
        return Ok(());
    }
    w.length(values.len())?;
    for value in values {
        value.write(w)?;
    }
    Ok(())
}
