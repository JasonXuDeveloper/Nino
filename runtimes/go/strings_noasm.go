//go:build !(amd64 || arm64) || purego

package nino

import "unsafe"

// hasASCIIRuns tells whether asciiCopy and widenASCII take runs of ASCII here.
const hasASCIIRuns = false

// asciiCopy and widenASCII (strings_asm.go) take nothing here: the word loops do all of it.

func asciiCopy(dst, src unsafe.Pointer, n int) int { return 0 }

func widenASCII(dst, src unsafe.Pointer, n int) int { return 0 }
