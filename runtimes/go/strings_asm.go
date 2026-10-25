//go:build (amd64 || arm64) && !purego

package nino

import "unsafe"

// hasASCIIRuns tells whether asciiCopy and widenASCII take runs of ASCII here.
const hasASCIIRuns = true

// asciiCopy copies the text at src (n bytes) to dst 16 bytes at a time while they are ASCII, and returns how many
// it copied: a multiple of 16, at most n.
//
//go:noescape
func asciiCopy(dst, src unsafe.Pointer, n int) int

// widenASCII writes the text at src (n bytes) to dst as UTF-16 code units 16 bytes at a time while they are ASCII,
// and returns how many bytes it took: a multiple of 16, at most n (dst has room for 2*n bytes).
//
//go:noescape
func widenASCII(dst, src unsafe.Pointer, n int) int
