//go:build !purego

#include "textflag.h"

// func prefetch8(p *[8]uintptr)
TEXT ·prefetch8(SB), NOSPLIT, $0-8
	MOVQ       p+0(FP), AX
	MOVQ       0(AX), BX
	PREFETCHT0 (BX)
	MOVQ       8(AX), BX
	PREFETCHT0 (BX)
	MOVQ       16(AX), BX
	PREFETCHT0 (BX)
	MOVQ       24(AX), BX
	PREFETCHT0 (BX)
	MOVQ       32(AX), BX
	PREFETCHT0 (BX)
	MOVQ       40(AX), BX
	PREFETCHT0 (BX)
	MOVQ       48(AX), BX
	PREFETCHT0 (BX)
	MOVQ       56(AX), BX
	PREFETCHT0 (BX)
	RET
