//! The tests of every kernel against a plain decoder or encoder (Miri runs them too: the module root says how).

use super::decode::*;
use super::*;

/// The plain decoder: Latin-1 when every char is at most U+00FF, else UTF-16 LE.
fn reference(text: &str) -> (Vec<u8>, bool) {
    if text.chars().all(|c| u32::from(c) <= 0xFF) {
        (text.chars().map(|c| u32::from(c) as u8).collect(), true)
    } else {
        (text.encode_utf16().flat_map(u16::to_le_bytes).collect(), false)
    }
}

/// Every way this target can transcode: words, and the vector blocks it has.
type Way = fn(&mut Vec<u8>, &str) -> bool;

fn ways() -> Vec<(&'static str, Way)> {
    #[allow(unused_mut)]
    let mut ways: Vec<(&'static str, Way)> = vec![("words", transcode_with::<Words>), ("default", transcode)];
    #[cfg(target_arch = "x86_64")]
    {
        ways.push(("sse2", transcode_with::<Sse2>));
        if x86::tier() >= x86::Tier::Ssse3 {
            // SAFETY: the CPU has SSSE3.
            ways.push(("ssse3", |out, text| unsafe { transcode_ssse3(out, text) }));
        }
    }
    #[cfg(all(target_arch = "aarch64", target_endian = "little"))]
    {
        ways.push(("neon-ascii", transcode_with::<NeonAscii>));
        ways.push(("neon", transcode_with::<Neon>));
    }
    ways
}

/// Every one-pass writer this CPU can run (each tier's, not only the best).
#[allow(clippy::vec_init_then_push)] // Which pushes run depends on the target.
fn one_pass() -> Vec<(&'static str, Writer)> {
    #[allow(unused_mut)]
    let mut kernels: Vec<(&'static str, Writer)> = vec![];
    #[cfg(target_arch = "x86_64")]
    {
        if x86::tier() >= x86::Tier::Sse41 {
            // SAFETY: the CPU has SSE4.1 (its tier), and the text is short.
            kernels.push(("sse4.1", |out, text| unsafe {
                if text.len() < 64 {
                    x86::wide_sse41::<true>(out, text)
                } else {
                    x86::wide_sse41::<false>(out, text)
                }
            }));
            // SAFETY: as above; the copy for any length, on short text too.
            kernels.push(("sse4.1 (any length)", |out, text| unsafe { x86::wide_sse41::<false>(out, text) }));
        }
        if x86::tier() >= x86::Tier::Avx2 {
            // SAFETY: the CPU has AVX2 (its tier), and the text is short.
            kernels.push(("avx2", |out, text| unsafe {
                if text.len() < 64 {
                    x86::wide_avx2::<true>(out, text)
                } else {
                    x86::wide_avx2::<false>(out, text)
                }
            }));
            // SAFETY: as above; the copy for any length, on short text too.
            kernels.push(("avx2 (any length)", |out, text| unsafe { x86::wide_avx2::<false>(out, text) }));
        }
        #[cfg(nino_avx512)]
        if x86::tier() >= x86::Tier::Avx512 {
            // SAFETY: the CPU has AVX-512 (its tier), and the text is short.
            kernels.push(("avx-512", |out, text| unsafe {
                if text.len() < 64 {
                    x86::wide_avx512::<true>(out, text)
                } else {
                    x86::wide_avx512::<false>(out, text)
                }
            }));
            // SAFETY: as above; the copy for any length, on short text too.
            kernels.push(("avx-512 (any length)", |out, text| unsafe { x86::wide_avx512::<false>(out, text) }));
        }
    }
    #[cfg(all(target_arch = "aarch64", target_endian = "little"))]
    kernels.push(("neon", neon::wide));
    kernels
}

/// A one-pass writer.
type Writer = fn(&mut Vec<u8>, &str);
type Latin1Way = unsafe fn(&[u8], *mut u8, usize) -> usize;
type Ways<T> = Vec<(&'static str, T)>;
type Utf16Way = unsafe fn(&[[u8; 2]], *mut u8, usize) -> usize;

/// Every reader's decoder this CPU can run: the plain ones and each tier's kernels.
fn decoder_ways() -> (Ways<Latin1Way>, Ways<Utf16Way>) {
    #[allow(unused_mut)]
    let mut latin1: Ways<Latin1Way> = vec![(
        "words",
        // SAFETY: the caller's room is the UTF-8's length.
        |b, d, _| unsafe { latin1_words(b, 0, d, 0) },
    )];
    #[allow(unused_mut)]
    let mut utf16: Ways<Utf16Way> = vec![(
        "words",
        // SAFETY: the caller's room is what the units count.
        |u, d, _| unsafe { utf16_words(u, 0, u.len(), d, 0).1 },
    )];
    #[cfg(target_arch = "x86_64")]
    {
        if x86::tier() >= x86::Tier::Ssse3 {
            // SAFETY: the CPU has SSSE3 (its tier); the caller's room.
            latin1.push(("ssse3", |b, d, r| unsafe { x86::latin1_ssse3(b, d, r) }));
        }
        if x86::tier() >= x86::Tier::Sse41 {
            // SAFETY: the CPU has SSE4.1 (its tier); the caller's room.
            utf16.push(("sse4.1", |u, d, r| unsafe { x86::utf16_sse41(u, d, r) }));
        }
        #[cfg(nino_avx512)]
        if x86::tier() >= x86::Tier::Avx512 {
            // SAFETY: the CPU has AVX-512 (its tier); the caller's room.
            latin1.push(("avx-512", |b, d, _| unsafe { x86::latin1_avx512(b, d) }));
            // SAFETY: as above.
            utf16.push(("avx-512", |u, d, _| unsafe { x86::utf16_avx512(u, d) }));
        }
    }
    #[cfg(all(target_arch = "aarch64", target_endian = "little"))]
    {
        latin1.push(("neon", neon::latin1));
        utf16.push(("neon", neon::utf16));
    }
    (latin1, utf16)
}

fn check(text: &str) {
    let expected = reference(text);
    // The ways that write the header too: the header, then the same bytes.
    let count = if expected.1 { expected.0.len() } else { expected.0.len() / 2 };
    let mut want = vec![0xAA; 3];
    let (header, n) = crate::strings::header((count as u32).wrapping_mul(2).wrapping_add(u32::from(expected.1)));
    want.extend_from_slice(header.get(..n).unwrap_or_default());
    want.extend_from_slice(&expected.0);
    if text.len() <= SHORT_WIDE && !text.is_ascii() {
        let mut out = vec![0xAA; 3];
        short_wide(&mut out, text);
        assert_eq!(out, want, "short_wide: {text:?}");
    }
    if !text.is_ascii() {
        let mut out = vec![0xAA; 3];
        if wide(&mut out, text) {
            assert_eq!(out, want, "wide: {text:?}");
        } else {
            assert_eq!(out, [0xAA; 3], "wide: {text:?}");
        }
    }
    for (name, kernel) in one_pass() {
        // ASCII text too, which `wide` is not given.
        let mut out = vec![0xAA; 3];
        kernel(&mut out, text);
        assert_eq!(out, want, "{name}: {text:?}");
    }
    for (name, way) in ways() {
        // After a few bytes already in the buffer, so the output is unaligned.
        for prefix in if cfg!(miri) { [3usize].as_slice() } else { [0usize, 3].as_slice() } {
            let prefix = *prefix;
            let mut out = vec![0xAA; prefix];
            let latin1 = way(&mut out, text);
            assert_eq!(
                (out.get(prefix..).unwrap_or_default(), latin1),
                (&expected.0[..], expected.1),
                "{name}: {text:?}"
            );
        }
    }
}

const CLASSES: [char; 12] =
    ['a', '\u{7F}', '\u{80}', 'é', 'ÿ', 'Ā', '\u{7FF}', '\u{800}', '字', '\u{FFFF}', '\u{10000}', '😀'];

/// Every length up to 256 chars with every char class at every position. Under Miri, which is slow, the lengths
/// around a block's and a word's edges and the pair steps' threshold, at the first, a middle and the last
/// position.
fn shapes() -> Vec<(usize, Vec<usize>)> {
    if cfg!(miri) {
        [0, 1, 2, 7, 8, 9, 15, 16, 17, 23, 24, 31, 32, 33, 47, 48]
            .into_iter()
            .map(|len: usize| (len, if len == 0 { vec![] } else { vec![0, len / 2, len.saturating_sub(1)] }))
            .collect()
    } else {
        (0..=256).map(|len| (len, (0..len).collect())).collect()
    }
}

#[test]
fn every_class_at_every_position() {
    // On fillers of ASCII, Latin-1 and wider text, so blocks start and end at every offset of a run.
    let fillers: &[char] = if cfg!(miri) { &['x', 'é', 'Ж', '字'] } else { &['x', 'é', 'Ж', '字', '😀'] };
    for &filler in fillers {
        for (len, positions) in shapes() {
            check(&core::iter::repeat_n(filler, len).collect::<String>());
            for at in positions {
                for c in CLASSES {
                    let text: String = (0..len).map(|i| if i == at { c } else { filler }).collect();
                    check(&text);
                }
            }
        }
    }
}

/// Every way `finish` places a one-pass writer's chars: below 64 bytes, from 64 to 127 with a one-byte header and
/// moved up for a three-byte one (two blocks, or a copy past 128 bytes of output), and from 128 bytes left in
/// place or moved down for a one-byte header (under 64 code units in 128 to 255 bytes). Lengths at each edge.
#[test]
fn header_moves() {
    let mixed = |ascii: usize, c: char, n: usize| -> String {
        core::iter::repeat_n('a', ascii).chain(core::iter::repeat_n(c, n)).collect()
    };
    for text in [
        mixed(62, 'é', 1),   // 63 chars in 64 bytes: one-byte header
        mixed(62, 'é', 2),   // 64 chars in 66 bytes: Latin-1, moved up
        mixed(125, 'é', 1),  // 126 chars in 127 bytes: moved up
        mixed(30, 'é', 34),  // 64 chars in 98 bytes
        mixed(63, '字', 1),  // 64 code units in 66 bytes: UTF-16, moved up (128 bytes)
        mixed(64, '字', 1),  // 65 code units: moved up as a copy (130 bytes)
        mixed(120, '字', 2), // 122 code units in 126 bytes
        mixed(0, '字', 42),  // 42 code units in 126 bytes: in place
        mixed(0, '字', 43),  // 43 code units in 129 bytes: moved down
        mixed(1, '字', 63),  // 64 code units in 190 bytes: in place
        mixed(0, '😀', 31),  // 62 code units in 124 bytes: in place
        mixed(0, '😀', 32),  // 64 code units in 128 bytes: in place
        mixed(0, '字', 85),  // 255 bytes
        mixed(0, 'é', 64),   // 128 bytes
    ] {
        check(&text);
    }
}

/// The reader's decoders against the plain ones, for every length up to 256 code units (or bytes) with each class
/// at each position (fewer under Miri), and random input: unpaired, reversed and split surrogates included.
#[test]
fn decoders() {
    let (latin1_ways, utf16_ways) = decoder_ways();
    let latin1 = |bytes: &[u8]| {
        let want = bytes.iter().map(|&b| char::from(b)).collect::<String>();
        assert_eq!(latin1_string(bytes), want, "{bytes:?}");
        let room = bytes.len() + bytes.iter().filter(|&&b| b >= 0x80).count();
        for (name, way) in &latin1_ways {
            let mut text = Vec::<u8>::with_capacity(room);
            // SAFETY: `room` bytes of room, the UTF-8's length.
            let o = unsafe { way(bytes, text.as_mut_ptr(), room) };
            assert!(o <= room, "{name}: {bytes:?}");
            // SAFETY: `o` bytes written.
            unsafe { text.set_len(o) };
            assert_eq!(text, want.as_bytes(), "{name}: {bytes:?}");
        }
    };
    let utf16 = |units: &[u16]| {
        let le: Vec<[u8; 2]> = units.iter().map(|u| u.to_le_bytes()).collect();
        let want = String::from_utf16_lossy(units);
        assert_eq!(utf16_string(&le), want, "{units:?}");
        let room: usize = units.iter().map(|&u| 1 + usize::from(u >= 0x80) + usize::from(u >= 0x800)).sum();
        for (name, way) in &utf16_ways {
            let mut text = Vec::<u8>::with_capacity(room);
            // SAFETY: `room` bytes of room, what the units count.
            let o = unsafe { way(&le, text.as_mut_ptr(), room) };
            assert!(o <= room, "{name}: {units:?}");
            // SAFETY: `o` bytes written.
            unsafe { text.set_len(o) };
            assert_eq!(text, want.as_bytes(), "{name}: {units:?}");
        }
    };
    let bytes: [u8; 6] = [b'a', 0x7F, 0x80, 0xBF, 0xE9, 0xFF];
    let units: [u16; 12] = [0x61, 0x7F, 0x80, 0x7FF, 0x800, 0xD7FF, 0xD800, 0xDBFF, 0xDC00, 0xDFFF, 0xE000, 0xFFFF];
    let lengths: Vec<usize> = if cfg!(miri) { vec![0, 1, 3, 4, 5, 7, 8, 9, 16, 17] } else { (0..=256).collect() };
    for &len in &lengths {
        let positions: Vec<usize> =
            if cfg!(miri) { [0, len / 2, len.saturating_sub(1)].into() } else { (0..len).collect() };
        for &filler in &bytes {
            latin1(&vec![filler; len]);
            for &at in &positions {
                for &b in &bytes {
                    latin1(&(0..len).map(|i| if i == at { b } else { filler }).collect::<Vec<_>>());
                }
            }
        }
        for &filler in &[0x61u16, 0xE9, 0x416, 0x5B57] {
            utf16(&vec![filler; len]);
            for &at in &positions {
                for &u in &units {
                    utf16(&(0..len).map(|i| if i == at { u } else { filler }).collect::<Vec<_>>());
                    // A pair or a reversed one at `at`.
                    let mut v: Vec<u16> = vec![filler; len.max(2)];
                    let pair = [u, if u < 0xDC00 { 0xDC00 | (u & 0x3FF) } else { 0xD800 | (u & 0x3FF) }];
                    if let Some(two) = v.get_mut(at.min(len.max(2) - 2)..).and_then(|t| t.first_chunk_mut::<2>()) {
                        *two = pair;
                    }
                    utf16(&v);
                }
            }
        }
    }
    let mut seed = 0x2545_F491_4F6C_DD1Du64;
    let mut next = move || {
        seed ^= seed << 13;
        seed ^= seed >> 7;
        seed ^= seed << 17;
        seed
    };
    for _ in 0..if cfg!(miri) { 20 } else { 20_000 } {
        let len = (next() % 300) as usize;
        let raw: Vec<u8> = (0..len).map(|_| next() as u8 & if next() & 1 == 0 { 0x7F } else { 0xFF }).collect();
        latin1(&raw);
        let wide: Vec<u16> = (0..len)
            .map(|_| {
                let r = next();
                let class = units.get((r % 13) as usize).copied().unwrap_or(0x41);
                class ^ ((r >> 8) as u16 & if class < 0x80 { 0x1F } else { 0x3FF })
            })
            .collect();
        utf16(&wide);
    }
}

#[test]
fn two_and_short_bytes() {
    // Every length up to SHORT and past it, after a prefix: the head, then the bytes, or nothing.
    for len in 0..=SHORT + 2 {
        let bytes: Vec<u8> = (0..len).map(|i| 0x80 | i as u8).collect();
        let mut out = vec![0xAA; 3];
        let done = two_and_short(&mut out, [7, 9], &bytes);
        if len <= SHORT {
            let mut expected = vec![0xAA, 0xAA, 0xAA, 7, 9];
            expected.extend_from_slice(&bytes);
            assert!(done, "{len}");
            assert_eq!(out, expected, "{len}");
        } else {
            assert!(!done, "{len}");
            assert_eq!(out, [0xAA; 3], "{len}");
        }
    }
}

#[test]
fn encoded_blocks() {
    // Every count up to 40, after output of every length up to 9 with and without spare room: what the output
    // held, the head, then the bytes of each value in order.
    for count in 0..=40u32 {
        let values: Vec<u32> = (0..count).map(|i| i.wrapping_mul(0x0102_0304) ^ 0xA5).collect();
        for held in 0..=9 {
            for room in [0, 7] {
                let mut out = Vec::with_capacity(held + room);
                out.extend((0..held).map(|i| i as u8));
                let mut expected = out.clone();
                expected.extend([0xEE; 3]);
                expected.extend(values.iter().flat_map(|v| v.to_le_bytes()));
                extend_encoded(&mut out, [0xEE; 3], &values, |v| v.to_le_bytes());
                assert_eq!(out, expected, "{count} {held} {room}");
            }
        }
    }
}

#[test]
fn short_ascii() {
    // Every length up to SHORT and past it, all ASCII and with a non-ASCII char at each position, after a prefix.
    for len in 0..=SHORT + 2 {
        for at in (0..len).map(Some).chain([None]) {
            let text: String = (0..len).map(|i| if Some(i) == at { 'é' } else { char::from(b'a' + i as u8) }).collect();
            let mut out = vec![0xAA; 3];
            let done = ascii_short(&mut out, text.as_bytes());
            if text.len() <= SHORT && text.is_ascii() {
                let mut expected = vec![0xAA; 3];
                expected.push((2 * len + 1) as u8);
                expected.extend_from_slice(text.as_bytes());
                assert!(done, "{text:?}");
                assert_eq!(out, expected, "{text:?}");
            } else {
                assert!(!done, "{text:?}");
                assert_eq!(out, [0xAA; 3], "{text:?}");
            }
        }
    }
}

#[test]
fn ascii_lists() {
    // Lists of strings of every length up to past SHORT, ASCII or not, longer than a run: what each written on
    // its own gives.
    let texts: Vec<String> = (0..=SHORT + 2)
        .flat_map(|len| {
            let ascii: String = (0..len).map(|i| char::from(b'a' + (i % 26) as u8)).collect();
            let mut latin1 = ascii.clone();
            latin1.push('é');
            [ascii, latin1]
        })
        .collect();
    let rounds = if cfg!(miri) { 3 } else { 40 };
    for round in 0..rounds {
        let values: Vec<String> = texts.iter().cycle().skip(round * 7).take(RUN * 2 + round).cloned().collect();
        let mut out = vec![0xAA; 3];
        let mut rest = values.as_slice();
        while !rest.is_empty() {
            let (done, run) = ascii_strings(&mut out, rest);
            assert!(done <= run && run <= RUN);
            rest = rest.get(done..).unwrap_or_default();
            if done < run {
                let Some(first) = rest.first() else { break };
                let mut expected = vec![(2 * first.chars().count() + 1) as u8];
                expected.extend(first.chars().map(|c| c as u32 as u8));
                assert!(!(first.len() <= SHORT && first.is_ascii()), "{first:?}");
                out.extend_from_slice(&expected);
                rest = rest.get(1..).unwrap_or_default();
            }
        }
        let mut expected = vec![0xAA; 3];
        for v in &values {
            expected.push((2 * v.chars().count() + 1) as u8);
            expected.extend(v.chars().map(|c| c as u32 as u8));
        }
        assert_eq!(out, expected);
    }
}

/// A string's bytes in the default encoding, header first, from the plain decoder.
fn encoded(text: &str) -> Vec<u8> {
    let (bytes, latin1) = reference(text);
    let count = if latin1 { bytes.len() } else { bytes.len() / 2 };
    let (header, n) = crate::strings::header((count as u32).wrapping_mul(2).wrapping_add(u32::from(latin1)));
    let mut out = header.get(..n).unwrap_or_default().to_vec();
    out.extend_from_slice(&bytes);
    out
}

/// A list loop: its name, the loop, and the longest string it must take.
type ListWay = (&'static str, fn(&mut Vec<u8>, &[String]) -> usize, usize);

/// Every list loop this CPU can run, with the longest string it must take: the plain one, each x86_64 tier's
/// (which [`strings`] picks from), and [`strings`] itself.
#[allow(clippy::vec_init_then_push)] // Which pushes run depends on the target.
fn list_ways() -> Vec<ListWay> {
    #[allow(unused_mut)]
    let mut ways: Vec<ListWay> = vec![("short", strings_short, SHORT), ("default", strings, SHORT)];
    #[cfg(target_arch = "x86_64")]
    {
        if x86::tier() >= x86::Tier::Sse41 {
            // SAFETY: the CPU has SSE4.1 (its tier).
            ways.push(("sse4.1", |out, values| unsafe { x86::strings_sse41(out, values) }, 63));
        }
        if x86::tier() >= x86::Tier::Avx2 {
            // SAFETY: the CPU has AVX2 (its tier).
            ways.push(("avx2", |out, values| unsafe { x86::strings_avx2(out, values) }, 63));
        }
        #[cfg(nino_avx512)]
        if x86::tier() >= x86::Tier::Avx512 {
            // SAFETY: the CPU has AVX-512 (its tier).
            ways.push(("avx-512", |out, values| unsafe { x86::strings_avx512(out, values) }, 63));
        }
    }
    ways
}

#[test]
fn list_kernels() {
    // Lists of every class of text at every length up to past 64 bytes (fewer under Miri), mixed: what each string
    // written on its own gives, every list loop stopping only at a string longer than it must take.
    let lengths = if cfg!(miri) { vec![0, 1, 7, 15, 16, 31, 32, 33, 63, 64, 70] } else { (0..=70).collect() };
    let texts: Vec<String> = lengths
        .iter()
        .flat_map(|&len| {
            ['a', 'é', 'Ж', '字', '😀'].map(|c| {
                let mut text: String = (0..len).map(|i| char::from(b'a' + (i % 26) as u8)).collect();
                if c != 'a' {
                    // The char last, the text cut back to about `len` bytes.
                    while !text.is_empty() && text.len() + c.len_utf8() > len.max(c.len_utf8()) {
                        text.pop();
                    }
                    text.push(c);
                }
                text
            })
        })
        .collect();
    let rounds = if cfg!(miri) { 2 } else { 30 };
    for (name, way, most) in list_ways() {
        for round in 0..rounds {
            let values: Vec<String> =
                texts.iter().cycle().skip(round * 11).take(texts.len() + round).cloned().collect();
            let mut out = vec![0xAA; 3];
            let mut rest = values.as_slice();
            while !rest.is_empty() {
                let done = way(&mut out, rest);
                assert!(done <= rest.len(), "{name}");
                rest = rest.get(done..).unwrap_or_default();
                let Some((first, tail)) = rest.split_first() else { break };
                assert!(first.len() > most, "{name}: {first:?}");
                out.extend_from_slice(&encoded(first));
                rest = tail;
            }
            let mut expected = vec![0xAA; 3];
            for v in &values {
                expected.extend_from_slice(&encoded(v));
            }
            assert_eq!(out, expected, "{name}");
        }
    }
}

#[test]
fn short_text_at_every_byte_length() {
    // Text drawn at random from the chars at each width's edges, at every byte length up to `SHORT_WIDE`, against
    // std's `encode_utf16` (`reference`): `short_wide` on each, after a byte so the output is unaligned, and the
    // list loops over those of up to `SHORT` bytes.
    const CHARS: [char; 14] = [
        'a',
        '\u{0}',
        '\u{7F}',
        '\u{80}',
        'é',
        '\u{FF}',
        '\u{100}',
        'Ж',
        '\u{7FF}',
        '\u{800}',
        '字',
        '\u{FFFF}',
        '\u{10000}',
        '\u{10FFFF}',
    ];
    let mut seed = 0x2545_F491_4F6C_DD1Du64;
    for _ in 0..if cfg!(miri) { 2 } else { 300 } {
        let texts: Vec<String> = (0..=SHORT_WIDE)
            .map(|len| {
                let mut text = String::new();
                while text.len() < len {
                    seed ^= seed << 13;
                    seed ^= seed >> 7;
                    seed ^= seed << 17;
                    let c = CHARS.get((seed % 14) as usize).copied().unwrap_or('q');
                    text.push(if text.len() + c.len_utf8() <= len { c } else { 'z' });
                }
                text
            })
            .collect();
        for text in &texts {
            let mut out = vec![0xAA];
            short_wide(&mut out, text);
            let mut want = vec![0xAA];
            want.extend_from_slice(&encoded(text));
            assert_eq!(out, want, "short_wide: {text:?}");
        }
        let short: Vec<String> = texts.into_iter().filter(|t| t.len() <= SHORT).collect();
        let mut want = vec![0xAA];
        for text in &short {
            want.extend_from_slice(&encoded(text));
        }
        for (name, way, _) in list_ways() {
            let mut out = vec![0xAA];
            assert_eq!(way(&mut out, &short), short.len(), "{name}");
            assert_eq!(out, want, "{name}");
        }
    }
}

#[test]
fn ascii_blocks() {
    // Every length, all ASCII and with a byte of 0x80 or above at each position, the bytes past the text either.
    for n in 0..=17 {
        for at in (0..16).map(Some).chain([None]) {
            let mut block = [0u8; 16];
            for (i, b) in block.iter_mut().enumerate() {
                *b = if Some(i) == at { 0xE9 } else { b'a' + i as u8 };
            }
            let got = ascii_block(&block, n);
            let text = block.get(..n.min(16)).unwrap_or_default();
            if (1..=16).contains(&n) && text.is_ascii() {
                assert_eq!(got.as_deref().map(str::as_bytes), Some(text), "{n} {at:?}");
            } else {
                assert_eq!(got, None, "{n} {at:?}");
            }
        }
    }
}

#[test]
fn runs_and_random_text() {
    // Runs of each class between ASCII runs of every length around a block.
    let (ks, ns): (&[usize], &[usize]) =
        if cfg!(miri) { (&[0, 17], &[1, 9]) } else { (&[0, 1, 7, 15, 16, 17, 31], &[1, 2, 9, 17]) };
    for &k in ks {
        for &n in ns {
            for c1 in CLASSES {
                for c2 in CLASSES {
                    let mut text = "x".repeat(k);
                    text.extend(core::iter::repeat_n(c1, n));
                    text.push_str(&"y".repeat(k));
                    text.extend(core::iter::repeat_n(c2, n));
                    check(&text);
                }
            }
        }
    }
    // Text drawn at random, half ASCII and half the classes, up to 300 chars.
    let mut seed = 0x9E37_79B9_7F4A_7C15u64;
    for _ in 0..if cfg!(miri) { 20 } else { 20_000 } {
        seed ^= seed << 13;
        seed ^= seed >> 7;
        seed ^= seed << 17;
        let len = (seed % 300) as usize;
        let mut s = seed;
        let text: String = (0..len)
            .map(|_| {
                s = s.wrapping_mul(6_364_136_223_846_793_005).wrapping_add(1);
                CLASSES.get((s >> 59) as usize).copied().unwrap_or('q')
            })
            .collect();
        check(&text);
    }
}

/// Bytes from a xorshift: `len` of them, a share of them set to `bad` (0 for none) so decoders fail at random
#[allow(clippy::indexing_slicing, clippy::arithmetic_side_effects)]
/// places.
fn random_bytes(seed: &mut u64, len: usize, bad: u8) -> Vec<u8> {
    (0..len)
        .map(|_| {
            *seed ^= *seed << 13;
            *seed ^= *seed >> 7;
            *seed ^= *seed << 17;
            if bad != 0 && seed.is_multiple_of(97) {
                bad
            } else {
                (*seed >> 24) as u8
            }
        })
        .collect()
}

/// `decode_block` against `collect` into a `Result` for every count to 256, values that own memory (Miri checks
#[allow(clippy::indexing_slicing, clippy::arithmetic_side_effects)]
/// none leaks or is dropped twice when a decode fails partway), random input and failures at random places.
#[test]
fn decode_block_matches_plain() {
    let decode = |c: &[u8; 3]| -> crate::error::Result<String> {
        if c.first() == Some(&0xFF) {
            return Err(crate::error::Error::new(crate::error::ErrorKind::InvalidBool, c.len() as i32));
        }
        Ok(c.iter().map(|&b| char::from(b)).collect())
    };
    let mut seed = 0x2545_F491_4F6C_DD1Du64;
    let counts: Vec<usize> = if cfg!(miri) { vec![0, 1, 2, 7, 33] } else { (0..=256).collect() };
    for n in counts {
        for bad in [0, 0xFF] {
            let bytes = random_bytes(&mut seed, n * 3, bad);
            let plain: crate::error::Result<Vec<String>> = bytes.as_chunks::<3>().0.iter().map(decode).collect();
            assert_eq!(decode_block(bytes.as_chunks::<3>().0.iter(), decode), plain, "{n}");
        }
    }
}

/// `extend_fixed` against encoding each value into a zeroed buffer and appending it, for every count to 256,
#[allow(clippy::indexing_slicing, clippy::arithmetic_side_effects)]
/// sizes of 1 to `FIXED_STACK` bytes and a head of none or four bytes, after what the `Vec` held.
#[test]
fn extend_fixed_matches_plain() {
    let mut seed = 0x9E37_79B9_7F4A_7C15u64;
    let counts: Vec<usize> = if cfg!(miri) { vec![0, 1, 5, 17] } else { (0..=256).collect() };
    for size in [1usize, 2, 3, 8, 10, 16, 33, FIXED_STACK] {
        for &n in &counts {
            let values: Vec<Vec<u8>> = (0..n).map(|_| random_bytes(&mut seed, size, 0)).collect();
            // Writes all but the last byte, which stays zero.
            let encode = |v: &Vec<u8>, out: &mut [u8]| {
                for (o, &b) in out.iter_mut().zip(v.iter().take(size - 1)) {
                    *o = b;
                }
            };
            let mut want = vec![7u8; 3];
            want.extend_from_slice(&[1, 2, 3, 4]);
            for v in &values {
                let mut b = vec![0u8; size];
                encode(v, &mut b);
                want.extend_from_slice(&b);
            }
            let mut got = vec![7u8; 3];
            extend_fixed(&mut got, [1, 2, 3, 4], &values, size, encode);
            assert_eq!(got, want, "{size} {n}");
            let mut got = vec![7u8; 3];
            extend_fixed(&mut got, [], &values, size, encode);
            assert_eq!(got.get(3..), want.get(7..), "{size} {n}");
        }
    }
}

/// `extend_fixed_iter` against encoding each item in turn, for every count to 256 and sizes of 1 to `FIXED_STACK`
/// bytes, and with an iterator that yields fewer items than its `len` says (only those are kept).
#[allow(clippy::indexing_slicing, clippy::arithmetic_side_effects)]
#[test]
fn extend_fixed_iter_matches_plain() {
    /// Yields the items of `inner`, while its `len` claims `extra` more.
    struct Short<I> {
        inner: I,
        extra: usize,
    }
    impl<I: ExactSizeIterator> Iterator for Short<I> {
        type Item = I::Item;
        fn next(&mut self) -> Option<I::Item> {
            self.inner.next()
        }
        fn size_hint(&self) -> (usize, Option<usize>) {
            let n = self.inner.len() + self.extra;
            (n, Some(n))
        }
    }
    impl<I: ExactSizeIterator> ExactSizeIterator for Short<I> {}
    let mut seed = 0xC2B2_AE3D_27D4_EB4Fu64;
    let counts: Vec<usize> = if cfg!(miri) { vec![0, 1, 5, 17] } else { (0..=256).collect() };
    for size in [1usize, 2, 3, 8, 10, 16, 33, FIXED_STACK] {
        for &n in &counts {
            let values: Vec<Vec<u8>> = (0..n).map(|_| random_bytes(&mut seed, size, 0)).collect();
            // Writes all but the last byte, which stays zero.
            let encode = |v: &Vec<u8>, out: &mut [u8]| {
                for (o, &b) in out.iter_mut().zip(v.iter().take(size - 1)) {
                    *o = b;
                }
            };
            let mut want = vec![7u8; 3];
            want.extend_from_slice(&[1, 2, 3, 4]);
            for v in &values {
                let mut b = vec![0u8; size];
                encode(v, &mut b);
                want.extend_from_slice(&b);
            }
            let mut got = vec![7u8; 3];
            extend_fixed_iter(&mut got, [1, 2, 3, 4], values.iter(), size, encode);
            assert_eq!(got, want, "{size} {n}");
            let mut got = vec![7u8; 3];
            extend_fixed_iter(&mut got, [1, 2, 3, 4], Short { inner: values.iter(), extra: 3 }, size, encode);
            assert_eq!(got, want, "{size} {n} short");
            let mut got = vec![7u8; 3];
            extend_fixed_iter(&mut got, [], values.iter(), size, encode);
            assert_eq!(got.get(3..), want.get(7..), "{size} {n}");
        }
    }
}

/// A map of fixed-size keys and values, written as one block and read with one bounds check an entry (a slice on
/// x86; off x86 an 8-byte array for `(i32, i32)` and the generic loop for the others), against reading and
/// writing its entries one by one: every count to 256 (fewer under Miri), random bytes with bad bools and
/// repeated keys, whole and cut short; the same map or the same error, and the same bytes.
#[allow(clippy::indexing_slicing, clippy::arithmetic_side_effects, clippy::unwrap_used)]
#[test]
fn maps_match_entry_by_entry() {
    use crate::error::{Error, ErrorKind, Result};
    use crate::reader::Reader;
    use crate::traits::{NinoRead, NinoWrite};
    use crate::types::Guid;
    use std::collections::{BTreeMap, HashMap};
    /// The entries one by one, as maps of other keys and values are read.
    fn each<K: NinoRead, V: NinoRead>(bytes: &[u8], mut insert: impl FnMut(K, V) -> bool) -> Result<()> {
        let mut r = Reader::new(bytes);
        // The length's own checks, as every map's read makes them.
        let count = r.length(K::MIN_SIZE + V::MIN_SIZE)?.ok_or_else(Error::null)?;
        for _ in 0..count {
            let key = K::read(&mut r)?;
            let value = V::read(&mut r)?;
            if !insert(key, value) {
                return Err(Error::of(ErrorKind::DuplicateKey));
            }
        }
        Ok(())
    }
    fn check<K, V>(seed: &mut u64, n: usize)
    where
        K: NinoRead + NinoWrite + Ord + core::hash::Hash + Clone + core::fmt::Debug,
        V: NinoRead + NinoWrite + PartialEq + core::fmt::Debug,
    {
        let name = core::any::type_name::<(K, V)>();
        let size = <K as NinoRead>::FIXED_SIZE + <V as NinoRead>::FIXED_SIZE;
        for bad in [0u8, 0xFF] {
            let mut bytes = (n as i32).to_le_bytes().to_vec();
            bytes.extend(random_bytes(seed, n * size, bad));
            // Mostly small keys and values (valid bools, repeated keys), some random.
            if !seed.is_multiple_of(3) {
                for (k, b) in bytes.iter_mut().enumerate().skip(4) {
                    if (k - 4) % size != 0 && *b != bad {
                        *b = 0;
                    } else if *b != bad {
                        *b &= 1;
                    }
                }
            }
            for cut in [0, 1, size] {
                let bytes = &bytes[..bytes.len().saturating_sub(cut).max(4)];
                let mut want = HashMap::new();
                let plain = each::<K, V>(bytes, |k, v| want.insert(k, v).is_none()).map(|()| want);
                let got = HashMap::<K, V>::read(&mut Reader::new(bytes));
                assert_eq!(got, plain, "{name} {n} {cut} hash");
                let mut want = BTreeMap::new();
                let plain = each::<K, V>(bytes, |k, v| want.insert(k, v).is_none()).map(|()| want);
                let got = BTreeMap::<K, V>::read(&mut Reader::new(bytes));
                assert_eq!(got, plain, "{name} {n} {cut} tree");
                // Written back: the length, then each entry in the map's order.
                if let Ok(map) = got {
                    let mut w = crate::writer::Writer::new();
                    w.length(map.len()).unwrap();
                    for (k, v) in &map {
                        k.write(&mut w).unwrap();
                        v.write(&mut w).unwrap();
                    }
                    assert_eq!(crate::serialize(&map).unwrap(), w.into_vec(), "{name} {n} tree write");
                    let hash: HashMap<K, V> = map.into_iter().collect();
                    let mut w = crate::writer::Writer::new();
                    w.length(hash.len()).unwrap();
                    for (k, v) in &hash {
                        k.write(&mut w).unwrap();
                        v.write(&mut w).unwrap();
                    }
                    assert_eq!(crate::serialize(&hash).unwrap(), w.into_vec(), "{name} {n} hash write");
                }
            }
        }
    }
    let mut seed = 0x6A09_E667_F3BC_C908u64;
    let counts: Vec<usize> = if cfg!(miri) { vec![0, 1, 7] } else { (0..=256).collect() };
    for n in counts {
        check::<i32, i32>(&mut seed, n);
        check::<u8, bool>(&mut seed, n);
        check::<bool, u64>(&mut seed, n);
        check::<(i16, bool), Guid>(&mut seed, n);
    }
}

/// Each fixed-size type's block read (`read_many`) against reading its elements one by one, for every count to
#[allow(clippy::indexing_slicing, clippy::arithmetic_side_effects)]
/// 256 and random bytes, valid or not: the same values, or the same error.
#[test]
fn blocks_match_one_by_one() {
    use crate::reader::Reader;
    use crate::traits::NinoRead;
    use crate::types::{DateOnly, DateTime, DateTimeOffset, Decimal, Guid, Half, Rune, TimeOnly, TimeSpan};
    fn check<T: NinoRead + PartialEq + core::fmt::Debug>(seed: &mut u64, n: usize) {
        let size = T::FIXED_SIZE;
        for bad in [0u8, 0xFF] {
            let mut bytes = random_bytes(seed, n * size, bad);
            // Mostly small values (valid dates, decimals and runes), some random.
            if !seed.is_multiple_of(4) {
                for (k, b) in bytes.iter_mut().enumerate() {
                    if k % size > size.min(4) / 2 && *b != bad {
                        *b = 0;
                    }
                }
            }
            let block = T::read_many(&mut Reader::new(&bytes), n);
            let mut r = Reader::new(&bytes);
            let each: crate::error::Result<Vec<T>> = (0..n).map(|_| T::read(&mut r)).collect();
            assert_eq!(block, each, "{} {n}", core::any::type_name::<T>());
            // With the allocation budget spent: the `Vec` grown as it goes.
            let mut spent = Reader::new(&bytes);
            spent.capacity::<u8>(usize::MAX);
            assert_eq!(T::read_many(&mut spent, n), each, "{} {n} spent", core::any::type_name::<T>());
        }
    }
    let mut seed = 0x1234_5678_9ABC_DEF1u64;
    let counts: Vec<usize> = if cfg!(miri) { vec![0, 1, 9] } else { (0..=256).collect() };
    for n in counts {
        for _ in 0..if cfg!(miri) { 1 } else { 4 } {
            check::<bool>(&mut seed, n);
            check::<i8>(&mut seed, n);
            check::<u16>(&mut seed, n);
            check::<u64>(&mut seed, n);
            check::<Decimal>(&mut seed, n);
            check::<Guid>(&mut seed, n);
            check::<DateTime>(&mut seed, n);
            check::<TimeSpan>(&mut seed, n);
            check::<DateOnly>(&mut seed, n);
            check::<TimeOnly>(&mut seed, n);
            check::<DateTimeOffset>(&mut seed, n);
            check::<Half>(&mut seed, n);
            check::<Rune>(&mut seed, n);
            check::<(i32, i32)>(&mut seed, n);
            check::<(i64, bool, u8)>(&mut seed, n);
            check::<(DateOnly, Decimal)>(&mut seed, n);
        }
    }
    // Values at the edges of the ranges, which the quick test sends one by one: read the same either way.
    let max = crate::types::MAX_TICKS;
    let edges = [
        (max - (1 << 32)).to_le_bytes(),
        max.to_le_bytes(),
        (max + 1).to_le_bytes(),
        (max | 3 << 62).to_le_bytes(),
        (crate::types::TICKS_PER_DAY - 1).to_le_bytes(),
        crate::types::TICKS_PER_DAY.to_le_bytes(),
    ];
    for k in 0..edges.len() {
        let bytes: Vec<u8> = edges.iter().take(k + 1).flatten().copied().collect();
        let n = k + 1;
        for (block, each) in [
            (
                format!("{:?}", DateTime::read_many(&mut Reader::new(&bytes), n)),
                format!(
                    "{:?}",
                    (0..n)
                        .map(|i| DateTime::read(&mut Reader::new(&bytes[i * 8..])))
                        .collect::<crate::error::Result<Vec<_>>>()
                ),
            ),
            (
                format!("{:?}", TimeOnly::read_many(&mut Reader::new(&bytes), n)),
                format!(
                    "{:?}",
                    (0..n)
                        .map(|i| TimeOnly::read(&mut Reader::new(&bytes[i * 8..])))
                        .collect::<crate::error::Result<Vec<_>>>()
                ),
            ),
        ] {
            assert_eq!(block, each, "{k}");
        }
    }
}
