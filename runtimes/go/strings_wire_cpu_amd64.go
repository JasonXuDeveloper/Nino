//go:build !darwin && !purego

package nino

// osAVX512 tells whether the system saves the opmask and ZMM state (and the XMM and YMM state): the bits of XCR0.
func osAVX512() bool {
	xcr0, _ := xgetbv()
	return xcr0&0xe6 == 0xe6
}
