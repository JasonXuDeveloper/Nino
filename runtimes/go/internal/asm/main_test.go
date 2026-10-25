package main

import (
	"bytes"
	"os"
	"testing"
)

// TestGenerated checks that the committed assembly is what the generator writes (run go generate in runtimes/go
// after changing it).
func TestGenerated(t *testing.T) {
	amd64, arm64, err := generate()
	if err != nil {
		t.Fatal(err)
	}
	for _, f := range []struct {
		path string
		text []byte
	}{{"../../strings_wire_amd64.s", amd64}, {"../../strings_wire_arm64.s", arm64}} {
		got, err := os.ReadFile(f.path)
		if err != nil {
			t.Fatal(err)
		}
		if !bytes.Equal(got, f.text) {
			t.Errorf("%s is not what internal/asm writes: run go generate in runtimes/go", f.path)
		}
	}
}
