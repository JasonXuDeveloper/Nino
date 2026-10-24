//! Each built-in value: its bytes, its round trip, and the errors its reader reports.

use std::collections::{BTreeMap, BTreeSet, HashMap, HashSet, VecDeque};

use nino::*;

fn hex(s: &str) -> Vec<u8> {
    let s: String = s.chars().filter(|c| c.is_ascii_hexdigit()).collect();
    (0..s.len()).step_by(2).map(|i| u8::from_str_radix(&s[i..i + 2], 16).unwrap()).collect()
}

fn round<T: NinoRead + NinoWrite + PartialEq + std::fmt::Debug>(value: T, bytes: &str) {
    let expected = hex(bytes);
    assert_eq!(serialize(&value).unwrap(), expected, "{value:?}");
    assert_eq!(deserialize_exact::<T>(&expected).unwrap(), value);
    // Every strict prefix is an error, never a panic.
    for n in 0..expected.len() {
        assert!(deserialize::<T>(&expected[..n]).is_err(), "{value:?} prefix {n}");
    }
}

fn read<T: NinoRead>(bytes: &str) -> T {
    deserialize_exact::<T>(&hex(bytes)).unwrap()
}

fn kind<T: NinoRead + std::fmt::Debug>(bytes: &str) -> ErrorKind {
    deserialize::<T>(&hex(bytes)).unwrap_err().kind()
}

#[test]
fn integers() {
    round(0x12u8, "12");
    round(-2i8, "FE");
    round(-2i16, "FEFF");
    round(0xBEEFu16, "EFBE");
    round(-2i32, "FEFFFFFF");
    round(0xDEADBEEFu32, "EFBEADDE");
    round(i64::MIN, "0000000000000080");
    round(u64::MAX, "FFFFFFFFFFFFFFFF");
    round(-1i128, "FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF");
    round(1u128 << 127, "00000000000000000000000000000080");
    round(1.0f32, "0000803F");
    round(-0.0f64, "0000000000000080");
    let nan = f64::from_bits(0x7FF0_0000_0000_0001);
    assert_eq!(serialize(&nan).unwrap(), hex("0100000000 00F07F"));
    assert_eq!(deserialize::<f64>(&hex("01000000 0000F07F")).unwrap().to_bits(), nan.to_bits());
}

#[test]
fn bools_and_presence() {
    round(true, "01");
    round(false, "00");
    assert_eq!(kind::<bool>("02"), ErrorKind::InvalidBool);
    round(Some(5u8), "01 05");
    round(None::<u8>, "00");
    assert_eq!(kind::<Option<u8>>("02 05"), ErrorKind::InvalidPresence);
    round(vec![true, false], "02000000 01 00");
    assert_eq!(kind::<Vec<bool>>("02000000 01 FF"), ErrorKind::InvalidBool);
}

#[test]
fn lengths() {
    round(vec![1i32, 2], "02000000 01000000 02000000");
    round(Vec::<i32>::new(), "00000000");
    round(None::<Vec<i32>>, "FFFFFFFF");
    round(Some(vec![7u8]), "01000000 07");
    // A non-nullable collection read from -1.
    assert_eq!(kind::<Vec<i32>>("FFFFFFFF"), ErrorKind::UnexpectedNull);
    // Below -1, or more elements than bytes left: rejected before allocating.
    assert_eq!(kind::<Vec<i32>>("FEFFFFFF"), ErrorKind::InvalidLength);
    assert_eq!(kind::<Vec<i32>>("FFFFFF7F"), ErrorKind::InvalidLength);
    assert_eq!(kind::<Vec<i32>>("02000000 01000000 020000"), ErrorKind::InvalidLength);
    assert_eq!(kind::<Vec<String>>("FFFFFF7F FFFFFFFF"), ErrorKind::InvalidLength);
    assert_eq!(kind::<Vec<Vec<u8>>>("FFFFFF7F"), ErrorKind::InvalidLength);
    round(VecDeque::from(vec![1u16, 2]), "02000000 0100 0200");
    round(vec![Some(1u8), None], "02000000 01 01 00");
}

#[test]
fn strings() {
    round(String::new(), "00000000");
    round(Some(String::from("abc")), "FCFFFFFF 616263");
    round(None::<String>, "FFFFFFFF");
    round(String::from("ÿ"), "FEFFFFFF FF");
    round(String::from("Ā"), "01000000 0001");
    // Latin-1 bytes for a header that says UTF-16 is still read (.NET writes Latin-1, but reads both).
    assert_eq!(deserialize_exact::<String>(&hex("01000000 6100")).unwrap(), "a");
    assert_eq!(kind::<String>("FDFFFFFF 61"), ErrorKind::InvalidLength);
    assert!(matches!(kind::<String>("02000000 6100"), ErrorKind::InvalidLength | ErrorKind::UnexpectedEnd));
    assert_eq!(kind::<String>("00000080"), ErrorKind::InvalidLength);

    round(Utf8(String::from("é")), "01000000 02000000 C3A9");
    round(Some(Utf8(String::new())), "00000000");
    // Byte count outside [chars, 3 * chars], bad UTF-8, or a char count that does not match.
    assert_eq!(kind::<Utf8>("01000000 04000000 C3A9C3A9"), ErrorKind::InvalidLength);
    assert_eq!(kind::<Utf8>("02000000 01000000 61"), ErrorKind::InvalidLength);
    assert_eq!(kind::<Utf8>("01000000 02000000 C328"), ErrorKind::InvalidUtf8);
    assert_eq!(kind::<Utf8>("02000000 02000000 C3A9"), ErrorKind::InvalidUtf8);
    assert_eq!(kind::<Utf8>("01000000 03000000 EDA080"), ErrorKind::InvalidUtf8);
    // An astral char is 2 UTF-16 units.
    round(Utf8(String::from("😀")), "02000000 04000000 F09F9880");

    round(WideString(vec![0xDC00, 0x61]), "02000000 00DC 6100");
    round(WideString(vec![0x61]), "FEFFFFFF 61");
    round(None::<WideString>, "FFFFFFFF");
    // A C# char is a UTF-16 code unit (u16): lone surrogates included.
    round(0xD800u16, "00D8");
}

/// What .NET writes for a string: Latin-1 when every char is at most U+00FF, else UTF-16 (the reference the
/// writer's one-pass transcoding is checked against).
fn dotnet_string(text: &str) -> Vec<u8> {
    let mut out = Vec::new();
    if text.chars().all(|c| u32::from(c) <= 0xFF) {
        let n = text.chars().count() as i32;
        out.extend_from_slice(&(if n == 0 { 0 } else { !n }).to_le_bytes());
        out.extend(text.chars().map(|c| u32::from(c) as u8));
    } else {
        let units: Vec<u16> = text.encode_utf16().collect();
        out.extend_from_slice(&(units.len() as i32).to_le_bytes());
        units.iter().for_each(|u| out.extend_from_slice(&u.to_le_bytes()));
    }
    out
}

#[test]
fn string_encodings() {
    // Every length up to 40 (the ASCII check reads words of 8 and 4 bytes, and loops past 16), with the char that
    // decides the encoding at each position.
    for len in 0..40 {
        for at in 0..=len {
            for c in ['a', '\u{7F}', '\u{80}', 'é', 'ÿ', 'Ā', '字', '\u{FFFF}', '😀'] {
                let text: String =
                    (0..len).map(|i| if i == at { c } else { (b'a' + (i % 26) as u8) as char }).collect();
                let bytes = serialize(&text).unwrap();
                assert_eq!(bytes, dotnet_string(&text), "{text:?}");
                assert_eq!(deserialize_exact::<String>(&bytes).unwrap(), text);
            }
        }
    }
    // Every Latin-1 char, every char of each UTF-8 length, and mixes, after other text in the buffer.
    let latin1: String = (0..=0xFFu8).map(char::from).collect();
    let mixed = "a\u{80}\u{7FF}\u{800}\u{FFFF}\u{10000}\u{10FFFF}é字😀z";
    for text in [latin1.as_str(), mixed, "\u{10FFFF}", "ÿÿ", "Āa", "é字"] {
        let mut out = vec![0xAA];
        serialize_into(&text.to_owned(), &mut out).unwrap();
        assert_eq!(out[1..], dotnet_string(text)[..], "{text:?}");
        assert_eq!(deserialize_exact::<String>(&out[1..]).unwrap(), text);
    }
    // The writer reads eight bytes at a time from an ASCII byte on and decodes runs of other chars in loops of their
    // own, switching from Latin-1 to UTF-16 at the first char above U+00FF: two chars of any classes at any two
    // positions (so every pair of classes meets across every word boundary, in either order).
    let classes = ['a', '\u{7F}', '\u{80}', 'é', 'ÿ', 'Ā', '\u{7FF}', '\u{800}', '字', '\u{FFFF}', '\u{10000}', '😀'];
    let check = |text: &str| {
        let mut out = vec![0xAA; 3];
        serialize_into(&text.to_owned(), &mut out).unwrap();
        assert_eq!(out[3..], dotnet_string(text)[..], "{text:?}");
        assert_eq!(deserialize_exact::<String>(&out[3..]).unwrap(), text);
    };
    for len in [1, 2, 7, 8, 9, 15, 16, 17, 24, 33] {
        for p in 0..len {
            for q in p..len {
                for c1 in classes {
                    for c2 in classes {
                        let text: String = (0..len)
                            .map(|i| match i {
                                _ if i == q => c2,
                                _ if i == p => c1,
                                _ => (b'a' + (i % 26) as u8) as char,
                            })
                            .collect();
                        check(&text);
                    }
                }
            }
        }
    }
    // Runs: k ASCII chars, then n of one class, then m ASCII chars, then n of another.
    for k in 0..12 {
        for n in 0..12 {
            for m in [0, 1, 7, 8, 9] {
                for c1 in classes {
                    for c2 in classes {
                        let mut text = "x".repeat(k);
                        text.extend(core::iter::repeat_n(c1, n));
                        text.push_str(&"y".repeat(m));
                        text.extend(core::iter::repeat_n(c2, n));
                        check(&text);
                    }
                }
            }
        }
    }
    // Text drawn at random from every class, lengths 0 to 40.
    let mut seed = 0x2545_F491_4F6C_DD1Du64;
    for round in 0..22_000 {
        seed ^= seed << 13;
        seed ^= seed >> 7;
        seed ^= seed << 17;
        // Then longer text, up to 600 chars.
        let len = if round < 20_000 { (seed % 41) as usize } else { (seed % 600) as usize };
        let text: String = (0..len)
            .map(|i| {
                let pick = (seed >> (i % 16 * 4)) as usize % (classes.len() * 2);
                classes.get(pick).copied().unwrap_or((b'a' + (i % 26) as u8) as char)
            })
            .collect();
        check(&text);
    }
    // Lone surrogates read as U+FFFD; a pair reads as its char.
    assert_eq!(deserialize_exact::<String>(&hex("03000000 3DD8 00DE 00DC")).unwrap(), "😀\u{FFFD}");
    assert_eq!(deserialize_exact::<String>(&hex("02000000 3DD8 6100")).unwrap(), "\u{FFFD}a");
}

#[test]
fn bcl_values() {
    round(Decimal::new(123_456, 4, true).unwrap(), "00000480 00000000 40E2010000000000");
    assert_eq!(Decimal::new(123_456, 4, true).unwrap().to_string(), "-12.3456");
    assert_eq!(Decimal::new(5, 3, false).unwrap().to_string(), "0.005");
    assert_eq!(kind::<Decimal>("00001D00 00000000 0000000000000000"), ErrorKind::InvalidDecimal);
    assert_eq!(kind::<Decimal>("01000000 00000000 0000000000000000"), ErrorKind::InvalidDecimal);

    let g = Guid::from_u128(0x0011_2233_4455_6677_8899_aabb_ccdd_eeff);
    round(g, "33221100 5544 7766 8899AABBCCDDEEFF");
    assert_eq!(g.to_string(), "00112233-4455-6677-8899-aabbccddeeff");
    assert_eq!(g.to_u128(), 0x0011_2233_4455_6677_8899_aabb_ccdd_eeff);

    round(DateTime::new(MAX_TICKS, DateTimeKind::Utc).unwrap(), "FF3F37F4 7528CA6B");
    round(DateTime::new(0, DateTimeKind::Local).unwrap(), "00000000 00000080");
    // Kind 3: .NET's local time in the repeated hour of a daylight saving change, read as Local and written back as is.
    round(read::<DateTime>("80C0BF7A 3F0BDCC8"), "80C0BF7A 3F0BDCC8");
    assert_eq!(read::<DateTime>("00000000 000000C0").kind(), DateTimeKind::Local);
    assert!(read::<DateTime>("00000000 000000C0").is_ambiguous_local());
    assert_eq!(kind::<DateTime>("0040 37F4 757CCA2B"), ErrorKind::InvalidDateTime);
    round(TimeSpan(-1), "FFFFFFFFFFFFFFFF");
    round(DateOnly::new(DateOnly::MAX_DAY).unwrap(), "DAB93700");
    assert_eq!(kind::<DateOnly>("DBB93700"), ErrorKind::InvalidDateTime);
    assert_eq!(kind::<DateOnly>("FFFFFFFF"), ErrorKind::InvalidDateTime);
    round(TimeOnly::new(TICKS_PER_DAY - 1).unwrap(), "FFBF69 2AC9000000");
    assert_eq!(kind::<TimeOnly>("00C0692AC9000000"), ErrorKind::InvalidDateTime);

    round(DateTimeOffset::new(0x0100_0000_0000, -840).unwrap(), "0000000000010000 B8FC");
    assert_eq!(kind::<DateTimeOffset>("0000000000010000 B7FC"), ErrorKind::InvalidDateTime);
    // The local time (UTC + offset) must be in range too.
    assert_eq!(kind::<DateTimeOffset>("0000000000000000 B8FC"), ErrorKind::InvalidDateTime);

    round(Half(0x3C00), "003C");
    round(Rune('😀'), "00F60100");
    assert_eq!(kind::<Rune>("00D80000"), ErrorKind::InvalidChar);
    assert_eq!(kind::<Rune>("00001100"), ErrorKind::InvalidChar);
}

#[test]
fn version() {
    let v = Version { major: 1, minor: 2, build: 3, revision: -1 };
    round(Some(v), "01000000 02000000 03000000 FFFFFFFF");
    round(None::<Version>, "FFFFFFFF");
    // A negative build makes build and revision undefined, as in .NET.
    assert_eq!(
        deserialize_exact::<Option<Version>>(&hex("01000000 02000000 FEFFFFFF 05000000")).unwrap(),
        Some(Version { major: 1, minor: 2, build: -1, revision: -1 })
    );
    assert_eq!(kind::<Option<Version>>("01000000 FEFFFFFF FFFFFFFF FFFFFFFF"), ErrorKind::InvalidVersion);
    assert_eq!(kind::<Option<Version>>("FEFFFFFF 00000000 FFFFFFFF FFFFFFFF"), ErrorKind::InvalidVersion);
    assert_eq!(kind::<Version>("FFFFFFFF"), ErrorKind::UnexpectedNull);
}

#[test]
fn big_integer() {
    round(BigInteger::from_i128(0), "01000000 00");
    round(BigInteger::from_i128(-1), "01000000 FF");
    round(BigInteger::from_i128(128), "02000000 8000");
    round(BigInteger::from_i128(-129), "02000000 7FFF");
    assert_eq!(BigInteger::from_i128(i128::MIN).to_i128(), Some(i128::MIN));
    // Not minimal, or empty.
    assert_eq!(kind::<BigInteger>("02000000 0100"), ErrorKind::InvalidLength);
    assert_eq!(kind::<BigInteger>("02000000 FFFF"), ErrorKind::InvalidLength);
    // A count of 0 reads as zero (.NET never writes it); -1 is invalid.
    assert_eq!(deserialize_exact::<BigInteger>(&hex("00000000")).unwrap(), BigInteger::from_i128(0));
    assert_eq!(kind::<BigInteger>("FFFFFFFF"), ErrorKind::InvalidLength);
}

#[test]
fn bit_array() {
    let bits = BitArray::from_bools(&[true, false, true, true, false, false, false, false, true]);
    round(Some(bits.clone()), "09000000 0D01");
    assert_eq!(bits.to_bools().len(), 9);
    round(None::<BitArray>, "FFFFFFFF");
    assert_eq!(kind::<BitArray>("09000000 0D03"), ErrorKind::InvalidBits);
}

#[test]
fn tuples() {
    round((1u8, 2i32), "01 02000000");
    round((1u8, String::from("a"), Some(3i16)), "01 FEFFFFFF61 01 0300");
}

#[test]
fn maps_and_sets() {
    let mut m = BTreeMap::new();
    m.insert(1i32, String::from("a"));
    m.insert(2, String::from("b"));
    round(m, "02000000 01000000 FEFFFFFF61 02000000 FEFFFFFF62");
    assert_eq!(kind::<BTreeMap<i32, i32>>("02000000 01000000 02000000 01000000 03000000"), ErrorKind::DuplicateKey);
    assert_eq!(kind::<HashMap<i32, i32>>("02000000 01000000 02000000 01000000 03000000"), ErrorKind::DuplicateKey);
    assert_eq!(kind::<HashSet<u8>>("02000000 01 01"), ErrorKind::DuplicateKey);
    assert_eq!(kind::<BTreeSet<u8>>("02000000 01 01"), ErrorKind::DuplicateKey);
    round(BTreeSet::from([1u8, 2]), "02000000 01 02");
    let h: HashMap<String, u8> = deserialize_exact(&hex("01000000 FEFFFFFF61 05")).unwrap();
    assert_eq!(h["a"], 5);
    round(None::<HashMap<u8, u8>>, "FFFFFFFF");
}

#[test]
fn grids() {
    let g = Grid::<i32, 2>::new([2, 3], (1..=6).collect()).unwrap();
    round(Some(g), "02000000 03000000 01000000 02000000 03000000 04000000 05000000 06000000");
    round(None::<Grid<i32, 2>>, "FFFFFFFF");
    // A later dimension below 0, a first one below -1, or more elements than bytes.
    assert_eq!(kind::<Option<Grid<i32, 2>>>("02000000 FFFFFFFF"), ErrorKind::InvalidLength);
    assert_eq!(kind::<Option<Grid<i32, 2>>>("FEFFFFFF 00000000"), ErrorKind::InvalidLength);
    assert_eq!(kind::<Option<Grid<i32, 2>>>("FFFF0000 FFFF0000"), ErrorKind::InvalidLength);
    // Zero rows with a large second dimension is valid (no elements).
    assert!(deserialize_exact::<Option<Grid<i32, 2>>>(&hex("00000000 FFFFFF7F")).unwrap().is_some());
}

#[test]
fn trailing_data() {
    assert_eq!(deserialize::<u8>(&hex("01 02")).unwrap(), 1);
    assert_eq!(deserialize_exact::<u8>(&hex("01 02")).unwrap_err().kind(), ErrorKind::TrailingData);
}

#[test]
fn serialize_into_appends_and_restores() {
    let mut out = vec![9u8];
    serialize_into(&7u16, &mut out).unwrap();
    assert_eq!(out, hex("09 0700"));
    let too_deep: Vec<Option<Box<Deep>>> = vec![Deep::chain(600)];
    assert!(serialize_into(&too_deep, &mut out).is_err());
    assert_eq!(out, hex("09 0700"));
    std::mem::forget(too_deep);
}

/// A non-deferrable recursive class, to make writing fail.
#[derive(Default, Debug)]
struct Deep {
    next: Option<Box<Deep>>,
}

impl Deep {
    fn chain(n: usize) -> Option<Box<Deep>> {
        let mut head = None;
        for _ in 0..n {
            head = Some(Box::new(Deep { next: head }));
        }
        head
    }
}

impl NinoReadBody for Deep {
    const DEFERRABLE: bool = false;
    fn read_body(&mut self, r: &mut Reader<'_>) -> Result<()> {
        self.next = r.read()?;
        Ok(())
    }
}

impl NinoWriteBody for Deep {
    const DEFERRABLE: bool = false;

    fn write_body<'a>(&'a self, w: &mut Writer<'a>) -> Result<()> {
        w.write(&self.next)
    }
}

impl NinoRead for Deep {
    fn read(r: &mut Reader<'_>) -> Result<Self> {
        r.read_sealed_value()
    }
    fn read_option(r: &mut Reader<'_>) -> Result<Option<Self>> {
        r.read_sealed()
    }
}

impl NinoWrite for Deep {
    fn write<'a>(&'a self, w: &mut Writer<'a>) -> Result<()> {
        w.write_sealed(Some(self))
    }
    fn write_option<'a>(value: Option<&'a Self>, w: &mut Writer<'a>) -> Result<()> {
        w.write_sealed(value)
    }
}

#[test]
fn type_ids() {
    // FNV-1a over UTF-16 code units, low byte first; 0 becomes 1.
    assert_eq!(type_id("Spec.Circle") as u32, 0xEDC02BAC);
    assert_eq!(type_id("Id") as u32, 0x98C41948);
    assert_eq!(type_id("中"), {
        let mut h: u32 = 0x811C9DC5;
        for b in [0x2D, 0x4E] {
            h = (h ^ b).wrapping_mul(0x0100_0193);
        }
        h as i32
    });
}

#[cfg(feature = "uuid")]
#[test]
fn uuid_feature() {
    let g = Guid::from_u128(0x0011_2233_4455_6677_8899_aabb_ccdd_eeff);
    let u: uuid::Uuid = g.into();
    assert_eq!(u.to_string(), "00112233-4455-6677-8899-aabbccddeeff");
    assert_eq!(serialize(&u).unwrap(), serialize(&g).unwrap());
    assert_eq!(Guid::from(u), g);
}

#[cfg(feature = "rust_decimal")]
#[test]
fn rust_decimal_feature() {
    let d = Decimal::new(123_456, 4, true).unwrap();
    let r: rust_decimal::Decimal = d.into();
    assert_eq!(r.to_string(), "-12.3456");
    assert_eq!(serialize(&r).unwrap(), serialize(&d).unwrap());
    assert_eq!(deserialize_exact::<rust_decimal::Decimal>(&serialize(&d).unwrap()).unwrap(), r);
}

#[cfg(feature = "num-bigint")]
#[test]
fn num_bigint_feature() {
    for v in [0i64, -1, 127, 128, -129, i64::MIN] {
        let b = num_bigint::BigInt::from(v);
        let bytes = serialize(&b).unwrap();
        assert_eq!(bytes, serialize(&BigInteger::from_i128(v.into())).unwrap());
        assert_eq!(deserialize_exact::<num_bigint::BigInt>(&bytes).unwrap(), b);
    }
}

#[cfg(feature = "chrono")]
#[test]
fn chrono_feature() {
    use chrono::{NaiveDate, NaiveDateTime, NaiveTime, TimeDelta};
    let epoch = DateTime::new(UNIX_EPOCH_TICKS, DateTimeKind::Utc).unwrap();
    let n = NaiveDateTime::try_from(epoch).unwrap();
    assert_eq!(n, NaiveDate::from_ymd_opt(1970, 1, 1).unwrap().and_hms_opt(0, 0, 0).unwrap());
    assert_eq!(DateTime::try_from(n).unwrap().ticks(), UNIX_EPOCH_TICKS);
    let max = DateTime::new(MAX_TICKS, DateTimeKind::Unspecified).unwrap();
    assert_eq!(DateTime::try_from(NaiveDateTime::try_from(max).unwrap()).unwrap(), max);
    let d = DateOnly::new(DateOnly::MAX_DAY).unwrap();
    assert_eq!(NaiveDate::try_from(d).unwrap(), NaiveDate::from_ymd_opt(9999, 12, 31).unwrap());
    let t = TimeOnly::new(TICKS_PER_DAY - 1).unwrap();
    assert_eq!(TimeOnly::try_from(NaiveTime::try_from(t).unwrap()).unwrap(), t);
    let span = TimeSpan(-12_345_678_901);
    assert_eq!(TimeSpan::try_from(TimeDelta::from(span)).unwrap(), span);
    let o = DateTimeOffset::new(UNIX_EPOCH_TICKS, 330).unwrap();
    let c = chrono::DateTime::<chrono::FixedOffset>::try_from(o).unwrap();
    assert_eq!(c.naive_local(), NaiveDate::from_ymd_opt(1970, 1, 1).unwrap().and_hms_opt(5, 30, 0).unwrap());
    assert_eq!(DateTimeOffset::try_from(c).unwrap(), o);
}

#[cfg(feature = "indexmap")]
#[test]
fn indexmap_feature() {
    use indexmap::{IndexMap, IndexSet};
    let bytes = hex("02000000 02000000 FEFFFFFF62 01000000 FEFFFFFF61");
    let m: IndexMap<i32, String> = deserialize_exact(&bytes).unwrap();
    assert_eq!(m.keys().copied().collect::<Vec<_>>(), vec![2, 1]);
    assert_eq!(serialize(&m).unwrap(), bytes);
    assert_eq!(kind::<IndexMap<u8, u8>>("02000000 01 01 01 02"), ErrorKind::DuplicateKey);
    let s: IndexSet<u8> = deserialize_exact(&hex("02000000 02 01")).unwrap();
    assert_eq!(serialize(&s).unwrap(), hex("02000000 02 01"));
    assert_eq!(kind::<IndexSet<u8>>("02000000 01 01"), ErrorKind::DuplicateKey);
}

#[test]
fn text_forms() {
    for v in [0i128, 1, -1, 127, 128, -128, -129, 255, 256, i128::from(i64::MAX), i128::MIN + 1, i128::MAX] {
        let b = BigInteger::from_i128(v);
        assert_eq!(b.to_string(), v.to_string());
        assert_eq!(v.to_string().parse::<BigInteger>().unwrap(), b);
    }
    assert_eq!("+00042".parse::<BigInteger>().unwrap(), BigInteger::from_i128(42));
    assert_eq!("-0".parse::<BigInteger>().unwrap(), BigInteger::from_i128(0));
    for bad in ["", "-", "1.5", "0x10", " 1"] {
        assert_eq!(bad.parse::<BigInteger>().unwrap_err().kind(), ErrorKind::InvalidText, "{bad:?}");
    }

    for text in ["0", "1.50", "-12.3456", "79228162514264337593543950335", "0.0000000000000000000000000001"] {
        let d: Decimal = text.parse().unwrap();
        assert_eq!(d.to_string(), text);
    }
    assert_eq!("1.50".parse::<Decimal>().unwrap(), Decimal::new(150, 2, false).unwrap());
    for bad in ["", ".", "1e5", "--1", "1.2.3"] {
        assert_eq!(bad.parse::<Decimal>().unwrap_err().kind(), ErrorKind::InvalidText, "{bad:?}");
    }
    // Text in the form but out of range is the reader's error.
    for big in ["79228162514264337593543950336", "0.00000000000000000000000000001"] {
        assert_eq!(big.parse::<Decimal>().unwrap_err().kind(), ErrorKind::InvalidDecimal, "{big:?}");
    }

    let g: Guid = "00112233-4455-6677-8899-AABBCCDDEEFF".parse().unwrap();
    assert_eq!(g.to_u128(), 0x0011_2233_4455_6677_8899_aabb_ccdd_eeff);
    assert_eq!(g.to_string().parse::<Guid>().unwrap(), g);
    for bad in [
        "",
        "00112233445566778899aabbccddeeff",
        "0011223-34455-6677-8899-aabbccddeeff",
        "+0112233-4455-6677-8899-aabbccddeeff",
    ] {
        assert_eq!(bad.parse::<Guid>().unwrap_err().kind(), ErrorKind::InvalidText, "{bad:?}");
    }
}

#[cfg(feature = "serde")]
#[test]
fn serde_feature() {
    fn json<T: serde::Serialize + serde::de::DeserializeOwned + PartialEq + std::fmt::Debug>(v: T, text: &str) {
        assert_eq!(serde_json::to_string(&v).unwrap(), text);
        assert_eq!(serde_json::from_str::<T>(text).unwrap(), v);
    }
    json(Decimal::new(150, 2, true).unwrap(), r#""-1.50""#);
    json(BigInteger::from_i128(-129), r#""-129""#);
    json(Guid::from_u128(1), r#""00000000-0000-0000-0000-000000000001""#);
    json(DateTime::new(5, DateTimeKind::Utc).unwrap(), r#"{"ticks":5,"kind":"Utc"}"#);
    json(DateTimeOffset::new(36_000_000_000, -60).unwrap(), r#"{"utcTicks":36000000000,"offsetMinutes":-60}"#);
    json(TimeSpan(-3), "-3");
    json(DateOnly::new(7).unwrap(), "7");
    json(Version { major: 1, minor: 2, build: -1, revision: -1 }, r#"{"major":1,"minor":2,"build":-1,"revision":-1}"#);
    json(BitArray::from_bools(&[true, false]), "[true,false]");
    json(WideString::from("hi"), r#""hi""#);
    json(WideString(vec![0xD800]), "[55296]");
    json(Grid::new([1, 2], vec![1, 2]).unwrap(), r#"{"lengths":[1,2],"items":[1,2]}"#);
    // Validated as the reader validates.
    assert!(serde_json::from_str::<DateOnly>("-1").is_err());
    assert!(serde_json::from_str::<Decimal>(r#""1e3""#).is_err());
    assert!(serde_json::from_str::<Version>(r#"{"major":1,"minor":2,"build":-1,"revision":0}"#).is_err());
    assert!(serde_json::from_str::<Grid<i32, 2>>(r#"{"lengths":[2,2],"items":[1]}"#).is_err());
}
