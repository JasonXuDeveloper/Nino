// Command asm writes the string paths' assembly: strings_wire_amd64.s (SSSE3, AVX2 and AVX-512; see
// strings_wire_amd64.go) with avo (github.com/mmcloughlin/avo), and strings_wire_arm64.s (NEON; see
// strings_wire_arm64.go), which avo cannot write (it is x86 only), as text. The runtime's go:generate line runs it:
//
//	go run -C internal/asm . -amd64 ../../strings_wire_amd64.s -arm64 ../../strings_wire_arm64.s
//
// It is a module of its own so that the runtime's module keeps no dependencies.
package main

import (
	"flag"
	"fmt"
	"log"
	"os"

	"github.com/mmcloughlin/avo/build"
	"github.com/mmcloughlin/avo/ir"
	"github.com/mmcloughlin/avo/pass"
	"github.com/mmcloughlin/avo/printer"
)

// c is the amd64 file being built.
var c = build.NewContext()

func main() {
	amd64 := flag.String("amd64", "strings_wire_amd64.s", "the amd64 file to write")
	arm64 := flag.String("arm64", "strings_wire_arm64.s", "the arm64 file to write")
	flag.Parse()
	log.SetFlags(0)
	log.SetPrefix("asm: ")
	a, b, err := generate()
	if err != nil {
		log.Fatal(err)
	}
	for _, f := range []struct {
		path string
		text []byte
	}{{*amd64, a}, {*arm64, b}} {
		if err := os.WriteFile(f.path, f.text, 0o644); err != nil {
			log.Fatal(err)
		}
	}
}

// generate returns the text of strings_wire_amd64.s and strings_wire_arm64.s. It builds the amd64 file in c, so
// it runs once.
func generate() (amd64, arm64 []byte, err error) {
	fileAMD64()
	f, err := c.Result()
	if err != nil {
		return nil, nil, err
	}
	// The constants the Go code declares (wireSlack, wireMaxText) come from go_asm.h.
	f.Includes = append(f.Includes, "go_asm.h")
	if err := pass.Concat(pass.Compile, checkRegisters).Execute(f); err != nil {
		return nil, nil, err
	}
	cfg := printer.Config{Name: "internal/asm with avo (see strings_wire_amd64.go)", Pkg: "nino"}
	amd64, err = printer.NewGoAsm(cfg).Print(f)
	if err != nil {
		return nil, nil, err
	}
	return amd64, []byte(fileARM64()), nil
}

// checkRegisters fails the build when register allocation chose BP (the frame pointer) or R15 (what a global's
// address is loaded through when linking dynamically) for a virtual register.
var checkRegisters = pass.FunctionPass(func(fn *ir.Function) error {
	for _, i := range fn.Instructions() {
		for _, r := range i.Registers() {
			if n := r.Asm(); n == "BP" || n == "R15" {
				return fmt.Errorf("%s: %s takes %s", fn.Name, i.Opcode, n)
			}
		}
	}
	return nil
})
