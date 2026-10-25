// The arm64 kernels (strings_wire_arm64.s): NEON, which every arm64 processor has.

package main

import (
	"fmt"
	"strings"
)

// fileARM64 is strings_wire_arm64.s.
func fileARM64() string {
	var b strings.Builder
	b.WriteString(header)
	b.WriteString(constantsARM64())
	b.WriteString(readersNEON)
	b.WriteString(writersNEON())
	return encodeARM64(b.String())
}

// Go's assembler lacks some NEON instructions; the kernels write them as
//
//	WORD CMHI V1.H8, V2.H8, V3.H8
//
// (operands in Go's order: the second source first, the destination last), which encodeARM64 replaces with their
// encoding and the instruction as a comment.

// neon is an instruction the assembler lacks: its encoding with Q=1 and no registers, the kind of its operands, and
// whether the arrangement sets the size field.
type neon struct {
	bits uint32
	kind byte // '3' three registers, '2' two, 'v' across lanes (to a scalar), 's' shift by an immediate
}

var neonOps = map[string]neon{
	"CMHI":  {0x6E203400, '3'}, // unsigned n > m
	"CMHS":  {0x6E203C00, '3'}, // unsigned n >= m
	"CMGT":  {0x4E203400, '3'}, // signed n > m
	"UQSUB": {0x6E202C00, '3'}, // n - m, at least 0
	"BIC":   {0x4E601C00, '3'}, // n &^ m (bytes only)
	"CMLT":  {0x4E20A800, '2'}, // n < 0
	"UMAXV": {0x6E30A800, 'v'}, // the largest lane
	"SSHR":  {0x4F000400, 's'}, // shift right, signed
	"SHRN":  {0x0F008400, 's'}, // shift right and narrow (to the lower half)
	"ORN":   {0x4EE01C00, '3'}, // n | ^m (bytes only)
}

// arrangements are the size field of each arrangement Go writes.
var arrangements = map[string]uint32{"B16": 0, "B8": 0, "H8": 1, "S4": 2, "D2": 3}

// encodeARM64 replaces each "WORD <instruction>" line of s with its encoding.
func encodeARM64(s string) string {
	lines := strings.Split(s, "\n")
	for i, line := range lines {
		t := strings.TrimSpace(line)
		if !strings.HasPrefix(t, "WORD ") || strings.HasPrefix(t, "WORD $") {
			continue
		}
		f := strings.Fields(strings.NewReplacer(",", " ").Replace(strings.TrimPrefix(t, "WORD ")))
		op, ok := neonOps[f[0]]
		if !ok {
			panic("wiregen: no encoding for " + t)
		}
		reg := func(v string) (uint32, uint32) {
			name, arr, _ := strings.Cut(v, ".")
			var n uint32
			if _, err := fmt.Sscanf(name, "V%d", &n); err != nil || n > 31 {
				panic("wiregen: bad register in " + t)
			}
			size, ok := arrangements[arr]
			if arr != "" && !ok {
				panic("wiregen: bad arrangement in " + t)
			}
			return n, size
		}
		var enc uint32
		switch op.kind {
		case '3':
			m, size := reg(f[1])
			n, _ := reg(f[2])
			d, _ := reg(f[3])
			enc = op.bits | size<<22 | m<<16 | n<<5 | d
			if f[0] == "BIC" || f[0] == "ORN" {
				enc = op.bits | m<<16 | n<<5 | d
				if size != 0 {
					panic("wiregen: BIC and ORN take bytes")
				}
			}
		case '2':
			n, size := reg(f[1])
			d, _ := reg(f[2])
			enc = op.bits | size<<22 | n<<5 | d
		case 'v':
			n, size := reg(f[1])
			d, _ := reg(f[2])
			enc = op.bits | size<<22 | n<<5 | d
		case 's':
			var shift uint32
			if _, err := fmt.Sscanf(f[1], "$%d", &shift); err != nil {
				panic("wiregen: bad shift in " + t)
			}
			n, size := reg(f[2])
			d, _ := reg(f[3])
			esize := uint32(8) << size
			if f[0] == "SHRN" {
				esize >>= 1 // the size of the result's lanes
			}
			if shift < 1 || shift > esize {
				panic("wiregen: bad shift in " + t)
			}
			enc = op.bits | (2*esize-shift)<<16 | n<<5 | d
		}
		indent := line[:len(line)-len(strings.TrimLeft(line, " \t"))]
		lines[i] = fmt.Sprintf("%sWORD $%#08x // %s", indent, enc, strings.TrimPrefix(t, "WORD "))
	}
	return strings.Join(lines, "\n")
}

// constantsARM64 is the wireN<> table: the bit of each byte of 8 (twice), 16 bytes of FF then 16 of 0 (read from
// 16 minus the bytes left on, a mask of the bytes of the text), the bit of each 16-bit lane of 8, then two bits
// apart (and the higher of those), and the tables of the UTF-8 validation.
func constantsARM64() string {
	var t []byte
	for i := 0; i < 16; i++ {
		t = append(t, 1<<(i&7))
	}
	for i := 0; i < 16; i++ {
		t = append(t, 0xFF)
	}
	t = append(t, make([]byte, 16)...)
	for _, step := range []uint{1, 2} {
		for i := uint(0); i < 8; i++ {
			v := uint16(1) << (step * i)
			t = append(t, byte(v), byte(v>>8))
		}
	}
	for i := uint(0); i < 8; i++ {
		v := uint16(2) << (2 * i)
		t = append(t, byte(v), byte(v>>8))
	}
	t1, t2, t3 := validationTables()
	t = append(append(append(t, t1[:]...), t2[:]...), t3[:]...)
	tail := make([]byte, 16)
	for i := range tail {
		tail[i] = 0xFF
	}
	tail[13], tail[14], tail[15] = 0xEF, 0xDF, 0xBF
	t = append(t, tail...)
	var b strings.Builder
	for i := 0; i < len(t); i += 8 {
		var v uint64
		for k := 7; k >= 0; k-- {
			v = v<<8 | uint64(t[i+k])
		}
		fmt.Fprintf(&b, "DATA wireN<>+%#x(SB)/8, $%#016x\n", i, v)
	}
	fmt.Fprintf(&b, "GLOBL wireN<>(SB), RODATA|NOPTR, $%#x\n\n", len(t))
	b.WriteString(`#define N_BITS 0     // the bit of each byte of 8, twice
#define N_MASK 16    // 16 bytes of FF, then 16 of 0
#define N_LANES 48   // the bit of each 16-bit lane of 8
#define N_PAIRS 64   // 1, 4, 16 ... 4^7: two bits a lane
#define N_PAIRS2 80  // twice those
#define N_T1 96      // the tables of the UTF-8 validation (see strings_wire_amd64.s)
#define N_T2 112
#define N_T3 128
#define N_TAIL 144   // the most each of the last three bytes may be (no char cut at the end)
`)
	return b.String()
}

// tailNEON loads the last block, of R5 lanes of size bytes (fewer than 16 bytes) at R6, into V0 with zeros past
// them, then jumps to body. Where the 16 bytes from R6 lie in one page they are read and masked; else the bytes are
// copied to R0 (room not written yet) and read from there.
func tailNEON(p, size, body string) string {
	return sub(`
{p}tail:
	LSL     ${size}, R5, R7         // the bytes left
	AND     $4095, R6, R8
	CMP     $4080, R8
	BHI     {p}cross
	VLD1    (R6), [V0.B16]
	ADD     $(N_MASK+16), R21, R8
	SUB     R7, R8, R8
	VLD1    (R8), [V1.B16]
	VAND    V1.B16, V0.B16, V0.B16
	B       {p}{body}

{p}cross:
	VEOR    V0.B16, V0.B16, V0.B16
	VST1    [V0.B16], (R0)
	MOVD    $0, R8

{p}byte:
	MOVBU   (R6)(R8), R9
	MOVB    R9, (R0)(R8)
	ADD     $1, R8
	CMP     R7, R8
	BLT     {p}byte
	VLD1    (R0), [V0.B16]
	B       {p}{body}
`, "p", p, "size", size, "body", body)
}

// readersNEON are the string reader's NEON paths: text from the wire as UTF-8 (WTF-8). The last block is read
// only within the text (tailNEON); each block stores 16 bytes, so dst has room for 16 bytes past the most the text
// can take. Lanes past the text are zeros, each written as one byte: the bytes written are cut by those at the end.
var readersNEON = `
// func latin1UTF8NEON(dst, src unsafe.Pointer, n int) int
//
// It writes n Latin-1 bytes at src as UTF-8 at dst (room for 2*n+16 bytes) and returns the bytes written. A block
// of 16 bytes of ASCII is stored as it is; else each byte is given its two bytes of UTF-8 (lead, then the byte
// itself for ASCII or its continuation byte), eight such pairs at a time, and the leads of ASCII dropped by a table
// shuffle (pairShuf).
TEXT ·latin1UTF8NEON(SB), NOSPLIT, $0-32
	MOVD    dst+0(FP), R0
	MOVD    src+8(FP), R1
	MOVD    n+16(FP), R2
	MOVD    R0, R4
	MOVD    $0, R3
	MOVD    $·pairShuf(SB), R19
	MOVD    $·pairLen(SB), R20
	MOVD    $wireN<>(SB), R21
	MOVD    $0x8080808080808080, R22
	VMOVI   $0xC0, V30.B16
	VMOVI   $0x40, V29.B16
	VLD1    (R21), [V31.B16]        // N_BITS

l1loop:
	SUB     R3, R2, R5              // the bytes left
	CMP     $0, R5
	BLE     l1done
	ADD     R3, R1, R6
	CMP     $16, R5
	BLT     l1tail
	VLD1    (R6), [V0.B16]

l1body:
	VMOV    V0.D[0], R7
	VMOV    V0.D[1], R8
	ORR     R7, R8, R9
	TST     R22, R9
	BNE     l1high
	VST1    [V0.B16], (R0)
	ADD     $16, R0
	ADD     $16, R3
	B       l1loop

l1high:
	WORD    CMLT V0.B16, V2.B16     // 80 or above
	VUSHR   $6, V0.B16, V3.B16
	VORR    V30.B16, V3.B16, V3.B16 // the leads: C0 | c >> 6
	VAND    V29.B16, V2.B16, V4.B16
	WORD    BIC V4.B16, V0.B16, V4.B16 // the bytes after them: c, less 40 for 80 and above
	VZIP1   V4.B16, V3.B16, V5.B16  // the pairs of bytes 0 to 7
	VZIP2   V4.B16, V3.B16, V6.B16  // of 8 to 15
	VAND    V31.B16, V2.B16, V7.B16
	VADDP   V7.B16, V7.B16, V7.B16
	VADDP   V7.B16, V7.B16, V7.B16
	VADDP   V7.B16, V7.B16, V7.B16
	VMOV    V7.H[0], R7             // the bytes of 80 or above, a bit each
	AND     $0xff, R7, R8
	LSR     $8, R7, R9
	ADD     R8<<4, R19, R10
	VLD1    (R10), [V8.B16]
	ADD     R9<<4, R19, R11
	VLD1    (R11), [V9.B16]
	MOVBU   (R20)(R8), R12
	MOVBU   (R20)(R9), R13
	VTBL    V8.B16, [V5.B16], V5.B16
	VTBL    V9.B16, [V6.B16], V6.B16
	VST1    [V5.B16], (R0)
	ADD     R12, R0
	VST1    [V6.B16], (R0)
	ADD     R13, R0
	ADD     $16, R3
	B       l1loop
` + tailNEON("l1", "0", "body") + `
l1done:
	SUB     R2, R3, R5              // the zeros past the text
	SUB     R5, R0, R0
	SUB     R4, R0, R0
	MOVD    R0, ret+24(FP)
	RET

// func utf16UTF8NEON(dst, src unsafe.Pointer, units int) int
//
// It writes units UTF-16 code units (little-endian) at src as WTF-8 at dst (room for 3*units+16 bytes) and returns
// the bytes written, eight units at a time: ASCII narrowed; units below 800 as pairs of bytes, as latin1UTF8NEON;
// else each unit as its three bytes, the bytes kept by a table shuffle of each four units (u3Shuf). A high surrogate
// ends the block before it, and is written with the low one after it (or alone) one at a time.
TEXT ·utf16UTF8NEON(SB), NOSPLIT, $0-32
	MOVD    dst+0(FP), R0
	MOVD    src+8(FP), R1
	MOVD    units+16(FP), R2
	MOVD    R0, R4
	MOVD    $0, R3
	MOVD    $·pairShuf(SB), R19
	MOVD    $·pairLen(SB), R20
	MOVD    $wireN<>(SB), R21
	MOVD    $·u3Shuf(SB), R23
	MOVD    $·u3Len(SB), R24
	MOVD    $0xFF80, R7
	VDUP    R7, V31.H8
	MOVD    $0xF800, R7
	VDUP    R7, V30.H8
	MOVD    $0x3F, R7
	VDUP    R7, V29.H8
	MOVD    $0x80, R7
	VDUP    R7, V28.H8
	MOVD    $0xC0, R7
	VDUP    R7, V27.H8
	MOVD    $0xE0, R7
	VDUP    R7, V26.H8
	MOVD    $0x40, R7
	VDUP    R7, V25.H8
	MOVD    $0xFC00, R7
	VDUP    R7, V24.H8
	MOVD    $0xD800, R7
	VDUP    R7, V23.H8
	ADD     $N_LANES, R21, R7
	VLD1    (R7), [V20.B16, V21.B16, V22.B16] // N_LANES, N_PAIRS, N_PAIRS2

u16loop:
	SUB     R3, R2, R5              // the units left
	CMP     $0, R5
	BLE     u16done
	ADD     R3<<1, R1, R6
	CMP     $8, R5
	BLT     u16tail
	VLD1    (R6), [V0.B16]

u16body:
	VCMTST  V31.H8, V0.H8, V1.H8    // not ASCII
	VMOV    V1.D[0], R7
	VMOV    V1.D[1], R8
	ORR     R7, R8, R9
	CBNZ    R9, u16wide
	VUZP1   V0.B16, V0.B16, V2.B16
	FMOVD   F2, (R0)
	ADD     $8, R0
	ADD     $8, R3
	B       u16loop

u16wide:
	VCMTST  V30.H8, V0.H8, V3.H8    // 800 or above: 3 bytes
	VMOV    V3.D[0], R7
	VMOV    V3.D[1], R8
	ORR     R7, R8, R9
	// The last byte of each: the unit for ASCII, else 80 | u & 3F.
	VAND    V29.B16, V0.B16, V4.B16
	VORR    V28.B16, V4.B16, V4.B16
	VORR    V0.B16, V0.B16, V5.B16
	VBIT    V1.B16, V4.B16, V5.B16
	CBNZ    R9, u16three
	// Each below 800: its lead C0 | u >> 6 and the last byte, as a pair.
	VUSHR   $6, V0.H8, V6.H8
	VORR    V27.B16, V6.B16, V6.B16
	VSLI    $8, V5.H8, V6.H8
	VAND    V20.B16, V1.B16, V7.B16
	VADDV   V7.H8, V7
	VMOV    V7.H[0], R7
	ADD     R7<<4, R19, R10
	VLD1    (R10), [V8.B16]
	MOVBU   (R20)(R7), R11
	VTBL    V8.B16, [V6.B16], V6.B16
	VST1    [V6.B16], (R0)
	ADD     R11, R0
	ADD     $8, R3
	B       u16loop

u16three:
	// Each as 4 bytes: E0 | u >> 12, 80 | (u >> 6) & 3F (C0 | u >> 6 below 800), the last byte, 0.
	VUSHR   $12, V0.H8, V6.H8
	VORR    V26.B16, V6.B16, V6.B16
	VUSHR   $6, V0.H8, V7.H8
	VAND    V29.B16, V7.B16, V7.B16
	VORR    V28.B16, V7.B16, V7.B16
	WORD    BIC V3.B16, V25.B16, V8.B16
	VORR    V8.B16, V7.B16, V7.B16
	VSLI    $8, V7.H8, V6.H8
	VZIP1   V5.H8, V6.H8, V9.H8     // units 0 to 3
	VZIP2   V5.H8, V6.H8, V10.H8    // 4 to 7
	// Two bits a unit: 0 for 1 byte, 1 for 2, 3 for 3 (2: none).
	VAND    V21.B16, V1.B16, V11.B16
	VAND    V22.B16, V3.B16, V12.B16
	VORR    V12.B16, V11.B16, V11.B16
	VADDV   V11.H8, V11
	VMOV    V11.H[0], R7
	// High surrogates.
	VAND    V24.B16, V0.B16, V12.B16
	VCMEQ   V23.H8, V12.H8, V12.H8
	VAND    V21.B16, V12.B16, V12.B16
	VADDV   V12.H8, V12
	VMOV    V12.H[0], R12
	MOVD    $8, R13                 // the units taken
	CBNZ    R12, u16cut

u16emit:
	AND     $0xff, R7, R8
	LSR     $8, R7, R9
	ADD     R8<<4, R23, R10
	VLD1    (R10), [V13.B16]
	ADD     R9<<4, R23, R11
	VLD1    (R11), [V14.B16]
	MOVBU   (R24)(R8), R14
	MOVBU   (R24)(R9), R15
	VTBL    V13.B16, [V9.B16], V9.B16
	VTBL    V14.B16, [V10.B16], V10.B16
	VST1    [V9.B16], (R0)
	ADD     R14, R0
	VST1    [V10.B16], (R0)
	ADD     R15, R0
	ADD     R13, R3
	CMP     $8, R13
	BNE     u16surrogate
	B       u16loop

u16cut:
	// The units before the first high surrogate (k of them) only.
	RBIT    R12, R13
	CLZ     R13, R14                // 2k
	LSR     $1, R14, R13
	MOVD    $1, R15
	LSL     R14, R15, R15
	SUB     $1, R15, R15
	AND     R15, R7, R7
	MOVD    $0xAAAA, R16
	BIC     R15, R16, R16
	ORR     R16, R7, R7
	B       u16emit

u16surrogate:
	// A high surrogate at R3: with the low one after it, 4 bytes; else alone, 3.
	ADD     R3<<1, R1, R6
	MOVHU   (R6), R7
	ADD     $1, R3, R8
	CMP     R2, R8
	BGE     u16lone
	MOVHU   2(R6), R9
	AND     $0xFC00, R9, R10
	CMP     $0xDC00, R10
	BNE     u16lone
	LSL     $10, R7, R7
	ADD     R9, R7, R7
	SUB     $0x35FDC00, R7, R7      // the code point: (hi << 10) + lo + 10000 - (D800 << 10) - DC00
	LSR     $18, R7, R10
	ORR     $0xF0, R10, R10
	UBFX    $12, R7, $6, R11
	ORR     $0x80, R11, R11
	UBFX    $6, R7, $6, R12
	ORR     $0x80, R12, R12
	AND     $0x3F, R7, R13
	ORR     $0x80, R13, R13
	ORR     R11<<8, R10, R10
	ORR     R12<<16, R10, R10
	ORR     R13<<24, R10, R10
	MOVW    R10, (R0)
	ADD     $4, R0
	ADD     $2, R3
	B       u16loop

u16lone:
	LSR     $12, R7, R10
	ORR     $0xE0, R10, R10
	UBFX    $6, R7, $6, R11
	ORR     $0x80, R11, R11
	AND     $0x3F, R7, R12
	ORR     $0x80, R12, R12
	ORR     R11<<8, R10, R10
	ORR     R12<<16, R10, R10
	MOVW    R10, (R0)
	ADD     $3, R0
	ADD     $1, R3
	B       u16loop
` + tailNEON("u16", "1", "body") + `
u16done:
	SUB     R2, R3, R5              // the zero units past the text
	SUB     R5, R0, R0
	SUB     R4, R0, R0
	MOVD    R0, ret+24(FP)
	RET
`

// setupNEON loads what kernelNEON keeps in registers: the tables' addresses (R20 pairLen, R23 narrowLo, R24
// narrowHi, R25 wideShuf, R21 wireN<>), R22 the high bit of each byte, and constants in V13 and V15 to V31.
const setupNEON = `	MOVD    $·pairLen(SB), R20
	MOVD    $wireN<>(SB), R21
	MOVD    $0x8080808080808080, R22
	MOVD    $·narrowLo(SB), R23
	MOVD    $·narrowHi(SB), R24
	MOVD    $·wideShuf(SB), R25
	MOVD    $0x3C3C3C3C3C3C3C3C, R19
	MOVD    $0x1010101010101010, R17
	ADD     $N_T1, R21, R8
	VLD1    (R8), [V20.B16, V21.B16, V22.B16]
	ADD     $N_TAIL, R21, R8
	VLD1    (R8), [V19.B16]
	VMOVI   $0x80, V13.B16
	VMOVI   $0xF8, V15.B16
	VMOVI   $0x70, V17.B16
	VMOVI   $0x60, V18.B16
	VMOVI   $0xF0, V23.B16
	VMOVI   $0xE0, V24.B16
	VMOVI   $0xC2, V25.B16
	VMOVI   $0xFE, V26.B16
	VMOVI   $0x40, V27.B16
	VMOVI   $0xC3, V28.B16
	VMOVI   $0x3F, V29.B16
	VMOVI   $0x0F, V30.B16
	VMOVI   $0xC0, V31.B16
`

// tailW loads the last block of the writer's text, R7 bytes (fewer than 16) at R6, into V0 with zeros past them,
// as tailNEON, and its two halves into R8 and R9.
func tailW(p, l, body string) string {
	return sub(`
{p}{l}tail:
	AND     $4095, R6, R8
	CMP     $4080, R8
	BHI     {p}{l}cross
	VLD1    (R6), [V0.B16]
	ADD     $(N_MASK+16), R21, R8
	SUB     R7, R8, R8
	VLD1    (R8), [V1.B16]
	VAND    V1.B16, V0.B16, V0.B16
	VMOV    V0.D[0], R8
	VMOV    V0.D[1], R9
	B       {p}{body}

{p}{l}cross:
	VEOR    V0.B16, V0.B16, V0.B16
	VST1    [V0.B16], (R0)
	MOVD    $0, R8

{p}{l}byte:
	MOVBU   (R6)(R8), R9
	MOVB    R9, (R0)(R8)
	ADD     $1, R8
	CMP     R7, R8
	BLT     {p}{l}byte
	VLD1    (R0), [V0.B16]
	LDP     (R0), (R8, R9)
	B       {p}{body}
`, "p", p, "l", l, "body", body)
}

// movemask sets R9 and R10 to the high bits of bytes 0 to 7 and 8 to 15 of V{v} (each 00 or FF), or of their
// complement with not: a nibble a byte (SHRN), gathered to a bit each in the scalar registers. R8 and V11 are lost.
func movemask(v string, not bool) string {
	inv := ""
	if not {
		inv = "\tMVN     R8, R8\n"
	}
	return sub(`	WORD    SHRN $4, V{v}.H8, V11.B8
	VMOV    V11.D[0], R8
{inv}	AND     $0x1111111111111111, R8
	ORR     R8>>3, R8, R8
	AND     $0x0303030303030303, R8
	ORR     R8>>6, R8, R8
	AND     $0x000F000F000F000F, R8
	ORR     R8>>12, R8, R8
	AND     $0xff, R8, R9
	UBFX    $32, R8, $8, R10
`, "v", v, "inv", inv)
}

// kernelNEON converts the text at R1 (R2 bytes, R2 > 0) to Latin-1 at R4, or to UTF-16 at R5, 16 bytes at a time
// as kernelAVX2 does 32; R7 the result (bytes, ^units or wireInvalid), then a jump to {p}done. The bytes past the
// text in the last block are zeros, each written as a byte (or unit) and cut from the count at the end.
func kernelNEON(p string) string {
	return sub(`	MOVD    $0, R3
	MOVD    R4, R0
	VEOR    V14.B16, V14.B16, V14.B16
	VEOR    V12.B16, V12.B16, V12.B16

{p}latin1:
	SUB     R3, R2, R7
	CMP     $0, R7
	BLE     {p}latin1Done
	ADD     R3, R1, R6
	CMP     $16, R7
	BLT     {p}ltail
	VLD1    (R6), [V0.B16]
	LDP     (R6), (R8, R9)

{p}lbody:
	ORR     R8, R9, R10
	TST     R22, R10
	BNE     {p}lchars
	// ASCII: no char of the block before may be cut.
	WORD    UQSUB V19.B16, V14.B16, V4.B16
	VORR    V4.B16, V12.B16, V12.B16
	VST1    [V0.B16], (R0)
	ADD     $16, R0
	VORR    V0.B16, V0.B16, V14.B16
	ADD     $16, R3
	B       {p}latin1

{p}lchars:
	// A byte above C3 (a char above U+00FF, or not UTF-8): UTF-16, from the start. A byte b of 80 or above is C4
	// or above when (b & 7F) + 3C carries into its high bit.
	AND     $0x7f7f7f7f7f7f7f7f, R8, R10
	ADD     R19, R10, R10
	AND     R8, R10, R10
	AND     $0x7f7f7f7f7f7f7f7f, R9, R11
	ADD     R19, R11, R11
	AND     R9, R11, R11
	ORR     R10, R11, R10
	TST     R22, R10
	BNE     {p}toUTF16
	// Its bytes are ASCII, leads C2 and C3 and continuations: it is valid when the continuations are exactly the
	// bytes after the leads, and there are no C0 or C1.
	VEXT    $15, V0.B16, V14.B16, V2.B16 // the byte before each
	VAND    V26.B16, V0.B16, V7.B16
	VCMEQ   V25.B16, V7.B16, V4.B16      // the leads
	VCMEQ   V31.B16, V7.B16, V7.B16      // C0 and C1
	VORR    V7.B16, V12.B16, V12.B16
	VAND    V26.B16, V2.B16, V5.B16
	VCMEQ   V25.B16, V5.B16, V5.B16      // after a lead
	WORD    CMGT V0.B16, V31.B16, V6.B16 // continuations
	VEOR    V6.B16, V5.B16, V5.B16
	VORR    V5.B16, V12.B16, V12.B16
	VCMEQ   V28.B16, V2.B16, V5.B16
	VAND    V27.B16, V5.B16, V5.B16
	VADD    V5.B16, V0.B16, V6.B16       // the bytes: 40 more after C3
	VORR    V0.B16, V0.B16, V14.B16
`+movemask("4", true)+`	// kept: all but the leads
	FMOVD   (R23)(R9<<3), F8
	MOVD    (R24)(R10<<3), R11
	VMOV    R11, V8.D[1]
	VTBL    V8.B16, [V6.B16], V6.B16
	MOVBU   (R20)(R9), R9
	MOVBU   (R20)(R10), R10
	VST1    [V6.B16], (R0)
	SUB     $8, R9, R9
	ADD     R9, R0
	VMOV    V6.D[1], R11
	MOVD    R11, (R0)
	SUB     $8, R10, R10
	ADD     R10, R0
	ADD     $16, R3
	B       {p}latin1
`+tailW(p, "l", "lbody")+`
{p}latin1Done:
	WORD    UQSUB V19.B16, V14.B16, V4.B16
	VORR    V4.B16, V12.B16, V12.B16
	VMOV    V12.D[0], R8
	VMOV    V12.D[1], R9
	ORR     R8, R9, R10
	CBNZ    R10, {p}invalid
	SUB     R4, R0, R7
	SUB     R2, R3, R8                   // the zeros past the text
	SUB     R8, R7, R7
	B       {p}done

{p}toUTF16:
	// From the start again, as UTF-16: the block in V0 when it is the first.
	MOVD    R5, R0
	MOVD    $0, R13                      // the block before has a lead of 4 bytes (or more)
	VEOR    V14.B16, V14.B16, V14.B16
	VEOR    V12.B16, V12.B16, V12.B16
	CBZ     R3, {p}wchars
	MOVD    $0, R3

{p}wide:
	SUB     R3, R2, R7
	CMP     $0, R7
	BLE     {p}wideDone
	ADD     R3, R1, R6
	CMP     $16, R7
	BLT     {p}wtail
	VLD1    (R6), [V0.B16]
	LDP     (R6), (R8, R9)

{p}wbody:
	ORR     R8, R9, R10
	TST     R22, R10
	BNE     {p}wchars
	WORD    UQSUB V19.B16, V14.B16, V4.B16
	VORR    V4.B16, V12.B16, V12.B16
	VUXTL   V0.B8, V4.H8
	VUXTL2  V0.B16, V5.H8
	VST1    [V4.B16, V5.B16], (R0)
	ADD     $32, R0
	MOVD    $0, R13
	VORR    V0.B16, V0.B16, V14.B16
	ADD     $16, R3
	B       {p}wide

{p}wchars:
	// Check the block (Keiser and Lemire): V2 the byte one before each, V3 two before, V1 three before.
	VEXT    $15, V0.B16, V14.B16, V2.B16
	VEXT    $14, V0.B16, V14.B16, V3.B16
	VEXT    $13, V0.B16, V14.B16, V1.B16
	VUSHR   $4, V2.B16, V4.B16
	VTBL    V4.B16, [V20.B16], V4.B16
	VAND    V30.B16, V2.B16, V5.B16
	VTBL    V5.B16, [V21.B16], V5.B16
	VUSHR   $4, V0.B16, V6.B16
	VTBL    V6.B16, [V22.B16], V6.B16
	VAND    V5.B16, V4.B16, V4.B16
	VAND    V6.B16, V4.B16, V4.B16
	WORD    UQSUB V18.B16, V3.B16, V5.B16
	WORD    UQSUB V17.B16, V1.B16, V6.B16
	VORR    V6.B16, V5.B16, V5.B16
	VAND    V13.B16, V5.B16, V5.B16
	VEOR    V5.B16, V4.B16, V4.B16
	VORR    V4.B16, V12.B16, V12.B16
	VORR    V0.B16, V0.B16, V14.B16
	// A lead of 4 bytes (F0 or above) here or in the block before: surrogates (R12).
	AND     $0x7f7f7f7f7f7f7f7f, R8, R10
	ADD     R17, R10, R10
	AND     R8, R10, R10
	AND     $0x7f7f7f7f7f7f7f7f, R9, R11
	ADD     R17, R11, R11
	AND     R9, R11, R11
	ORR     R10, R11, R10
	AND     R22, R10, R10
	ORR     R10, R13, R12
	MOVD    R10, R13
	WORD    CMGT V0.B16, V31.B16, V4.B16 // b0 a continuation
	WORD    CMGT V2.B16, V31.B16, V5.B16 // b1 a continuation
	VAND    V24.B16, V2.B16, V6.B16
	VCMEQ   V31.B16, V6.B16, V6.B16      // b1 the lead of 2 bytes
	WORD    CMLT V0.B16, V7.B16          // b0 not ASCII
	VORR    V6.B16, V5.B16, V5.B16
	VAND    V4.B16, V5.B16, V5.B16       // b0 ends a char of 2 or more bytes, or is a third byte
	WORD    ORN V7.B16, V5.B16, V5.B16   // kept: those and ASCII
`+movemask("5", false)+`	// The low bytes: b0 for ASCII, else (b0 & 3F) | b1 << 6.
	VORR    V0.B16, V0.B16, V4.B16
	VSLI    $6, V2.B16, V4.B16
	VBIF    V7.B16, V0.B16, V4.B16
	// The high bytes: 0 for ASCII, (b1 >> 2) & 0F after the lead of 2 bytes, and that | b2 << 4 else.
	VUSHR   $2, V2.B16, V5.B16
	VAND    V30.B16, V5.B16, V5.B16
	VSHL    $4, V3.B16, V8.B16
	VORR    V5.B16, V8.B16, V8.B16
	VBIT    V6.B16, V5.B16, V8.B16
	VAND    V7.B16, V8.B16, V8.B16
	CBZ     R12, {p}wpack
	// Surrogates: b2 the lead of 4 bytes (the high one), or b3 (the low one).
	VAND    V15.B16, V3.B16, V9.B16
	VCMEQ   V23.B16, V9.B16, V9.B16
	VAND    V15.B16, V1.B16, V10.B16
	VCMEQ   V23.B16, V10.B16, V10.B16
	// The high surrogate: D800 + ((b2 & 7) << 8 | (b1 & 3F) << 2 | (b0 >> 4) & 3) - 40.
	VAND    V29.B16, V2.B16, V6.B16
	VSHL    $2, V6.B16, V6.B16
	VUSHR   $4, V0.B16, V7.B16
	VMOVI   $3, V11.B16
	VAND    V11.B16, V7.B16, V7.B16
	VORR    V7.B16, V6.B16, V6.B16
	VSUB    V27.B16, V6.B16, V7.B16
	VBIT    V9.B16, V7.B16, V4.B16
	VCMTST  V31.B16, V6.B16, V6.B16      // no borrow
	VMOVI   $7, V11.B16
	VAND    V11.B16, V3.B16, V7.B16
	VMOVI   $0xD7, V11.B16
	VADD    V11.B16, V7.B16, V7.B16
	VSUB    V6.B16, V7.B16, V7.B16
	VBIT    V9.B16, V7.B16, V8.B16
	// The low surrogate: DC00 | (b1 & 0F) << 6 | b0 & 3F.
	VMOVI   $3, V11.B16
	VAND    V11.B16, V5.B16, V7.B16
	VMOVI   $0xDC, V11.B16
	VORR    V11.B16, V7.B16, V7.B16
	VBIT    V10.B16, V7.B16, V8.B16

{p}wpack:
	VZIP1   V8.B16, V4.B16, V9.B16       // the units of positions 0 to 7
	VZIP2   V8.B16, V4.B16, V10.B16      // 8 to 15
	ADD     R9<<4, R25, R11
	VLD1    (R11), [V6.B16]
	ADD     R10<<4, R25, R11
	VLD1    (R11), [V7.B16]
	MOVBU   (R20)(R9), R9
	MOVBU   (R20)(R10), R10
	VTBL    V6.B16, [V9.B16], V9.B16
	VTBL    V7.B16, [V10.B16], V10.B16
	VST1    [V9.B16], (R0)
	ADD     R9<<1, R0, R0
	SUB     $16, R0, R0
	VST1    [V10.B16], (R0)
	ADD     R10<<1, R0, R0
	SUB     $16, R0, R0
	ADD     $16, R3
	B       {p}wide
`+tailW(p, "w", "wbody")+`
{p}wideDone:
	WORD    UQSUB V19.B16, V14.B16, V4.B16
	VORR    V4.B16, V12.B16, V12.B16
	VMOV    V12.D[0], R8
	VMOV    V12.D[1], R9
	ORR     R8, R9, R10
	CBNZ    R10, {p}invalid
	SUB     R5, R0, R7
	LSR     $1, R7, R7
	SUB     R2, R3, R8                   // the zeros past the text
	SUB     R8, R7, R7
	MVN     R7, R7
	B       {p}done

{p}invalid:
	MOVD    $0x7fffffffffffffff, R7
	B       {p}done
`, "p", p)
}

// singleNEON is wireStringNEON: kernelNEON for one string.
const singleNEON = `
// func wireStringNEON(dst unsafe.Pointer, i int, src unsafe.Pointer, n int) int
TEXT ·wireStringNEON(SB), NOSPLIT, $0-40
	MOVD    src+16(FP), R1
	MOVD    i+8(FP), R3
	ADD     R3, R1, R1               // the text from i on
	MOVD    n+24(FP), R2
	SUB     R3, R2, R2               // its length
	MOVD    dst+0(FP), R4
	ADD     R3<<1, R4, R5            // UTF-16 goes to dst+2*i
	ADD     R3, R4, R4               // Latin-1 to dst+i
`

const singleNEONEnd = `
sdone:
	MOVD    R7, ret+32(FP)
	RET
`

// manyNEON is wireStringsNEON: kernelNEON for each string of a list.
const manyNEON = `
// func wireStringsNEON(dst unsafe.Pointer, room int, strs unsafe.Pointer, count int, ptrs bool) (done, written int)
//
// It writes strings from strs (count of them: strings, or pointers to them when ptrs, nil the null string) at dst
// (room bytes), each with its header, while each has room for 4+2*len+64 bytes and is WTF-8 of fewer than 2^30
// bytes; it returns how many it wrote and the bytes they took.
TEXT ·wireStringsNEON(SB), NOSPLIT, $0-56
	MOVD    dst+0(FP), R15           // where the next string goes
	MOVD    room+8(FP), R16
	ADD     R15, R16, R16            // the end of the room
	MOVD    $0, R14                  // the strings done
`

const manyNEONLoop = `
mnext:
	MOVD    count+24(FP), R8
	CMP     R8, R14
	BGE     mfinish
	MOVD    strs+16(FP), R9
	MOVBU   ptrs+32(FP), R10
	CBZ     R10, mvalue
	MOVD    (R9)(R14<<3), R9
	CBNZ    R9, mheader
	// nil: the null string.
	ADD     $4, R15, R10
	CMP     R16, R10
	BHI     mfinish
	MOVW    $-1, R11
	MOVW    R11, (R15)
	MOVD    R10, R15
	ADD     $1, R14
	B       mnext

mvalue:
	ADD     R14<<4, R9, R9

mheader:
	MOVD    (R9), R1
	MOVD    8(R9), R2
	CBNZ    R2, mtext
	ADD     $4, R15, R10
	CMP     R16, R10
	BHI     mfinish
	MOVW    ZR, (R15)
	MOVD    R10, R15
	ADD     $1, R14
	B       mnext

mtext:
	MOVD    $0x40000000, R10
	CMP     R10, R2
	BHS     mfinish
	ADD     R2<<1, R15, R10
	ADD     $68, R10
	CMP     R16, R10
	BHI     mfinish
	ADD     $4, R15, R4
	MOVD    R4, R5
`

const manyNEONEnd = `
mdone:
	MOVD    $0x7fffffffffffffff, R8
	CMP     R8, R7
	BEQ     mfinish
	TBNZ    $63, R7, mstore16
	MVN     R7, R8
	MOVW    R8, (R15)
	ADD     $4, R7, R7
	ADD     R7, R15, R15
	ADD     $1, R14
	B       mnext

mstore16:
	MVN     R7, R7
	MOVW    R7, (R15)
	ADD     R7<<1, R15, R15
	ADD     $4, R15
	ADD     $1, R14
	B       mnext

mfinish:
	MOVD    R14, done+40(FP)
	MOVD    dst+0(FP), R8
	SUB     R8, R15, R8
	MOVD    R8, written+48(FP)
	RET
`

// writersNEON are wireStringNEON and wireStringsNEON.
func writersNEON() string {
	return singleNEON + setupNEON + kernelNEON("s") + singleNEONEnd +
		manyNEON + setupNEON + manyNEONLoop + kernelNEON("m") + manyNEONEnd
}
