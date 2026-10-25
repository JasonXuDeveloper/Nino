// The amd64 SSSE3 kernels (strings_wire_amd64.s): 16 bytes at a time, as the NEON ones (arm64.go).

package main

import "strings"

// tailSSE loads the last block, CX lanes of size bytes (shift: log2 of size) at R10, fewer than 16 bytes, into X0
// with zeros past them, then jumps to target. Where the 16 bytes from R10 lie in one page they are read and masked;
// else the bytes are copied to DI (room not written yet) and read from there.
func tailSSE(p, shift, target string) string {
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
	JMP     {target}

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
	JMP     {target}
`, "p", p, "shift", shift, "target", target)
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
` + tailSSE("l1", "0", "l1body") + `
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
` + tailSSE("u16", "1", "u16body") + `
u16done:
	SUBQ    BX, DX                  // the zero units past the text
	SUBQ    DX, DI
	SUBQ    R11, DI
	MOVQ    DI, ret+24(FP)
	RET
`

// setupSSE loads what kernelSSE keeps in registers: X13 zeros, X14 the low nibble, X15 C0.
const setupSSE = `	PXOR    X13, X13
	MOVOU   K_NIB, X14
	MOVOU   K_C0, X15
`

// kernelSSE is kernelNEON (arm64.go) with SSSE3, taking the registers of kernelAVX2: the text at SI (BX bytes) to
// Latin-1 at DI, or UTF-16 at the pointer in local wide; AX the result, then a jump to {p}done. The bytes past the
// text in the last block are zeros, each written as a byte (or unit) and cut from the count at the end.
func kernelSSE(p string) string {
	return sub(`	XORQ    DX, DX
	PXOR    X11, X11
	PXOR    X12, X12

{p}latin1:
	MOVQ    BX, CX
	SUBQ    DX, CX
	JLE     {p}latin1Done
	LEAQ    (SI)(DX*1), R10
	CMPQ    CX, $16
	JB      {p}ltail
	MOVOU   (R10), X0

{p}lbody:
	PMOVMSKB X0, AX
	TESTL   AX, AX
	JNZ     {p}lchars
	// ASCII: no char of the block before may be cut.
	MOVOU   K_TAIL16, X4
	MOVOA   X11, X5
	PSUBUSB X4, X5
	POR     X5, X12
	MOVOU   X0, (DI)
	ADDQ    $16, DI
	MOVOA   X0, X11
	ADDQ    $16, DX
	JMP     {p}latin1

{p}lchars:
	// A byte above C3 (a char above U+00FF, or not UTF-8): UTF-16, from the start.
	MOVOA   X0, X4
	MOVOU   K_C3, X5
	PSUBUSB X5, X4
	PCMPEQB X13, X4
	PMOVMSKB X4, R8
	CMPL    R8, $0xffff
	JNE     {p}toUTF16
	// Its bytes are ASCII, leads C2 and C3 and continuations: it is valid when the continuations are exactly the
	// bytes after the leads, and there are no C0 or C1.
	MOVOA   X0, X2
	PALIGNR $15, X11, X2               // the byte before each
	MOVOU   K_FE, X6
	MOVOU   K_C2, X7
	MOVOA   X0, X4
	PAND    X6, X4
	MOVOA   X4, X5
	PCMPEQB X7, X4                     // the leads
	PCMPEQB X15, X5                    // C0 and C1
	POR     X5, X12
	MOVOA   X2, X5
	PAND    X6, X5
	PCMPEQB X7, X5                     // after a lead
	MOVOA   X15, X6
	PCMPGTB X0, X6                     // continuations
	PXOR    X6, X5
	POR     X5, X12
	MOVOU   K_C3, X5
	PCMPEQB X2, X5
	MOVOU   K_B40, X6
	PAND    X6, X5
	PADDB   X0, X5                     // the bytes: 40 more after C3
	MOVOA   X0, X11
	PMOVMSKB X4, R8
	NOTL    R8                         // kept: all but the leads
	MOVL    R8, R9
	ANDL    $0xff, R9
	SHRL    $8, R8
	ANDL    $0xff, R8
	LEAQ    ·narrowLo(SB), AX
	MOVQ    (AX)(R9*8), X6
	LEAQ    ·narrowHi(SB), AX
	MOVQ    (AX)(R8*8), X7
	PUNPCKLQDQ X7, X6
	PSHUFB  X6, X5
	LEAQ    ·pairLen(SB), AX
	MOVBLZX (AX)(R9*1), R10
	MOVBLZX (AX)(R8*1), R11
	MOVQ    X5, (DI)
	LEAQ    -8(DI)(R10*1), DI
	PSRLDQ  $8, X5
	MOVQ    X5, (DI)
	LEAQ    -8(DI)(R11*1), DI
	ADDQ    $16, DX
	JMP     {p}latin1
`+tailSSE(p+"l", "0", p+"lbody")+`
{p}latin1Done:
	MOVOU   K_TAIL16, X4
	MOVOA   X11, X5
	PSUBUSB X4, X5
	POR     X5, X12
	PCMPEQB X13, X12
	PMOVMSKB X12, R8
	CMPL    R8, $0xffff
	JNE     {p}invalid
	MOVQ    DI, AX
	SUBQ    start-8(SP), AX
	MOVQ    DX, CX
	SUBQ    BX, CX                     // the zeros past the text
	SUBQ    CX, AX
	JMP     {p}done

{p}toUTF16:
	// From the start again, as UTF-16: the block in X0 when it is the first.
	MOVQ    wide-16(SP), DI
	PXOR    X11, X11
	PXOR    X12, X12
	TESTQ   DX, DX
	JZ      {p}wchars
	XORQ    DX, DX

{p}wide:
	MOVQ    BX, CX
	SUBQ    DX, CX
	JLE     {p}wideDone
	LEAQ    (SI)(DX*1), R10
	CMPQ    CX, $16
	JB      {p}wtail
	MOVOU   (R10), X0

{p}wbody:
	PMOVMSKB X0, AX
	TESTL   AX, AX
	JNZ     {p}wchars
	MOVOU   K_TAIL16, X4
	MOVOA   X11, X5
	PSUBUSB X4, X5
	POR     X5, X12
	MOVOA   X0, X4
	PUNPCKLBW X13, X4
	MOVOA   X0, X5
	PUNPCKHBW X13, X5
	MOVOU   X4, (DI)
	MOVOU   X5, 16(DI)
	ADDQ    $32, DI
	MOVOA   X0, X11
	ADDQ    $16, DX
	JMP     {p}wide

{p}wchars:
	// Check the block (Keiser and Lemire): X2 the byte one before each, X3 two before, X1 three before.
	MOVOA   X0, X2
	PALIGNR $15, X11, X2
	MOVOA   X0, X3
	PALIGNR $14, X11, X3
	MOVOA   X0, X1
	PALIGNR $13, X11, X1
	MOVOA   X2, X4
	PSRLW   $4, X4
	PAND    X14, X4
	MOVOU   K_T1, X7
	PSHUFB  X4, X7
	MOVOA   X2, X5
	PAND    X14, X5
	MOVOU   K_T2, X8
	PSHUFB  X5, X8
	MOVOA   X0, X6
	PSRLW   $4, X6
	PAND    X14, X6
	MOVOU   K_T3, X9
	PSHUFB  X6, X9
	PAND    X8, X7
	PAND    X9, X7
	MOVOA   X3, X5
	MOVOU   K_S60, X8
	PSUBUSB X8, X5
	MOVOA   X1, X6
	MOVOU   K_S70, X8
	PSUBUSB X8, X6
	POR     X6, X5
	MOVOU   K_B80, X8
	PAND    X8, X5
	PXOR    X5, X7
	POR     X7, X12
	MOVOA   X0, X11
	MOVOA   X15, X4
	PCMPGTB X0, X4                     // b0 a continuation
	MOVOA   X15, X5
	PCMPGTB X2, X5                     // b1 a continuation
	MOVOU   K_E0, X6
	PAND    X2, X6
	PCMPEQB X15, X6                    // b1 the lead of 2 bytes
	PXOR    X7, X7
	PCMPGTB X0, X7                     // b0 not ASCII
	POR     X6, X5
	PAND    X4, X5                     // b0 ends a char of 2 or more bytes, or is a third byte
	PANDN   X7, X5                     // dropped: the rest of the bytes not ASCII
	PMOVMSKB X5, R8
	NOTL    R8                         // kept
	// The low bytes: b0 for ASCII, else (b0 & 3F) | b1 << 6.
	MOVOU   K_B3F, X4
	PAND    X0, X4
	MOVOA   X2, X5
	PSLLW   $6, X5
	PAND    X15, X5
	POR     X5, X4
	PAND    X7, X4
	MOVOA   X7, X5
	PANDN   X0, X5
	POR     X5, X4
	// The high bytes: 0 for ASCII, (b1 >> 2) & 0F after the lead of 2 bytes, and that | b2 << 4 else.
	MOVOA   X2, X9
	PSRLW   $2, X9
	PAND    X14, X9
	MOVOA   X3, X8
	PSLLW   $4, X8
	MOVOU   K_F0, X5
	PAND    X5, X8
	POR     X9, X8
	MOVOA   X6, X5
	PAND    X9, X5
	PANDN   X8, X6
	POR     X5, X6
	PAND    X7, X6
	// Surrogates: b2 the lead of 4 bytes (the high one, X10), or b3 (the low one, X7).
	MOVOU   K_F8, X5
	MOVOU   K_F0, X8
	MOVOA   X3, X10
	PAND    X5, X10
	PCMPEQB X8, X10
	MOVOA   X1, X7
	PAND    X5, X7
	PCMPEQB X8, X7
	MOVOA   X10, X5
	POR     X7, X5
	PMOVMSKB X5, AX
	TESTL   AX, AX
	JZ      {p}wpack
	// The high surrogate: D800 + ((b2 & 7) << 8 | (b1 & 3F) << 2 | (b0 >> 4) & 3) - 40.
	MOVOU   K_B3F, X5
	PAND    X2, X5
	PSLLW   $2, X5
	MOVOA   X0, X8
	PSRLW   $4, X8
	MOVOU   K_B03, X1
	PAND    X1, X8
	POR     X8, X5
	MOVOU   K_B40, X8
	MOVOA   X5, X9
	PSUBB   X8, X9
	PAND    X10, X9
	MOVOA   X10, X8
	PANDN   X4, X8
	POR     X9, X8
	MOVOA   X8, X4
	PAND    X15, X5
	PCMPEQB X13, X5                    // the borrow
	MOVOU   K_B07, X8
	PAND    X3, X8
	MOVOU   K_D8, X9
	PADDB   X9, X8
	PADDB   X5, X8
	PAND    X10, X8
	PANDN   X6, X10
	POR     X8, X10
	MOVOA   X10, X6
	// The low surrogate: DC00 | (b1 & 0F) << 6 | b0 & 3F.
	MOVOA   X2, X8
	PSRLW   $2, X8
	MOVOU   K_B03, X9
	PAND    X9, X8
	MOVOU   K_DC, X9
	POR     X9, X8
	PAND    X7, X8
	PANDN   X6, X7
	POR     X8, X7
	MOVOA   X7, X6

{p}wpack:
	MOVOA   X4, X5
	PUNPCKLBW X6, X5                   // the units of positions 0 to 7
	PUNPCKHBW X6, X4                   // 8 to 15
	MOVL    R8, R9
	ANDL    $0xff, R9
	SHRL    $8, R8
	ANDL    $0xff, R8
	LEAQ    ·wideShuf(SB), AX
	MOVQ    R9, R10
	SHLQ    $4, R10
	MOVOU   (AX)(R10*1), X7
	PSHUFB  X7, X5
	MOVQ    R8, R10
	SHLQ    $4, R10
	MOVOU   (AX)(R10*1), X7
	PSHUFB  X7, X4
	LEAQ    ·pairLen(SB), AX
	MOVBLZX (AX)(R9*1), R10
	MOVBLZX (AX)(R8*1), R11
	MOVOU   X5, (DI)
	LEAQ    -16(DI)(R10*2), DI
	MOVOU   X4, (DI)
	LEAQ    -16(DI)(R11*2), DI
	ADDQ    $16, DX
	JMP     {p}wide
`+tailSSE(p+"w", "0", p+"wbody")+`
{p}wideDone:
	MOVOU   K_TAIL16, X4
	MOVOA   X11, X5
	PSUBUSB X4, X5
	POR     X5, X12
	PCMPEQB X13, X12
	PMOVMSKB X12, R8
	CMPL    R8, $0xffff
	JNE     {p}invalid
	MOVQ    DI, AX
	SUBQ    wide-16(SP), AX
	SHRQ    $1, AX
	MOVQ    DX, CX
	SUBQ    BX, CX                     // the zeros past the text
	SUBQ    CX, AX
	NOTQ    AX
	JMP     {p}done

{p}invalid:
	MOVQ    $0x7fffffffffffffff, AX
	JMP     {p}done
`, "p", p)
}

// writersSSE are wireStringSSE and wireStringsSSE (no VZEROUPPER: no AVX here).
func writersSSE() string {
	s := single("wireStringSSE", setupSSE, kernelSSE, "e") + many("wireStringsSSE", setupSSE, kernelSSE, "f")
	return strings.ReplaceAll(s, "\tVZEROUPPER\n", "")
}
