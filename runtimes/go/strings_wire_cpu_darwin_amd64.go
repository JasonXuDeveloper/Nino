//go:build !purego

package nino

import "syscall"

// osAVX512 tells whether macOS lets a thread use AVX-512, as golang.org/x/sys/cpu decides it
// (darwinSupportsAVX512, cpu_darwin_x86.go), through the standard library's sysctl calls instead of x/sys's own.
//
// macOS saves the opmask and ZMM state only for a thread that has used AVX-512, so XCR0 shows those bits clear
// until then (Go issue 43089): the kernel tells whether the processor has it (hw.optional.avx512f). Kernels 19.6.0
// to 21.2.0 (macOS 10.15.6 to 12.1) can corrupt the mask registers on return from a signal (Go issue 49233), so
// AVX-512 is taken from 21.3.0 (macOS 12.2) on.
//
// Adapted from golang.org/x/sys/cpu (cpu_darwin_x86.go):
//
//	Copyright 2024 The Go Authors. All rights reserved.
//	Use of this source code is governed by a BSD-style
//	license that can be found in the LICENSE file.
//
// (golang.org/x/sys's LICENSE: the 3-clause BSD license.)
func osAVX512() bool {
	if v, err := syscall.SysctlUint32("hw.optional.avx512f"); err != nil || v == 0 {
		return false
	}
	release, err := syscall.Sysctl("kern.osrelease")
	return err == nil && darwinRelease(release, 21, 3, 0)
}
