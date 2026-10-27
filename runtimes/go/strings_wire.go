package nino

import "unsafe"

// What the string paths' Go code and their kernels (strings_wire_amd64.s, strings_wire_arm64.s) share; the
// kernels read the constants from go_asm.h.

// wireInvalid is what wireString returns for text that is not WTF-8.
const wireInvalid = int(^uint(0) >> 1)

// wireSlack is the most a kernel stores past the bytes it writes: a buffer has room for that many more.
const wireSlack = 64

// wireMaxText is the length from which the list kernels leave a string to the Go code: they write strings of fewer
// bytes, so of at most 63 chars, whose header is one byte.
const wireMaxText = 64

func best(tiers []int) int {
	if len(tiers) == 0 {
		return 0
	}
	return tiers[len(tiers)-1]
}

// setWriteTier and setReadTier take a tier of the writer's or reader's vector paths, or the Go loops (0).
func setWriteTier(t int) {
	writeTier, useWire = t, t != 0
}

func setReadTier(t int) {
	readTier, useRead = t, t != 0
}

// wireStrings writes strings from the start of one of strs or ptrs (at p) with the vector path, first giving the
// buffer room for the next of them (up to 64) at the most a string the kernels take can need, and returns how many
// it wrote. The kernels check each string's room themselves and stop at one that lacks it, so the room is not
// summed string by string (a loop over the strings that cost about as much as writing short ones).
func (w *Writer) wireStrings(p unsafe.Pointer, strs []string, ptrs []*string) int {
	count := len(strs) + len(ptrs)
	need := min(count, 64) * (1 + 2*(wireMaxText-1) + wireSlack)
	if cap(w.buf)-len(w.buf) < need {
		w.grow(need)
	}
	at := len(w.buf)
	done, written := wireStrings(unsafe.Add(unsafe.Pointer(unsafe.SliceData(w.buf)), at), cap(w.buf)-at, p, count, ptrs != nil)
	w.buf = w.buf[:at+written]
	return done
}
