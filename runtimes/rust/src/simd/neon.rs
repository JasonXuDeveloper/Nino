//! Little-endian Arm64's kernels (NEON is part of every such target): the one-pass string writer ([`wide`]) and the
//! reader's decoders. See the module root for what each writes and how it is tested.

use core::arch::aarch64::*;

use super::{KEEP16, KEEP8};

/// The bit set of a vector of whole bytes (each 0x00 or 0xFF): bit `k` for byte `k`. Each byte ANDed with its bit
/// in its half, the halves' bytes added pairwise three times into two bytes.
#[inline(always)]
fn bits(set: uint8x16_t) -> u32 {
    // SAFETY: NEON; these touch no memory.
    unsafe {
        let weights = vcombine_u8(vcreate_u8(0x8040_2010_0804_0201), vcreate_u8(0x8040_2010_0804_0201));
        let w = vandq_u8(set, weights);
        let w = vpaddq_u8(w, w);
        let w = vpaddq_u8(w, w);
        let w = vpaddq_u8(w, w);
        u32::from(vgetq_lane_u16::<0>(vreinterpretq_u16_u8(w)))
    }
}

/// The bits set in each byte value. Arm64 before FEAT_CSSC has no scalar popcount: `count_ones` moves the value to a
/// vector register and back, about ten cycles on the chain of output offsets; a load from this table is four.
// A const block cannot call `get_mut`; `k < 256` indexes the table, and `k + 1 <= 256` cannot wrap.
#[allow(clippy::indexing_slicing, clippy::arithmetic_side_effects)]
static ONES: [u8; 256] = {
    let mut table = [0u8; 256];
    let mut k = 0;
    while k < 256 {
        table[k] = (k as u8).count_ones() as u8;
        k += 1;
    }
    table
};

/// The bits set in `k` (below 256), from [`ONES`].
#[inline(always)]
fn ones(k: usize) -> usize {
    usize::from(ONES.get(k).copied().unwrap_or(0))
}

/// Row `k` of a table of shuffles, as a vector.
///
/// # Safety
/// `k < 256`.
#[inline(always)]
unsafe fn row(table: &[[u8; 16]; 256], k: usize) -> uint8x16_t {
    // SAFETY: `k < 256` (the caller's contract): a row of 16 bytes.
    unsafe { vld1q_u8(table.get_unchecked(k).as_ptr()) }
}

/// The 16 bytes of the text from `i` (zeros past `len`), read without touching a byte outside `src[..len]`: one load
/// while 16 are left; else the text's last 16 moved down (`tbl`, whose indices past 15 give zeros); in text shorter
/// than 16 bytes, words.
///
/// # Safety
/// `i < len`, and `len` bytes are readable at `src`.
#[inline(always)]
unsafe fn window(src: *const u8, len: usize, i: usize) -> uint8x16_t {
    // `i < len`: no difference below wraps.
    let rest = len.wrapping_sub(i);
    // SAFETY: with 16 bytes left, they are at `i`; else with 16 bytes or more in all, the last 16 end at `len`; else
    // `short_window`'s contract is the caller's.
    unsafe {
        if rest >= 16 {
            return vld1q_u8(src.add(i));
        }
        if len >= 16 {
            let last = vld1q_u8(src.add(len.wrapping_sub(16)));
            // Byte `k` from byte `k + 16 - rest` (1..15 more).
            let iota = vcombine_u8(vcreate_u8(0x0706_0504_0302_0100), vcreate_u8(0x0F0E_0D0C_0B0A_0908));
            return vqtbl1q_u8(last, vaddq_u8(iota, vdupq_n_u8(16u8.wrapping_sub(rest as u8))));
        }
        let (low, high, _) = super::short_window(src, i, len);
        vcombine_u8(vcreate_u8(low), vcreate_u8(high))
    }
}

/// The code units of the chars that start in each 16-bit lane, for text whose longest char has `LONGEST` bytes: see
/// `x86::units_sse41`.
#[inline(always)]
fn units<const LONGEST: u8>(b0: uint16x8_t, b1: uint16x8_t, b2: uint16x8_t) -> uint16x8_t {
    // SAFETY: NEON; these touch no memory.
    unsafe {
        let c1 = vandq_u16(b1, vdupq_n_u16(0x3F));
        let u2 = vorrq_u16(vshlq_n_u16::<6>(vandq_u16(b0, vdupq_n_u16(0x1F))), c1);
        let above = |t: u16| vcgtq_u16(b0, vdupq_n_u16(t));
        if LONGEST == 2 {
            return vbslq_u16(above(0xBF), u2, b0);
        }
        let u3 = vorrq_u16(vorrq_u16(vshlq_n_u16::<12>(b0), vshlq_n_u16::<6>(c1)), vandq_u16(b2, vdupq_n_u16(0x3F)));
        if LONGEST == 3 {
            return vbslq_u16(above(0xDF), u3, vbslq_u16(above(0xBF), u2, b0));
        }
        let low = vorrq_u16(vandq_u16(u2, vdupq_n_u16(0x3FF)), vdupq_n_u16(0xDC00));
        let top = vorrq_u16(
            vorrq_u16(vshlq_n_u16::<8>(vandq_u16(b0, vdupq_n_u16(7))), vshlq_n_u16::<2>(c1)),
            vandq_u16(vshrq_n_u16::<4>(b2), vdupq_n_u16(3)),
        );
        let high = vaddq_u16(top, vdupq_n_u16(0xD7C0));
        let u = vbslq_u16(above(0x7F), low, b0);
        let u = vbslq_u16(above(0xBF), u2, u);
        let u = vbslq_u16(above(0xDF), u3, u);
        vbslq_u16(above(0xEF), high, u)
    }
}

/// Both halves' code units of the 16 bytes `v` (the bytes after them in `next`) for the window's longest char (see
/// [`units`]).
#[inline(always)]
fn halves<const LONGEST: u8>(v: uint8x16_t, next: uint8x16_t) -> (uint16x8_t, uint16x8_t) {
    // SAFETY: NEON; these touch no memory.
    unsafe {
        let (n1, n2) = (vextq_u8::<1>(v, next), vextq_u8::<2>(v, next));
        (
            units::<LONGEST>(vmovl_u8(vget_low_u8(v)), vmovl_u8(vget_low_u8(n1)), vmovl_u8(vget_low_u8(n2))),
            units::<LONGEST>(vmovl_high_u8(v), vmovl_high_u8(n1), vmovl_high_u8(n2)),
        )
    }
}

/// The 32 bytes of the text from `i` (zeros past `len`) as two vectors, read without touching a byte outside
/// `src[..len]` ([`window`] for each half that is not whole).
///
/// # Safety
/// `i < len`, and `len` bytes are readable at `src`.
#[inline(always)]
unsafe fn window32(src: *const u8, len: usize, i: usize) -> (uint8x16_t, uint8x16_t) {
    // `i < len`: no difference or sum below wraps.
    let rest = len.wrapping_sub(i);
    // SAFETY: with 32 bytes left, both halves are whole; with more than 16, the first is and the second starts before
    // `len`; else the first starts before `len`.
    unsafe {
        if rest >= 32 {
            return (vld1q_u8(src.add(i)), vld1q_u8(src.add(i).add(16)));
        }
        if rest > 16 {
            return (vld1q_u8(src.add(i)), window(src, len, i.wrapping_add(16)));
        }
        (window(src, len, i), vdupq_n_u8(0))
    }
}

/// The one-pass writer (see `x86::wide_sse41`, which this follows): 32 bytes of text a step ([`window32`]: text of up
/// to 32 bytes, the most common, is one step), each char's bytes combined in its first byte's lane and the lanes kept
/// moved to the front with `tbl` ([`KEEP8`], [`KEEP16`]); Latin-1 while no byte is above 0xC3, else UTF-16 from the
/// start. The header is written last.
///
/// The text is at most `WIDE_MAX` bytes (the caller's check; the header would not fit otherwise).
#[inline(never)]
pub(crate) fn wide(out: &mut Vec<u8>, text: &str) {
    let (src, len) = (text.as_ptr(), text.len());
    // UTF-16 takes at most two bytes per byte of UTF-8, Latin-1 one; a step stores up to 64 bytes from where its
    // output starts. `len <= i32::MAX`: no sum below wraps.
    out.reserve(len.wrapping_mul(2).wrapping_add(4 + 64));
    let at = out.len();
    // Invariants: `i <= len` is a char boundary of the valid UTF-8 at `src`, and `o <= i` bytes (Latin-1) or
    // `o <= 2 * i` (UTF-16) are written after the header, so each store (8 or 16 bytes at `o`, at most 64 from where
    // the step started) ends within the room reserved: `4 + 2 * len + 64` bytes from `at`.
    // SAFETY: as just said.
    let dst = unsafe { out.as_mut_ptr().add(at).add(4) };
    let (mut i, mut o) = (0usize, 0usize);
    // SAFETY: NEON, and every access is as the comment beside it says.
    unsafe {
        let zero = vdupq_n_u8(0);
        // The bytes that start a char (not 0x80..0xBF), of both halves.
        let starts = |lo: uint8x16_t, hi: uint8x16_t| {
            let s = |v: uint8x16_t| bits(vcgeq_s8(vreinterpretq_s8_u8(v), vdupq_n_s8(-64)));
            u64::from(s(lo) | (s(hi) << 16))
        };
        'latin1: {
            while i < len {
                let rest = len.wrapping_sub(i);
                // `i < len`.
                let (lo, hi) = window32(src, len, i);
                if vmaxvq_u8(vmaxq_u8(lo, hi)) > 0xC3 {
                    break 'latin1;
                }
                let s = starts(lo, hi);
                // `rest = len - i`.
                let q = span(src, i, rest, s);
                let keep = s & first(q);
                for (v, next, keep) in [(lo, hi, keep & 0xFFFF), (hi, zero, keep >> 16)] {
                    // A two-byte char's code point: its lead byte's two low bits, then its continuation byte's six.
                    let two = vorrq_u8(vshlq_n_u8::<6>(v), vandq_u8(vextq_u8::<1>(v, next), vdupq_n_u8(0x3F)));
                    let chars = vbslq_u8(vcltzq_s8(vreinterpretq_s8_u8(v)), two, v);
                    let (k0, k1) = ((keep & 0xFF) as usize, ((keep >> 8) & 0xFF) as usize);
                    // `k0, k1 < 256`; the stores end within the room (the invariants).
                    vst1_u8(dst.add(o), vget_low_u8(vqtbl1q_u8(chars, row(&KEEP8, k0))));
                    o = o.wrapping_add(ones(k0));
                    vst1_u8(dst.add(o), vget_low_u8(vqtbl1q_u8(vextq_u8::<8>(chars, zero), row(&KEEP8, k1))));
                    o = o.wrapping_add(ones(k1));
                }
                i = i.wrapping_add(q);
            }
            // The header's four bytes are reserved at `at`, and `o` bytes after it written.
            super::finish(out, at, o, true);
            return;
        }
        (i, o) = (0, 0);
        while i < len {
            let rest = len.wrapping_sub(i);
            // `i < len`.
            let (lo, hi) = window32(src, len, i);
            let max = vmaxvq_u8(vmaxq_u8(lo, hi));
            if max < 0x80 {
                // ASCII: each byte widened, 64 bytes stored (within the room), as many as the window holds kept.
                vst1q_u8(dst.add(o), vzip1q_u8(lo, zero));
                vst1q_u8(dst.add(o).add(16), vzip2q_u8(lo, zero));
                vst1q_u8(dst.add(o).add(32), vzip1q_u8(hi, zero));
                vst1q_u8(dst.add(o).add(48), vzip2q_u8(hi, zero));
                let n = rest.min(32);
                i = i.wrapping_add(n);
                o = o.wrapping_add(n.wrapping_mul(2));
                continue;
            }
            let s = starts(lo, hi);
            // `rest = len - i`.
            let q = span(src, i, rest, s);
            // With four-byte chars, the lane two after each one's first (its low surrogate) is kept too, when the
            // char ends in the window.
            let keep = if max < 0xF0 {
                s
            } else {
                let four = |v: uint8x16_t| bits(vcgeq_u8(v, vdupq_n_u8(0xF0)));
                s | (u64::from(four(lo) | (four(hi) << 16)) << 2)
            };
            let keep = keep & first(q);
            for (v, next, keep) in [(lo, hi, keep & 0xFFFF), (hi, zero, (keep >> 16) & 0xFFFF)] {
                // By the window's longest char.
                let (low, high) = if max < 0xE0 {
                    halves::<2>(v, next)
                } else if max < 0xF0 {
                    halves::<3>(v, next)
                } else {
                    halves::<4>(v, next)
                };
                let (k0, k1) = ((keep & 0xFF) as usize, ((keep >> 8) & 0xFF) as usize);
                // `k0, k1 < 256`; the stores end within the room (the invariants).
                vst1q_u8(dst.add(o), vqtbl1q_u8(vreinterpretq_u8_u16(low), row(&KEEP16, k0)));
                o = o.wrapping_add(ones(k0).wrapping_mul(2));
                vst1q_u8(dst.add(o), vqtbl1q_u8(vreinterpretq_u8_u16(high), row(&KEEP16, k1)));
                o = o.wrapping_add(ones(k1).wrapping_mul(2));
            }
            i = i.wrapping_add(q);
        }
        // The header's four bytes are reserved at `at`, and `o` bytes after it written.
        super::finish(out, at, o, false);
    }
}

/// The bytes a step at `i` takes: see `x86::span` (a window of 32 bytes).
///
/// # Safety
/// `rest = len - i`, and when `rest > 32`, the byte at `src + i + 32` is readable.
#[inline(always)]
unsafe fn span(src: *const u8, i: usize, rest: usize, starts: u64) -> usize {
    if rest <= 32 {
        rest
    // SAFETY: `i + 32 < len`.
    } else if unsafe { src.add(i.wrapping_add(32)).read() } as i8 >= -64 {
        32
    } else {
        // Valid UTF-8 has a char start in every four bytes, so `starts` is not zero: 0..31.
        63usize.wrapping_sub(starts.leading_zeros() as usize)
    }
}

/// The bit set of the first `q` (1..=32) bytes.
#[inline(always)]
fn first(q: usize) -> u64 {
    // `1 <= q <= 32`.
    (1u64 << q).wrapping_sub(1)
}

/// The reader's Latin-1 decoder (see `x86::latin1_ssse3`, which this follows): 16 bytes a step, stored as they are
/// when ASCII, else each byte as a 16-bit lane of its UTF-8 packed with `tbl` ([`super::EXPAND`]); the last bytes,
/// and those past the room a step needs, a word at a time.
///
/// # Safety
/// `room` bytes are writable at `dst`, the UTF-8's length.
#[inline(never)]
pub(crate) unsafe fn latin1(bytes: &[u8], dst: *mut u8, room: usize) -> usize {
    let (src, len) = (bytes.as_ptr(), bytes.len());
    let (mut k, mut o) = (0usize, 0usize);
    // Invariant: `o <= room` is the UTF-8 of `bytes[..k]`, so `room - o` is that of the rest (no wrap). A step reads
    // 16 bytes and stores at most 32 from `o`.
    // SAFETY: NEON, and every access is as the comment beside it says.
    unsafe {
        while k.wrapping_add(16) <= len && room.wrapping_sub(o) >= 32 {
            // `k + 16 <= len`.
            let v = vld1q_u8(src.add(k));
            if vmaxvq_u8(v) < 0x80 {
                // 32 bytes of room.
                vst1q_u8(dst.add(o), v);
                k = k.wrapping_add(16);
                o = o.wrapping_add(16);
                continue;
            }
            let is_high = vcltzq_s8(vreinterpretq_s8_u8(v));
            let high = bits(is_high);
            let lead = vbslq_u8(is_high, vorrq_u8(vdupq_n_u8(0xC0), vshrq_n_u8::<6>(v)), v);
            let cont = vorrq_u8(vandq_u8(v, vdupq_n_u8(0x3F)), vdupq_n_u8(0x80));
            let (m0, m1) = ((high & 0xFF) as usize, (high >> 8) as usize);
            // `m0, m1 < 256`; 32 bytes of room from `o`, and the second store starts at most 16 after it.
            vst1q_u8(dst.add(o), vqtbl1q_u8(vzip1q_u8(lead, cont), row(&super::EXPAND, m0)));
            o = o.wrapping_add(8).wrapping_add(ones(m0));
            vst1q_u8(dst.add(o), vqtbl1q_u8(vzip2q_u8(lead, cont), row(&super::EXPAND, m1)));
            o = o.wrapping_add(8).wrapping_add(ones(m1));
            k = k.wrapping_add(16);
        }
        // The invariant: the room from `o` holds the rest's UTF-8.
        super::latin1_words(bytes, k, dst, o)
    }
}

/// The reader's UTF-16 decoder (see `x86::utf16_sse41`, which this follows): eight units a step, by their longest
/// UTF-8, packed with `tbl` ([`super::EXPAND`], [`super::PACK3`]); units with a surrogate, the last units, and those
/// past the room a step needs, as the plain decoder does.
///
/// # Safety
/// `room` bytes are writable at `dst`, at least what the units count.
#[inline(never)]
pub(crate) unsafe fn utf16(units: &[[u8; 2]], dst: *mut u8, room: usize) -> usize {
    let (src, n) = (units.as_ptr().cast::<u16>(), units.len());
    let (mut k, mut o) = (0usize, 0usize);
    // Invariant: `o <= room` holds the UTF-8 of `units[..k]`, and `room - o` at least what the rest count (no wrap). A
    // step reads eight units and stores at most 28 bytes from `o`.
    // SAFETY: NEON, and every access is as the comment beside it says.
    unsafe {
        while k.wrapping_add(8) <= n && room.wrapping_sub(o) >= 32 {
            // `k + 8 <= n` units (unaligned: `ld1` of bytes, reinterpreted).
            let w = vreinterpretq_u16_u8(vld1q_u8(src.add(k).cast::<u8>()));
            let max = vmaxvq_u16(w);
            if max < 0x80 {
                // 32 bytes of room.
                vst1_u8(dst.add(o), vmovn_u16(w));
                k = k.wrapping_add(8);
                o = o.wrapping_add(8);
                continue;
            }
            let ascii = vcltq_u16(w, vdupq_n_u16(0x80));
            if max < 0x800 {
                let two = vorrq_u16(
                    vorrq_u16(vdupq_n_u16(0xC0), vshrq_n_u16::<6>(w)),
                    vshlq_n_u16::<8>(vorrq_u16(vandq_u16(w, vdupq_n_u16(0x3F)), vdupq_n_u16(0x80))),
                );
                let lanes = vbslq_u16(ascii, w, two);
                let m = (!bits(vcombine_u8(vmovn_u16(ascii), vdup_n_u8(0xFF))) & 0xFF) as usize;
                // `m < 256`; 32 bytes of room.
                vst1q_u8(dst.add(o), vqtbl1q_u8(vreinterpretq_u8_u16(lanes), row(&super::EXPAND, m)));
                k = k.wrapping_add(8);
                o = o.wrapping_add(8).wrapping_add(ones(m));
                continue;
            }
            let surrogate = vceqq_u16(vandq_u16(w, vdupq_n_u16(0xF800)), vdupq_n_u16(0xD800));
            if vmaxvq_u16(surrogate) != 0 {
                // The invariant; the plain decoder takes these eight units (and the low half of a pair after them).
                (k, o) = super::utf16_words(units, k, k.wrapping_add(8), dst, o);
                continue;
            }
            // Two bits a unit, as `PACK3` takes them: at least U+0080, at least U+0800.
            let ge80 = bits(vcombine_u8(vmovn_u16(vmvnq_u16(ascii)), vdup_n_u8(0)));
            let ge800 = bits(vcombine_u8(vmovn_u16(vcgeq_u16(w, vdupq_n_u16(0x800))), vdup_n_u8(0)));
            for (half, u) in [(0usize, vmovl_u16(vget_low_u16(w))), (4, vmovl_high_u16(w))] {
                let s32 = vdupq_n_u32;
                let two = vorrq_u32(
                    vorrq_u32(s32(0xC0), vshrq_n_u32::<6>(u)),
                    vshlq_n_u32::<8>(vorrq_u32(vandq_u32(u, s32(0x3F)), s32(0x80))),
                );
                let three = vorrq_u32(
                    vorrq_u32(
                        vorrq_u32(s32(0xE0), vshrq_n_u32::<12>(u)),
                        vshlq_n_u32::<8>(vorrq_u32(vandq_u32(vshrq_n_u32::<6>(u), s32(0x3F)), s32(0x80))),
                    ),
                    vshlq_n_u32::<16>(vorrq_u32(vandq_u32(u, s32(0x3F)), s32(0x80))),
                );
                let lanes = vbslq_u32(vcgtq_u32(u, s32(0x7F)), two, u);
                let lanes = vbslq_u32(vcgtq_u32(u, s32(0x7FF)), three, lanes);
                let mut index = 0usize;
                for q in 0..4 {
                    // `half + q < 8`.
                    let unit = half.wrapping_add(q);
                    index |= (((ge80 >> unit) & 1) as usize) << q.wrapping_mul(2);
                    index |= (((ge800 >> unit) & 1) as usize) << q.wrapping_mul(2).wrapping_add(1);
                }
                let (shuffle, count) = super::PACK3.get(index).copied().unwrap_or(([0x80; 16], 0));
                // 32 bytes of room from where the step started; the first half stores 16 bytes from there, the second
                // from at most 12 after it.
                vst1q_u8(dst.add(o), vqtbl1q_u8(vreinterpretq_u8_u32(lanes), vld1q_u8(shuffle.as_ptr())));
                o = o.wrapping_add(usize::from(count));
            }
            k = k.wrapping_add(8);
        }
        // The invariant.
        super::utf16_words(units, k, n, dst, o).1
    }
}
