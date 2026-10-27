// strings_wire_amd64.s: the file, its constants, the functions' frames and what the kernels share.

package main

import (
	"go/token"
	"go/types"
	"log"
	"strings"

	"github.com/mmcloughlin/avo/attr"
	"github.com/mmcloughlin/avo/gotypes"
	. "github.com/mmcloughlin/avo/operand"
	. "github.com/mmcloughlin/avo/reg"
	"github.com/mmcloughlin/avo/x86"
)

// fileAMD64 builds strings_wire_amd64.s.
func fileAMD64() {
	c.ConstraintExpr("!purego")
	constants()
	writerAVX("wireStringAVX2", "wireStringsAVX2", avx2)
	writerAVX("wireStringAVX512", "wireStringsAVX512", avx512)
	utf16UTF8AVX512()
	latin1UTF8AVX512()
	latin1UTF8SSE()
	utf16UTF8SSE()
	writerSSE()
	cpu()
}

// The wire<> table: each constant is 32 bytes (or more), named here.
var (
	wire    Mem
	offsets = map[string]int{}
)

// k is the constant called name in the wire<> table.
func k(name string) Mem {
	at, ok := offsets[name]
	if !ok {
		log.Fatalf("no constant %s", name)
	}
	return wire.Offset(at)
}

func fill(b byte) []byte { return []byte(strings.Repeat(string([]byte{b}), 32)) }

// words is 16 lanes of 16 bits of v.
func words(v uint16) []byte {
	b := make([]byte, 32)
	for i := 0; i < 32; i += 2 {
		b[i], b[i+1] = byte(v), byte(v>>8)
	}
	return b
}

// lanes is a 16-byte table in both lanes.
func lanes(t [16]byte) []byte { return append(t[:], t[:]...) }

func constants() {
	t1, t2, t3 := validationTables()
	tail := fill(0xFF)
	tail[29], tail[30], tail[31] = 0xEF, 0xDF, 0xBF
	mask := append(fill(0xFF), make([]byte, 32)...)
	not0 := fill(0xFF)
	not0[0] = 0
	table := []struct {
		name  string
		bytes []byte
	}{
		{"C0", fill(0xC0)}, // a continuation is less (signed)
		{"NIB", fill(0x0F)},
		{"S60", fill(0x60)}, // E0 - 80
		{"S70", fill(0x70)}, // F0 - 80
		{"B80", fill(0x80)},
		{"C3", fill(0xC3)},
		{"B40", fill(0x40)},
		{"FE", fill(0xFE)},
		{"C2", fill(0xC2)},
		{"E0", fill(0xE0)},
		{"F0", fill(0xF0)},
		{"F8", fill(0xF8)},
		{"B3F", fill(0x3F)},
		{"B07", fill(0x07)},
		{"B03", fill(0x03)},
		{"D8", fill(0xD8)},
		{"DC", fill(0xDC)},
		{"TAIL", tail},    // the most each of the last three bytes may be (no char cut at the end); TAIL16 its last 16
		{"T1", lanes(t1)}, // the checks by the high nibble of the byte before
		{"T2", lanes(t2)}, // by its low nibble
		{"T3", lanes(t3)}, // by the high nibble of the byte
		{"MASK", mask},    // 32 bytes of FF, then 32 of 0: from 32 minus the bytes left on, a mask of the bytes of the text
		{"NOT0", not0},    // 0, then 31 bytes of FF: clears the byte before the text (see writer.short)
		{"WFF80", words(0xFF80)},
		{"WF800", words(0xF800)},
		{"WFC00", words(0xFC00)},
		{"WD800", words(0xD800)},
		{"W3F", words(0x3F)},
		{"W80", words(0x80)},
		{"WC0", words(0xC0)},
		{"WE0", words(0xE0)},
		{"W40", words(0x40)},
	}
	wire = c.StaticGlobal("wire")
	c.DataAttributes(attr.RODATA | attr.NOPTR)
	at := 0
	for _, t := range table {
		offsets[t.name] = at
		for i := 0; i < len(t.bytes); i += 8 {
			var v uint64
			for b := 7; b >= 0; b-- {
				v = v<<8 | uint64(t.bytes[at-offsets[t.name]+i+b])
			}
			c.AddDatum(at+i, U64(v))
		}
		at += len(t.bytes)
	}
	offsets["TAIL16"] = offsets["TAIL"] + 16
}

// goVar is the package's variable called name (a table of strings_wire.go).
func goVar(name string) Mem { return NewDataAddr(Symbol{Name: "·" + name}, 0) }

// at is (base)(index*scale).
func at(base, index Register, scale uint8) Mem { return Mem{Base: base, Index: index, Scale: scale} }

// param is the argument called name of the function being built.
func param(name string) Mem { return addr(c.Param(name).Resolve()) }

func addr(b *gotypes.Basic, err error) Mem {
	if err != nil {
		log.Fatal(err)
	}
	return b.Addr
}

// pkg is the package the signatures are in: nino, with unsafe imported.
var pkg = func() *types.Package {
	p := types.NewPackage("github.com/JasonXuDeveloper/Nino/runtimes/go/v4", "nino")
	p.Scope().Insert(types.NewPkgName(token.NoPos, p, "unsafe", types.Unsafe))
	return p
}()

// function starts the function called name (NOSPLIT) with the signature expr.
func function(name, expr string) {
	s, err := gotypes.ParseSignatureInPackage(pkg, expr)
	if err != nil {
		log.Fatal(err)
	}
	c.Function(name)
	c.Attributes(attr.NOSPLIT)
	c.Signature(s)
}

// cmpConst compares r with the constant name of go_asm.h, which avo cannot write as an operand: the instruction is
// built (and its form checked) with the constant's value, then the operand renamed.
func cmpConst(r Register, name string, value uint32) {
	i, err := x86.CMPQ(r, U32(value))
	if err != nil {
		log.Fatal(err)
	}
	i.Operands[1] = LabelRef("$const_" + name) // printed as it is, and no register in it
	i.Inputs = []Op{r}
	c.Instruction(i)
}

// slack is the address disp+wireSlack(base)(index*scale), wireSlack from go_asm.h.
func slack(disp int, base, index Register, scale uint8) Mem {
	return Mem{Symbol: Symbol{Name: "const_wireSlack"}, Disp: disp, Base: base, Index: index, Scale: scale}
}

// Labels: each function's own (no prefixes: a label belongs to the function it is in).
func l(name string) LabelRef { return LabelRef(name) }

// invalid is wireInvalid.
const invalid = 0x7fffffffffffffff

// frame is where the writers' kernels put their result: Latin-1 from start, UTF-16 from wide (locals of the
// function), the result in AX, then a jump to done.
type frame struct{ start, wide Mem }

// writer is wireString* or wireStrings* of a tier: setup loads the constants its kernel keeps in registers, kernel
// converts the text at SI (BX bytes, BX > 0) to Latin-1 at DI (equal to the local start) or to UTF-16 at the
// local wide, the result in AX (bytes, ^units or wireInvalid), then jumps to done.
type writer struct {
	setup  func()
	kernel func(frame)
	short  func(frame) // the list kernel's path for short ASCII text (see above many), else a jump to long or cold
	cold   func(frame) // what short jumps to that is not long, placed after the loop (nil: nothing)
	vex    bool        // VZEROUPPER before returning
}

// single is wireString* of a tier: func(dst unsafe.Pointer, i int, src unsafe.Pointer, n int) int.
func single(name string, w writer) {
	function(name, "func(dst unsafe.Pointer, i int, src unsafe.Pointer, n int) int")
	f := frame{wide: c.AllocLocal(8), start: c.AllocLocal(8)}
	c.MOVQ(param("src"), RSI)
	c.MOVQ(param("i"), RDX)
	c.ADDQ(RDX, RSI) // the text from i on
	c.MOVQ(param("n"), RBX)
	c.SUBQ(RDX, RBX) // its length
	c.MOVQ(param("dst"), RDI)
	c.LEAQ(at(RDI, RDX, 2), RAX)
	c.MOVQ(RAX, f.wide) // UTF-16 goes to dst+2*i
	c.ADDQ(RDX, RDI)    // Latin-1 to dst+i
	c.MOVQ(RDI, f.start)
	w.setup()
	w.kernel(f)

	c.Label("done")
	c.Store(RAX, c.ReturnIndex(0))
	if w.vex {
		c.VZEROUPPER()
	}
	c.RET()
}

// many is wireStrings* of a tier: func(dst unsafe.Pointer, room int, strs unsafe.Pointer, count int, ptrs bool)
// (done, written int). It writes strings from strs (count of them: strings, or pointers to them when ptrs, nil the
// null string) at dst (room bytes), each with its one-byte header, while each has room for 1+2*len+wireSlack bytes
// and is WTF-8 of fewer than wireMaxText (64) bytes, so of at most 63 chars; it returns how many it wrote and the
// bytes they took.
func many(name string, w writer) {
	function(name, "func(dst unsafe.Pointer, room int, strs unsafe.Pointer, count int, ptrs bool) (done, written int)")
	f := frame{wide: c.AllocLocal(8), start: c.AllocLocal(8)}
	out, end := c.AllocLocal(8), c.AllocLocal(8) // where the next string goes, the end of the room
	c.MOVQ(param("dst"), RDI)
	c.MOVQ(RDI, out)
	c.MOVQ(RDI, RAX)
	c.ADDQ(param("room"), RAX)
	c.MOVQ(RAX, end)
	c.XORQ(R14, R14) // the strings done (R14 is free in ABI0)
	w.setup()

	// The loop: a string of []string, not empty, short ASCII text falls through to the jump back (one branch taken a
	// string); pointers, nil, empty and other text are placed after it. DI is out at the top of the loop (out is
	// stored too, for the kernel's paths and finish), so a string does not wait for the store of the one before.
	align(32)
	c.Label("next")
	c.MOVQ(R14, RCX)
	c.CMPQ(RCX, param("count"))
	c.JAE(l("finish"))
	c.MOVQ(param("strs"), R8)
	c.CMPB(param("ptrs"), U8(0))
	c.JNE(l("pointer"))
	c.SHLQ(U8(4), RCX)
	c.ADDQ(RCX, R8)

	c.Label("header")
	c.MOVQ(Mem{Base: R8}, RSI)
	c.MOVQ(Mem{Base: R8, Disp: 8}, RBX)
	c.TESTQ(RBX, RBX)
	c.JZ(l("empty"))
	cmpConst(RBX, "wireMaxText", 64)
	c.JAE(l("finish"))
	c.LEAQ(slack(1, RDI, RBX, 2), RAX)
	c.CMPQ(RAX, end)
	c.JA(l("finish"))
	w.short(f)
	c.Comment("Stored with its header.")
	c.LEAQ(Mem{Base: RDI, Disp: 1, Index: RBX, Scale: 1}, RDI)
	c.MOVQ(RDI, out)
	c.INCQ(R14)
	c.JMP(l("next"))

	c.Label("pointer")
	c.MOVQ(at(R8, RCX, 8), R8)
	c.TESTQ(R8, R8)
	c.JNZ(l("header"))
	c.Comment("nil: the null string.")
	c.LEAQ(Mem{Base: RDI, Disp: 1}, RAX)
	c.CMPQ(RAX, end)
	c.JA(l("finish"))
	c.MOVB(U8(0), Mem{Base: RDI})
	c.MOVQ(RAX, RDI)
	c.MOVQ(RAX, out)
	c.INCQ(R14)
	c.JMP(l("next"))

	c.Label("empty")
	c.LEAQ(Mem{Base: RDI, Disp: 1}, RAX)
	c.CMPQ(RAX, end)
	c.JA(l("finish"))
	c.MOVB(U8(1), Mem{Base: RDI})
	c.MOVQ(RAX, RDI)
	c.MOVQ(RAX, out)
	c.INCQ(R14)
	c.JMP(l("next"))

	if w.cold != nil {
		w.cold(f)
	}

	c.Label("long")
	c.INCQ(RDI)
	c.MOVQ(RDI, f.start)
	c.MOVQ(RDI, f.wide)
	w.kernel(f)

	c.Label("done")
	c.MOVQ(out, RDI)
	c.MOVQ(U64(invalid), RCX)
	c.CMPQ(RAX, RCX)
	c.JEQ(l("finish"))
	c.TESTQ(RAX, RAX)
	c.JS(l("store16"))
	c.Comment("Latin-1: the header 2n+1.")
	c.LEAL(Mem{Base: RAX, Index: RAX, Scale: 1, Disp: 1}, ECX)
	c.MOVB(CL, Mem{Base: RDI})
	c.LEAQ(Mem{Base: RDI, Disp: 1, Index: RAX, Scale: 1}, RDI)
	c.MOVQ(RDI, out)
	c.INCQ(R14)
	c.JMP(l("next"))

	c.Label("store16")
	c.Comment("UTF-16: the header 2n.")
	c.NOTQ(RAX)
	c.LEAL(Mem{Base: RAX, Index: RAX, Scale: 1}, ECX)
	c.MOVB(CL, Mem{Base: RDI})
	c.LEAQ(Mem{Base: RDI, Disp: 1, Index: RAX, Scale: 2}, RDI)
	c.MOVQ(RDI, out)
	c.INCQ(R14)
	c.JMP(l("next"))

	c.Label("finish")
	c.Store(R14, c.Return("done"))
	c.MOVQ(out, RAX)
	c.SUBQ(param("dst"), RAX)
	c.Store(RAX, c.Return("written"))
	if w.vex {
		c.VZEROUPPER()
	}
	c.RET()
}

// The list kernels' path for short ASCII text (writer.short): the bytes from the one before the text (cleared) to
// the text's end, read as one block with zeros past them; the header, 2n+1, goes in the first byte, and the block
// is stored where the string goes: the header and the text in one store. The header stored as a byte of its own
// ahead of the text cost about 5% on short strings on Zen 4.

// bytesOf splits the 32 bits of m into its bytes, R9 (the lowest) to R12.
func bytesOf(m GPPhysical) {
	c.MOVL(m, R9L)
	c.ANDL(U32(0xff), R9L)
	c.MOVL(m, R10L)
	c.SHRL(U8(8), R10L)
	c.ANDL(U32(0xff), R10L)
	c.MOVL(m, R11L)
	c.SHRL(U8(16), R11L)
	c.ANDL(U32(0xff), R11L)
	c.MOVL(m, R12L)
	c.SHRL(U8(24), R12L)
}

func cpu() {
	function("cpuid", "func(eaxArg, ecxArg uint32) (eax, ebx, ecx, edx uint32)")
	c.MOVL(param("eaxArg"), EAX)
	c.MOVL(param("ecxArg"), ECX)
	c.CPUID()
	c.Store(EAX, c.Return("eax"))
	c.Store(EBX, c.Return("ebx"))
	c.Store(ECX, c.Return("ecx"))
	c.Store(EDX, c.Return("edx"))
	c.RET()

	function("xgetbv", "func() (eax, edx uint32)")
	c.MOVL(U32(0), ECX)
	c.XGETBV()
	c.Store(EAX, c.Return("eax"))
	c.Store(EDX, c.Return("edx"))
	c.RET()
}
