// The writers' AVX2 and AVX-512 kernels: one kernel, 32 bytes at a time, the parts the instruction sets do
// differently chosen by the tier.

package main

import (
	. "github.com/mmcloughlin/avo/operand"
	. "github.com/mmcloughlin/avo/reg"
)

// tier is what the AVX kernel takes from its instruction set.
type tier struct {
	evex bool // AVX-512 (F, BW, VL and VBMI2): masked loads, mask registers, compress
	// t1 to t3 are the tables of the validation: Y16 to Y18 with AVX-512 (loaded once), Y8 to Y10 with AVX2
	// (loaded with each block: no register is free for them).
	t1, t2, t3 VecPhysical
}

var (
	avx2   = tier{t1: Y8, t2: Y9, t3: Y10}
	avx512 = tier{evex: true, t1: Y16, t2: Y17, t3: Y18}
)

func writerAVX(one, list string, t tier) {
	w := writer{setup: t.setup, kernel: t.kernel, short: t.short, vex: true}
	single(one, w)
	many(list, w)
}

// setup loads the constants the kernel keeps in registers: Y11 the low nibble, Y13 C0, Y15 zeros, and with
// AVX-512 the tables of the validation.
func (t tier) setup() {
	c.VPXOR(Y15, Y15, Y15)
	c.VMOVDQU(k("NIB"), Y11)
	c.VMOVDQU(k("C0"), Y13)
	if t.evex {
		c.VBROADCASTI32X4(k("T1"), t.t1)
		c.VBROADCASTI32X4(k("T2"), t.t2)
		c.VBROADCASTI32X4(k("T3"), t.t3)
	}
}

// short takes ASCII text of fewer than 32 bytes (BX) at SI to the list kernel's one store (see many), else jumps
// to long: the 32 bytes from the byte before the text, read under a mask (AVX-512: the byte before and the bytes
// past the text masked out, so no fault) or where they lie in one page (AVX2: then masked).
func (t tier) short(f frame) {
	c.CMPQ(RBX, U8(32))
	c.JAE(l("long"))
	if t.evex {
		c.MOVQ(RBX, RCX)
		c.MOVL(U32(2), R13L)
		c.SHLL(CL, R13L)
		c.SUBL(U8(2), R13L) // bits 1 to n (n = 31: 2 << 31 is 0, less 2 is bits 1 to 31)
		c.KMOVD(R13L, K1)
		c.VMOVDQU8_Z(Mem{Base: RSI, Disp: -1}, K1, Y0)
	} else {
		c.LEAQ(Mem{Base: RSI, Disp: -1}, R8)
		c.MOVL(R8L, R9L)
		c.ANDL(U32(4095), R9L)
		c.CMPL(R9L, U32(4064))
		c.JA(l("long"))
		c.LEAQ(k("MASK"), R10)
		c.ADDQ(U8(31), R10)
		c.SUBQ(RBX, R10) // FF for the byte before and the n bytes of text
		c.VMOVDQU(Mem{Base: R8}, Y0)
		c.VPAND(Mem{Base: R10}, Y0, Y0)
		c.VPAND(k("NOT0"), Y0, Y0)
	}
	c.VPMOVMSKB(Y0, R8L)
	c.TESTL(R8L, R8L)
	if t.evex {
		c.JNZ(l("slatin"))
	} else {
		c.JNZ(l("shand"))
	}
	c.Label("sstore")
	c.Comment("The header, 2n+1, in the first byte.")
	c.LEAL(Mem{Base: RBX, Index: RBX, Scale: 1, Disp: 1}, ECX)
	c.VMOVD(ECX, X1)
	c.VPOR(Y1, Y0, Y0)
	c.VMOVDQU(Y0, Mem{Base: RDI})
	if t.evex {
		t.shortLatin1()
	} else {
		t.shortHand(f)
	}
}

// shortHand is the AVX2 short path for text with a byte of 0x80 or above: the block read (Y0, from the byte before
// the text) moved down a byte and handed to the kernel as the block it would read, past its page check and load.
func (t tier) shortHand(f frame) {
	c.JMP(l("sdone"))
	c.Label("shand")
	c.VPERM2I128(U8(0x81), Y0, Y0, Y1) // the high half low, zeros high
	c.VPALIGNR(U8(1), Y0, Y1, Y0)      // the text from the first byte, zeros past it
	c.MOVQ(RBX, RAX)
	lowBits(R13L)
	c.INCQ(RDI)
	c.MOVQ(RDI, f.start)
	c.MOVQ(RDI, f.wide)
	c.XORQ(RDX, RDX)
	c.VPXOR(Y14, Y14, Y14)
	c.VPXOR(Y12, Y12, Y12)
	c.JMP(l("lbody"))
	c.Label("sdone")
}

// shortLatin1 is the AVX-512 short path for text with a byte of 0x80 or above (R8 the mask of those, Y0 the block
// from the byte before the text): Latin-1 (ASCII, leads C2 and C3, each followed by its continuation) compressed as
// latin1Masks does, the byte before kept as the first, the chars in BX, then back to sstore; anything else to long.
func (t tier) shortLatin1() {
	c.JMP(l("sdone"))
	c.Label("slatin")
	c.VPSUBUSB(k("C3"), Y0, Y4)
	c.VPTEST(Y4, Y4)
	c.JNZ(l("long"))            // a char above U+00FF, or not UTF-8
	c.VPCMPGTB(Y0, Y13, K1)     // continuations
	c.VPCMPEQB(k("C3"), Y0, K2) // C3
	c.VPAND(k("FE"), Y0, Y4)
	c.VPCMPEQB(k("C2"), Y4, K3) // the leads
	c.KMOVD(K1, R9L)
	c.KMOVD(K3, R10L)
	c.KMOVD(K2, R11L)
	c.MOVL(R10L, EAX)
	c.ADDQ(RAX, RAX) // the bytes after the leads (a lead last sets bit 32)
	c.XORQ(R9, RAX)
	c.JNZ(l("long")) // a lead without its continuation (the bytes past the text are zeros), or the reverse
	c.MOVL(R10L, EAX)
	c.ORL(R9L, EAX)
	c.NOTL(EAX)
	c.ANDL(R8L, EAX)
	c.JNZ(l("long")) // C0 or C1
	c.ADDL(R11L, R11L)
	c.KMOVD(R11L, K4)
	c.VPADDB(k("B40"), Y0, K4, Y0) // the bytes: 40 more after C3
	c.POPCNTL(R10L, EAX)
	c.SUBQ(RAX, RBX) // the chars: a byte each but the leads
	c.NOTL(R10L)
	c.KMOVD(R10L, K2)
	c.VPCOMPRESSB(Y0, K2, Y0) // all but the leads; the first byte (0) stays first
	c.JMP(l("sstore"))
	c.Label("sdone")
}

// load reads the block at SI+DX (AX bytes left, AX > 0) into Y0, R13 the mask of its bytes, then jumps to body;
// the labels it adds start with p.
func (t tier) load(p, body string) {
	tail := p + "tail"
	if t.evex {
		c.CMPQ(RAX, U8(32))
		c.JB(l(tail))
		c.MOVL(U32(0xffffffff), R13L)
		c.VMOVDQU(at(RSI, RDX, 1), Y0)
		c.JMP(l(body))

		c.Label(tail)
		c.Comment("Fewer than 32 bytes left: R13 the mask of those, read alone (masked, so no fault past them).")
		lowBits(R13L)
		c.KMOVD(R13L, K1)
		c.VMOVDQU8_Z(at(RSI, RDX, 1), K1, Y0)
		c.JMP(l(body))
		return
	}
	c.MOVL(U32(0xffffffff), R13L)
	c.CMPQ(RAX, U8(32))
	c.JB(l(tail))
	c.VMOVDQU(at(RSI, RDX, 1), Y0)
	c.JMP(l(body))

	c.Label(tail)
	c.Comment("Fewer than 32 bytes left: R13 the mask of those, the bytes past them zeros.")
	lowBits(R13L)
	c.LEAQ(at(RSI, RDX, 1), R8)
	c.MOVL(R8L, R9L)
	c.ANDL(U32(4095), R9L)
	c.CMPL(R9L, U32(4064))
	c.JA(l(p + "copy"))
	c.VMOVDQU(Mem{Base: R8}, Y0)
	c.LEAQ(k("MASK"), R10)
	c.ADDQ(U8(32), R10)
	c.SUBQ(RAX, R10)
	c.VPAND(Mem{Base: R10}, Y0, Y0)
	c.JMP(l(body))

	c.Label(p + "copy")
	c.Comment("The 32 bytes from there cross into the next page: the bytes left are copied to where the output goes",
		"next (room not written yet), then read from there.")
	c.VPXOR(Y0, Y0, Y0)
	c.VMOVDQU(Y0, Mem{Base: RDI})
	c.XORQ(R9, R9)
	c.Label(p + "byte")
	c.MOVBLZX(at(R8, R9, 1), R10L)
	c.MOVB(R10B, at(RDI, R9, 1))
	c.INCQ(R9)
	c.CMPQ(R9, RAX)
	c.JB(l(p + "byte"))
	c.VMOVDQU(Mem{Base: RDI}, Y0)
	c.JMP(l(body))
}

// lowBits sets r to the mask of the low AX bits (AX < 32).
func lowBits(r GPPhysical) {
	c.MOVL(U32(1), r)
	c.MOVQ(RAX, RCX)
	c.SHLL(CL, r)
	c.DECL(r)
}

// blend is ifSet where the mask is set, else ifClear, to dst: the mask a vector (AVX2) or a mask register.
func (t tier) blend(mask, ifSet, ifClear, dst Op) {
	if t.evex {
		c.VPBLENDMB(ifSet, ifClear, mask, dst)
	} else {
		c.VPBLENDVB(mask, ifSet, ifClear, dst)
	}
}

// kernel converts the text at SI (BX bytes) to Latin-1 at DI, or to UTF-16 at the local wide (see writer). Y14 is
// the block before, Y12 the errors found.
func (t tier) kernel(f frame) {
	c.XORQ(RDX, RDX)
	c.VPXOR(Y14, Y14, Y14)
	c.VPXOR(Y12, Y12, Y12)
	if t.evex {
		// Latin-1 text needs no full validation (see lchars): R12 carries the end of the block before, bit 0 a lead,
		// bit 1 C3.
		c.XORL(R12L, R12L)
	}

	c.Label("latin1")
	c.MOVQ(RBX, RAX)
	c.SUBQ(RDX, RAX)
	c.JLE(l("latin1Done"))
	t.load("l", "lbody")

	c.Label("lbody")
	c.VPMOVMSKB(Y0, R8L)
	c.TESTL(R8L, R8L)
	c.JNZ(l("lchars"))
	c.Comment("ASCII: no char of the block before may be cut.")
	if t.evex {
		c.TESTL(R12L, R12L)
		c.JNZ(l("invalid"))
	} else {
		c.VPSUBUSB(k("TAIL"), Y14, Y4)
		c.VPOR(Y4, Y12, Y12)
	}
	c.VMOVDQU(Y0, Mem{Base: RDI})
	c.POPCNTL(R13L, R8L)
	c.ADDQ(R8, RDI)
	if !t.evex {
		c.VMOVDQA(Y0, Y14)
	}
	c.ADDQ(U8(32), RDX)
	c.JMP(l("latin1"))

	c.Label("lchars")
	c.Comment("A byte above C3 (a char above U+00FF, or not UTF-8): UTF-16, from the start.")
	c.VPSUBUSB(k("C3"), Y0, Y4)
	c.VPTEST(Y4, Y4)
	c.JNZ(l("toUTF16"))
	c.Comment("Its bytes are ASCII, leads C2 and C3 and continuations: it is valid when the continuations are exactly",
		"the bytes after the leads, and there are no C0 or C1.")
	if t.evex {
		t.latin1Masks()
	} else {
		t.latin1Tables()
	}

	c.Label("latin1Done")
	if t.evex {
		c.TESTL(R12L, R12L)
	} else {
		c.VPSUBUSB(k("TAIL"), Y14, Y4)
		c.VPOR(Y4, Y12, Y12)
		c.VPTEST(Y12, Y12)
	}
	c.JNZ(l("invalid"))
	c.MOVQ(RDI, RAX)
	c.SUBQ(f.start, RAX)
	c.JMP(l("done"))

	c.Label("toUTF16")
	c.Comment("From the start again, as UTF-16: the block in Y0 when it is the first.")
	c.MOVQ(f.wide, RDI)
	c.VPXOR(Y14, Y14, Y14)
	c.VPXOR(Y12, Y12, Y12)
	c.TESTQ(RDX, RDX)
	c.JZ(l("wchars"))
	c.XORQ(RDX, RDX)

	c.Label("wide")
	c.MOVQ(RBX, RAX)
	c.SUBQ(RDX, RAX)
	c.JLE(l("wideDone"))
	t.load("w", "wbody")

	c.Label("wbody")
	c.VPMOVMSKB(Y0, R8L)
	c.TESTL(R8L, R8L)
	c.JNZ(l("wchars"))
	c.VPSUBUSB(k("TAIL"), Y14, Y4)
	c.VPOR(Y4, Y12, Y12)
	if t.evex {
		c.VPMOVZXBW(Y0, Z4)
		c.VMOVDQU64(Z4, Mem{Base: RDI})
	} else {
		c.VPMOVZXBW(X0, Y4)
		c.VEXTRACTI128(U8(1), Y0, X5)
		c.VPMOVZXBW(X5, Y5)
		c.VMOVDQU(Y4, Mem{Base: RDI})
		c.VMOVDQU(Y5, Mem{Base: RDI, Disp: 32})
	}
	c.POPCNTL(R13L, R8L)
	c.LEAQ(at(RDI, R8, 2), RDI)
	c.VMOVDQA(Y0, Y14)
	c.ADDQ(U8(32), RDX)
	c.JMP(l("wide"))

	c.Label("wchars")
	t.validate()
	c.VMOVDQA(Y0, Y14)
	t.utf16()

	c.Label("wideDone")
	c.VPSUBUSB(k("TAIL"), Y14, Y4)
	c.VPOR(Y4, Y12, Y12)
	c.VPTEST(Y12, Y12)
	c.JNZ(l("invalid"))
	c.MOVQ(RDI, RAX)
	c.SUBQ(f.wide, RAX)
	c.SHRQ(U8(1), RAX)
	c.NOTQ(RAX)
	c.JMP(l("done"))

	c.Label("invalid")
	c.MOVQ(U64(invalid), RAX)
	c.JMP(l("done"))
}

// latin1Tables writes a block of ASCII, leads C2 and C3 and continuations as Latin-1 (AVX2): the errors in Y12, the
// bytes kept (all but the leads) moved to the front of each 8 by the narrowLo and narrowHi tables.
func (t tier) latin1Tables() {
	c.VPERM2I128(U8(0x21), Y0, Y14, Y1)
	c.VPALIGNR(U8(15), Y1, Y0, Y2) // the byte before each
	c.VPAND(k("FE"), Y0, Y7)
	c.VPCMPEQB(k("C2"), Y7, Y4) // the leads
	c.VPCMPEQB(Y13, Y7, Y7)     // C0 and C1
	c.VPOR(Y7, Y12, Y12)
	c.VPAND(k("FE"), Y2, Y5)
	c.VPCMPEQB(k("C2"), Y5, Y5) // after a lead
	c.VPCMPGTB(Y0, Y13, Y6)     // continuations
	c.VPXOR(Y6, Y5, Y5)
	c.VPOR(Y5, Y12, Y12)
	c.VPCMPEQB(k("C3"), Y2, Y5)
	c.VPAND(k("B40"), Y5, Y5)
	c.VPADDB(Y5, Y0, Y6) // the bytes: 40 more after C3
	c.VPMOVMSKB(Y4, R8L)
	c.NOTL(R8L)
	c.ANDL(R13L, R8L) // kept
	bytesOf(R8L)
	c.LEAQ(goVar("narrowLo"), RAX)
	c.LEAQ(goVar("narrowHi"), RCX)
	c.VMOVQ(at(RAX, R9, 8), X4)
	c.VPINSRQ(U8(1), at(RCX, R10, 8), X4, X4)
	c.VMOVQ(at(RAX, R11, 8), X7)
	c.VPINSRQ(U8(1), at(RCX, R12, 8), X7, X7)
	c.VINSERTI128(U8(1), X7, Y4, Y4)
	c.VPSHUFB(Y4, Y6, Y6)
	c.POPCNTL(R9L, R9L)
	c.POPCNTL(R10L, R10L)
	c.POPCNTL(R11L, R11L)
	c.POPCNTL(R12L, R12L)
	c.VMOVDQU(X6, Mem{Base: RDI})
	c.VPEXTRQ(U8(1), X6, RCX)
	c.MOVQ(RCX, at(RDI, R9, 1))
	c.ADDQ(R9, RDI)
	c.ADDQ(R10, RDI)
	c.VEXTRACTI128(U8(1), Y6, X7)
	c.VMOVDQU(X7, Mem{Base: RDI})
	c.VPEXTRQ(U8(1), X7, RCX)
	c.MOVQ(RCX, at(RDI, R11, 1))
	c.ADDQ(R11, RDI)
	c.ADDQ(R12, RDI)
	c.VMOVDQA(Y0, Y14)
	c.ADDQ(U8(32), RDX)
	c.JMP(l("latin1"))
}

// latin1Masks is latin1Tables with AVX-512: the checks on the masks of the leads and continuations in scalar
// registers (R12 the end of the block before), the bytes kept compressed.
func (t tier) latin1Masks() {
	c.VPCMPGTB(Y0, Y13, K1)     // continuations
	c.VPCMPEQB(k("C3"), Y0, K2) // C3
	c.VPAND(k("FE"), Y0, Y4)
	c.VPCMPEQB(k("C2"), Y4, K3) // the leads
	c.KMOVD(K1, R9L)
	c.KMOVD(K3, R10L)
	c.KMOVD(K2, R11L)
	c.MOVL(R12L, EAX)
	c.ANDL(U8(1), EAX)
	c.LEAQ(at(RAX, R10, 2), RAX)
	c.XORL(R9L, EAX)
	c.JNZ(l("invalid")) // a lead without its continuation, or a continuation without its lead
	c.MOVL(R10L, EAX)
	c.ORL(R9L, EAX)
	c.NOTL(EAX)
	c.ANDL(R8L, EAX)
	c.JNZ(l("invalid")) // C0 or C1
	c.MOVL(R12L, ECX)
	c.SHRL(U8(1), ECX)
	c.LEAQ(at(RCX, R11, 2), RCX) // the bytes after C3
	c.KMOVD(ECX, K4)
	c.MOVL(R10L, R12L)
	c.SHRL(U8(31), R12L)
	c.SHRL(U8(31), R11L)
	c.LEAL(at(R12, R11, 2), R12L)
	c.VMOVDQA(Y0, Y6)
	c.VPADDB(k("B40"), Y0, K4, Y6) // the bytes: 40 more after C3
	c.NOTL(R10L)
	c.ANDL(R13L, R10L) // kept: all but the leads
	c.KMOVD(R10L, K2)
	c.VPCOMPRESSB(Y6, K2, Y6)
	c.VMOVDQU(Y6, Mem{Base: RDI})
	c.POPCNTL(R10L, R10L)
	c.ADDQ(R10, RDI)
	c.ADDQ(U8(32), RDX)
	c.JMP(l("latin1"))
}

// validate checks the block in Y0 after the one in Y14 (Keiser and Lemire), adding errors to Y12; it leaves the byte
// three before each in Y1, one before in Y2 and two before in Y3.
func (t tier) validate() {
	c.Comment("Check the block (Keiser and Lemire): Y1 the byte three before each, Y2 one before, Y3 two before.")
	c.VPERM2I128(U8(0x21), Y0, Y14, Y1)
	c.VPALIGNR(U8(15), Y1, Y0, Y2)
	c.VPALIGNR(U8(14), Y1, Y0, Y3)
	c.VPALIGNR(U8(13), Y1, Y0, Y1)
	if !t.evex {
		c.VBROADCASTI128(k("T1"), t.t1)
		c.VBROADCASTI128(k("T2"), t.t2)
		c.VBROADCASTI128(k("T3"), t.t3)
	}
	c.VPSRLW(U8(4), Y2, Y4)
	c.VPAND(Y11, Y4, Y4)
	c.VPSHUFB(Y4, t.t1, Y4)
	c.VPAND(Y11, Y2, Y5)
	c.VPSHUFB(Y5, t.t2, Y5)
	c.VPSRLW(U8(4), Y0, Y6)
	c.VPAND(Y11, Y6, Y6)
	c.VPSHUFB(Y6, t.t3, Y6)
	c.VPAND(Y5, Y4, Y4)
	c.VPAND(Y6, Y4, Y4)
	c.VPSUBUSB(k("S60"), Y3, Y5)
	c.VPSUBUSB(k("S70"), Y1, Y6)
	c.VPOR(Y6, Y5, Y5)
	c.VPAND(k("B80"), Y5, Y5)
	c.VPXOR(Y5, Y4, Y4)
	c.VPOR(Y4, Y12, Y12)
}

// utf16 writes the block in Y0 (validated; Y1 to Y3 the bytes before each) as UTF-16 at DI: each byte that ends a
// char (or is the third of four) gives a unit, its low byte in Y4 and high byte in Y8, the rest dropped.
func (t tier) utf16() {
	// The masks: b1 the lead of 2 bytes, b0 not ASCII, and the surrogates (b2 the lead of 4 bytes: the high one;
	// b3: the low one), in vectors (AVX2) or mask registers.
	var lead2, high, high1, low Op
	if t.evex {
		lead2, high, high1, low = K6, K7, K1, K2
		c.VPCMPGTB(Y0, Y13, K3) // b0 a continuation
		c.VPCMPGTB(Y2, Y13, K4) // b1 a continuation
		c.VPAND(k("E0"), Y2, Y6)
		c.VPCMPEQB(Y13, Y6, K6) // b1 the lead of 2 bytes
		c.VPMOVB2M(Y0, K7)      // b0 not ASCII
		c.KORD(K6, K4, K4)
		c.KANDD(K3, K4, K4) // b0 ends a char of 2 or more bytes, or is a third byte
		c.KNOTD(K7, K5)
		c.KORD(K5, K4, K4) // kept: those and ASCII
		c.KMOVD(K4, R8L)
		c.ANDL(R13L, R8L)
	} else {
		lead2, high, high1, low = Y6, Y7, Y9, Y10
		c.VPCMPGTB(Y0, Y13, Y4) // b0 a continuation
		c.VPCMPGTB(Y2, Y13, Y5) // b1 a continuation
		c.VPAND(k("E0"), Y2, Y6)
		c.VPCMPEQB(Y13, Y6, Y6) // b1 the lead of 2 bytes
		c.VPCMPGTB(Y0, Y15, Y7) // b0 not ASCII
		c.VPOR(Y6, Y5, Y5)
		c.VPAND(Y4, Y5, Y5)  // b0 ends a char of 2 or more bytes, or is a third byte
		c.VPANDN(Y7, Y5, Y5) // dropped: the rest of the bytes not ASCII
		c.VPMOVMSKB(Y5, R8L)
		c.NOTL(R8L)
		c.ANDL(R13L, R8L) // kept
	}
	c.Comment("The low bytes: b0 for ASCII, else (b0 & 3F) | b1 << 6.")
	c.VPAND(k("B3F"), Y0, Y4)
	c.VPSLLW(U8(6), Y2, Y5)
	c.VPAND(Y13, Y5, Y5)
	c.VPOR(Y5, Y4, Y4)
	t.blend(high, Y4, Y0, Y4)
	c.Comment("The high bytes: 0 for ASCII, (b1 >> 2) & 0F after the lead of 2 bytes, and that | b2 << 4 else.")
	c.VPSRLW(U8(2), Y2, Y5)
	c.VPAND(Y11, Y5, Y5)
	c.VPSLLW(U8(4), Y3, Y8)
	c.VPAND(k("F0"), Y8, Y8)
	c.VPOR(Y5, Y8, Y8)
	t.blend(lead2, Y5, Y8, Y8)
	if t.evex {
		c.VMOVDQU8_Z(Y8, K7, Y8)
	} else {
		c.VPAND(Y7, Y8, Y8)
	}
	c.Comment("Surrogates: b2 the lead of 4 bytes (the high one), or b3 (the low one).")
	c.VPAND(k("F8"), Y3, Y9)
	c.VPCMPEQB(k("F0"), Y9, high1)
	c.VPAND(k("F8"), Y1, Y10)
	c.VPCMPEQB(k("F0"), Y10, low)
	if t.evex {
		c.KORTESTD(K1, K2)
	} else {
		c.VPOR(Y10, Y9, Y6)
		c.VPTEST(Y6, Y6)
	}
	c.JZ(l("wpack"))
	c.Comment("The high surrogate: D800 + ((b2 & 7) << 8 | (b1 & 3F) << 2 | (b0 >> 4) & 3) - 40.")
	c.VPAND(k("B3F"), Y2, Y6)
	c.VPSLLW(U8(2), Y6, Y6)
	c.VPSRLW(U8(4), Y0, Y7)
	c.VPAND(k("B03"), Y7, Y7)
	c.VPOR(Y7, Y6, Y6)
	c.VPSUBB(k("B40"), Y6, Y7)
	t.blend(high1, Y7, Y4, Y4)
	c.VPAND(Y13, Y6, Y6)
	c.VPCMPEQB(Y15, Y6, Y6) // the borrow
	c.VPAND(k("B07"), Y3, Y7)
	c.VPADDB(k("D8"), Y7, Y7)
	c.VPADDB(Y6, Y7, Y7)
	t.blend(high1, Y7, Y8, Y8)
	c.Comment("The low surrogate: DC00 | (b1 & 0F) << 6 | b0 & 3F.")
	c.VPAND(k("B03"), Y5, Y7)
	c.VPOR(k("DC"), Y7, Y7)
	t.blend(low, Y7, Y8, Y8)

	c.Label("wpack")
	if t.evex {
		c.VPMOVZXBW(Y4, Z4)
		c.VPMOVZXBW(Y8, Z5)
		c.VPSLLW(U8(8), Z5, Z5)
		c.VPORQ(Z5, Z4, Z4)
		c.KMOVD(R8L, K2)
		c.VPCOMPRESSW(Z4, K2, Z4)
		c.VMOVDQU64(Z4, Mem{Base: RDI})
		c.POPCNTL(R8L, R8L)
		c.LEAQ(at(RDI, R8, 2), RDI)
	} else {
		c.VPUNPCKLBW(Y8, Y4, Y5) // the units of positions 0 to 7 and 16 to 23
		c.VPUNPCKHBW(Y8, Y4, Y6) // 8 to 15 and 24 to 31
		bytesOf(R8L)
		c.LEAQ(goVar("wideShuf"), RAX)
		for _, half := range []struct {
			lo, hi GPPhysical
			v      VecPhysical
		}{{R9L, R11L, Y5}, {R10L, R12L, Y6}} {
			c.MOVL(half.lo, ECX)
			c.SHLL(U8(4), ECX)
			c.VMOVDQU(at(RAX, RCX, 1), X7)
			c.MOVL(half.hi, ECX)
			c.SHLL(U8(4), ECX)
			c.VINSERTI128(U8(1), at(RAX, RCX, 1), Y7, Y7)
			c.VPSHUFB(Y7, half.v, half.v)
		}
		c.POPCNTL(R9L, R9L)
		c.POPCNTL(R10L, R10L)
		c.POPCNTL(R11L, R11L)
		c.POPCNTL(R12L, R12L)
		c.VMOVDQU(X5, Mem{Base: RDI})
		c.LEAQ(at(RDI, R9, 2), RDI)
		c.VMOVDQU(X6, Mem{Base: RDI})
		c.LEAQ(at(RDI, R10, 2), RDI)
		c.VEXTRACTI128(U8(1), Y5, X7)
		c.VMOVDQU(X7, Mem{Base: RDI})
		c.LEAQ(at(RDI, R11, 2), RDI)
		c.VEXTRACTI128(U8(1), Y6, X7)
		c.VMOVDQU(X7, Mem{Base: RDI})
		c.LEAQ(at(RDI, R12, 2), RDI)
	}
	c.ADDQ(U8(32), RDX)
	c.JMP(l("wide"))
}
