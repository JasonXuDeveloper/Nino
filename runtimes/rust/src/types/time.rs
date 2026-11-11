//! .NET's dates and times (`DateTime`, `TimeSpan`, `DateOnly`, `TimeOnly`, `DateTimeOffset`) as their ticks, days and
//! offsets, with the ranges .NET accepts.

use super::*;

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
    /// 0 when the high 32 bits of the ticks are below those of [`MAX_TICKS`] (a 32-bit compare: x86_64's SSE2 has
    /// no 64-bit one); the last 2^32 ticks of the range go through [`DateTime::decode`].
    #[inline]
    fn bad(b: &[u8; 8]) -> u32 {
        let high = (u64::from_le_bytes(*b) >> 32) as u32 & 0x3FFF_FFFF;
        u32::from(high >= (MAX_TICKS >> 32) as u32)
    }

    #[inline]
    fn wire(b: &[u8; 8]) -> Self {
        Self(u64::from_le_bytes(*b))
    }

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
    #[inline]
    fn bad(_: &[u8; 8]) -> u32 {
        0
    }

    #[inline]
    fn wire(b: &[u8; 8]) -> Self {
        Self(i64::from_le_bytes(*b))
    }

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
    #[inline]
    fn bad(b: &[u8; 4]) -> u32 {
        // Negative numbers are above `MAX_DAY` as `u32`.
        u32::from(u32::from_le_bytes(*b) > Self::MAX_DAY as u32)
    }

    #[inline]
    fn wire(b: &[u8; 4]) -> Self {
        Self(i32::from_le_bytes(*b))
    }

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
    /// As `DateTime::bad`: 0 when the high 32 bits are below those of a day (negative ticks are above them as
    /// `u32`).
    #[inline]
    fn bad(b: &[u8; 8]) -> u32 {
        u32::from(((u64::from_le_bytes(*b) >> 32) as u32) >= (TICKS_PER_DAY >> 32) as u32)
    }

    #[inline]
    fn wire(b: &[u8; 8]) -> Self {
        Self(i64::from_le_bytes(*b))
    }

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
    const FIXED_SIZE: usize = 10;

    #[inline]
    fn decode_fixed(bytes: &[u8]) -> Result<Self> {
        bytes.first_chunk::<10>().ok_or_else(Error::end).and_then(Self::decode)
    }

    #[inline]
    fn read(r: &mut Reader<'_>) -> Result<Self> {
        Self::decode(&r.array::<10>()?)
    }
}

impl NinoWrite for DateTimeOffset {
    const FIXED_SIZE: usize = 10;

    #[inline]
    fn encode_fixed(&self, out: &mut [u8]) {
        if let Some(dst) = out.first_chunk_mut::<10>() {
            *dst = self.encode();
        }
    }

    #[inline]
    fn write<'a>(&'a self, w: &mut Writer<'a>) -> Result<()> {
        w.array(self.encode());
        Ok(())
    }
}
