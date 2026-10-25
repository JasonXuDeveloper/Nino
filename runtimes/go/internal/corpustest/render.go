package corpustest

import (
	"encoding/hex"
	"encoding/json"
	"fmt"
	"math"
	"math/big"
	"reflect"
	"regexp"
	"strconv"
	"strings"
	"unicode/utf8"

	nino "github.com/JasonXuDeveloper/Nino/runtimes/go/v4"
)

// Render converts a decoded value to the corpus's lossless JSON (conformance/README.md, Json.cs), as the values
// encoding/json decodes with UseNumber: nil, bool, string, json.Number, []any, map[string]any. Test code: it uses
// reflection, which the runtime and the codecs do not.
//
// A struct is a class or struct of the corpus, named by its NinoTypeName method (generated code) or else
// "Conformance." and its Go name (internal/sample), except for the generic pairs and grids both packages declare:
// nino.Entry, Tuple2 or NinoTuple2 render as an array of their items, Grid or NinoGrid as {lengths, items}.
func Render(v any) any { return renderValue(reflect.ValueOf(v)) }

var (
	tBytes    = reflect.TypeOf([]byte(nil))
	tSBytes   = reflect.TypeOf([]int8(nil))
	tBig      = reflect.TypeOf((*big.Int)(nil))
	tVersion  = reflect.TypeOf((*nino.Version)(nil))
	tString   = reflect.TypeOf((*string)(nil))
	tHalf     = reflect.TypeOf(nino.Half(0))
	tInt128   = reflect.TypeOf(nino.Int128{})
	tUInt128  = reflect.TypeOf(nino.UInt128{})
	tDecimal  = reflect.TypeOf(nino.Decimal{})
	tGuid     = reflect.TypeOf(nino.Guid{})
	tDateTime = reflect.TypeOf(nino.DateTime(0))
	tOffset   = reflect.TypeOf(nino.DateTimeOffset{})
)

var safeInteger = big.NewInt(1 << 53)

func integer(b *big.Int) any {
	if new(big.Int).Abs(b).Cmp(safeInteger) <= 0 {
		return json.Number(b.String())
	}
	return b.String()
}

func renderValue(v reflect.Value) any {
	if !v.IsValid() {
		return nil
	}
	t := v.Type()
	switch t {
	case tBytes:
		if v.IsNil() {
			return nil
		}
		return hex.EncodeToString(v.Bytes())
	case tSBytes:
		// The corpus writes sbyte[] as hex too (Json.cs's `case byte[]` matches it: the CLR lets an sbyte[] pass
		// for a byte[]).
		if v.IsNil() {
			return nil
		}
		b := make([]byte, v.Len())
		for i := range b {
			b[i] = byte(v.Index(i).Int())
		}
		return hex.EncodeToString(b)
	case tBig:
		if v.IsNil() {
			return "0"
		}
		return v.Interface().(*big.Int).String()
	case tVersion:
		if v.IsNil() {
			return nil
		}
		ver := v.Interface().(*nino.Version)
		return []any{num(int64(ver.Major)), num(int64(ver.Minor)), num(int64(ver.Build)), num(int64(ver.Revision))}
	case tString:
		if v.IsNil() {
			return nil
		}
		return renderString(v.Elem().String())
	case tHalf:
		return fmt.Sprintf("0x%04x", v.Uint())
	case tInt128:
		return integer(v.Interface().(nino.Int128).Big())
	case tUInt128:
		return integer(v.Interface().(nino.UInt128).Big())
	case tDecimal:
		return v.Interface().(nino.Decimal).String()
	case tGuid:
		return v.Interface().(nino.Guid).String()
	case tDateTime:
		d := v.Interface().(nino.DateTime)
		kind := [...]string{"Unspecified", "Utc", "Local", "3"}[d.Kind()&3]
		return map[string]any{"ticks": integer(big.NewInt(d.Ticks())), "kind": kind}
	case tOffset:
		o := v.Interface().(nino.DateTimeOffset)
		return map[string]any{"utcTicks": integer(big.NewInt(o.UtcTicks)), "offsetMinutes": num(int64(o.OffsetMinutes))}
	}
	switch t.Kind() {
	case reflect.Bool:
		return v.Bool()
	case reflect.Int8, reflect.Int16, reflect.Int32, reflect.Int64:
		return integer(big.NewInt(v.Int()))
	case reflect.Uint8, reflect.Uint16, reflect.Uint32, reflect.Uint64:
		return integer(new(big.Int).SetUint64(v.Uint()))
	case reflect.Float32:
		// Not through v.Float(): widening a signaling NaN to float64 and back quiets it.
		return fmt.Sprintf("0x%08x", math.Float32bits(v.Interface().(float32)))
	case reflect.Float64:
		return fmt.Sprintf("0x%016x", math.Float64bits(v.Float()))
	case reflect.String:
		return renderString(v.String())
	case reflect.Interface, reflect.Pointer:
		if v.IsNil() {
			return nil
		}
		return renderValue(v.Elem())
	case reflect.Slice:
		if v.IsNil() {
			return nil
		}
		out := make([]any, v.Len())
		for i := range out {
			out[i] = renderValue(v.Index(i))
		}
		return out
	case reflect.Struct:
		name := t.Name()
		switch {
		case strings.HasPrefix(name, "Entry["), strings.HasPrefix(name, "Tuple2["), strings.HasPrefix(name, "NinoTuple2["):
			out := make([]any, t.NumField())
			for i := range out {
				out[i] = renderValue(v.Field(i))
			}
			return out
		case strings.HasPrefix(name, "Grid["), strings.HasPrefix(name, "NinoGrid["):
			lengths := v.FieldByName("Lengths")
			return map[string]any{
				"lengths": []any{num(lengths.Index(0).Int()), num(lengths.Index(1).Int())},
				"items":   renderValue(v.FieldByName("Items")),
			}
		}
		out := map[string]any{"$type": dotnetName(v)}
		for i := 0; i < t.NumField(); i++ {
			f := v.Field(i)
			if f.Type() == tBytes && !f.IsNil() && !declaredAsArray(v, t.Field(i).Name) {
				// A List<byte> is a []byte too: the corpus writes it as a list of numbers.
				items := make([]any, f.Len())
				for j := range items {
					items[j] = num(int64(f.Index(j).Uint()))
				}
				out[t.Field(i).Name] = items
				continue
			}
			out[t.Field(i).Name] = renderValue(f)
		}
		return out
	}
	panic("no JSON for " + t.String())
}

func num(n int64) json.Number { return json.Number(strconv.FormatInt(n, 10)) }

// declaredAsArray reports whether the member is a byte[] or sbyte[] in .NET, from the type's NinoLayout. A type
// without one (a hand-written type that gives List<byte> a type of its own) declares every []byte an array.
func declaredAsArray(v reflect.Value, member string) bool {
	typed, ok := v.Interface().(interface{ NinoLayout() string })
	if !ok {
		return true
	}
	layout := typed.NinoLayout()
	m := regexp.MustCompile(`(?:members=|;)` + regexp.QuoteMeta(member) + `:([^:]+):`).FindStringSubmatch(layout)
	return m != nil && (m[1] == "byte[]" || m[1] == "sbyte[]")
}

// dotnetName is the .NET name of a class or struct: its NinoTypeName, or "Conformance." and its Go name.
func dotnetName(v reflect.Value) string {
	if named, ok := v.Interface().(interface{ NinoTypeName() string }); ok {
		return named.NinoTypeName()
	}
	return "Conformance." + v.Type().Name()
}

// renderString renders a string read as WTF-8: a JSON string, or {"utf16": [units]} when it holds any surrogate
// (a lone one, or a pair: a char past U+FFFF).
func renderString(s string) any {
	units := wtf8Units(s)
	for _, u := range units {
		if u >= 0xD800 && u < 0xE000 {
			out := make([]any, len(units))
			for i, u := range units {
				out[i] = num(int64(u))
			}
			return map[string]any{"utf16": out}
		}
	}
	return s
}

// wtf8Units decodes WTF-8 (UTF-8 that may encode lone surrogates) to UTF-16 code units.
func wtf8Units(s string) []uint16 {
	var units []uint16
	for i := 0; i < len(s); {
		if s[i] == 0xED && i+2 < len(s) && s[i+1] >= 0xA0 {
			units = append(units, uint16(s[i]&0x0F)<<12|uint16(s[i+1]&0x3F)<<6|uint16(s[i+2]&0x3F))
			i += 3
			continue
		}
		r, n := utf8.DecodeRuneInString(s[i:])
		if r >= 0x10000 {
			r -= 0x10000
			units = append(units, uint16(0xD800+r>>10), uint16(0xDC00+r&0x3FF))
		} else {
			units = append(units, uint16(r))
		}
		i += n
	}
	return units
}
