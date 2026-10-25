//go:build !purego

#include "textflag.h"

// func asciiCopy(dst, src unsafe.Pointer, n int) int
TEXT ·asciiCopy(SB), NOSPLIT, $0-32
	MOVQ dst+0(FP), DI
	MOVQ src+8(FP), SI
	MOVQ n+16(FP), CX
	XORQ AX, AX

loop:
	LEAQ 16(AX), DX
	CMPQ DX, CX
	JGT  done
	MOVOU    (SI)(AX*1), X0
	PMOVMSKB X0, BX
	TESTL    BX, BX
	JNZ      done
	MOVOU    X0, (DI)(AX*1)
	MOVQ     DX, AX
	JMP      loop

done:
	MOVQ AX, ret+24(FP)
	RET

// func widenASCII(dst, src unsafe.Pointer, n int) int
TEXT ·widenASCII(SB), NOSPLIT, $0-32
	MOVQ dst+0(FP), DI
	MOVQ src+8(FP), SI
	MOVQ n+16(FP), CX
	XORQ AX, AX
	PXOR X1, X1

wloop:
	LEAQ 16(AX), DX
	CMPQ DX, CX
	JGT  wdone
	MOVOU     (SI)(AX*1), X0
	PMOVMSKB  X0, BX
	TESTL     BX, BX
	JNZ       wdone
	MOVOU     X0, X2
	PUNPCKLBW X1, X0
	PUNPCKHBW X1, X2
	MOVOU     X0, (DI)(AX*2)
	MOVOU     X2, 16(DI)(AX*2)
	MOVQ      DX, AX
	JMP       wloop

wdone:
	MOVQ AX, ret+24(FP)
	RET
