//go:build race

package nino

// raceEnabled is whether the race detector is on: sync.Pool then drops a quarter of what is put back (on purpose),
// so a pooled writer or reader is allocated again about 0.76 times a call, and allocation counts do not hold.
const raceEnabled = true
