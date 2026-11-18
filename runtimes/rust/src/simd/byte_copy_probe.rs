//! Runner-only candidate for #263. The benchmark compares this narrow range with stock slice-to-Vec deserialization;
//! this experiment does not select a policy from the installed libc version.

#[inline]
pub(crate) fn to_vec(bytes: &[u8]) -> Vec<u8> {
    #[cfg(all(target_arch = "x86_64", target_os = "linux", target_env = "gnu", not(miri)))]
    {
        let mut out = Vec::new();
        if try_append(&mut out, [], bytes) {
            return out;
        }
    }
    bytes.to_vec()
}

/// Puts the length header and payload in one reservation, exactly as the counted writer does.
#[cfg(all(target_arch = "x86_64", target_os = "linux", target_env = "gnu", not(miri)))]
#[inline]
pub(crate) fn try_append<const H: usize>(out: &mut Vec<u8>, head: [u8; H], bytes: &[u8]) -> bool {
    if !(2113..=4096).contains(&bytes.len()) || !std::arch::is_x86_feature_detected!("avx2") {
        return false;
    }
    let len = out.len();
    let added = H.saturating_add(bytes.len());
    out.reserve(added);
    let dst = out.spare_capacity_mut().as_mut_ptr().cast::<u8>();
    // SAFETY: AVX2 was detected. The input is an immutable slice and out is mutably borrowed, so they cannot
    // overlap. The reservation holds the whole header and payload. Both are initialized before set_len.
    unsafe {
        dst.cast::<[u8; H]>().write(head);
        copy_avx2(dst.add(H), bytes.as_ptr(), bytes.len());
        out.set_len(len.wrapping_add(added));
    }
    true
}

/// # Safety
/// The caller has checked AVX2 and provides disjoint source and destination blocks of at least len >= 128 bytes.
/// Destination bytes can be uninitialized.
#[cfg(all(target_arch = "x86_64", target_os = "linux", target_env = "gnu", not(miri)))]
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
