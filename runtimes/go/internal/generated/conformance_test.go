package generated

import (
	"testing"

	"github.com/JasonXuDeveloper/Nino/runtimes/go/v4/internal/corpustest"
)

// The conformance corpus over the code nino-gen generated (nino_types.go), as internal/sample runs it over
// hand-written codecs (internal/corpustest).
var corpus = corpustest.Suite{
	Codec: func(name string) (corpustest.Codec, bool) {
		c, ok := NinoCodecOf(name)
		return corpustest.Funcs(c.Read, c.Write), ok
	},
	Unsupported: unsupported,
	Expected:    sameGoType,
}

func TestConformanceValid(t *testing.T)       { corpus.Valid(t) }
func TestConformanceMalicious(t *testing.T)   { corpus.Malicious(t) }
func TestConformanceTruncations(t *testing.T) { corpus.Truncations(t) }

// TestCorpusTypesCovered checks that every type the corpus names has a codec here or is listed as unsupported.
func TestCorpusTypesCovered(t *testing.T) { corpus.Covered(t) }

// unsupported is the reason a type cannot be read here, or "": nino-gen leaves it out of Go (NINOGEN002).
func unsupported(typeName string) string {
	if typeName == "Conformance.NestHolder" {
		return "Nest<T> holds itself with other type arguments, which Go's generics reject"
	}
	return ""
}

// goTypeOf names the closed generic types that are one Go type with another (nino-gen's NINOGEN002 warnings: "left
// out of the polymorphic slots in Go"): Wrap<int?[]>, Wrap<List<int?>> and Wrap<HashSet<int?>> are all
// Wrap[[]*int32], whose NinoTypeName is the first one's. Their bytes are the same; only the name differs.
var goTypeOf = map[string]string{
	"Conformance.Wrap<System.Nullable<System.Int32>[]>":                                "Conformance.Wrap<System.Collections.Generic.HashSet<System.Nullable<System.Int32>>>",
	"Conformance.Wrap<System.Collections.Generic.List<System.Nullable<System.Int32>>>": "Conformance.Wrap<System.Collections.Generic.HashSet<System.Nullable<System.Int32>>>",
}

// sameGoType replaces, in an expected value, the .NET names of goTypeOf with the name Go gives their type.
func sameGoType(v any) any {
	switch v := v.(type) {
	case map[string]any:
		for k, x := range v {
			if name, ok := x.(string); ok && k == "$type" && goTypeOf[name] != "" {
				v[k] = goTypeOf[name]
				continue
			}
			v[k] = sameGoType(x)
		}
	case []any:
		for i, x := range v {
			v[i] = sameGoType(x)
		}
	}
	return v
}
