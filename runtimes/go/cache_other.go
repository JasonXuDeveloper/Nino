//go:build !linux && !darwin

package nino

// readLastLevelCache does not know the cache here: the caller takes defaultLastLevelCache.
func readLastLevelCache() int { return 0 }
