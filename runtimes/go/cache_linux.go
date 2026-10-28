//go:build linux

package nino

import (
	"os"
	"path/filepath"
	"strconv"
	"strings"
)

// readLastLevelCache returns the size of cpu0's last-level cache from sysfs: its level 3 data or unified cache,
// else the highest level it lists; 0 when sysfs does not say.
func readLastLevelCache() int {
	dirs, _ := filepath.Glob("/sys/devices/system/cpu/cpu0/cache/index*")
	bestLevel, best := 0, 0
	for _, d := range dirs {
		read := func(name string) string {
			b, _ := os.ReadFile(filepath.Join(d, name))
			return strings.TrimSpace(string(b))
		}
		if read("type") == "Instruction" {
			continue
		}
		level, err := strconv.Atoi(read("level"))
		size := parseCacheSize(read("size"))
		if err != nil || size <= 0 || level > 3 {
			continue
		}
		if level > bestLevel || (level == bestLevel && size > best) {
			bestLevel, best = level, size
		}
	}
	return best
}
