//go:build ninocopyprobe && !purego

#include "textflag.h"

// func copyProbeSIMD(dst, src *byte, n int)
TEXT ·copyProbeSIMD(SB), NOSPLIT, $0-24
	MOVQ dst+0(FP), DI
	MOVQ src+8(FP), SI
	MOVQ n+16(FP), CX

	// Copy a head and tail inside the slice so the main loop can always use whole cache lines.
	VMOVDQU (SI), Y0
	VMOVDQU 32(SI), Y1
	VMOVDQU -64(SI)(CX*1), Y2
	VMOVDQU -32(SI)(CX*1), Y3
	VMOVDQU Y0, (DI)
	VMOVDQU Y1, 32(DI)
	VMOVDQU Y2, -64(DI)(CX*1)
	VMOVDQU Y3, -32(DI)(CX*1)
	MOVQ DI, AX
	NEGQ AX
	ANDQ $63, AX
	SUBQ $64, CX

loop:
	CMPQ AX, CX
	JAE done
	VMOVDQU (SI)(AX*1), Y0
	VMOVDQU 32(SI)(AX*1), Y1
	VMOVDQA Y0, (DI)(AX*1)
	VMOVDQA Y1, 32(DI)(AX*1)
	ADDQ $64, AX
	JMP loop
done:
	VZEROUPPER
	RET
