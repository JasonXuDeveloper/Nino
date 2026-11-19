//go:build !amd64 || purego || race

package nino

// Leave copying to the caller's builtin, which preserves race instrumentation.
func tryCopyFreshBytes([]byte) []byte { return nil }
