//! Experimental, disjoint block-copy kernels. These are linked into the benchmark only.

/// Align the destination for the main loop. An overlapping head/tail copy avoids reading beyond either slice.
///
/// # Safety
///
/// Source and destination must point to separate blocks of at least `len >= 128` bytes.
#[cfg(any(target_arch = "aarch64", target_arch = "x86_64"))]
macro_rules! cache_lines {
    ($dst:expr, $src:expr, $len:expr, $head_tail:ident, $line:ident) => {{
        let (dst, src, len) = ($dst, $src, $len);
        let tail = len - 64;
        // SAFETY: The head and tail each cover a full cache line within both disjoint blocks.
        unsafe {
            $head_tail(dst, src);
            $head_tail(dst.add(tail), src.add(tail));
        }
        let mut off = dst.align_offset(64);
        while off < tail {
            // SAFETY: off < len - 64 and dst + off is a multiple of 64.
            unsafe { $line(dst.add(off), src.add(off)) };
            off += 64;
        }
    }};
}

#[cfg(target_arch = "x86_64")]
mod x86 {
    use std::arch::x86_64::*;

    #[target_feature(enable = "avx2")]
    pub unsafe fn avx2(dst: &mut [u8], src: &[u8]) {
        #[inline]
        #[target_feature(enable = "avx2")]
        unsafe fn unaligned(dst: *mut u8, src: *const u8) {
            let a = _mm256_loadu_si256(src.cast());
            let b = _mm256_loadu_si256(src.add(32).cast());
            _mm256_storeu_si256(dst.cast(), a);
            _mm256_storeu_si256(dst.add(32).cast(), b);
        }

        #[inline]
        #[target_feature(enable = "avx2")]
        unsafe fn aligned(dst: *mut u8, src: *const u8) {
            let a = _mm256_loadu_si256(src.cast());
            let b = _mm256_loadu_si256(src.add(32).cast());
            _mm256_store_si256(dst.cast(), a);
            _mm256_store_si256(dst.add(32).cast(), b);
        }

        if src.len() < 128 {
            dst.copy_from_slice(src);
            return;
        }
        debug_assert_eq!(src.len(), dst.len());
        cache_lines!(dst.as_mut_ptr(), src.as_ptr(), src.len(), unaligned, aligned);
    }

    // Use asm so the benchmark workspace can still compile on the runtime's Rust 1.88 MSRV. Unlike an AVX-512
    // target_feature function, it does not require stabilized intrinsics. The only callers check AVX-512F.
    #[inline(never)]
    pub unsafe fn avx512(dst: &mut [u8], src: &[u8]) {
        #[inline]
        unsafe fn unaligned(dst: *mut u8, src: *const u8) {
            std::arch::asm!(
                "vmovdqu64 zmm0, [{src}]",
                "vmovdqu64 [{dst}], zmm0",
                src = in(reg) src, dst = in(reg) dst, out("zmm0") _,
                options(nostack, preserves_flags),
            );
        }

        #[inline]
        unsafe fn aligned(dst: *mut u8, src: *const u8) {
            std::arch::asm!(
                "vmovdqu64 zmm0, [{src}]",
                "vmovdqa64 [{dst}], zmm0",
                src = in(reg) src, dst = in(reg) dst, out("zmm0") _,
                options(nostack, preserves_flags),
            );
        }

        if src.len() < 128 {
            dst.copy_from_slice(src);
            return;
        }
        debug_assert_eq!(src.len(), dst.len());
        cache_lines!(dst.as_mut_ptr(), src.as_ptr(), src.len(), unaligned, aligned);
        std::arch::asm!("vzeroupper", options(nostack, nomem, preserves_flags));
    }
}

#[cfg(target_arch = "aarch64")]
mod arm {
    use std::arch::asm;

    pub unsafe fn neon(dst: &mut [u8], src: &[u8]) {
        #[inline]
        unsafe fn line(dst: *mut u8, src: *const u8) {
            asm!(
                "ldp q0, q1, [{src}]",
                "ldp q2, q3, [{src}, #32]",
                "stp q0, q1, [{dst}]",
                "stp q2, q3, [{dst}, #32]",
                src = in(reg) src, dst = in(reg) dst,
                out("v0") _, out("v1") _, out("v2") _, out("v3") _,
                options(nostack, preserves_flags),
            );
        }

        if src.len() < 128 {
            dst.copy_from_slice(src);
            return;
        }
        debug_assert_eq!(src.len(), dst.len());
        cache_lines!(dst.as_mut_ptr(), src.as_ptr(), src.len(), line, line);
    }
}

pub type Copy = unsafe fn(&mut [u8], &[u8]);

pub fn available() -> Vec<(&'static str, Copy)> {
    let copies: Vec<(&str, Copy)> = vec![("standard", <[u8]>::copy_from_slice)];
    #[cfg(any(target_arch = "x86_64", target_arch = "aarch64"))]
    let mut copies = copies;
    #[cfg(target_arch = "x86_64")]
    {
        if is_x86_feature_detected!("avx2") {
            copies.push(("avx2", x86::avx2));
        }
        if is_x86_feature_detected!("avx512f") {
            copies.push(("avx512", x86::avx512));
        }
    }
    #[cfg(target_arch = "aarch64")]
    if std::arch::is_aarch64_feature_detected!("neon") {
        copies.push(("neon", arm::neon));
    }
    copies
}
