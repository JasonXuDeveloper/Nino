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

    #[inline]
    fn read_into(&mut self, r: &mut Reader<'_>) -> Result<()> {
        let count = r.length_not_null(T::MIN_SIZE)?;
        T::read_many_into(r, count, self)
    }

    #[inline]
    fn read_option_into(slot: &mut Option<Self>, r: &mut Reader<'_>) -> Result<()> {
        match (r.length(T::MIN_SIZE)?, slot) {
            (None, slot) => *slot = None,
            (Some(count), Some(values)) => T::read_many_into(r, count, values)?,
            (Some(count), slot) => *slot = Some(T::read_many(r, count)?),
        }
        Ok(())
    }
}

impl<T: NinoWrite> NinoWrite for Vec<T> {
    const PREFETCH_BYTES: usize = 64;

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

    /// Cleared and refilled, keeping its buffer.
    fn read_into(&mut self, r: &mut Reader<'_>) -> Result<()> {
        let count = r.length_not_null(T::MIN_SIZE)?;
        refill_deque(self, r, count)
    }

    fn read_option_into(slot: &mut Option<Self>, r: &mut Reader<'_>) -> Result<()> {
        let Some(queue) = slot else {
            *slot = Self::read_option(r)?;
            return Ok(());
        };
        match r.length(T::MIN_SIZE)? {
            None => *slot = None,
            Some(count) => refill_deque(queue, r, count)?,
        }
        Ok(())
    }
}

/// Empties `queue` and reads `count` elements into it, keeping its buffer when it has room.
fn refill_deque<T: NinoRead>(queue: &mut VecDeque<T>, r: &mut Reader<'_>, count: usize) -> Result<()> {
    queue.clear();
    if queue.capacity() < count {
        queue.reserve(r.capacity::<T>(count));
    }
    for i in 0..count {
        if T::MAY_DEFER {
            let mark = r.defer_mark();
            queue.push_back(T::read(r)?);
            r.defer_trail(mark, Step::Index(deque_item::<T>, i));
        } else {
            queue.push_back(T::read(r)?);
        }
    }
    Ok(())
}

impl<T: NinoWrite> NinoWrite for VecDeque<T> {
    fn write<'a>(&'a self, w: &mut Writer<'a>) -> Result<()> {
        w.length(self.len())?;
        let (front, back) = self.as_slices();
        T::write_many(front, w)?;
        T::write_many(back, w)
    }

    fn write_option<'a>(value: Option<&'a Self>, w: &mut Writer<'a>) -> Result<()> {
        w.or_null_length(value)
    }
}
