//! The counted byte writer's narrow AVX2 experiment on AMD family 19h.

use std::arch::x86_64::__cpuid;
use std::sync::OnceLock;

#[cold]
fn use_avx2() -> bool {
    let vendor = __cpuid(0);
    if (vendor.ebx, vendor.edx, vendor.ecx) != (0x6874_7541, 0x6974_6e65, 0x444d_4163) {
        return false;
    }
    let version = __cpuid(1).eax;
    let family = (version >> 8) & 0xf;
    let extended_family = (version >> 20) & 0xff;
    family == 0xf && extended_family == 0xa && std::arch::is_x86_feature_detected!("avx2")
}

/// Appends a counted byte block when the size and CPU match the candidate.
#[inline]
pub(crate) fn try_write_counted(out: &mut Vec<u8>, bytes: &[u8]) -> bool {
    static ENABLED: OnceLock<bool> = OnceLock::new();
    if !(2113..=4096).contains(&bytes.len()) || !*ENABLED.get_or_init(use_avx2) {
        return false;
    }
    append_counted(out, bytes);
    true
}

#[inline(never)]
fn append_counted(out: &mut Vec<u8>, bytes: &[u8]) {
    // The caller's size gate limits the count to 4096.
    let head = (bytes.len() as i32).to_le_bytes();
    let len = out.len();
    let added = bytes.len().wrapping_add(head.len());
    out.reserve(added);
    let dst = out.spare_capacity_mut().as_mut_ptr().cast::<u8>();
    // SAFETY: The caller checked AVX2 and the length. The immutable source cannot overlap the
    // mutably borrowed Vec. The reservation holds the whole header and payload, both initialized
    // before set_len. Successful reserve also guarantees that len + added fits.
    unsafe {
        dst.cast::<[u8; 4]>().write(head);
        copy_avx2(dst.add(head.len()), bytes.as_ptr(), bytes.len());
        out.set_len(len.wrapping_add(added));
    }
}

/// # Safety
/// The caller has checked AVX2 and provides disjoint source and destination blocks of at least len >= 128 bytes.
/// Destination bytes can be uninitialized.
#[target_feature(enable = "avx2")]
unsafe fn copy_avx2(dst: *mut u8, src: *const u8, len: usize) {
    use core::arch::x86_64::*;

    #[inline]
    #[target_feature(enable = "avx2")]
    unsafe fn line<const ALIGNED: bool>(dst: *mut u8, src: *const u8) {
        // SAFETY: Each caller provides 64 accessible bytes on both sides, with a 64-byte aligned destination
        // when ALIGNED is true.
        unsafe {
            let a = _mm256_loadu_si256(src.cast());
            let b = _mm256_loadu_si256(src.add(32).cast());
            if ALIGNED {
                _mm256_store_si256(dst.cast(), a);
                _mm256_store_si256(dst.add(32).cast(), b);
            } else {
                _mm256_storeu_si256(dst.cast(), a);
                _mm256_storeu_si256(dst.add(32).cast(), b);
            }
        }
    }

    // The microprobe's aligned loop, with unaligned head and tail. len >= 128, so tail does not wrap.
    let tail = len.wrapping_sub(64);
    // SAFETY: The first and last 64 bytes each lie inside both disjoint blocks.
    unsafe {
        line::<false>(dst, src);
        line::<false>(dst.add(tail), src.add(tail));
    }
    let mut off = dst.align_offset(64);
    while off < tail {
        // SAFETY: Offsets are at least 64 bytes before len and dst + off is cache-line aligned.
        unsafe { line::<true>(dst.add(off), src.add(off)) };
        // off < len - 64, so the step does not wrap.
        off = off.wrapping_add(64);
    }
}
