package bench

import (
	"flag"
	"os"
	"testing"
)

// TestMain shortens every benchmark in quick mode (NINO_BENCH_QUICK=1, the trend check a pull request labeled
// `benchmark` runs, as the .NET suite's quick job): 100 ms per benchmark instead of Go's 1 s, unless -benchtime is
// given. A release (report.yml) runs without it: the only numbers that are published.
func TestMain(m *testing.M) {
	if os.Getenv("NINO_BENCH_QUICK") == "1" {
		given := false
		flag.Parse()
		flag.Visit(func(f *flag.Flag) { given = given || f.Name == "test.benchtime" })
		if !given {
			if err := flag.Set("test.benchtime", "100ms"); err != nil {
				panic(err)
			}
		}
	}
	os.Exit(m.Run())
}
