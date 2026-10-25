// The string reader's AVX-512 paths (BW, VL, VBMI and VBMI2): text from the wire as UTF-8 (WTF-8). Every load is
// masked to the text, so nothing past it is read; each block stores 64 bytes, so dst has room for 64 bytes past the
// most the text can take. Every instruction here has an EVEX form, so avo allocates the registers (any of the 32).

package main

import (
	. "github.com/mmcloughlin/avo/operand"
	. "github.com/mmcloughlin/avo/reg"
)

// broadcastD is a vector of v in each 32-bit lane.
func broadcastD(v uint32) VecVirtual {
	r, z := c.GP32(), c.ZMM()
	c.MOVL(U32(v), r)
	c.VPBROADCASTD(r, z)
	return z
}

// utf16UTF8AVX512 writes units UTF-16 code units (little-endian) at src as WTF-8 at dst (room for 3*units+64
// bytes) and returns the bytes written. Each block of 16 units is widened to 16 lanes of 32 bits, each lane given
// the code point c (of the pair, for a high surrogate followed by a low one) and its UTF-8 in full 4-byte form,
// shifted down to its length (0 for the low surrogate of a pair), and the bytes kept compressed to the front.
func utf16UTF8AVX512() {
	function("utf16UTF8AVX512", "func(dst, src unsafe.Pointer, units int) int")
	dst, src, units := c.GP64(), c.GP64(), c.GP64()
	c.MOVQ(param("dst"), dst)
	c.MOVQ(param("src"), src)
	c.MOVQ(param("units"), units)
	start := c.GP64()
	c.MOVQ(dst, start)
	surrogate := broadcastD(0xFC00)
	high := broadcastD(0xD800)
	low := broadcastD(0xDC00)
	b80 := broadcastD(0x80)
	b800 := broadcastD(0x800)
	pair := broadcastD(0xFCA02400) // 0x10000 - (D800 << 10) - DC00
	shifts, t := c.ZMM(), c.GP64()
	c.MOVQ(U64(0x20262C3200060C12), t) // the bits of each byte: c >> 18, 12, 6 and 0, in both lanes of 64
	c.VPBROADCASTQ(t, shifts)
	keep := broadcastD(0x3F3F3F07)
	marks := broadcastD(0x808080F0)
	b24 := broadcastD(24)
	b8 := broadcastD(8)
	b32 := broadcastD(32)
	c40 := broadcastD(0x40)
	c20 := broadcastD(0x20)
	ones := c.ZMM()
	c.VPTERNLOGD(U8(0xFF), ones, ones, ones)
	i := c.GP64()
	c.XORQ(i, i)

	c.Label("next")
	left := RCX // CL: the shift below
	c.MOVQ(units, left)
	c.SUBQ(i, left) // the units left
	c.JLE(l("finish"))
	// in the units of the block, after the units after each, before the units before each (none before the first).
	in, after, before := c.GP32(), c.GP32(), c.GP32()
	c.MOVL(U32(0xFFFF), in)
	c.CMPQ(left, U8(16))
	c.JAE(l("full"))
	c.MOVL(U32(1), in)
	c.SHLL(CL, in)
	c.DECL(in)

	c.Label("full")
	c.MOVL(in, after)
	c.SHRL(U8(1), after)
	c.CMPQ(left, U8(17))
	c.JB(l("after"))
	c.MOVL(U32(0xFFFF), after)

	c.Label("after")
	c.MOVL(in, before)
	c.TESTQ(i, i)
	c.JNZ(l("before"))
	c.ANDL(U32(0xFFFE), before)

	c.Label("before")
	kIn, kAfter, kBefore := c.K(), c.K(), c.K()
	c.KMOVW(in, kIn)
	c.KMOVW(after, kAfter)
	c.KMOVW(before, kBefore)
	u0, u1, u2 := c.YMM(), c.YMM(), c.YMM()
	c.VMOVDQU16_Z(at(src, i, 2), kIn, u0)
	c.VMOVDQU16_Z(Mem{Base: src, Index: i, Scale: 2, Disp: 2}, kAfter, u1)
	c.VMOVDQU16_Z(Mem{Base: src, Index: i, Scale: 2, Disp: -2}, kBefore, u2)
	cp, next, prev := c.ZMM(), c.ZMM(), c.ZMM()
	c.VPMOVZXWD(u0, cp)   // the units
	c.VPMOVZXWD(u1, next) // the unit after each
	c.VPMOVZXWD(u2, prev) // the unit before each
	m := c.ZMM()
	isHigh, isLow, nextLow, prevHigh := c.K(), c.K(), c.K(), c.K()
	c.VPANDD(surrogate, cp, m)
	c.VPCMPEQD(high, m, isHigh) // high surrogates
	c.VPCMPEQD(low, m, isLow)   // low surrogates
	c.VPANDD(surrogate, next, m)
	c.VPCMPEQD(low, m, nextLow)
	c.VPANDD(surrogate, prev, m)
	c.VPCMPEQD(high, m, prevHigh)
	c.KANDW(nextLow, isHigh, isHigh) // a high surrogate followed by a low one: the pair's 4 bytes
	c.KANDW(prevHigh, isLow, isLow)  // a low surrogate after a high one: no bytes
	c.VPSLLD(U8(10), cp, m)
	c.VPADDD(next, m, m)
	c.VPADDD(pair, m, m)
	c.VMOVDQA32(m, isHigh, cp) // c
	utf8 := c.ZMM()
	c.VPMULTISHIFTQB(cp, shifts, utf8)
	c.VPTERNLOGD(U8(0xEA), marks, keep, utf8) // F0 | c >> 18, 80 | c >> 12 & 3F, 80 | c >> 6 & 3F, 80 | c & 3F
	two, three := c.K(), c.K()
	c.VPCMPUD(U8(5), b80, cp, two)    // c >= 80
	c.VPCMPUD(U8(5), b800, cp, three) // c >= 800
	drop := c.ZMM()
	c.VMOVDQA32(b24, drop)
	c.VPSUBD(b8, drop, two, drop)
	c.VPSUBD(b8, drop, three, drop)
	c.VPSUBD(b8, drop, isHigh, drop)
	c.VMOVDQA32(b32, isLow, drop) // the bits to drop: 8 for each byte short of 4
	c.VPSRLVD(drop, utf8, utf8)
	c.VPORD(c40, utf8, two, utf8)   // the lead of 2 bytes: C0
	c.VPORD(c20, utf8, three, utf8) // of 3: E0 (of 4: F0 already)
	ascii := c.K()
	c.KNOTW(two, ascii)
	c.VMOVDQA32(cp, ascii, utf8) // ASCII: c
	kept := c.ZMM()
	c.VPSRLVD(drop, ones, kept)
	c.VMOVDQA32_Z(kept, kIn, kept)
	keptK := c.K()
	c.VPTESTMB(kept, kept, keptK) // the bytes kept
	c.VPCOMPRESSB(utf8, keptK, utf8)
	c.VMOVDQU64(utf8, Mem{Base: dst})
	n := c.GP64()
	c.KMOVQ(keptK, n)
	c.POPCNTQ(n, n)
	c.ADDQ(n, dst)
	c.ADDQ(U8(16), i)
	c.JMP(l("next"))

	c.Label("finish")
	c.SUBQ(start, dst)
	c.Store(dst, c.ReturnIndex(0))
	c.VZEROUPPER()
	c.RET()
}

// latin1UTF8AVX512 writes n Latin-1 bytes at src as UTF-8 at dst (room for 2*n+64 bytes) and returns the bytes
// written: each block of 32 widened to 16-bit lanes, a byte of 80 or above given its two bytes, and the bytes kept
// compressed to the front.
func latin1UTF8AVX512() {
	function("latin1UTF8AVX512", "func(dst, src unsafe.Pointer, n int) int")
	dst, src, n := c.GP64(), c.GP64(), c.GP64()
	c.MOVQ(param("dst"), dst)
	c.MOVQ(param("src"), src)
	c.MOVQ(param("n"), n)
	start := c.GP64()
	c.MOVQ(dst, start)
	low, marks, t := c.ZMM(), c.ZMM(), c.GP32()
	c.MOVL(U32(0x3F00), t)
	c.VPBROADCASTW(t, low)
	c.MOVL(U32(0x80C0), t)
	c.VPBROADCASTW(t, marks)
	evens := c.GP64()
	c.MOVQ(U64(0x5555555555555555), evens)
	i := c.GP64()
	c.XORQ(i, i)

	c.Label("next")
	left := RCX // CL: the shifts below
	c.MOVQ(n, left)
	c.SUBQ(i, left) // the bytes left
	c.JLE(l("finish"))
	// in the bytes of the block, lows the low byte of each of their lanes.
	in, lows := c.GP32(), c.GP64()
	c.MOVL(U32(0xFFFFFFFF), in)
	c.MOVQ(evens, lows)
	c.CMPQ(left, U8(32))
	c.JAE(l("full"))
	c.MOVL(U32(1), in)
	c.SHLL(CL, in)
	c.DECL(in)
	c.ADDQ(left, left)
	mask := c.GP64()
	c.MOVQ(U32(1), mask)
	c.SHLQ(CL, mask)
	c.DECQ(mask)
	c.ANDQ(mask, lows)

	c.Label("full")
	kIn, high := c.K(), c.K()
	c.KMOVD(in, kIn)
	b, w, x, y := c.YMM(), c.ZMM(), c.ZMM(), c.ZMM()
	c.VMOVDQU8_Z(at(src, i, 1), kIn, b)
	c.VPMOVB2M(b, high) // 80 or above
	c.VPMOVZXBW(b, w)
	c.VPSRLW(U8(6), w, x)
	c.VPSLLW(U8(8), w, y)
	c.VPANDD(low, y, y)
	c.VPTERNLOGD(U8(0xFE), marks, y, x) // C0 | c >> 6, 80 | c & 3F
	c.VMOVDQU16(x, high, w)
	kept := c.K()
	c.VPMOVB2M(w, kept) // the second byte of those (and their first)
	k := c.GP64()
	c.KMOVQ(kept, k)
	c.ORQ(lows, k)
	c.KMOVQ(k, kept)
	c.VPCOMPRESSB(w, kept, w)
	c.VMOVDQU64(w, Mem{Base: dst})
	c.POPCNTQ(k, k)
	c.ADDQ(k, dst)
	c.ADDQ(U8(32), i)
	c.JMP(l("next"))

	c.Label("finish")
	c.SUBQ(start, dst)
	c.Store(dst, c.ReturnIndex(0))
	c.VZEROUPPER()
	c.RET()
}
