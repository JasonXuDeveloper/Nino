// ---------------------------------------------------------------------- numbers

macro_rules! number {
    ($($t:ty, $n:literal, $method:ident;)*) => {$(
        impl NinoRead for $t {
            const MIN_SIZE: usize = $n;
            const FIXED_SIZE: usize = $n;

            #[inline]
            fn decode_fixed(bytes: &[u8]) -> Result<Self> {
                bytes.first_chunk::<$n>().map(|b| <$t>::from_le_bytes(*b)).ok_or_else(Error::end)
            }

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

            #[inline]
            fn read_many_into(r: &mut Reader<'_>, count: usize, values: &mut Vec<Self>) -> Result<()> {
                let size = count.checked_mul($n).ok_or_else(Error::too_large)?;
                let bytes = r.take(size)?;
                values.clear();
                values.extend(bytes.as_chunks::<$n>().0.iter().map(|c| <$t>::from_le_bytes(*c)));
                Ok(())
            }
        }

        impl NinoWrite for $t {
            const FIXED_SIZE: usize = $n;

            #[inline]
            fn encode_fixed(&self, out: &mut [u8]) {
                if let Some(dst) = out.first_chunk_mut::<$n>() {
                    *dst = self.to_le_bytes();
                }
            }

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

            #[inline]
            fn write_counted<'a>(values: &'a [Self], w: &mut Writer<'a>) -> Result<()> {
                w.counted(values, |v| v.to_le_bytes())
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
    const FIXED_SIZE: usize = 1;

    #[inline]
    fn decode_fixed(bytes: &[u8]) -> Result<Self> {
        bytes.first().copied().ok_or_else(Error::end)
    }

    #[inline]
    fn read(r: &mut Reader<'_>) -> Result<Self> {
        r.u8()
    }

    /// A `byte[]`: one copy.
    #[inline]
    fn read_many(r: &mut Reader<'_>, count: usize) -> Result<Vec<Self>> {
        r.take(count).map(<[u8]>::to_vec)
    }

    #[inline]
    fn read_many_into(r: &mut Reader<'_>, count: usize, values: &mut Vec<Self>) -> Result<()> {
        let bytes = r.take(count)?;
        values.clear();
        values.extend_from_slice(bytes);
        Ok(())
    }
}

impl NinoWrite for u8 {
    const FIXED_SIZE: usize = 1;

    #[inline]
    fn encode_fixed(&self, out: &mut [u8]) {
        if let Some(b) = out.first_mut() {
            *b = *self;
        }
    }

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
    const FIXED_SIZE: usize = 1;

    #[inline]
    fn decode_fixed(bytes: &[u8]) -> Result<Self> {
        bytes.first().map(|&b| b as i8).ok_or_else(Error::end)
    }

    #[inline]
    fn read(r: &mut Reader<'_>) -> Result<Self> {
        r.i8()
    }

    #[inline]
    fn read_many(r: &mut Reader<'_>, count: usize) -> Result<Vec<Self>> {
        Ok(r.take(count)?.iter().map(|&b| b as i8).collect())
    }

    #[inline]
    fn read_many_into(r: &mut Reader<'_>, count: usize, values: &mut Vec<Self>) -> Result<()> {
        let bytes = r.take(count)?;
        values.clear();
        values.extend(bytes.iter().map(|&b| b as i8));
        Ok(())
    }
}

impl NinoWrite for i8 {
    const FIXED_SIZE: usize = 1;

    #[inline]
    fn encode_fixed(&self, out: &mut [u8]) {
        if let Some(b) = out.first_mut() {
            *b = *self as u8;
        }
    }

    #[inline]
    fn write<'a>(&'a self, w: &mut Writer<'a>) -> Result<()> {
        w.i8(*self);
        Ok(())
    }

    #[inline]
    fn write_many<'a>(values: &'a [Self], w: &mut Writer<'a>) -> Result<()> {
        w.encoded(values, |&v| [v as u8]);
        Ok(())
    }

    #[inline]
    fn write_counted<'a>(values: &'a [Self], w: &mut Writer<'a>) -> Result<()> {
        w.counted(values, |&v| [v as u8])
    }
}

impl NinoRead for bool {
    const MIN_SIZE: usize = 1;
    const FIXED_SIZE: usize = 1;

    #[inline]
    fn decode_fixed(bytes: &[u8]) -> Result<Self> {
        match bytes.first() {
            Some(0) => Ok(false),
            Some(1) => Ok(true),
            Some(&b) => Err(Error::new(ErrorKind::InvalidBool, i32::from(b))),
            None => Err(Error::end()),
        }
    }

    #[inline]
    fn read(r: &mut Reader<'_>) -> Result<Self> {
        r.bool()
    }

    /// One pass that ORs every byte (no early exit, so it runs a vector at a time): a byte other than 0 and 1 sets
    /// a bit above the lowest. Only then is the bad byte looked for.
    #[inline]
    fn read_many(r: &mut Reader<'_>, count: usize) -> Result<Vec<Self>> {
        let bytes = r.take(count)?;
        if bytes.iter().fold(0u8, |a, &b| a | b) > 1 {
            return Err(invalid_bool(bytes));
        }
        Ok(bytes.iter().map(|&b| b != 0).collect())
    }

    #[inline]
    fn read_many_into(r: &mut Reader<'_>, count: usize, values: &mut Vec<Self>) -> Result<()> {
        let bytes = r.take(count)?;
        if bytes.iter().fold(0u8, |a, &b| a | b) > 1 {
            return Err(invalid_bool(bytes));
        }
        values.clear();
        values.extend(bytes.iter().map(|&b| b != 0));
        Ok(())
    }
}

/// The error for the first byte of `bytes` that is not 0 or 1.
#[cold]
#[inline(never)]
fn invalid_bool(bytes: &[u8]) -> Error {
    let b = bytes.iter().copied().find(|&b| b > 1).unwrap_or(2);
    Error::new(ErrorKind::InvalidBool, i32::from(b))
}

impl NinoWrite for bool {
    const FIXED_SIZE: usize = 1;

    #[inline]
    fn encode_fixed(&self, out: &mut [u8]) {
        if let Some(b) = out.first_mut() {
            *b = u8::from(*self);
        }
    }

    #[inline]
    fn write<'a>(&'a self, w: &mut Writer<'a>) -> Result<()> {
        w.bool(*self);
        Ok(())
    }

    #[inline]
    fn write_many<'a>(values: &'a [Self], w: &mut Writer<'a>) -> Result<()> {
        w.encoded(values, |&v| [u8::from(v)]);
        Ok(())
    }

    #[inline]
    fn write_counted<'a>(values: &'a [Self], w: &mut Writer<'a>) -> Result<()> {
        w.counted(values, |&v| [u8::from(v)])
    }
}
