package bench

import (
	"strconv"
	"unsafe"

	"github.com/JasonXuDeveloper/Nino/runtimes/go/bench/benchpb"
	nino "github.com/JasonXuDeveloper/Nino/runtimes/go/v4"
)

// The string cases, the same data as the Rust and TypeScript benchmarks' (runtimes/rust/bench, Program.cs):
// 1,000 short ASCII strings, and 1,000 strings of three kinds in turn: ASCII, Latin-1 (one byte a char in Nino)
// and other text (UTF-16 in Nino; Cyrillic, CJK and a char outside the BMP, a surrogate pair).

// ASCIIStrings returns "string 0" to "string 999".
func ASCIIStrings() []string {
	v := make([]string, 1000)
	for i := range v {
		v[i] = "string " + strconv.Itoa(i)
	}
	return v
}

// MixedStrings returns 1,000 strings: "string i", "crème brûlée i" and "строка 字符串 🎉 i" in turn.
func MixedStrings() []string {
	v := make([]string, 1000)
	for i := range v {
		switch i % 3 {
		case 0:
			v[i] = "string " + strconv.Itoa(i)
		case 1:
			v[i] = "crème brûlée " + strconv.Itoa(i)
		default:
			v[i] = "строка 字符串 🎉 " + strconv.Itoa(i)
		}
	}
	return v
}

// ReadStrings and WriteStrings are a string[] (List<string> writes the same bytes).
func ReadStrings(r *nino.Reader) []string { return nino.ReadStrings(r) }

func WriteStrings(w *nino.Writer, v []string) { nino.WriteStrings(w, v) }

// ReadUtf8Strings and WriteUtf8Strings are a [NinoUtf8] string[]: each element as UTF-8 (wire format §4.2), read
// with the same checks (strict UTF-8, the char count) as a [NinoUtf8] member.
func ReadUtf8Strings(r *nino.Reader) []string {
	n := r.Length(1)
	if n < 0 {
		return nil
	}
	var e string
	v := make([]string, 0, r.SliceCap(n, int(unsafe.Sizeof(e))))
	for range n {
		e = r.UTF8()
		if r.Failed() {
			return nil
		}
		v = append(v, e)
	}
	return v
}

func WriteUtf8Strings(w *nino.Writer, v []string) {
	if v == nil {
		w.Null()
		return
	}
	w.Length(len(v))
	for _, e := range v {
		w.UTF8(e)
	}
}

// The values as Protocol Buffers messages (benchpb/bench.proto).

func protoSimpleClass(v *SimpleClass) *benchpb.SimpleClass {
	p := &benchpb.SimpleClass{
		Id: v.ID, Tag: v.Tag, Guid: v.Guid[:], CreateTime: uint64(v.CreateTime), Name: v.Name, Desc: v.Desc,
		Numbers: v.Numbers, Map1: v.Map1, Map2: v.Map2,
	}
	for _, d := range v.Dates {
		p.Dates = append(p.Dates, uint64(d))
	}
	return p
}

func protoSimpleClasses(v []*SimpleClass) *benchpb.SimpleClasses {
	p := &benchpb.SimpleClasses{}
	for _, x := range v {
		p.Values = append(p.Values, protoSimpleClass(x))
	}
	return p
}
