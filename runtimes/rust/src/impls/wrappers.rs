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
        let mark = r.defer_mark();
        let value = T::read_option(r)?;
        r.defer_trail(mark, Step::Field(option_inner::<T>));
        Ok(value)
    }

    #[inline]
    fn read_into(&mut self, r: &mut Reader<'_>) -> Result<()> {
        if !T::MAY_DEFER {
            return T::read_option_into(self, r);
        }
        let mark = r.defer_mark();
        T::read_option_into(self, r)?;
        r.defer_trail(mark, Step::Field(option_inner::<T>));
        Ok(())
    }
}

impl<T: NinoWrite> NinoWrite for Option<T> {
    const PREFETCH_BYTES: usize = T::PREFETCH_BYTES;

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
        let mark = r.defer_mark();
        let value = T::read_boxed(r)?;
        r.defer_trail(mark, Step::Field(box_inner::<T>));
        Ok(value)
    }

    #[inline]
    fn read_option(r: &mut Reader<'_>) -> Result<Option<Self>> {
        if !T::MAY_DEFER {
            return T::read_boxed_option(r);
        }
        let mark = r.defer_mark();
        let value = T::read_boxed_option(r)?;
        r.defer_trail(mark, Step::Field(box_inner::<T>));
        Ok(value)
    }

    #[inline]
    fn read_into(&mut self, r: &mut Reader<'_>) -> Result<()> {
        if !T::MAY_DEFER {
            return T::read_boxed_into(self, r);
        }
        let mark = r.defer_mark();
        T::read_boxed_into(self, r)?;
        r.defer_trail(mark, Step::Field(box_inner::<T>));
        Ok(())
    }

    #[inline]
    fn read_option_into(slot: &mut Option<Self>, r: &mut Reader<'_>) -> Result<()> {
        if !T::MAY_DEFER {
            return T::read_boxed_option_into(slot, r);
        }
        let mark = r.defer_mark();
        T::read_boxed_option_into(slot, r)?;
        r.defer_trail(mark, Step::Field(box_inner::<T>));
        Ok(())
    }
}

impl<T: NinoWrite> NinoWrite for Box<T> {
    // Its target's size and 64 for the target's own targets (not `T::PREFETCH_BYTES`: a type that holds itself through a
    // `Box` would define the constant through itself).
    const PREFETCH_BYTES: usize = core::mem::size_of::<T>().saturating_add(64);

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
