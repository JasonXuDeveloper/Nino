package generated

import (
	"testing"

	"github.com/JasonXuDeveloper/Nino/runtimes/go/v4/internal/corpustest"
)

// The conformance corpus over the code nino-gen generated (nino_types.go), as internal/sample runs it over
// hand-written codecs (internal/corpustest).
var corpus = corpustest.Suite{Codec: func(name string) (corpustest.Codec, bool) {
	c, ok := NinoCodecOf(name)
	return corpustest.Funcs(c.Read, c.Write), ok
}}

func TestConformanceValid(t *testing.T)       { corpus.Valid(t) }
func TestConformanceMalicious(t *testing.T)   { corpus.Malicious(t) }
func TestConformanceTruncations(t *testing.T) { corpus.Truncations(t) }

// TestCorpusTypesCovered checks that every type the corpus names has a codec here or is listed as unsupported.
func TestCorpusTypesCovered(t *testing.T) { corpus.Covered(t) }
