//go:build !purego && !race

#include "textflag.h"

// func copyMediumFresh(dst, src *byte, n int)
TEXT ·copyMediumFresh(SB), NOSPLIT, $0-24
	MOVD dst+0(FP), R0
	MOVD src+8(FP), R1
	MOVD n+16(FP), R2
	SUB $64, R2
	ADD R2, R1, R3
	ADD R2, R0, R4

	// Cover both ends before aligning the inner stores to 64 bytes.
	VLD1 (R1), [V0.B16, V1.B16, V2.B16, V3.B16]
	VLD1 (R3), [V4.B16, V5.B16, V6.B16, V7.B16]
	VST1 [V0.B16, V1.B16, V2.B16, V3.B16], (R0)
	VST1 [V4.B16, V5.B16, V6.B16, V7.B16], (R4)

	NEG R0, R3
	AND $63, R3
	ADD R3, R0
	ADD R3, R1

loop:
	CMP R4, R0
	BHS done
	VLD1.P 64(R1), [V0.B16, V1.B16, V2.B16, V3.B16]
	VST1.P [V0.B16, V1.B16, V2.B16, V3.B16], 64(R0)
	B loop
done:
	RET
