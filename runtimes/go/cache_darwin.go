//go:build darwin

package nino

import (
	"encoding/binary"
	"syscall"
)

// readLastLevelCache returns the size of the last-level cache from sysctl: hw.l3cachesize, else the performance
// cores' L2 (Apple silicon has no L3 it reports); 0 when neither says.
func readLastLevelCache() int {
	for _, name := range []string{"hw.l3cachesize", "hw.perflevel0.l2cachesize", "hw.l2cachesize"} {
		if n := sysctlInt(name); n > 0 {
			return n
		}
	}
	return 0
}

// sysctlInt reads an integer sysctl (4 or 8 bytes, little-endian on every darwin Go supports): syscall.Sysctl
// returns its bytes as a string, without a last zero byte.
func sysctlInt(name string) int {
	s, err := syscall.Sysctl(name)
	if err != nil || len(s) > 8 {
		return 0
	}
	var b [8]byte
	copy(b[:], s)
	return int(binary.LittleEndian.Uint64(b[:]))
}
