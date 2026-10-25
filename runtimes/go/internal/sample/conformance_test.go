package sample

import (
	"strings"
	"testing"

	"github.com/JasonXuDeveloper/Nino/runtimes/go/v4/internal/corpustest"
)

// The conformance corpus over the hand-written codecs (internal/corpustest).
var corpus = corpustest.Suite{
	Codec: func(name string) (corpustest.Codec, bool) {
		k, ok := codecs[name]
		return corpustest.Funcs(k.read, k.write), ok
	},
	Unsupported: unsupported,
}

func TestConformanceValid(t *testing.T)       { corpus.Valid(t) }
func TestConformanceMalicious(t *testing.T)   { corpus.Malicious(t) }
func TestConformanceTruncations(t *testing.T) { corpus.Truncations(t) }

// TestCorpusTypesCovered checks that every type the corpus names has a codec here or is listed as unsupported.
func TestCorpusTypesCovered(t *testing.T) { corpus.Covered(t) }

// The .NET names of the closed generic types, which corpustest.Render cannot take from their Go names (generated
// code declares NinoTypeName on every type; these test-only methods stand in for it here).

func (BoxInt32) NinoTypeName() string  { return "Conformance.Box<System.Int32>" }
func (BoxString) NinoTypeName() string { return "Conformance.Box<System.String>" }
func (BoxPoints) NinoTypeName() string {
	return "Conformance.Box<System.Collections.Generic.List<Conformance.Point>>"
}
func (BoxVec2) NinoTypeName() string { return "Conformance.Box<Conformance.Vec2>" }

// render is corpustest.Render, for the fuzz and tag tests.
func render(v any) any { return corpustest.Render(v) }

// unsupported is the reason a type cannot be read here, or "".
func unsupported(typeName string) string {
	if strings.HasPrefix(typeName, "Conformance.Tracked") {
		return "reference tracking is not implemented here (internal/generated runs its cases)"
	}
	switch typeName {
	case "Conformance.BagHolder", "Conformance.GenericSlots", "Conformance.WrappedArgs":
		// [NinoType] collections, generic types behind a type id, Nullable and KeyValuePair type arguments.
		return "left to the code nino-gen writes (internal/generated runs its cases)"
	case "Conformance.NestHolder":
		return "Nest<T> holds Nest<List<T>>, a type Go cannot instantiate (polymorphic recursion)"
	}
	return ""
}
