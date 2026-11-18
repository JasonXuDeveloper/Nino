//go:build ninocopyprobe && !purego

package nino

var hasCopyProbe = detectAVX2()
