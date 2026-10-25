// Command wiregen writes the string paths' assembly: strings_wire_amd64.s (SSE, AVX2 and AVX-512; see
// strings_wire_amd64.go) and strings_wire_arm64.s (NEON; see strings_wire_arm64.go):
//
//	go run ./internal/wiregen
//
// The kernels are written once here and placed in each function that takes them, with its own label prefix: Go's
// assembler has no macros that could hold labels.
package main

import (
	"flag"
	"log"
	"os"
	"strings"
)

func main() {
	amd64 := flag.String("amd64", "strings_wire_amd64.s", "the amd64 file to write")
	arm64 := flag.String("arm64", "strings_wire_arm64.s", "the arm64 file to write")
	flag.Parse()
	for _, f := range []struct{ path, text string }{{*amd64, fileAMD64()}, {*arm64, fileARM64()}} {
		if err := os.WriteFile(f.path, []byte(f.text), 0o644); err != nil {
			log.Fatal(err)
		}
	}
}

// sub replaces each {key} in s with its value (pairs of key and value).
func sub(s string, kv ...string) string {
	for i := 0; i < len(kv); i += 2 {
		s = strings.ReplaceAll(s, "{"+kv[i]+"}", kv[i+1])
	}
	return s
}

// replaceOnce replaces old, which must occur in s exactly once.
func replaceOnce(s, old, new string) string {
	if strings.Count(s, old) != 1 {
		log.Fatalf("wiregen: %q occurs %d times", old, strings.Count(s, old))
	}
	return strings.Replace(s, old, new, 1)
}

// replaceSpan replaces the text from the first from (at or after the first after) up to and including the first to
// after it.
func replaceSpan(s, after, from, to, new string) string {
	a := strings.Index(s, after)
	if a < 0 {
		log.Fatalf("wiregen: no %q", after)
	}
	i := strings.Index(s[a:], from)
	if i < 0 {
		log.Fatalf("wiregen: no %q", from)
	}
	i += a
	j := strings.Index(s[i:], to)
	if j < 0 {
		log.Fatalf("wiregen: no %q", to)
	}
	return s[:i] + new + s[i+j+len(to):]
}
