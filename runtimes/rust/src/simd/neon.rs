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

/// Both halves' code units of the window `v` for its longest char (see [`units`]).
#[inline(always)]
fn halves<const LONGEST: u8>(v: uint8x16_t) -> (uint16x8_t, uint16x8_t) {
    // SAFETY: NEON; these touch no memory.
    unsafe {
        let zero = vdupq_n_u8(0);
        let (n1, n2) = (vextq_u8::<1>(v, zero), vextq_u8::<2>(v, zero));
        (
            units::<LONGEST>(vmovl_u8(vget_low_u8(v)), vmovl_u8(vget_low_u8(n1)), vmovl_u8(vget_low_u8(n2))),
            units::<LONGEST>(vmovl_high_u8(v), vmovl_high_u8(n1), vmovl_high_u8(n2)),
        )
    }
}

/// The one-pass writer (see `x86::wide_sse41`, which this follows step for step): 16 bytes of text a step, each
/// char's bytes combined in its first byte's lane and the lanes kept moved to the front with `tbl` ([`KEEP8`],
/// [`KEEP16`]); Latin-1 while no byte is above 0xC3, else UTF-16 from the start. The header is written last.
///
/// The text is at most `WIDE_MAX` bytes (the caller's check; the header would not fit otherwise).
#[inline(never)]
pub(crate) fn wide(out: &mut Vec<u8>, text: &str) {
    let (src, len) = (text.as_ptr(), text.len());
    // UTF-16 takes at most two bytes per byte of UTF-8, Latin-1 one; a step stores up to 32 bytes from where its
    // output starts. `len <= i32::MAX`: no sum below wraps.
    out.reserve(len.wrapping_mul(2).wrapping_add(4 + 32));
    let at = out.len();
    // Invariants: `i <= len` is a char boundary of the valid UTF-8 at `src`, and `o <= i` bytes (Latin-1) or
    // `o <= 2 * i` (UTF-16) are written after the header, so each store (8 or 16 bytes at `o`, at most 32 from where
    // the step started) ends within the room reserved: `4 + 2 * len + 32` bytes from `at`.
    // SAFETY: as just said.
    let dst = unsafe { out.as_mut_ptr().add(at).add(4) };
    let (mut i, mut o) = (0usize, 0usize);
    // SAFETY: NEON, and every access is as the comment beside it says.
    unsafe {
        let zero = vdupq_n_u8(0);
        // The bytes that start a char (not 0x80..0xBF).
        let starts = |v: uint8x16_t| bits(vcgeq_s8(vreinterpretq_s8_u8(v), vdupq_n_s8(-64)));
        'latin1: {
            while i < len {
                // `i < len`.
                let v = window(src, len, i);
                if vmaxvq_u8(v) > 0xC3 {
                    break 'latin1;
                }
                let s = starts(v);
                // `rest = len - i`.
                let q = span(src, i, len.wrapping_sub(i), s);
                // A two-byte char's code point: its lead byte's two low bits, then its continuation byte's six.
                let two = vorrq_u8(vshlq_n_u8::<6>(v), vandq_u8(vextq_u8::<1>(v, zero), vdupq_n_u8(0x3F)));
                let chars = vbslq_u8(vcltzq_s8(vreinterpretq_s8_u8(v)), two, v);
                let keep = s & first(q);
                let (k0, k1) = ((keep & 0xFF) as usize, (keep >> 8) as usize);
                // `k0, k1 < 256`; the stores end within the room (the invariants), the second at `o + k0.count`.
                vst1_u8(dst.add(o), vget_low_u8(vqtbl1q_u8(chars, row(&KEEP8, k0))));
                o = o.wrapping_add(k0.count_ones() as usize);
                vst1_u8(dst.add(o), vget_low_u8(vqtbl1q_u8(vextq_u8::<8>(chars, zero), row(&KEEP8, k1))));
                o = o.wrapping_add(k1.count_ones() as usize);
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
            let v = window(src, len, i);
            let max = vmaxvq_u8(v);
            if max < 0x80 {
                // ASCII: each byte widened, 32 bytes stored (within the room), as many as the window holds kept.
                vst1q_u8(dst.add(o), vzip1q_u8(v, zero));
                vst1q_u8(dst.add(o).add(16), vzip2q_u8(v, zero));
                let n = rest.min(16);
                i = i.wrapping_add(n);
                o = o.wrapping_add(n.wrapping_mul(2));
                continue;
            }
            let s = starts(v);
            // `rest = len - i`.
            let q = span(src, i, rest, s);
            // By the longest char; with four-byte chars, the lane two after each one's first (its low surrogate) is
            // kept too, when the char ends in the window.
            let (keep, (low, high)) = if max < 0xE0 {
                (s, halves::<2>(v))
            } else if max < 0xF0 {
                (s, halves::<3>(v))
            } else {
                (s | (bits(vcgeq_u8(v, vdupq_n_u8(0xF0))) << 2), halves::<4>(v))
            };
            let keep = keep & first(q);
            let (k0, k1) = ((keep & 0xFF) as usize, ((keep >> 8) & 0xFF) as usize);
            // `k0, k1 < 256`; the stores end within the room (the invariants), the second at `o + 2 * k0.count`.
            vst1q_u8(dst.add(o), vqtbl1q_u8(vreinterpretq_u8_u16(low), row(&KEEP16, k0)));
            o = o.wrapping_add((k0.count_ones() as usize).wrapping_mul(2));
            vst1q_u8(dst.add(o), vqtbl1q_u8(vreinterpretq_u8_u16(high), row(&KEEP16, k1)));
            o = o.wrapping_add((k1.count_ones() as usize).wrapping_mul(2));
            i = i.wrapping_add(q);
        }
        // The header's four bytes are reserved at `at`, and `o` bytes after it written.
        super::finish(out, at, o, false);
    }
}

/// The bytes a step at `i` takes: see `x86::span` (a window of 16 bytes).
///
/// # Safety
/// `rest = len - i`, and when `rest > 16`, the byte at `src + i + 16` is readable.
#[inline(always)]
unsafe fn span(src: *const u8, i: usize, rest: usize, starts: u32) -> usize {
    if rest <= 16 {
        rest
    // SAFETY: `i + 16 < len`.
    } else if unsafe { src.add(i.wrapping_add(16)).read() } as i8 >= -64 {
        16
    } else {
        // Valid UTF-8 has a char start in every four bytes, so `starts` is not zero: 0..15.
        31usize.wrapping_sub(starts.leading_zeros() as usize)
    }
}

/// The bit set of the first `q` (1..=16) bytes.
#[inline(always)]
fn first(q: usize) -> u32 {
    // `1 <= q <= 16`.
    (1u32 << q).wrapping_sub(1)
}
