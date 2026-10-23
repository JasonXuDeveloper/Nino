//! `serde` for Nino's value types (feature `serde`), so a type with Nino codecs can also be written as JSON, YAML
//! or any other serde format. The forms are the readable ones: decimals, Guids and big integers as strings, dates
//! as ticks; deserializing validates as Nino's reader does.

use serde::de::Error as _;
use serde::{Deserialize, Deserializer, Serialize, Serializer};

use crate::types::{
    BigInteger, BitArray, DateOnly, DateTime, DateTimeKind, DateTimeOffset, Decimal, Grid, Guid, Half, RawNullables,
    Rune, TimeOnly, TimeSpan, Utf8, Version, WideString,
};

fn invalid<E: serde::de::Error>(what: &str, e: crate::Error) -> E {
    E::custom(format_args!("invalid {what}: {e}"))
}

/// Serializes as its text (`Display`), deserializes by parsing it.
macro_rules! as_text {
    ($t:ty, $what:literal) => {
        impl Serialize for $t {
            fn serialize<S: Serializer>(&self, s: S) -> core::result::Result<S::Ok, S::Error> {
                s.collect_str(self)
            }
        }

        impl<'de> Deserialize<'de> for $t {
            fn deserialize<D: Deserializer<'de>>(d: D) -> core::result::Result<Self, D::Error> {
                let text = <std::borrow::Cow<'de, str>>::deserialize(d)?;
                text.parse().map_err(|e| invalid($what, e))
            }
        }
    };
}

as_text!(Decimal, "decimal");
as_text!(Guid, "Guid");
as_text!(BigInteger, "BigInteger");

#[derive(Serialize, Deserialize)]
#[serde(rename = "DateTime")]
struct DateTimeRepr {
    ticks: i64,
    kind: DateTimeKind,
}

impl Serialize for DateTimeKind {
    fn serialize<S: Serializer>(&self, s: S) -> core::result::Result<S::Ok, S::Error> {
        s.serialize_str(match self {
            DateTimeKind::Unspecified => "Unspecified",
            DateTimeKind::Utc => "Utc",
            DateTimeKind::Local => "Local",
        })
    }
}

impl<'de> Deserialize<'de> for DateTimeKind {
    fn deserialize<D: Deserializer<'de>>(d: D) -> core::result::Result<Self, D::Error> {
        match &*<std::borrow::Cow<'de, str>>::deserialize(d)? {
            "Unspecified" => Ok(DateTimeKind::Unspecified),
            "Utc" => Ok(DateTimeKind::Utc),
            "Local" => Ok(DateTimeKind::Local),
            other => Err(D::Error::unknown_variant(other, &["Unspecified", "Utc", "Local"])),
        }
    }
}

/// `{ "ticks": n, "kind": "Utc" | "Local" | "Unspecified" }`.
impl Serialize for DateTime {
    fn serialize<S: Serializer>(&self, s: S) -> core::result::Result<S::Ok, S::Error> {
        DateTimeRepr { ticks: self.ticks(), kind: self.kind() }.serialize(s)
    }
}

impl<'de> Deserialize<'de> for DateTime {
    fn deserialize<D: Deserializer<'de>>(d: D) -> core::result::Result<Self, D::Error> {
        let r = DateTimeRepr::deserialize(d)?;
        DateTime::new(r.ticks, r.kind).map_err(|e| invalid("DateTime", e))
    }
}

#[derive(Serialize, Deserialize)]
#[serde(rename = "DateTimeOffset", rename_all = "camelCase")]
struct DateTimeOffsetRepr {
    utc_ticks: i64,
    offset_minutes: i16,
}

/// `{ "utcTicks": n, "offsetMinutes": m }`.
impl Serialize for DateTimeOffset {
    fn serialize<S: Serializer>(&self, s: S) -> core::result::Result<S::Ok, S::Error> {
        DateTimeOffsetRepr { utc_ticks: self.utc_ticks(), offset_minutes: self.offset_minutes() }.serialize(s)
    }
}

impl<'de> Deserialize<'de> for DateTimeOffset {
    fn deserialize<D: Deserializer<'de>>(d: D) -> core::result::Result<Self, D::Error> {
        let r = DateTimeOffsetRepr::deserialize(d)?;
        DateTimeOffset::new(r.utc_ticks, r.offset_minutes).map_err(|e| invalid("DateTimeOffset", e))
    }
}

/// A number: ticks, day number, bits.
macro_rules! as_number {
    ($t:ty, $n:ty, $get:expr, $make:expr, $what:literal) => {
        impl Serialize for $t {
            fn serialize<S: Serializer>(&self, s: S) -> core::result::Result<S::Ok, S::Error> {
                let get: fn(&$t) -> $n = $get;
                get(self).serialize(s)
            }
        }

        impl<'de> Deserialize<'de> for $t {
            fn deserialize<D: Deserializer<'de>>(d: D) -> core::result::Result<Self, D::Error> {
                let make: fn($n) -> crate::Result<$t> = $make;
                make(<$n>::deserialize(d)?).map_err(|e| invalid($what, e))
            }
        }
    };
}

as_number!(TimeSpan, i64, |v| v.0, |n| Ok(TimeSpan(n)), "TimeSpan");
as_number!(TimeOnly, i64, |v| v.ticks(), TimeOnly::new, "TimeOnly");
as_number!(DateOnly, i32, |v| v.day_number(), DateOnly::new, "DateOnly");
as_number!(Half, u16, |v| v.0, |n| Ok(Half(n)), "Half");

impl Serialize for Rune {
    fn serialize<S: Serializer>(&self, s: S) -> core::result::Result<S::Ok, S::Error> {
        self.0.serialize(s)
    }
}

impl<'de> Deserialize<'de> for Rune {
    fn deserialize<D: Deserializer<'de>>(d: D) -> core::result::Result<Self, D::Error> {
        char::deserialize(d).map(Rune)
    }
}

#[derive(Serialize, Deserialize)]
#[serde(rename = "Version")]
struct VersionRepr {
    major: i32,
    minor: i32,
    build: i32,
    revision: i32,
}

/// `{ "major", "minor", "build", "revision" }` (an undefined build or revision is -1).
impl Serialize for Version {
    fn serialize<S: Serializer>(&self, s: S) -> core::result::Result<S::Ok, S::Error> {
        VersionRepr { major: self.major, minor: self.minor, build: self.build, revision: self.revision }.serialize(s)
    }
}

impl<'de> Deserialize<'de> for Version {
    fn deserialize<D: Deserializer<'de>>(d: D) -> core::result::Result<Self, D::Error> {
        let r = VersionRepr::deserialize(d)?;
        if r.major < 0 || r.minor < 0 || r.build < -1 || r.revision < -1 || (r.build == -1 && r.revision != -1) {
            return Err(D::Error::custom("invalid Version: negative part"));
        }
        Ok(Version { major: r.major, minor: r.minor, build: r.build, revision: r.revision })
    }
}

/// The bits, as booleans.
impl Serialize for BitArray {
    fn serialize<S: Serializer>(&self, s: S) -> core::result::Result<S::Ok, S::Error> {
        self.to_bools().serialize(s)
    }
}

impl<'de> Deserialize<'de> for BitArray {
    fn deserialize<D: Deserializer<'de>>(d: D) -> core::result::Result<Self, D::Error> {
        Vec::<bool>::deserialize(d).map(|b| BitArray::from_bools(&b))
    }
}

/// A string when it holds no unpaired surrogate, else its UTF-16 code units (both are read back).
impl Serialize for WideString {
    fn serialize<S: Serializer>(&self, s: S) -> core::result::Result<S::Ok, S::Error> {
        match String::from_utf16(&self.0) {
            Ok(text) => s.serialize_str(&text),
            Err(_) => self.0.serialize(s),
        }
    }
}

#[derive(Deserialize)]
#[serde(untagged)]
enum WideRepr {
    Text(String),
    Units(Vec<u16>),
}

impl<'de> Deserialize<'de> for WideString {
    fn deserialize<D: Deserializer<'de>>(d: D) -> core::result::Result<Self, D::Error> {
        Ok(match WideRepr::deserialize(d)? {
            WideRepr::Text(t) => WideString(t.encode_utf16().collect()),
            WideRepr::Units(u) => WideString(u),
        })
    }
}

impl Serialize for Utf8 {
    fn serialize<S: Serializer>(&self, s: S) -> core::result::Result<S::Ok, S::Error> {
        s.serialize_str(&self.0)
    }
}

impl<'de> Deserialize<'de> for Utf8 {
    fn deserialize<D: Deserializer<'de>>(d: D) -> core::result::Result<Self, D::Error> {
        String::deserialize(d).map(Utf8)
    }
}

impl<T: Serialize> Serialize for RawNullables<T> {
    fn serialize<S: Serializer>(&self, s: S) -> core::result::Result<S::Ok, S::Error> {
        self.0.serialize(s)
    }
}

impl<'de, T: Deserialize<'de>> Deserialize<'de> for RawNullables<T> {
    fn deserialize<D: Deserializer<'de>>(d: D) -> core::result::Result<Self, D::Error> {
        Vec::deserialize(d).map(RawNullables)
    }
}

#[derive(Deserialize)]
#[serde(rename = "Grid")]
struct GridRepr<T> {
    lengths: Vec<usize>,
    items: Vec<T>,
}

#[derive(Serialize)]
#[serde(rename = "Grid")]
struct GridRef<'a, T> {
    lengths: Vec<usize>,
    items: &'a [T],
}

/// `{ "lengths": [..], "items": [.. row-major] }`.
impl<T: Serialize, const R: usize> Serialize for Grid<T, R> {
    fn serialize<S: Serializer>(&self, s: S) -> core::result::Result<S::Ok, S::Error> {
        GridRef { lengths: self.dims().to_vec(), items: self.data() }.serialize(s)
    }
}

impl<'de, T: Deserialize<'de>, const R: usize> Deserialize<'de> for Grid<T, R> {
    fn deserialize<D: Deserializer<'de>>(d: D) -> core::result::Result<Self, D::Error> {
        let r = GridRepr::<T>::deserialize(d)?;
        let dims: [usize; R] = r.lengths.try_into().map_err(|_| D::Error::custom("Grid: wrong number of lengths"))?;
        Grid::new(dims, r.items).ok_or_else(|| D::Error::custom("Grid: the items do not fill the lengths"))
    }
}
