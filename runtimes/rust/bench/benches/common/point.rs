//! The codecs of the benches' `Point` (`[NinoType] struct Point { int X; int Y; }`, raw), as nino-gen's Rust templates
//! generate them: one block of 8 bytes. Included by `#[path]` from `compare.rs` and `blocks.rs`, which declare `Point`.

use nino::{NinoRead, NinoWrite, Reader, Result, Writer};

use super::Point;

impl nino::Raw for Point {
    const SIZE: usize = 8;
    const ALIGN: usize = 4;

    fn decode_raw(b: &[u8]) -> nino::Result<Self> {
        let b = b.get(..8).ok_or_else(|| nino::Error::of(nino::ErrorKind::UnexpectedEnd))?;
        Ok(Self {
            x: <i32 as nino::Raw>::decode_raw(b.get(0..).unwrap_or_default())?,
            y: <i32 as nino::Raw>::decode_raw(b.get(4..).unwrap_or_default())?,
        })
    }

    fn encode_raw(&self, out: &mut [u8]) {
        if let Some(o) = out.get_mut(0..) {
            nino::Raw::encode_raw(&self.x, o);
        }
        if let Some(o) = out.get_mut(4..) {
            nino::Raw::encode_raw(&self.y, o);
        }
    }
}

impl NinoRead for Point {
    const MIN_SIZE: usize = 8;
    const FIXED_SIZE: usize = 8;

    fn decode_fixed(b: &[u8]) -> Result<Self> {
        <Self as nino::Raw>::decode_raw(b)
    }

    fn read(r: &mut Reader<'_>) -> Result<Self> {
        <Self as nino::Raw>::decode_raw(&r.array::<8>()?)
    }
}

impl NinoWrite for Point {
    const FIXED_SIZE: usize = 8;

    fn encode_fixed(&self, out: &mut [u8]) {
        nino::Raw::encode_raw(self, out);
    }

    fn write<'a>(&'a self, w: &mut Writer<'a>) -> Result<()> {
        let mut b = [0u8; 8];
        nino::Raw::encode_raw(self, &mut b);
        w.array(b);
        Ok(())
    }
}
