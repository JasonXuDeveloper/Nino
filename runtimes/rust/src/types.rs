//! The .NET value types Nino writes, as plain Rust types that keep their exact bits (so a value read and written
//! again gives the same bytes). Optional features convert them to `uuid`, `rust_decimal`, `chrono` and
//! `num-bigint` types (see `convert.rs`).

use core::fmt;

use crate::error::{Error, ErrorKind, Result};
use crate::reader::Reader;
use crate::traits::{NinoRead, NinoWrite};
use crate::writer::Writer;

/// Implements [`NinoRead`]/[`NinoWrite`] for a fixed-size value through `decode`/`encode` on its bytes.
macro_rules! fixed {
    ($t:ty, $n:literal) => {
        impl NinoRead for $t {
            const MIN_SIZE: usize = $n;

            #[inline]
            fn read(r: &mut Reader<'_>) -> Result<Self> {
                Self::decode(&r.array::<$n>()?)
            }

            #[inline]
            fn read_many(r: &mut Reader<'_>, count: usize) -> Result<Vec<Self>> {
                let bytes = r.take(count.checked_mul($n).ok_or_else(Error::too_large)?)?;
                bytes.as_chunks::<$n>().0.iter().map(Self::decode).collect()
            }
        }

        impl NinoWrite for $t {
            #[inline]
            fn write<'a>(&'a self, w: &mut Writer<'a>) -> Result<()> {
                w.array(self.encode());
                Ok(())
            }

            #[inline]
            fn write_many<'a>(values: &'a [Self], w: &mut Writer<'a>) -> Result<()> {
                let out = w.reserve(values.len().saturating_mul($n));
                for (dst, v) in out.as_chunks_mut::<$n>().0.iter_mut().zip(values) {
                    *dst = v.encode();
                }
                Ok(())
            }
        }

        impl Raw for $t {
            const SIZE: usize = $n;
            const ALIGN: usize = Self::CANONICAL_ALIGN;

            #[inline]
            fn decode_raw(bytes: &[u8]) -> Result<Self> {
                bytes.first_chunk::<$n>().ok_or_else(|| Error::end()).and_then(Self::decode)
            }

            #[inline]
            fn encode_raw(&self, out: &mut [u8]) {
                if let Some(dst) = out.first_chunk_mut::<$n>() {
                    *dst = self.encode();
                }
            }
        }
    };
}

/// A value copied as raw bytes in the canonical layout (the specification, 5): what a `T?[]` of it holds at a
/// fixed stride ([`RawNullables`]). Generated raw structs implement it too.
pub trait Raw: Sized + 'static {
    /// Its size in the canonical layout.
    const SIZE: usize;
    /// Its alignment in the canonical layout.
    const ALIGN: usize;
    /// Reads it from the first [`Raw::SIZE`] bytes of `bytes`.
    fn decode_raw(bytes: &[u8]) -> Result<Self>;
    /// Writes it into the first [`Raw::SIZE`] bytes of `out` (zeroed by the caller: padding stays 0).
    fn encode_raw(&self, out: &mut [u8]);
}

macro_rules! raw_number {
    ($($t:ty, $n:literal, $align:literal;)*) => {$(
        impl Raw for $t {
            const SIZE: usize = $n;
            const ALIGN: usize = $align;

            #[inline]
            fn decode_raw(bytes: &[u8]) -> Result<Self> {
                bytes.first_chunk::<$n>().map(|b| <$t>::from_le_bytes(*b)).ok_or_else(|| Error::end())
            }

            #[inline]
            fn encode_raw(&self, out: &mut [u8]) {
                if let Some(dst) = out.first_chunk_mut::<$n>() {
                    *dst = self.to_le_bytes();
                }
            }
        }
    )*};
}

raw_number! {
    u8, 1, 1; i8, 1, 1; u16, 2, 2; i16, 2, 2; u32, 4, 4; i32, 4, 4; u64, 8, 8; i64, 8, 8;
    f32, 4, 4; f64, 8, 8; u128, 16, 8; i128, 16, 8;
}

impl Raw for bool {
    const SIZE: usize = 1;
    const ALIGN: usize = 1;

    fn decode_raw(bytes: &[u8]) -> Result<Self> {
        match bytes.first() {
            Some(0) => Ok(false),
            Some(1) => Ok(true),
            Some(&b) => Err(Error::new(ErrorKind::InvalidBool, i32::from(b))),
            None => Err(Error::end()),
        }
    }

    fn encode_raw(&self, out: &mut [u8]) {
        if let Some(b) = out.first_mut() {
            *b = u8::from(*self);
        }
    }
}

// ---------------------------------------------------------------------- decimal

/// `System.Decimal`, as its 16 wire bytes: `flags` (scale in bits 16-23, sign in bit 31), the high 32 bits and the
/// low 64 bits of the 96-bit integer. The value is `(-1)^sign * (hi * 2^64 + lo) / 10^scale`.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Default)]
pub struct Decimal {
    pub flags: i32,
    pub hi: u32,
    pub lo: u64,
}

impl Decimal {
    const CANONICAL_ALIGN: usize = 8;

    /// A decimal of `mantissa` (below 2^96) / 10^`scale` (at most 28), negative if `negative`.
    pub fn new(mantissa: u128, scale: u8, negative: bool) -> Result<Self> {
        if mantissa >> 96 != 0 || scale > 28 {
            return Err(Error::of(ErrorKind::InvalidDecimal));
        }
        let sign = if negative { i32::MIN } else { 0 };
        Ok(Self { flags: (i32::from(scale) << 16) | sign, hi: (mantissa >> 64) as u32, lo: mantissa as u64 })
    }

    /// The 96-bit integer.
    pub fn mantissa(&self) -> u128 {
        (u128::from(self.hi) << 64) | u128::from(self.lo)
    }

    /// The power of ten the integer is divided by.
    pub fn scale(&self) -> u8 {
        (self.flags >> 16) as u8
    }

    pub fn is_negative(&self) -> bool {
        self.flags < 0
    }

    /// Whether the flags are those .NET produces (scale at most 28, other bits zero).
    pub fn is_valid(&self) -> bool {
        self.flags & 0x7F00_FFFF == 0 && self.scale() <= 28
    }

    #[inline]
    fn decode(b: &[u8; 16]) -> Result<Self> {
        let (flags, rest) = b.split_first_chunk::<4>().ok_or_else(Error::end)?;
        let (hi, lo) = rest.split_first_chunk::<4>().ok_or_else(Error::end)?;
        let lo = lo.first_chunk::<8>().ok_or_else(Error::end)?;
        let value =
            Self { flags: i32::from_le_bytes(*flags), hi: u32::from_le_bytes(*hi), lo: u64::from_le_bytes(*lo) };
        // .NET copies any bits; a scale past 28 or other flag bits make a decimal .NET's arithmetic misreads.
        if !value.is_valid() {
            return Err(Error::new(ErrorKind::InvalidDecimal, value.flags));
        }
        Ok(value)
    }

    #[inline]
    pub(crate) fn encode(&self) -> [u8; 16] {
        let mut out = [0u8; 16];
        let (a, rest) = out.split_at_mut(4);
        let (b, c) = rest.split_at_mut(4);
        a.copy_from_slice(&self.flags.to_le_bytes());
        b.copy_from_slice(&self.hi.to_le_bytes());
        c.copy_from_slice(&self.lo.to_le_bytes());
        out
    }
}

fixed!(Decimal, 16);

impl fmt::Display for Decimal {
    /// The exact decimal text (`-0.010`).
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        let digits = self.mantissa().to_string();
        let scale = usize::from(self.scale());
        let sign = if self.is_negative() { "-" } else { "" };
        if scale == 0 {
            return write!(f, "{sign}{digits}");
        }
        let padded = format!("{digits:0>width$}", width = scale.saturating_add(1));
        let (int, frac) = padded.split_at(padded.len().saturating_sub(scale));
        write!(f, "{sign}{int}.{frac}")
    }
}

impl core::str::FromStr for Decimal {
    type Err = Error;

    /// `-12.3456`: an optional sign, digits and an optional fraction (at most 28 digits after the point and a
    /// 96-bit integer); the scale is kept (`"1.50"` has scale 2).
    fn from_str(text: &str) -> Result<Self> {
        let bad = || Error::of(ErrorKind::InvalidText);
        let (negative, digits) = match text.strip_prefix('-') {
            Some(rest) => (true, rest),
            None => (false, text.strip_prefix('+').unwrap_or(text)),
        };
        let (int, frac) = digits.split_once('.').unwrap_or((digits, ""));
        if int.is_empty() && frac.is_empty() {
            return Err(bad());
        }
        let mut mantissa: u128 = 0;
        for c in int.chars().chain(frac.chars()) {
            let d = c.to_digit(10).ok_or_else(bad)?;
            mantissa = mantissa.checked_mul(10).and_then(|m| m.checked_add(u128::from(d))).ok_or_else(bad)?;
        }
        let scale = u8::try_from(frac.len()).map_err(|_| bad())?;
        Decimal::new(mantissa, scale, negative)
    }
}

// ---------------------------------------------------------------------- Guid

/// `System.Guid`, as its 16 wire bytes (`Guid.ToByteArray()`: the first three groups little-endian).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Default, PartialOrd, Ord)]
pub struct Guid(pub [u8; 16]);

impl Guid {
    const CANONICAL_ALIGN: usize = 4;

    /// The Guid whose text form, read as one big-endian 128-bit number, is `value`
    /// (`0x00112233_4455_6677_8899_aabbccddeeff` is `00112233-4455-6677-8899-aabbccddeeff`).
    pub fn from_u128(value: u128) -> Self {
        let be = value.to_be_bytes();
        let mut b = [0u8; 16];
        for (dst, src) in b.iter_mut().zip([3usize, 2, 1, 0, 5, 4, 7, 6, 8, 9, 10, 11, 12, 13, 14, 15]) {
            *dst = be.get(src).copied().unwrap_or(0);
        }
        Self(b)
    }

    /// The text form as a big-endian 128-bit number (the inverse of [`Guid::from_u128`]).
    pub fn to_u128(&self) -> u128 {
        let mut be = [0u8; 16];
        for (dst, src) in be.iter_mut().zip([3usize, 2, 1, 0, 5, 4, 7, 6, 8, 9, 10, 11, 12, 13, 14, 15]) {
            *dst = self.0.get(src).copied().unwrap_or(0);
        }
        u128::from_be_bytes(be)
    }

    #[inline]
    fn decode(b: &[u8; 16]) -> Result<Self> {
        Ok(Self(*b))
    }

    #[inline]
    fn encode(&self) -> [u8; 16] {
        self.0
    }
}

fixed!(Guid, 16);

impl fmt::Display for Guid {
    /// The usual text form, `00112233-4455-6677-8899-aabbccddeeff`.
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        let v = self.to_u128();
        write!(
            f,
            "{:08x}-{:04x}-{:04x}-{:04x}-{:012x}",
            (v >> 96) as u32,
            (v >> 80) as u16,
            (v >> 64) as u16,
            (v >> 48) as u16,
            v & 0xFFFF_FFFF_FFFF
        )
    }
}

impl core::str::FromStr for Guid {
    type Err = Error;

    /// `00112233-4455-6677-8899-aabbccddeeff` (hex digits in either case, the hyphens where they are).
    fn from_str(text: &str) -> Result<Self> {
        let bad = || Error::of(ErrorKind::InvalidText);
        let b = text.as_bytes();
        if b.len() != 36 || [8, 13, 18, 23].iter().any(|&i| b.get(i) != Some(&b'-')) {
            return Err(bad());
        }
        let mut value: u128 = 0;
        for c in text.chars().filter(|&c| c != '-') {
            let d = c.to_digit(16).ok_or_else(bad)?;
            value = (value << 4) | u128::from(d);
        }
        Ok(Guid::from_u128(value))
    }
}

// ---------------------------------------------------------------------- dates and times

/// Ticks (100 ns) in a day.
pub const TICKS_PER_DAY: i64 = 864_000_000_000;
/// `DateTime.MaxValue.Ticks`: 9999-12-31T23:59:59.9999999.
pub const MAX_TICKS: i64 = 3_155_378_975_999_999_999;
/// Ticks from 0001-01-01 to 1970-01-01.
pub const UNIX_EPOCH_TICKS: i64 = 621_355_968_000_000_000;

/// The kind of a [`DateTime`].
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum DateTimeKind {
    Unspecified = 0,
    Utc = 1,
    Local = 2,
}

/// `System.DateTime`: ticks (100 ns since 0001-01-01) in bits 0-61, the kind in bits 62-63. Kind 3 is a local time in
/// the repeated hour of a daylight saving change (.NET writes it from `ToLocalTime`): [`DateTime::kind`] reports it as
/// `Local`, [`DateTime::is_ambiguous_local`] tells it apart, and it is written back as read. Reading rejects ticks past
/// `DateTime.MaxValue` (.NET reads them; see the README).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Default, PartialOrd, Ord)]
pub struct DateTime(u64);

impl DateTime {
    const CANONICAL_ALIGN: usize = 8;

    /// A `DateTime` of `ticks` (0 to [`MAX_TICKS`]) and `kind`.
    pub fn new(ticks: i64, kind: DateTimeKind) -> Result<Self> {
        if !(0..=MAX_TICKS).contains(&ticks) {
            return Err(Error::of(ErrorKind::InvalidDateTime));
        }
        Ok(Self(ticks as u64 | ((kind as u64) << 62)))
    }

    pub fn ticks(&self) -> i64 {
        (self.0 & 0x3FFF_FFFF_FFFF_FFFF) as i64
    }

    /// The kind, as .NET's `DateTime.Kind` reports it: kind 3 is `Local`.
    pub fn kind(&self) -> DateTimeKind {
        match self.0 >> 62 {
            0 => DateTimeKind::Unspecified,
            1 => DateTimeKind::Utc,
            _ => DateTimeKind::Local,
        }
    }

    /// Whether this is a local time in the repeated hour of a daylight saving change (kind 3 on the wire).
    pub fn is_ambiguous_local(&self) -> bool {
        self.0 >> 62 == 3
    }

    /// The 64 bits on the wire.
    pub fn to_bits(&self) -> u64 {
        self.0
    }

    #[inline]
    fn decode(b: &[u8; 8]) -> Result<Self> {
        let bits = u64::from_le_bytes(*b);
        let value = Self(bits);
        if value.ticks() > MAX_TICKS {
            return Err(Error::of(ErrorKind::InvalidDateTime));
        }
        Ok(value)
    }

    #[inline]
    fn encode(&self) -> [u8; 8] {
        self.0.to_le_bytes()
    }
}

fixed!(DateTime, 8);

/// `System.TimeSpan`: ticks (100 ns).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Default, PartialOrd, Ord)]
pub struct TimeSpan(pub i64);

impl TimeSpan {
    const CANONICAL_ALIGN: usize = 8;

    #[inline]
    fn decode(b: &[u8; 8]) -> Result<Self> {
        Ok(Self(i64::from_le_bytes(*b)))
    }

    #[inline]
    fn encode(&self) -> [u8; 8] {
        self.0.to_le_bytes()
    }
}

fixed!(TimeSpan, 8);

/// `System.DateOnly`: the day number (days since 0001-01-01, up to 9999-12-31).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Default, PartialOrd, Ord)]
pub struct DateOnly(i32);

impl DateOnly {
    const CANONICAL_ALIGN: usize = 4;
    /// The day number of 9999-12-31.
    pub const MAX_DAY: i32 = 3_652_058;

    pub fn new(day_number: i32) -> Result<Self> {
        if !(0..=Self::MAX_DAY).contains(&day_number) {
            return Err(Error::of(ErrorKind::InvalidDateTime));
        }
        Ok(Self(day_number))
    }

    pub fn day_number(&self) -> i32 {
        self.0
    }

    #[inline]
    fn decode(b: &[u8; 4]) -> Result<Self> {
        Self::new(i32::from_le_bytes(*b))
    }

    #[inline]
    fn encode(&self) -> [u8; 4] {
        self.0.to_le_bytes()
    }
}

fixed!(DateOnly, 4);

/// `System.TimeOnly`: ticks since midnight (below one day).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Default, PartialOrd, Ord)]
pub struct TimeOnly(i64);

impl TimeOnly {
    const CANONICAL_ALIGN: usize = 8;

    pub fn new(ticks: i64) -> Result<Self> {
        if !(0..TICKS_PER_DAY).contains(&ticks) {
            return Err(Error::of(ErrorKind::InvalidDateTime));
        }
        Ok(Self(ticks))
    }

    pub fn ticks(&self) -> i64 {
        self.0
    }

    #[inline]
    fn decode(b: &[u8; 8]) -> Result<Self> {
        Self::new(i64::from_le_bytes(*b))
    }

    #[inline]
    fn encode(&self) -> [u8; 8] {
        self.0.to_le_bytes()
    }
}

fixed!(TimeOnly, 8);

/// `System.DateTimeOffset`: the UTC ticks and the offset in minutes (10 bytes, no padding). Reading checks what
/// .NET's constructor checks: an offset of at most 14 hours, and UTC and local times inside `DateTime`'s range.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Default)]
pub struct DateTimeOffset {
    utc_ticks: i64,
    offset_minutes: i16,
}

impl DateTimeOffset {
    /// The largest offset, in minutes (14 hours).
    pub const MAX_OFFSET_MINUTES: i16 = 840;

    pub fn new(utc_ticks: i64, offset_minutes: i16) -> Result<Self> {
        let local = i128::from(utc_ticks).wrapping_add(i128::from(offset_minutes).wrapping_mul(600_000_000));
        if offset_minutes.unsigned_abs() > Self::MAX_OFFSET_MINUTES as u16
            || !(0..=MAX_TICKS).contains(&utc_ticks)
            || !(0..=i128::from(MAX_TICKS)).contains(&local)
        {
            return Err(Error::of(ErrorKind::InvalidDateTime));
        }
        Ok(Self { utc_ticks, offset_minutes })
    }

    pub fn utc_ticks(&self) -> i64 {
        self.utc_ticks
    }

    pub fn offset_minutes(&self) -> i16 {
        self.offset_minutes
    }

    #[inline]
    fn decode(b: &[u8; 10]) -> Result<Self> {
        let (ticks, minutes) = b.split_first_chunk::<8>().ok_or_else(Error::end)?;
        let minutes = minutes.first_chunk::<2>().ok_or_else(Error::end)?;
        Self::new(i64::from_le_bytes(*ticks), i16::from_le_bytes(*minutes))
    }

    #[inline]
    fn encode(&self) -> [u8; 10] {
        let mut out = [0u8; 10];
        let (a, b) = out.split_at_mut(8);
        a.copy_from_slice(&self.utc_ticks.to_le_bytes());
        b.copy_from_slice(&self.offset_minutes.to_le_bytes());
        out
    }
}

impl NinoRead for DateTimeOffset {
    const MIN_SIZE: usize = 10;

    #[inline]
    fn read(r: &mut Reader<'_>) -> Result<Self> {
        Self::decode(&r.array::<10>()?)
    }

    fn read_many(r: &mut Reader<'_>, count: usize) -> Result<Vec<Self>> {
        let bytes = r.take(count.checked_mul(10).ok_or_else(Error::too_large)?)?;
        bytes.as_chunks::<10>().0.iter().map(Self::decode).collect()
    }
}

impl NinoWrite for DateTimeOffset {
    #[inline]
    fn write<'a>(&'a self, w: &mut Writer<'a>) -> Result<()> {
        w.array(self.encode());
        Ok(())
    }
}

/// `System.Half`, as its 16 bits.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Default)]
pub struct Half(pub u16);

impl Half {
    const CANONICAL_ALIGN: usize = 2;

    #[inline]
    fn decode(b: &[u8; 2]) -> Result<Self> {
        Ok(Self(u16::from_le_bytes(*b)))
    }

    #[inline]
    fn encode(&self) -> [u8; 2] {
        self.0.to_le_bytes()
    }
}

fixed!(Half, 2);

/// `System.Text.Rune`: a Unicode scalar value (as an `i32`). Values that are not one are rejected.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, PartialOrd, Ord)]
pub struct Rune(pub char);

impl Default for Rune {
    fn default() -> Self {
        Self('\0')
    }
}

impl Rune {
    const CANONICAL_ALIGN: usize = 4;

    #[inline]
    fn decode(b: &[u8; 4]) -> Result<Self> {
        let v = u32::from_le_bytes(*b);
        char::from_u32(v).map(Self).ok_or_else(|| Error::new(ErrorKind::InvalidChar, v as i32))
    }

    #[inline]
    fn encode(&self) -> [u8; 4] {
        u32::from(self.0).to_le_bytes()
    }
}

fixed!(Rune, 4);

// ---------------------------------------------------------------------- variable-size values

/// `System.Version`: major, minor, build and revision; an undefined build or revision is -1. Null is a major of -1
/// (read it as `Option<Version>`).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Default, PartialOrd, Ord)]
pub struct Version {
    pub major: i32,
    pub minor: i32,
    pub build: i32,
    pub revision: i32,
}

impl NinoRead for Version {
    const MIN_SIZE: usize = 4;

    fn read(r: &mut Reader<'_>) -> Result<Self> {
        Self::read_option(r)?.ok_or_else(Error::null)
    }

    /// As .NET's constructors: a negative major (other than -1) or minor is invalid, and a negative build makes
    /// build and revision undefined (-1), as does a negative revision.
    fn read_option(r: &mut Reader<'_>) -> Result<Option<Self>> {
        let major = r.i32()?;
        if major == -1 {
            return Ok(None);
        }
        let parts = r.array::<12>()?;
        let (minor, rest) = parts.split_first_chunk::<4>().ok_or_else(Error::end)?;
        let (build, revision) = rest.split_first_chunk::<4>().ok_or_else(Error::end)?;
        let revision = revision.first_chunk::<4>().ok_or_else(Error::end)?;
        let (minor, build, revision) =
            (i32::from_le_bytes(*minor), i32::from_le_bytes(*build), i32::from_le_bytes(*revision));
        if major < 0 || minor < 0 {
            return Err(Error::of(ErrorKind::InvalidVersion));
        }
        let (build, revision) = if build < 0 { (-1, -1) } else { (build, revision.max(-1)) };
        Ok(Some(Self { major, minor, build, revision }))
    }
}

impl NinoWrite for Version {
    fn write<'a>(&'a self, w: &mut Writer<'a>) -> Result<()> {
        w.i32(self.major);
        w.i32(self.minor);
        w.i32(self.build);
        w.i32(self.revision);
        Ok(())
    }

    fn write_option<'a>(value: Option<&'a Self>, w: &mut Writer<'a>) -> Result<()> {
        match value {
            Some(v) => v.write(w),
            None => {
                w.null_length();
                Ok(())
            }
        }
    }
}

/// `System.Numerics.BigInteger`: its two's complement bytes, least significant first, minimal length (as
/// `ToByteArray()`). Zero is `[0]`. Reading rejects a longer form (.NET reads it).
#[derive(Debug, Clone, PartialEq, Eq, Hash)]
pub struct BigInteger(Vec<u8>);

impl Default for BigInteger {
    fn default() -> Self {
        Self(vec![0])
    }
}

impl BigInteger {
    /// From two's complement bytes, least significant first (any length; empty is zero).
    pub fn from_signed_bytes_le(bytes: &[u8]) -> Self {
        let negative = bytes.last().is_some_and(|&b| b & 0x80 != 0);
        let fill = if negative { 0xFF } else { 0 };
        let mut len = bytes.len();
        // Drop sign bytes that the next byte's top bit repeats.
        while len > 1 {
            let (Some(&last), Some(&next)) = (bytes.get(len.saturating_sub(1)), bytes.get(len.saturating_sub(2)))
            else {
                break;
            };
            if last != fill || (next & 0x80 != 0) != negative {
                break;
            }
            len = len.saturating_sub(1);
        }
        match bytes.get(..len) {
            Some(b) if !b.is_empty() => Self(b.to_vec()),
            _ => Self(vec![0]),
        }
    }

    /// The minimal two's complement bytes, least significant first.
    pub fn to_signed_bytes_le(&self) -> &[u8] {
        &self.0
    }

    pub fn from_i128(v: i128) -> Self {
        Self::from_signed_bytes_le(&v.to_le_bytes())
    }

    /// The value, when it fits in an `i128`.
    pub fn to_i128(&self) -> Option<i128> {
        if self.0.len() > 16 {
            return None;
        }
        let fill = if self.0.last().is_some_and(|&b| b & 0x80 != 0) { 0xFF } else { 0 };
        let mut b = [fill; 16];
        for (dst, src) in b.iter_mut().zip(&self.0) {
            *dst = *src;
        }
        Some(i128::from_le_bytes(b))
    }
}

impl fmt::Display for BigInteger {
    /// The decimal text (`-129`).
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        let negative = self.0.last().is_some_and(|&b| b & 0x80 != 0);
        // The magnitude, least significant byte first.
        let mut mag = self.0.clone();
        if negative {
            let mut carry = true;
            for b in &mut mag {
                let (v, c) = (!*b).overflowing_add(u8::from(carry));
                *b = v;
                carry = c;
            }
        }
        let mut digits = Vec::new();
        while mag.iter().any(|&b| b != 0) {
            let mut rem = 0u32;
            for b in mag.iter_mut().rev() {
                let cur = (rem << 8) | u32::from(*b);
                *b = (cur / 10) as u8;
                rem = cur % 10;
            }
            digits.push(char::from(b'0'.wrapping_add(rem as u8)));
        }
        if digits.is_empty() {
            digits.push('0');
        }
        if negative {
            digits.push('-');
        }
        f.write_str(&digits.iter().rev().collect::<String>())
    }
}

impl core::str::FromStr for BigInteger {
    type Err = Error;

    /// Decimal text with an optional sign.
    fn from_str(text: &str) -> Result<Self> {
        let (negative, digits) = match text.strip_prefix('-') {
            Some(rest) => (true, rest),
            None => (false, text.strip_prefix('+').unwrap_or(text)),
        };
        if digits.is_empty() {
            return Err(Error::of(ErrorKind::InvalidText));
        }
        // The magnitude, least significant byte first: times 10 plus the digit, for each digit.
        let mut mag: Vec<u8> = vec![0];
        for c in digits.chars() {
            let mut carry = c.to_digit(10).ok_or_else(|| Error::of(ErrorKind::InvalidText))?;
            for b in &mut mag {
                let v = u32::from(*b).wrapping_mul(10).wrapping_add(carry); // at most 255 * 10 + 9 + 255
                *b = v as u8;
                carry = v >> 8;
            }
            while carry != 0 {
                mag.push(carry as u8);
                carry >>= 8;
            }
        }
        mag.push(0); // room for the sign bit
        if negative {
            let mut carry = true;
            for b in &mut mag {
                let (v, c) = (!*b).overflowing_add(u8::from(carry));
                *b = v;
                carry = c;
            }
        }
        Ok(Self::from_signed_bytes_le(&mag))
    }
}

impl NinoRead for BigInteger {
    const MIN_SIZE: usize = 4;

    fn read(r: &mut Reader<'_>) -> Result<Self> {
        let count = r.i32()?;
        let n = usize::try_from(count).map_err(|_| Error::length(count))?;
        if n == 0 {
            // .NET reads a count of 0 as zero (the specification, 3.5), though it never writes one.
            return Ok(Self::default());
        }
        if n > r.remaining() {
            return Err(Error::length(count));
        }
        // Only the minimal form, as `ToByteArray()` writes it (.NET reads longer forms too; see the README).
        let value = Self::from_signed_bytes_le(r.take(n)?);
        if value.0.len() != n {
            return Err(Error::length(count));
        }
        Ok(value)
    }
}

impl NinoWrite for BigInteger {
    fn write<'a>(&'a self, w: &mut Writer<'a>) -> Result<()> {
        w.length(self.0.len())?;
        w.bytes(&self.0);
        Ok(())
    }
}

/// `System.Collections.BitArray`: the bit count, then the bits, eight per byte, least significant bit first.
/// Reading rejects bits set past the count (.NET ignores them; see the README).
#[derive(Debug, Clone, PartialEq, Eq, Hash, Default)]
pub struct BitArray {
    len: usize,
    bytes: Vec<u8>,
}

impl BitArray {
    pub fn from_bools(bits: &[bool]) -> Self {
        let mut bytes = vec![0u8; bits.len().div_ceil(8)];
        for (i, &bit) in bits.iter().enumerate() {
            if let Some(b) = bytes.get_mut(i / 8) {
                *b |= u8::from(bit) << (i % 8);
            }
        }
        Self { len: bits.len(), bytes }
    }

    pub fn len(&self) -> usize {
        self.len
    }

    pub fn is_empty(&self) -> bool {
        self.len == 0
    }

    pub fn get(&self, i: usize) -> Option<bool> {
        if i >= self.len {
            return None;
        }
        self.bytes.get(i / 8).map(|b| b >> (i % 8) & 1 != 0)
    }

    pub fn to_bools(&self) -> Vec<bool> {
        (0..self.len).map(|i| self.get(i).unwrap_or(false)).collect()
    }
}

impl NinoRead for BitArray {
    const MIN_SIZE: usize = 4;

    fn read(r: &mut Reader<'_>) -> Result<Self> {
        Self::read_option(r)?.ok_or_else(Error::null)
    }

    fn read_option(r: &mut Reader<'_>) -> Result<Option<Self>> {
        let count = r.i32()?;
        if count == -1 {
            return Ok(None);
        }
        let len = usize::try_from(count).map_err(|_| Error::length(count))?;
        let size = len.div_ceil(8);
        if size > r.remaining() {
            return Err(Error::length(count));
        }
        let bytes = r.take(size)?.to_vec();
        let used = len % 8;
        if used != 0 && bytes.last().is_some_and(|&b| b >> used != 0) {
            return Err(Error::of(ErrorKind::InvalidBits));
        }
        Ok(Some(Self { len, bytes }))
    }
}

impl NinoWrite for BitArray {
    fn write<'a>(&'a self, w: &mut Writer<'a>) -> Result<()> {
        w.length(self.len)?;
        w.bytes(&self.bytes);
        Ok(())
    }

    fn write_option<'a>(value: Option<&'a Self>, w: &mut Writer<'a>) -> Result<()> {
        match value {
            Some(v) => v.write(w),
            None => {
                w.null_length();
                Ok(())
            }
        }
    }
}

// ---------------------------------------------------------------------- strings

/// A string in the default encoding kept as its UTF-16 code units, so unpaired surrogates survive a round trip
/// (a `String` reads them as U+FFFD). Null is `Option<WideString>`.
#[derive(Debug, Clone, PartialEq, Eq, Hash, Default, PartialOrd, Ord)]
pub struct WideString(pub Vec<u16>);

impl WideString {
    /// The text, each unpaired surrogate as U+FFFD.
    pub fn to_string_lossy(&self) -> String {
        String::from_utf16_lossy(&self.0)
    }
}

impl From<&str> for WideString {
    fn from(s: &str) -> Self {
        Self(s.encode_utf16().collect())
    }
}

impl NinoRead for WideString {
    const MIN_SIZE: usize = 4;

    fn read(r: &mut Reader<'_>) -> Result<Self> {
        r.wide_string()?.map(Self).ok_or_else(Error::null)
    }

    fn read_option(r: &mut Reader<'_>) -> Result<Option<Self>> {
        Ok(r.wide_string()?.map(Self))
    }
}

impl NinoWrite for WideString {
    fn write<'a>(&'a self, w: &mut Writer<'a>) -> Result<()> {
        w.wide_string(Some(&self.0))
    }

    fn write_option<'a>(value: Option<&'a Self>, w: &mut Writer<'a>) -> Result<()> {
        w.wide_string(value.map(|v| v.0.as_slice()))
    }
}

/// A string written as UTF-8 (a `[NinoUtf8]` member). Null is `Option<Utf8>`.
#[derive(Debug, Clone, PartialEq, Eq, Hash, Default, PartialOrd, Ord)]
pub struct Utf8(pub String);

impl NinoRead for Utf8 {
    const MIN_SIZE: usize = 4;

    fn read(r: &mut Reader<'_>) -> Result<Self> {
        r.utf8()?.map(Self).ok_or_else(Error::null)
    }

    fn read_option(r: &mut Reader<'_>) -> Result<Option<Self>> {
        Ok(r.utf8()?.map(Self))
    }
}

impl NinoWrite for Utf8 {
    fn write<'a>(&'a self, w: &mut Writer<'a>) -> Result<()> {
        w.utf8(Some(&self.0))
    }

    fn write_option<'a>(value: Option<&'a Self>, w: &mut Writer<'a>) -> Result<()> {
        w.utf8(value.map(|v| v.0.as_str()))
    }
}

// ---------------------------------------------------------------------- arrays

/// A `T?[]` or `List<T?>` of a raw `T` (the specification, 6.2): each element at a fixed stride of
/// `align(T) + size(T)` bytes, the presence byte first and the value at offset `align(T)`, zeroed when absent.
#[derive(Debug, Clone, PartialEq, Eq, Hash, Default)]
pub struct RawNullables<T>(pub Vec<Option<T>>);

impl<T: Raw> RawNullables<T> {
    const STRIDE: usize = T::ALIGN.saturating_add(T::SIZE);
}

impl<T: Raw> NinoRead for RawNullables<T> {
    const MIN_SIZE: usize = 4;

    fn read(r: &mut Reader<'_>) -> Result<Self> {
        Self::read_option(r)?.ok_or_else(Error::null)
    }

    fn read_option(r: &mut Reader<'_>) -> Result<Option<Self>> {
        let Some(count) = r.length(Self::STRIDE)? else { return Ok(None) };
        let bytes = r.take(count.checked_mul(Self::STRIDE).ok_or_else(Error::too_large)?)?;
        let mut values = Vec::with_capacity(count);
        for element in bytes.chunks_exact(Self::STRIDE.max(1)) {
            let (flag, value) = element.split_at_checked(T::ALIGN).ok_or_else(Error::end)?;
            values.push(match flag.first() {
                Some(0) => None,
                Some(1) => Some(T::decode_raw(value)?),
                Some(&b) => return Err(Error::new(ErrorKind::InvalidPresence, i32::from(b))),
                None => return Err(Error::end()),
            });
        }
        Ok(Some(Self(values)))
    }
}

impl<T: Raw> NinoWrite for RawNullables<T> {
    fn write<'a>(&'a self, w: &mut Writer<'a>) -> Result<()> {
        w.length(self.0.len())?;
        let out = w.reserve(self.0.len().saturating_mul(Self::STRIDE));
        for (element, value) in out.chunks_exact_mut(Self::STRIDE.max(1)).zip(&self.0) {
            if let Some(v) = value {
                let (flag, rest) = element.split_at_mut(T::ALIGN.min(element.len()));
                if let Some(f) = flag.first_mut() {
                    *f = 1;
                }
                v.encode_raw(rest);
            }
        }
        Ok(())
    }

    fn write_option<'a>(value: Option<&'a Self>, w: &mut Writer<'a>) -> Result<()> {
        match value {
            Some(v) => v.write(w),
            None => {
                w.null_length();
                Ok(())
            }
        }
    }
}

/// A multi-dimensional array `T[,]` (rank `R`, 2 to 4): its lengths, then the elements in row-major order (the
/// last index varies fastest). Null is a first length of -1 (read it as `Option<Grid<T, R>>`).
#[derive(Debug, Clone, PartialEq, Eq, Hash)]
pub struct Grid<T, const R: usize> {
    dims: [usize; R],
    data: Vec<T>,
}

impl<T, const R: usize> Grid<T, R> {
    /// A grid of `dims` holding `data` (row-major); `None` when the lengths do not multiply to `data.len()`.
    pub fn new(dims: [usize; R], data: Vec<T>) -> Option<Self> {
        let count = dims.iter().try_fold(1usize, |n, &d| n.checked_mul(d))?;
        (count == data.len()).then_some(Self { dims, data })
    }

    pub fn dims(&self) -> [usize; R] {
        self.dims
    }

    pub fn data(&self) -> &[T] {
        &self.data
    }

    pub fn into_data(self) -> Vec<T> {
        self.data
    }
}

impl<T: NinoRead, const R: usize> NinoRead for Grid<T, R> {
    const MIN_SIZE: usize = 4;

    fn read(r: &mut Reader<'_>) -> Result<Self> {
        Self::read_option(r)?.ok_or_else(Error::null)
    }

    fn read_option(r: &mut Reader<'_>) -> Result<Option<Self>> {
        if T::MAY_DEFER {
            // Elements are read as a Vec, then moved into the grid: bodies deferred in them would be lost.
            return Err(Error::of(ErrorKind::Unsupported));
        }
        let mut dims = [0usize; R];
        let mut count = 1u64;
        for (i, dim) in dims.iter_mut().enumerate() {
            let length = r.i32()?;
            if i == 0 && length == -1 {
                return Ok(None);
            }
            let length_u = usize::try_from(length).map_err(|_| Error::length(length))?;
            count = count.checked_mul(length_u as u64).ok_or_else(|| Error::length(length))?;
            if count.saturating_mul(T::MIN_SIZE.max(1) as u64) > r.remaining() as u64 {
                return Err(Error::length(length));
            }
            *dim = length_u;
        }
        let count = usize::try_from(count).map_err(|_| Error::too_large())?;
        let data = T::read_many(r, count)?;
        Ok(Some(Self { dims, data }))
    }
}

impl<T: NinoWrite, const R: usize> NinoWrite for Grid<T, R> {
    fn write<'a>(&'a self, w: &mut Writer<'a>) -> Result<()> {
        for &d in &self.dims {
            w.length(d)?;
        }
        T::write_many(&self.data, w)
    }

    fn write_option<'a>(value: Option<&'a Self>, w: &mut Writer<'a>) -> Result<()> {
        match value {
            Some(v) => v.write(w),
            None => {
                w.null_length();
                Ok(())
            }
        }
    }
}
