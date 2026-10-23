package nino

import (
	"fmt"
	"strconv"
	"strings"
)

// Layout strings: what a type's NinoLayout method returns, in the grammar of the [NinoTypeLayout] metadata .NET
// publishes for every [NinoType] (wire-format spec §8):
//
//	format=Keyed; header=NullFlag; trackReferences=false; raw=false; typeId=0x8F865207; deferrable=true; members=Health:int:raw(4):key=0x99A16761;...
//
// The parser takes the parts in any order and any subset of them ("format=VersionTolerant" alone declares the
// format and leaves the rest to the defaults). "deferrable=true|false" (spec §7.10.1, §8.1) says whether .NET
// defers the type's bodies; false for records and types built through a constructor. Layouts from an older .NET
// generator leave it out, and the type is then taken to be deferrable.

// Formats and headers, as the layout names them.
const (
	formatPositional = iota
	formatTolerant
	formatKeyed
)

const (
	headerAuto = iota // not declared: NullFlag for a pointer, None for a value
	headerNone
	headerNullFlag
	headerTypeID
)

type tristate uint8

const (
	unset tristate = iota
	yes
	no
)

// layoutDecl is a parsed layout string.
type layoutDecl struct {
	format     int
	hasFormat  bool
	header     int
	track      tristate
	raw        tristate
	deferrable tristate
	typeID     int32
	hasTypeID  bool // typeId given and not 0 (0 is published for generic type definitions)
	hasMembers bool
	members    []layoutMember
	rawEnc     *encoding // members of a raw type: one raw encoding
}

type layoutMember struct {
	name    string
	typ     string
	enc     *encoding
	key     int32
	hasKey  bool
	formers []int32
}

// encoding is a parsed member encoding (spec §8.1).
type encoding struct {
	kind   string      // raw, string, utf8, object, type, nullable, seq, map, pair, tuple, ...
	size   int         // raw(n): n; -1 without a size
	fields []rawField  // raw(n){...}
	args   []*encoding // nullable, seq, map, pair, tuple, grid (its second argument)
	text   string      // type(T), formatter(F): T or F; the whole encoding otherwise
}

type rawField struct {
	name string
	typ  string
	off  int
	enc  *encoding
}

func (e *encoding) String() string { return e.text }

// parseLayout parses a layout string.
func parseLayout(s string) (*layoutDecl, error) {
	d := &layoutDecl{}
	rest := strings.TrimSpace(s)
	for rest != "" {
		var part string
		if strings.HasPrefix(rest, "members=") {
			part, rest = rest, ""
		} else if i := strings.IndexByte(rest, ';'); i >= 0 {
			part, rest = strings.TrimSpace(rest[:i]), strings.TrimSpace(rest[i+1:])
		} else {
			part, rest = rest, ""
		}
		if part == "" {
			continue
		}
		key, value, ok := strings.Cut(part, "=")
		if !ok {
			return nil, layoutErr("expected key=value, found %q", part)
		}
		switch key {
		case "format":
			d.hasFormat = true
			switch value {
			case "Positional":
				d.format = formatPositional
			case "VersionTolerant":
				d.format = formatTolerant
			case "Keyed":
				d.format = formatKeyed
			default:
				return nil, layoutErr("unknown format %q", value)
			}
		case "header":
			switch value {
			case "None":
				d.header = headerNone
			case "NullFlag":
				d.header = headerNullFlag
			case "TypeId":
				d.header = headerTypeID
			default:
				return nil, layoutErr("unknown header %q", value)
			}
		case "trackReferences", "raw", "deferrable":
			var t tristate
			switch value {
			case "true":
				t = yes
			case "false":
				t = no
			default:
				return nil, layoutErr("%s=%q is not true or false", key, value)
			}
			switch key {
			case "trackReferences":
				d.track = t
			case "raw":
				d.raw = t
			default:
				d.deferrable = t
			}
		case "typeId":
			id, err := parseHex32(value)
			if err != nil {
				return nil, err
			}
			d.typeID, d.hasTypeID = id, id != 0
		case "members":
			d.hasMembers = true
			if err := d.parseMembers(value); err != nil {
				return nil, err
			}
		default:
			return nil, layoutErr("unknown part %q", key)
		}
	}
	return d, nil
}

func (d *layoutDecl) parseMembers(s string) error {
	if s == "" {
		return nil
	}
	if d.raw == yes || (strings.HasPrefix(s, "raw") && len(splitTop(s, ':')) == 1) {
		// A raw type: one raw encoding.
		e, err := parseEncoding(s)
		if err != nil {
			return err
		}
		if e.kind != "raw" {
			return layoutErr("the members of a raw type are %q, not a raw encoding", s)
		}
		d.rawEnc = e
		if d.raw == unset {
			d.raw = yes
		}
		return nil
	}
	for _, m := range splitTop(s, ';') {
		if strings.HasPrefix(m, "[elements]") {
			return layoutErr("[NinoType] collections (%q) are not supported by struct tags", m)
		}
		parts := splitTop(m, ':')
		if len(parts) < 3 {
			return layoutErr("member %q: expected name:type:encoding", m)
		}
		lm := layoutMember{name: parts[0], typ: parts[1]}
		e, err := parseEncoding(parts[2])
		if err != nil {
			return err
		}
		lm.enc = e
		for _, p := range parts[3:] {
			k, v, _ := strings.Cut(p, "=")
			switch k {
			case "key":
				key, err := parseHex32(v)
				if err != nil {
					return err
				}
				lm.key, lm.hasKey = key, true
			case "former":
				for _, h := range strings.Split(v, ",") {
					key, err := parseHex32(h)
					if err != nil {
						return err
					}
					lm.formers = append(lm.formers, key)
				}
			default:
				return layoutErr("member %q: unknown part %q", lm.name, p)
			}
		}
		d.members = append(d.members, lm)
	}
	return nil
}

// parseEncoding parses one encoding.
func parseEncoding(s string) (*encoding, error) {
	e := &encoding{size: -1, text: s}
	name := s
	var args, fields string
	if i := strings.IndexAny(s, "({"); i >= 0 {
		name = s[:i]
		rest := s[i:]
		if rest[0] == '(' {
			end := matchClose(rest, '(', ')')
			if end < 0 {
				return nil, layoutErr("unbalanced encoding %q", s)
			}
			args, rest = rest[1:end], rest[end+1:]
		}
		if rest != "" {
			if rest[0] != '{' || matchClose(rest, '{', '}') != len(rest)-1 {
				return nil, layoutErr("unbalanced encoding %q", s)
			}
			fields = rest[1 : len(rest)-1]
		}
	}
	e.kind = name
	switch name {
	case "raw":
		if args != "" {
			n, err := strconv.Atoi(args)
			if err != nil || n < 0 {
				return nil, layoutErr("bad size in %q", s)
			}
			e.size = n
		}
		if fields != "" {
			for _, f := range splitTop(fields, ',') {
				parts := splitTop(f, ':')
				if len(parts) != 3 {
					return nil, layoutErr("raw field %q: expected name:type@offset:encoding", f)
				}
				at := strings.LastIndexByte(parts[1], '@')
				if at < 0 {
					return nil, layoutErr("raw field %q has no offset", f)
				}
				off, err := strconv.Atoi(parts[1][at+1:])
				if err != nil || off < 0 {
					return nil, layoutErr("raw field %q: bad offset", f)
				}
				fe, err := parseEncoding(parts[2])
				if err != nil {
					return nil, err
				}
				e.fields = append(e.fields, rawField{name: parts[0], typ: parts[1][:at], off: off, enc: fe})
			}
		}
	case "type", "formatter":
		e.text = args
	case "nullable", "seq", "map", "pair", "tuple", "group", "grid":
		as := splitTop(args, ',')
		if name == "grid" && len(as) == 2 {
			e.size, _ = strconv.Atoi(strings.TrimSpace(as[0])) // the rank
			as = as[1:]
		}
		for _, a := range as {
			ae, err := parseEncoding(strings.TrimSpace(a))
			if err != nil {
				return nil, err
			}
			e.args = append(e.args, ae)
		}
	case "string", "utf8", "object", "datetimeoffset", "bigint", "version", "bits", "timezone":
	default:
		return nil, layoutErr("unknown encoding %q", s)
	}
	return e, nil
}

// matchClose returns the index of the bracket closing s[0], or -1.
func matchClose(s string, open, close byte) int {
	depth := 0
	for i := 0; i < len(s); i++ {
		switch s[i] {
		case '(', '{', '<', '[':
			depth++
		case ')', '}', '>', ']':
			depth--
			if depth == 0 {
				if s[i] != close {
					return -1
				}
				return i
			}
		}
	}
	return -1
}

// splitTop splits s at sep outside brackets (spec §8.2).
func splitTop(s string, sep byte) []string {
	var out []string
	depth, start := 0, 0
	for i := 0; i < len(s); i++ {
		switch c := s[i]; {
		case c == '(' || c == '{' || c == '<' || c == '[':
			depth++
		case c == ')' || c == '}' || c == '>' || c == ']':
			depth--
		case c == sep && depth == 0:
			out = append(out, s[start:i])
			start = i + 1
		}
	}
	return append(out, s[start:])
}

func parseHex32(s string) (int32, error) {
	if !strings.HasPrefix(s, "0x") && !strings.HasPrefix(s, "0X") {
		return 0, layoutErr("%q is not a 0x hex number", s)
	}
	v, err := strconv.ParseUint(s[2:], 16, 32)
	if err != nil {
		return 0, layoutErr("%q is not a 32-bit hex number", s)
	}
	return int32(uint32(v)), nil
}

type layoutError string

func (e layoutError) Error() string { return "layout: " + string(e) }

func layoutErr(format string, args ...any) error {
	return layoutError(fmt.Sprintf(format, args...))
}
