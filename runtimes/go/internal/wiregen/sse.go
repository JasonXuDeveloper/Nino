// The amd64 SSSE3 kernels (strings_wire_amd64.s): 16 bytes at a time, as the NEON ones (arm64.go).

package main

// tailSSE loads the last block, CX lanes of size bytes (shift: log2 of size) at R10, fewer than 16 bytes, into X0
// with zeros past them, then jumps to body. Where the 16 bytes from R10 lie in one page they are read and masked;
// else the bytes are copied to DI (room not written yet) and read from there.
func tailSSE(p, shift, body string) string {
	return sub(`
{p}tail:
	MOVQ    CX, R12
	SHLQ    ${shift}, R12           // the bytes left
	MOVL    R10, R13
	ANDL    $4095, R13
	CMPL    R13, $4080
	JA      {p}cross
	MOVOU   (R10), X0
	LEAQ    K_MASK, R13
	ADDQ    $32, R13
	SUBQ    R12, R13
	MOVOU   (R13), X1
	PAND    X1, X0
	JMP     {p}{body}

{p}cross:
	PXOR    X0, X0
	MOVOU   X0, (DI)
	XORQ    R13, R13

{p}byte:
	MOVBLZX (R10)(R13*1), AX
	MOVB    AX, (DI)(R13*1)
	INCQ    R13
	CMPQ    R13, R12
	JB      {p}byte
	MOVOU   (DI), X0
	JMP     {p}{body}
`, "p", p, "shift", shift, "body", body)
}

// readersSSE are the string reader's SSSE3 paths, the NEON ones (readersNEON) on x86: taken where the processor
// has SSSE3 and no AVX-512 VBMI.
var readersSSE = `
// func latin1UTF8SSE(dst, src unsafe.Pointer, n int) int
//
// It writes n Latin-1 bytes at src as UTF-8 at dst (room for 2*n+16 bytes) and returns the bytes written, as
// latin1UTF8NEON.
TEXT ·latin1UTF8SSE(SB), NOSPLIT, $0-32
	MOVQ    dst+0(FP), DI
	MOVQ    src+8(FP), SI
	MOVQ    n+16(FP), BX
	MOVQ    DI, R11
	XORQ    DX, DX
	LEAQ    ·pairShuf(SB), R8
	LEAQ    ·pairLen(SB), R9
	MOVOU   K_B03, X13
	MOVOU   K_C0, X14
	MOVOU   K_B40, X15

l1loop:
	MOVQ    BX, CX
	SUBQ    DX, CX                  // the bytes left
	JLE     l1done
	LEAQ    (SI)(DX*1), R10
	CMPQ    CX, $16
	JB      l1tail
	MOVOU   (R10), X0

l1body:
	PMOVMSKB X0, AX
	TESTL   AX, AX
	JNZ     l1high
	MOVOU   X0, (DI)
	ADDQ    $16, DI
	ADDQ    $16, DX
	JMP     l1loop

l1high:
	MOVOA   X0, X1
	PSRLW   $6, X1
	PAND    X13, X1
	POR     X14, X1                 // the leads: C0 | c >> 6
	PXOR    X2, X2
	PCMPGTB X0, X2
	PAND    X15, X2
	PANDN   X0, X2                  // the bytes after them: c, less 40 for 80 and above
	MOVOA   X1, X3
	PUNPCKLBW X2, X3                // the pairs of bytes 0 to 7
	PUNPCKHBW X2, X1                // of 8 to 15
	MOVL    AX, CX
	ANDL    $0xff, CX
	SHRL    $8, AX
	MOVQ    CX, R12
	SHLQ    $4, R12
	MOVOU   (R8)(R12*1), X4
	PSHUFB  X4, X3
	MOVQ    AX, R12
	SHLQ    $4, R12
	MOVOU   (R8)(R12*1), X5
	PSHUFB  X5, X1
	MOVBLZX (R9)(CX*1), R12
	MOVBLZX (R9)(AX*1), R13
	MOVOU   X3, (DI)
	ADDQ    R12, DI
	MOVOU   X1, (DI)
	ADDQ    R13, DI
	ADDQ    $16, DX
	JMP     l1loop
` + tailSSE("l1", "0", "body") + `
l1done:
	SUBQ    BX, DX                  // the zeros past the text
	SUBQ    DX, DI
	SUBQ    R11, DI
	MOVQ    DI, ret+24(FP)
	RET

// func utf16UTF8SSE(dst, src unsafe.Pointer, units int) int
//
// It writes units UTF-16 code units at src as WTF-8 at dst (room for 3*units+16 bytes) and returns the bytes
// written, as utf16UTF8NEON.
TEXT ·utf16UTF8SSE(SB), NOSPLIT, $0-32
	MOVQ    dst+0(FP), DI
	MOVQ    src+8(FP), SI
	MOVQ    units+16(FP), BX
	MOVQ    DI, R11
	XORQ    DX, DX
	PXOR    X7, X7
	MOVOU   K_WFF80, X8
	MOVOU   K_WF800, X9
	MOVOU   K_W3F, X10
	MOVOU   K_W80, X11
	MOVOU   K_WC0, X12
	MOVOU   K_WE0, X13
	MOVOU   K_W40, X14
	MOVOU   K_WFC00, X15

u16loop:
	MOVQ    BX, CX
	SUBQ    DX, CX                  // the units left
	JLE     u16done
	LEAQ    (SI)(DX*2), R10
	CMPQ    CX, $8
	JB      u16tail
	MOVOU   (R10), X0

u16body:
	MOVOA   X0, X1
	PAND    X8, X1
	PCMPEQW X7, X1                  // ASCII
	PMOVMSKB X1, AX
	CMPL    AX, $0xffff
	JNE     u16wide
	PACKUSWB X0, X0
	MOVQ    X0, (DI)
	ADDQ    $8, DI
	ADDQ    $8, DX
	JMP     u16loop

u16wide:
	MOVOA   X0, X3
	PAND    X9, X3
	PCMPEQW X7, X3                  // below 800
	PMOVMSKB X3, CX
	MOVOA   X1, X6
	PACKSSWB X6, X6
	PMOVMSKB X6, R13
	NOTL    R13
	ANDL    $0xff, R13              // not ASCII, a bit a unit
	// The last byte of each: the unit for ASCII, else 80 | u & 3F.
	MOVOA   X0, X4
	PAND    X10, X4
	POR     X11, X4
	MOVOA   X1, X5
	PAND    X0, X5
	PANDN   X4, X1
	POR     X5, X1
	CMPL    CX, $0xffff
	JNE     u16three
	// Each below 800: its lead C0 | u >> 6 and the last byte, as a pair.
	MOVOA   X0, X2
	PSRLW   $6, X2
	POR     X12, X2
	PSLLW   $8, X1
	POR     X1, X2
	MOVQ    R13, R12
	SHLQ    $4, R12
	LEAQ    ·pairShuf(SB), R14
	MOVOU   (R14)(R12*1), X4
	PSHUFB  X4, X2
	MOVOU   X2, (DI)
	LEAQ    ·pairLen(SB), R14
	MOVBLZX (R14)(R13*1), R12
	ADDQ    R12, DI
	ADDQ    $8, DX
	JMP     u16loop

u16three:
	// Each as 4 bytes: E0 | u >> 12, 80 | (u >> 6) & 3F (C0 | u >> 6 below 800), the last byte, 0.
	MOVOA   X0, X2
	PSRLW   $12, X2
	POR     X13, X2
	MOVOA   X0, X4
	PSRLW   $6, X4
	PAND    X10, X4
	POR     X11, X4
	PAND    X14, X3
	POR     X3, X4
	PSLLW   $8, X4
	POR     X4, X2
	MOVOA   X2, X4
	PUNPCKLWL X1, X2                // units 0 to 3
	PUNPCKHWL X1, X4                // 4 to 7
	// Two bits a unit: 0 for 1 byte, 1 for 2, 3 for 3 (2: none).
	NOTL    AX
	ANDL    $0x5555, AX
	NOTL    CX
	ANDL    $0xaaaa, CX
	ORL     CX, AX
	// High surrogates.
	MOVOU   K_WD800, X6
	MOVOA   X0, X5
	PAND    X15, X5
	PCMPEQW X6, X5
	PMOVMSKB X5, R13
	MOVQ    $8, R14                 // the units taken
	TESTL   R13, R13
	JNZ     u16cut

u16emit:
	MOVL    AX, CX
	ANDL    $0xff, CX
	SHRL    $8, AX
	LEAQ    ·u3Shuf(SB), R15
	MOVQ    CX, R12
	SHLQ    $4, R12
	MOVOU   (R15)(R12*1), X5
	PSHUFB  X5, X2
	MOVQ    AX, R12
	SHLQ    $4, R12
	MOVOU   (R15)(R12*1), X6
	PSHUFB  X6, X4
	LEAQ    ·u3Len(SB), R15
	MOVBLZX (R15)(CX*1), R12
	MOVBLZX (R15)(AX*1), R13
	MOVOU   X2, (DI)
	ADDQ    R12, DI
	MOVOU   X4, (DI)
	ADDQ    R13, DI
	ADDQ    R14, DX
	CMPQ    R14, $8
	JNE     u16surrogate
	JMP     u16loop

u16cut:
	// The units before the first high surrogate (k of them) only.
	BSFL    R13, CX                 // 2k
	MOVL    CX, R14
	SHRL    $1, R14
	MOVL    $1, R12
	SHLL    CX, R12
	DECL    R12                     // the bits of the units kept
	ANDL    R12, AX
	NOTL    R12
	ANDL    $0xaaaa, R12
	ORL     R12, AX
	JMP     u16emit

u16surrogate:
	// A high surrogate at DX: with the low one after it, 4 bytes; else alone, 3.
	MOVWLZX (SI)(DX*2), AX
	LEAQ    1(DX), R12
	CMPQ    R12, BX
	JAE     u16lone
	MOVWLZX 2(SI)(DX*2), CX
	MOVL    CX, R12
	ANDL    $0xFC00, R12
	CMPL    R12, $0xDC00
	JNE     u16lone
	SHLL    $10, AX
	ADDL    CX, AX
	SUBL    $0x35FDC00, AX          // the code point: (hi << 10) + lo + 10000 - (D800 << 10) - DC00
	MOVL    AX, CX
	SHRL    $18, CX
	ORL     $0xF0, CX
	MOVL    AX, R12
	SHRL    $12, R12
	ANDL    $0x3F, R12
	ORL     $0x80, R12
	SHLL    $8, R12
	ORL     R12, CX
	MOVL    AX, R12
	SHRL    $6, R12
	ANDL    $0x3F, R12
	ORL     $0x80, R12
	SHLL    $16, R12
	ORL     R12, CX
	ANDL    $0x3F, AX
	ORL     $0x80, AX
	SHLL    $24, AX
	ORL     AX, CX
	MOVL    CX, (DI)
	ADDQ    $4, DI
	ADDQ    $2, DX
	JMP     u16loop

u16lone:
	MOVL    AX, CX
	SHRL    $12, CX
	ORL     $0xE0, CX
	MOVL    AX, R12
	SHRL    $6, R12
	ANDL    $0x3F, R12
	ORL     $0x80, R12
	SHLL    $8, R12
	ORL     R12, CX
	ANDL    $0x3F, AX
	ORL     $0x80, AX
	SHLL    $16, AX
	ORL     AX, CX
	MOVL    CX, (DI)
	ADDQ    $3, DI
	INCQ    DX
	JMP     u16loop
` + tailSSE("u16", "1", "body") + `
u16done:
	SUBQ    BX, DX                  // the zero units past the text
	SUBQ    DX, DI
	SUBQ    R11, DI
	MOVQ    DI, ret+24(FP)
	RET
`
