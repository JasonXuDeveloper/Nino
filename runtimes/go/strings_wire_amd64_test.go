//go:build !purego

package nino

import (
	"os"
	"strings"
	"testing"
)

func TestDarwinRelease(t *testing.T) {
	for _, c := range []struct {
		release string
		want    bool
	}{
		{"21.3.0", true}, {"21.2.9", false}, {"21.4.0", true}, {"22.0.0", true}, {"20.6.0", false},
		{"24.6.0\x00", true}, {"21.3", false}, {"21.3.0.1", false}, {"21.3.0-x", false}, {"", false},
	} {
		if got := darwinRelease(c.release, 21, 3, 0); got != c.want {
			t.Errorf("darwinRelease(%q) = %v, want %v", c.release, got, c.want)
		}
	}
}

// TestDetectedTiers checks the tiers detected against NINO_WANT_TIERS ("write=SSE,AVX2 read=SSE"), which the bench
// module's TestTiersMatchXSys sets from golang.org/x/sys/cpu (a dependency this module does not take).
func TestDetectedTiers(t *testing.T) {
	want := os.Getenv("NINO_WANT_TIERS")
	if want == "" {
		t.Skip("NINO_WANT_TIERS not set (see the bench module's TestTiersMatchXSys)")
	}
	if got := describeTiers(); got != want {
		t.Fatalf("detected %q, x/sys/cpu says %q", got, want)
	}
}

func describeTiers() string {
	names := func(tiers []int) string {
		var s []string
		for _, t := range tiers {
			s = append(s, tierName(t))
		}
		return strings.Join(s, ",")
	}
	return "write=" + names(writeTiers) + " read=" + names(readTiers)
}
