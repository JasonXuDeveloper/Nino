//go:build !purego

#include "textflag.h"

// func asciiCopy(dst, src unsafe.Pointer, n int) int
TEXT ·asciiCopy(SB), NOSPLIT, $0-32
	MOVD dst+0(FP), R0
	MOVD src+8(FP), R1
	MOVD n+16(FP), R2
	MOVD $0, R3
	MOVD $0x8080808080808080, R6

loop:
	ADD  $16, R3, R4
	CMP  R2, R4
	BGT  done
	ADD  R1, R3, R5
	LDP  (R5), (R7, R8)
	ORR  R7, R8, R9
	TST  R6, R9
	BNE  done
	ADD  R0, R3, R5
	STP  (R7, R8), (R5)
	MOVD R4, R3
	B    loop

done:
	MOVD R3, ret+24(FP)
	RET

// func widenASCII(dst, src unsafe.Pointer, n int) int
TEXT ·widenASCII(SB), NOSPLIT, $0-32
	MOVD dst+0(FP), R0
	MOVD src+8(FP), R1
	MOVD n+16(FP), R2
	MOVD $0, R3
	MOVD $0x8080808080808080, R6

wloop:
	ADD   $16, R3, R4
	CMP   R2, R4
	BGT   wdone
	ADD   R1, R3, R5
	LDP   (R5), (R7, R8)
	ORR   R7, R8, R9
	TST   R6, R9
	BNE   wdone
	VLD1  (R5), [V0.B16]
	VUXTL  V0.B8, V1.H8
	VUXTL2 V0.B16, V2.H8
	ADD   R3<<1, R0, R5
	VST1  [V1.H8, V2.H8], (R5)
	MOVD  R4, R3
	B     wloop

wdone:
	MOVD R3, ret+24(FP)
	RET
