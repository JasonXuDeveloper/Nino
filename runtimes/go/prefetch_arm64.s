//go:build !purego

#include "textflag.h"

// func prefetch8(p *[8]uintptr)
TEXT ·prefetch8(SB), NOSPLIT, $0-8
	MOVD p+0(FP), R0
	LDP  0(R0), (R1, R2)
	LDP  16(R0), (R3, R4)
	LDP  32(R0), (R5, R6)
	LDP  48(R0), (R7, R8)
	PRFM (R1), PLDL1KEEP
	PRFM (R2), PLDL1KEEP
	PRFM (R3), PLDL1KEEP
	PRFM (R4), PLDL1KEEP
	PRFM (R5), PLDL1KEEP
	PRFM (R6), PLDL1KEEP
	PRFM (R7), PLDL1KEEP
	PRFM (R8), PLDL1KEEP
	RET
