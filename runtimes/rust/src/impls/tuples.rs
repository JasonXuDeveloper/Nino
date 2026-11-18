use core::any::Any;

use crate::error::{Error, Result};
use crate::reader::Reader;
use crate::traits::{NinoRead, NinoWrite};
use crate::writer::Writer;

macro_rules! tuple {
    ($($name:ident $index:tt),+) => {
        /// `ValueTuple`, `KeyValuePair` (and, inside an `Option`, `Tuple`): the items in order, no header.
        impl<$($name: NinoRead),+> NinoRead for ($($name,)+) {
            const MAY_DEFER: bool = false $(|| $name::MAY_DEFER)+;
            const MIN_SIZE: usize = 0usize $(.saturating_add($name::MIN_SIZE))+;
            /// The items' sizes added up when every item is fixed-size.
            const FIXED_SIZE: usize =
                if false $(|| <$name as NinoRead>::FIXED_SIZE == 0)+ { 0 } else { 0usize $(.saturating_add(<$name as NinoRead>::FIXED_SIZE))+ };

            // The last item's tail is not read.
            #[allow(unused_assignments)]
            #[inline]
            fn decode_fixed(bytes: &[u8]) -> Result<Self> {
                let mut rest = bytes;
                Ok(($({
                    let (item, tail) = rest.split_at_checked(<$name as NinoRead>::FIXED_SIZE).ok_or_else(Error::end)?;
                    rest = tail;
                    $name::decode_fixed(item)?
                },)+))
            }

            #[inline]
            fn read(r: &mut Reader<'_>) -> Result<Self> {
                Ok(($(
                    r.read_member::<$name>(|p| Some(&mut p.downcast_mut::<Self>()?.$index as &mut dyn Any))?,
                )+))
            }
        }

        impl<$($name: NinoWrite),+> NinoWrite for ($($name,)+) {
            const FIXED_SIZE: usize =
                if false $(|| <$name as NinoWrite>::FIXED_SIZE == 0)+ { 0 } else { 0usize $(.saturating_add(<$name as NinoWrite>::FIXED_SIZE))+ };

            // The last item's tail is not written.
            #[allow(unused_assignments)]
            #[inline]
            fn encode_fixed(&self, out: &mut [u8]) {
                let mut rest = out;
                $(
                    let Some((item, tail)) = rest.split_at_mut_checked(<$name as NinoWrite>::FIXED_SIZE) else { return };
                    rest = tail;
                    self.$index.encode_fixed(item);
                )+
            }

            #[inline]
            fn write<'a>(&'a self, w: &mut Writer<'a>) -> Result<()> {
                $(self.$index.write(w)?;)+
                Ok(())
            }
        }
    };
}

tuple!(A 0);
tuple!(A 0, B 1);
tuple!(A 0, B 1, C 2);
tuple!(A 0, B 1, C 2, D 3);
tuple!(A 0, B 1, C 2, D 3, E 4);
tuple!(A 0, B 1, C 2, D 3, E 4, F 5);
tuple!(A 0, B 1, C 2, D 3, E 4, F 5, G 6);
