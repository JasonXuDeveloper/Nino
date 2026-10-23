//! Conversions between Nino's own value types and those of other crates, each behind an optional feature, and
//! `NinoRead`/`NinoWrite` for the types whose wire form is unambiguous (`Uuid`, `rust_decimal::Decimal`, `BigInt`,
//! `IndexMap`/`IndexSet`). Times convert with `From`/`TryFrom` only: a `DateTime`'s kind has no chrono equivalent
//! that generated code could pick for the user.

#[cfg(feature = "uuid")]
mod uuid_impl {
    use crate::error::Result;
    use crate::reader::Reader;
    use crate::traits::{NinoRead, NinoWrite};
    use crate::types::Guid;
    use crate::writer::Writer;

    impl From<Guid> for uuid::Uuid {
        /// The same Guid: .NET's byte order is `Uuid::from_bytes_le`'s.
        fn from(value: Guid) -> Self {
            uuid::Uuid::from_bytes_le(value.0)
        }
    }

    impl From<uuid::Uuid> for Guid {
        fn from(value: uuid::Uuid) -> Self {
            Guid(value.to_bytes_le())
        }
    }

    impl NinoRead for uuid::Uuid {
        const MIN_SIZE: usize = 16;

        #[inline]
        fn read(r: &mut Reader<'_>) -> Result<Self> {
            Ok(uuid::Uuid::from_bytes_le(r.array::<16>()?))
        }
    }

    impl NinoWrite for uuid::Uuid {
        #[inline]
        fn write<'a>(&'a self, w: &mut Writer<'a>) -> Result<()> {
            w.array(self.to_bytes_le());
            Ok(())
        }
    }
}

#[cfg(feature = "rust_decimal")]
mod decimal_impl {
    use crate::error::{Error, ErrorKind, Result};
    use crate::reader::Reader;
    use crate::traits::{NinoRead, NinoWrite};
    use crate::types::Decimal;
    use crate::writer::Writer;

    impl From<Decimal> for rust_decimal::Decimal {
        /// Exact: both are a 96-bit integer, a sign and a scale of at most 28 (a [`Decimal`] read from the wire is
        /// always valid).
        fn from(value: Decimal) -> Self {
            let m = value.mantissa();
            rust_decimal::Decimal::from_parts(
                m as u32,
                (m >> 32) as u32,
                (m >> 64) as u32,
                value.is_negative(),
                u32::from(value.scale().min(28)),
            )
        }
    }

    impl TryFrom<rust_decimal::Decimal> for Decimal {
        type Error = Error;

        fn try_from(value: rust_decimal::Decimal) -> Result<Self> {
            let scale = u8::try_from(value.scale()).map_err(|_| Error::of(ErrorKind::InvalidDecimal))?;
            Decimal::new(value.mantissa().unsigned_abs(), scale, value.is_sign_negative())
        }
    }

    impl NinoRead for rust_decimal::Decimal {
        const MIN_SIZE: usize = 16;

        #[inline]
        fn read(r: &mut Reader<'_>) -> Result<Self> {
            Ok(Decimal::read(r)?.into())
        }
    }

    impl NinoWrite for rust_decimal::Decimal {
        #[inline]
        fn write<'a>(&'a self, w: &mut Writer<'a>) -> Result<()> {
            let d = Decimal::try_from(*self)?;
            w.array(d.encode());
            Ok(())
        }
    }
}

#[cfg(feature = "num-bigint")]
mod bigint_impl {
    use crate::error::Result;
    use crate::reader::Reader;
    use crate::traits::{NinoRead, NinoWrite};
    use crate::types::BigInteger;
    use crate::writer::Writer;

    impl From<BigInteger> for num_bigint::BigInt {
        fn from(value: BigInteger) -> Self {
            num_bigint::BigInt::from_signed_bytes_le(value.to_signed_bytes_le())
        }
    }

    impl From<&num_bigint::BigInt> for BigInteger {
        fn from(value: &num_bigint::BigInt) -> Self {
            BigInteger::from_signed_bytes_le(&value.to_signed_bytes_le())
        }
    }

    /// The length and the minimal two's complement bytes, as [`BigInteger`] (not nullable: `BigInteger` is a
    /// struct in .NET).
    impl NinoRead for num_bigint::BigInt {
        const MIN_SIZE: usize = 4;

        fn read(r: &mut Reader<'_>) -> Result<Self> {
            Ok(BigInteger::read(r)?.into())
        }
    }

    impl NinoWrite for num_bigint::BigInt {
        fn write<'a>(&'a self, w: &mut Writer<'a>) -> Result<()> {
            let bytes = self.to_signed_bytes_le();
            w.length(bytes.len())?;
            w.bytes(&bytes);
            Ok(())
        }
    }
}

#[cfg(feature = "chrono")]
mod chrono_impl {
    use chrono::{Datelike, NaiveDate, NaiveDateTime, NaiveTime, Timelike};

    use crate::error::{Error, ErrorKind, Result};
    use crate::types::{DateOnly, DateTime, DateTimeKind, DateTimeOffset, TimeOnly, TimeSpan, TICKS_PER_DAY};

    const TICKS_PER_SECOND: i64 = 10_000_000;

    fn invalid() -> Error {
        Error::of(ErrorKind::InvalidDateTime)
    }

    /// Ticks since midnight to a time of day (`ticks` below one day).
    fn time_of(ticks: i64) -> Result<NaiveTime> {
        let secs = u32::try_from(ticks.checked_div(TICKS_PER_SECOND).ok_or_else(invalid)?).map_err(|_| invalid())?;
        let rest = u32::try_from(ticks.checked_rem(TICKS_PER_SECOND).ok_or_else(invalid)?).map_err(|_| invalid())?;
        NaiveTime::from_num_seconds_from_midnight_opt(secs, rest.checked_mul(100).ok_or_else(invalid)?)
            .ok_or_else(invalid)
    }

    /// A time of day to ticks (sub-100 ns digits dropped; a leap second is an error).
    fn ticks_of(time: NaiveTime) -> Result<i64> {
        if time.nanosecond() >= 1_000_000_000 {
            return Err(invalid());
        }
        i64::from(time.num_seconds_from_midnight())
            .checked_mul(TICKS_PER_SECOND)
            .and_then(|t| t.checked_add(i64::from(time.nanosecond().wrapping_div(100))))
            .ok_or_else(invalid)
    }

    fn date_of(day: i32) -> Result<NaiveDate> {
        NaiveDate::from_num_days_from_ce_opt(day.checked_add(1).ok_or_else(invalid)?).ok_or_else(invalid)
    }

    fn day_of(date: NaiveDate) -> Result<i32> {
        date.num_days_from_ce().checked_sub(1).ok_or_else(invalid)
    }

    fn naive_of(ticks: i64) -> Result<NaiveDateTime> {
        let day = i32::try_from(ticks.checked_div(TICKS_PER_DAY).ok_or_else(invalid)?).map_err(|_| invalid())?;
        let rest = ticks.checked_rem(TICKS_PER_DAY).ok_or_else(invalid)?;
        Ok(date_of(day)?.and_time(time_of(rest)?))
    }

    fn naive_ticks(value: NaiveDateTime) -> Result<i64> {
        i64::from(day_of(value.date())?)
            .checked_mul(TICKS_PER_DAY)
            .and_then(|t| t.checked_add(ticks_of(value.time()).ok()?))
            .ok_or_else(invalid)
    }

    impl TryFrom<DateOnly> for NaiveDate {
        type Error = Error;
        fn try_from(value: DateOnly) -> Result<Self> {
            date_of(value.day_number())
        }
    }

    impl TryFrom<NaiveDate> for DateOnly {
        type Error = Error;
        fn try_from(value: NaiveDate) -> Result<Self> {
            DateOnly::new(day_of(value)?)
        }
    }

    impl TryFrom<TimeOnly> for NaiveTime {
        type Error = Error;
        fn try_from(value: TimeOnly) -> Result<Self> {
            time_of(value.ticks())
        }
    }

    impl TryFrom<NaiveTime> for TimeOnly {
        type Error = Error;
        fn try_from(value: NaiveTime) -> Result<Self> {
            TimeOnly::new(ticks_of(value)?)
        }
    }

    /// The date and time; the kind is dropped (see [`DateTime::kind`]).
    impl TryFrom<DateTime> for NaiveDateTime {
        type Error = Error;
        fn try_from(value: DateTime) -> Result<Self> {
            naive_of(value.ticks())
        }
    }

    /// A `DateTime` of kind `Unspecified`.
    impl TryFrom<NaiveDateTime> for DateTime {
        type Error = Error;
        fn try_from(value: NaiveDateTime) -> Result<Self> {
            DateTime::new(naive_ticks(value)?, DateTimeKind::Unspecified)
        }
    }

    /// A `DateTime` of kind `Utc`.
    impl TryFrom<chrono::DateTime<chrono::Utc>> for DateTime {
        type Error = Error;
        fn try_from(value: chrono::DateTime<chrono::Utc>) -> Result<Self> {
            DateTime::new(naive_ticks(value.naive_utc())?, DateTimeKind::Utc)
        }
    }

    impl TryFrom<DateTimeOffset> for chrono::DateTime<chrono::FixedOffset> {
        type Error = Error;
        fn try_from(value: DateTimeOffset) -> Result<Self> {
            let seconds = i32::from(value.offset_minutes()).checked_mul(60).ok_or_else(invalid)?;
            let offset = chrono::FixedOffset::east_opt(seconds).ok_or_else(invalid)?;
            Ok(chrono::DateTime::from_naive_utc_and_offset(naive_of(value.utc_ticks())?, offset))
        }
    }

    /// Fails for an offset that is not whole minutes or is past ±14 hours.
    impl TryFrom<chrono::DateTime<chrono::FixedOffset>> for DateTimeOffset {
        type Error = Error;
        fn try_from(value: chrono::DateTime<chrono::FixedOffset>) -> Result<Self> {
            let seconds = value.offset().local_minus_utc();
            if seconds.checked_rem(60) != Some(0) {
                return Err(invalid());
            }
            let minutes = i16::try_from(seconds.checked_div(60).ok_or_else(invalid)?).map_err(|_| invalid())?;
            DateTimeOffset::new(naive_ticks(value.naive_utc())?, minutes)
        }
    }

    impl From<TimeSpan> for chrono::TimeDelta {
        fn from(value: TimeSpan) -> Self {
            // |ticks| / 10 microseconds always fits a TimeDelta (±i64::MAX milliseconds).
            let micros = chrono::TimeDelta::microseconds(value.0.wrapping_div(10));
            let nanos = chrono::TimeDelta::nanoseconds(value.0.wrapping_rem(10).wrapping_mul(100));
            micros.checked_add(&nanos).unwrap_or(micros)
        }
    }

    /// Sub-100 ns digits are dropped; fails past `TimeSpan`'s range.
    impl TryFrom<chrono::TimeDelta> for TimeSpan {
        type Error = Error;
        fn try_from(value: chrono::TimeDelta) -> Result<Self> {
            value
                .num_seconds()
                .checked_mul(TICKS_PER_SECOND)
                .and_then(|t| t.checked_add(i64::from(value.subsec_nanos().wrapping_div(100))))
                .map(TimeSpan)
                .ok_or_else(|| Error::of(ErrorKind::TooLarge))
        }
    }
}

#[cfg(feature = "indexmap")]
mod indexmap_impl {
    use core::any::Any;
    use core::hash::{BuildHasher, Hash};

    use indexmap::{IndexMap, IndexSet};

    use crate::defer::Step;
    use crate::error::{Error, ErrorKind, Result};
    use crate::impls::read_key;
    use crate::reader::Reader;
    use crate::traits::{NinoRead, NinoWrite};
    use crate::writer::Writer;

    fn map_value<K: 'static, V: 'static, S: 'static>(m: &mut dyn Any, i: usize) -> Option<&mut dyn Any> {
        Some(m.downcast_mut::<IndexMap<K, V, S>>()?.get_index_mut(i)?.1 as &mut dyn Any)
    }

    /// A dictionary in wire order (so writing it back gives the same bytes): its length, then each key and value.
    /// A repeated key is rejected.
    impl<K, V, S> NinoRead for IndexMap<K, V, S>
    where
        K: NinoRead + Eq + Hash,
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
            let mut map = IndexMap::with_capacity_and_hasher(r.capacity::<(K, V)>(count), S::default());
            for i in 0..count {
                let key = read_key::<K>(r)?;
                let value = if V::MAY_DEFER {
                    let mark = r.mark();
                    let value = V::read(r)?;
                    r.trail(mark, Step::Index(map_value::<K, V, S>, i));
                    value
                } else {
                    V::read(r)?
                };
                if map.insert(key, value).is_some() {
                    return Err(Error::of(ErrorKind::DuplicateKey));
                }
            }
            Ok(Some(map))
        }
    }

    impl<K: NinoWrite, V: NinoWrite, S> NinoWrite for IndexMap<K, V, S> {
        fn write<'a>(&'a self, w: &mut Writer<'a>) -> Result<()> {
            w.length(self.len())?;
            for (k, v) in self {
                k.write(w)?;
                v.write(w)?;
            }
            Ok(())
        }

        fn write_option<'a>(value: Option<&'a Self>, w: &mut Writer<'a>) -> Result<()> {
            match value {
                None => {
                    w.null_length();
                    Ok(())
                }
                Some(v) => v.write(w),
            }
        }
    }

    /// A set in wire order: its length, then each element. A repeated element is rejected.
    impl<T, S> NinoRead for IndexSet<T, S>
    where
        T: NinoRead + Eq + Hash,
        S: BuildHasher + Default + 'static,
    {
        const MIN_SIZE: usize = 4;

        fn read(r: &mut Reader<'_>) -> Result<Self> {
            Self::read_option(r)?.ok_or_else(Error::null)
        }

        fn read_option(r: &mut Reader<'_>) -> Result<Option<Self>> {
            let Some(count) = r.length(T::MIN_SIZE)? else { return Ok(None) };
            let mut set = IndexSet::with_capacity_and_hasher(r.capacity::<T>(count), S::default());
            for _ in 0..count {
                if !set.insert(read_key::<T>(r)?) {
                    return Err(Error::of(ErrorKind::DuplicateKey));
                }
            }
            Ok(Some(set))
        }
    }

    impl<T: NinoWrite, S> NinoWrite for IndexSet<T, S> {
        fn write<'a>(&'a self, w: &mut Writer<'a>) -> Result<()> {
            w.length(self.len())?;
            for v in self {
                v.write(w)?;
            }
            Ok(())
        }

        fn write_option<'a>(value: Option<&'a Self>, w: &mut Writer<'a>) -> Result<()> {
            match value {
                None => {
                    w.null_length();
                    Ok(())
                }
                Some(v) => v.write(w),
            }
        }
    }
}
