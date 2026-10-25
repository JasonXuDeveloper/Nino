//go:build !purego

package bench

import (
	"os"
	"os/exec"
	"strings"
	"testing"

	"golang.org/x/sys/cpu"
)

// TestTiersMatchXSys checks that the string paths' CPU detection (strings_wire_amd64.go, which keeps the runtime's
// module free of dependencies) takes the tiers golang.org/x/sys/cpu allows: it runs the runtime's TestDetectedTiers
// with the tiers x/sys/cpu gives.
func TestTiersMatchXSys(t *testing.T) {
	x := cpu.X86
	var write, read []string
	if x.HasSSSE3 {
		write, read = append(write, "SSE"), append(read, "SSE")
	}
	if x.HasAVX && x.HasAVX2 && x.HasPOPCNT {
		write = append(write, "AVX2")
		if x.HasAVX512F && x.HasAVX512BW && x.HasAVX512VL && x.HasAVX512VBMI2 {
			write = append(write, "AVX-512")
			if x.HasAVX512VBMI {
				read = append(read, "AVX-512")
			}
		}
	}
	want := "write=" + strings.Join(write, ",") + " read=" + strings.Join(read, ",")
	cmd := exec.Command("go", "test", "-count=1", "-run", "^TestDetectedTiers$", "-v", ".")
	cmd.Dir = ".."
	cmd.Env = append(os.Environ(), "NINO_WANT_TIERS="+want)
	out, err := cmd.CombinedOutput()
	if err != nil || !strings.Contains(string(out), "--- PASS: TestDetectedTiers") {
		t.Fatalf("x/sys/cpu: %s\n%s", want, out)
	}
	t.Log(want)
}
