//! Values as the corpus's JSON (conformance/README.md, "The value as JSON").

use indexmap::{IndexMap, IndexSet};
use nino::*;
use serde_json::{json, Map, Value};

/// A value's JSON form.
pub trait J {
    fn j(&self) -> Value;
}

/// Runs a generic function on the Rust type a case's type maps to.
pub trait Visit<R> {
    fn visit<T: NinoRead + NinoWrite + J + 'static>(self) -> R;
}

/// An integer: a number up to 2^53, else a decimal string.
fn int(v: i128) -> Value {
    if v.unsigned_abs() <= 1u128 << 53 {
        Value::from(v as i64)
    } else {
        Value::from(v.to_string())
    }
}

macro_rules! ints {
    ($($t:ty),*) => {$(
        impl J for $t {
            fn j(&self) -> Value {
                int(*self as i128)
            }
        }
    )*};
}

ints!(i8, u8, i16, u16, i32, u32, i64, u64, i128);

impl J for u128 {
    fn j(&self) -> Value {
        match i128::try_from(*self) {
            Ok(v) => int(v),
            Err(_) => Value::from(self.to_string()),
        }
    }
}

impl J for bool {
    fn j(&self) -> Value {
        Value::Bool(*self)
    }
}

impl J for f32 {
    fn j(&self) -> Value {
        Value::from(format!("0x{:08x}", self.to_bits()))
    }
}

impl J for f64 {
    fn j(&self) -> Value {
        Value::from(format!("0x{:016x}", self.to_bits()))
    }
}

impl J for Half {
    fn j(&self) -> Value {
        Value::from(format!("0x{:04x}", self.0))
    }
}

impl J for Decimal {
    fn j(&self) -> Value {
        Value::from(self.to_string())
    }
}

impl J for Guid {
    fn j(&self) -> Value {
        Value::from(self.to_string())
    }
}

impl J for DateTime {
    fn j(&self) -> Value {
        let kind = match self.kind() {
            DateTimeKind::Utc => "Utc",
            DateTimeKind::Local => "Local",
            DateTimeKind::Unspecified => "Unspecified",
        };
        json!({ "ticks": int(self.ticks().into()), "kind": kind })
    }
}

impl J for DateTimeOffset {
    fn j(&self) -> Value {
        json!({ "utcTicks": int(self.utc_ticks().into()), "offsetMinutes": self.offset_minutes() })
    }
}

impl J for TimeSpan {
    fn j(&self) -> Value {
        int(self.0.into())
    }
}

impl J for TimeOnly {
    fn j(&self) -> Value {
        int(self.ticks().into())
    }
}

impl J for DateOnly {
    fn j(&self) -> Value {
        int(self.day_number().into())
    }
}

impl J for Version {
    fn j(&self) -> Value {
        json!([self.major, self.minor, self.build, self.revision])
    }
}

impl J for BigInteger {
    /// The decimal text, from the two's complement bytes (any length).
    fn j(&self) -> Value {
        let bytes = self.to_signed_bytes_le();
        let negative = bytes.last().is_some_and(|b| b & 0x80 != 0);
        // Magnitude, little-endian.
        let mut mag: Vec<u8> = bytes.to_vec();
        if negative {
            let mut carry = 1u16;
            for b in &mut mag {
                let v = u16::from(!*b) + carry;
                *b = v as u8;
                carry = v >> 8;
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
            digits.push(b'0' + rem as u8);
        }
        if digits.is_empty() {
            digits.push(b'0');
        }
        if negative {
            digits.push(b'-');
        }
        digits.reverse();
        Value::from(String::from_utf8(digits).unwrap())
    }
}

fn units(units: &[u16]) -> Value {
    if units.iter().any(|u| (0xD800..0xE000).contains(u)) {
        json!({ "utf16": units })
    } else {
        Value::from(String::from_utf16(units).unwrap())
    }
}

impl J for String {
    fn j(&self) -> Value {
        units(&self.encode_utf16().collect::<Vec<_>>())
    }
}

impl J for WideString {
    fn j(&self) -> Value {
        units(&self.0)
    }
}

impl J for Utf8 {
    fn j(&self) -> Value {
        self.0.j()
    }
}

impl<T: J> J for Option<T> {
    fn j(&self) -> Value {
        match self {
            Some(v) => v.j(),
            None => Value::Null,
        }
    }
}

impl<T: J> J for Box<T> {
    fn j(&self) -> Value {
        (**self).j()
    }
}

impl<T: J> J for Vec<T> {
    fn j(&self) -> Value {
        Value::Array(self.iter().map(J::j).collect())
    }
}

impl<T: J> J for IndexSet<T> {
    fn j(&self) -> Value {
        Value::Array(self.iter().map(J::j).collect())
    }
}

impl<K: J, V: J> J for IndexMap<K, V> {
    fn j(&self) -> Value {
        Value::Array(self.iter().map(|(k, v)| json!([k.j(), v.j()])).collect())
    }
}

impl<A: J, B: J> J for (A, B) {
    fn j(&self) -> Value {
        json!([self.0.j(), self.1.j()])
    }
}

impl<T: J, const R: usize> J for Grid<T, R> {
    fn j(&self) -> Value {
        json!({ "lengths": self.dims().to_vec(), "items": self.data().iter().map(J::j).collect::<Vec<_>>() })
    }
}

impl<T: Raw + J> J for RawNullables<T> {
    fn j(&self) -> Value {
        Value::Array(self.0.iter().map(J::j).collect())
    }
}

/// `byte[]` and `sbyte[]`: lowercase hex.
pub fn hex<T: Copy + Into<i16>>(bytes: Option<&[T]>) -> Value {
    match bytes {
        None => Value::Null,
        Some(b) => Value::from(b.iter().map(|&x| format!("{:02x}", x.into() as u8)).collect::<String>()),
    }
}

#[allow(dead_code)]
fn _unused(_: Map<String, Value>) {}
