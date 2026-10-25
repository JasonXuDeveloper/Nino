package nino

import (
	"cmp"
	"fmt"
	"reflect"
	"slices"
	"strconv"
	"strings"
)

// Struct tags (tagged.go): struct plans, from a struct's tags and its declared layout.

// tagInfo is a parsed nino tag.
type tagInfo struct {
	present  bool
	skip     bool
	name     string // the name (keyed) or position (positional) part
	position int    // -1 unless the name part is a number
	utf8     bool
	formers  []string
}

func parseTag(t reflect.Type, f reflect.StructField, path string) (tagInfo, error) {
	s, ok := f.Tag.Lookup("nino")
	ti := tagInfo{present: ok, position: -1}
	if !ok {
		return ti, nil
	}
	if s == "-" {
		ti.skip = true
		return ti, nil
	}
	parts := strings.Split(s, ",")
	ti.name = strings.TrimSpace(parts[0])
	if ti.name != "" {
		if c := ti.name[0]; c >= '0' && c <= '9' {
			n, err := strconv.Atoi(ti.name)
			if err != nil || n < 0 || n > 1<<20 {
				return ti, &TagError{Type: t, Field: path, Detail: fmt.Sprintf("bad position %q", ti.name)}
			}
			ti.position = n
		}
	}
	for _, opt := range parts[1:] {
		opt = strings.TrimSpace(opt)
		switch {
		case opt == "utf8":
			ti.utf8 = true
		case strings.HasPrefix(opt, "former="):
			name := strings.TrimPrefix(opt, "former=")
			if name == "" {
				return ti, &TagError{Type: t, Field: path, Detail: "empty former name"}
			}
			ti.formers = append(ti.formers, name)
		case opt == "omitempty":
			return ti, &TagError{Type: t, Field: path, Detail: "omitempty: the wire format has no absent member (positional and version-tolerant bodies hold every member in order, and keyed writers must write an entry for every member, spec §7.6)"}
		case opt == "":
		default:
			return ti, &TagError{Type: t, Field: path, Detail: fmt.Sprintf("unknown option %q", opt)}
		}
	}
	return ti, nil
}

type rawFieldInfo struct {
	sf   reflect.StructField
	path string
	idx  []int
	tag  tagInfo
}

// collectFields lists the fields of t that take part, flattening untagged embedded structs as encoding/json does.
func collectFields(top, t reflect.Type, prefix string, index []int, out *[]rawFieldInfo) error {
	for i := 0; i < t.NumField(); i++ {
		sf := t.Field(i)
		path := sf.Name
		if prefix != "" {
			path = prefix + "." + sf.Name
		}
		idx := append(append([]int(nil), index...), i)
		ti, err := parseTag(top, sf, path)
		if err != nil {
			return err
		}
		if ti.skip {
			continue
		}
		if sf.Anonymous && !ti.present {
			ft := sf.Type
			if ft.Kind() == reflect.Struct && !isEntryType(ft) && !isNinoValue(ft) {
				if _, ok := layoutOf(ft); ok {
					return &TagError{Type: top, Field: path, Detail: "an embedded struct with its own layout: tag it (a member) or flatten it by removing its NinoLayout"}
				}
				if err := collectFields(top, ft, path, idx, out); err != nil {
					return err
				}
				continue
			}
			if ft.Kind() == reflect.Pointer && ft.Elem().Kind() == reflect.Struct {
				return &TagError{Type: top, Field: path, Detail: "an embedded pointer cannot be flattened: tag it or mark it nino:\"-\""}
			}
		}
		if !sf.IsExported() {
			if ti.present {
				return &TagError{Type: top, Field: path, Detail: "unexported fields cannot be read or written"}
			}
			continue
		}
		*out = append(*out, rawFieldInfo{sf: sf, path: path, idx: idx, tag: ti})
	}
	return nil
}

// layoutOf calls the type's NinoLayout, on a value or a pointer.
func layoutOf(t reflect.Type) (string, bool) {
	if t.Implements(typeLayouted) {
		return reflect.Zero(t).Interface().(Layouted).NinoLayout(), true
	}
	if reflect.PointerTo(t).Implements(typeLayouted) {
		return reflect.New(t).Interface().(Layouted).NinoLayout(), true
	}
	return "", false
}

func typedID(t reflect.Type) (int32, bool) {
	if t.Implements(typeTyped) && t.Kind() != reflect.Pointer {
		return reflect.Zero(t).Interface().(Typed).NinoTypeID(), true
	}
	if reflect.PointerTo(t).Implements(typeTyped) {
		return reflect.New(t).Interface().(Typed).NinoTypeID(), true
	}
	return 0, false
}

func isEntryType(t reflect.Type) bool {
	return t.PkgPath() == ninoPkg && strings.HasPrefix(t.Name(), "Entry[")
}

// plan returns the plan of the struct type t (built at most once per build; in progress when t holds itself).
func (b *builder) plan(t reflect.Type) (*plan, error) {
	if p, ok := b.plans[t]; ok {
		return p, nil
	}
	if p, ok := b.cache.plans[t]; ok {
		return p, nil
	}
	p := &plan{typ: t, deferrable: true}
	b.plans[t] = p
	if err := b.buildPlan(p); err != nil {
		delete(b.plans, t)
		return nil, err
	}
	return p, nil
}

// buildPlan fills the plan of p.typ: its declaration (layout, header, type id), its format, the order of its fields
// and each field's codec, checked against the layout's members.
func (b *builder) buildPlan(p *plan) error {
	t := p.typ
	d, err := declare(p)
	if err != nil {
		return err
	}
	var infos []rawFieldInfo
	if err := collectFields(t, t, "", nil, &infos); err != nil {
		return err
	}
	positions, err := chooseFormat(p, d, infos)
	if err != nil {
		return err
	}
	order, pks, err := fieldOrder(p, d, infos, positions)
	if err != nil {
		return err
	}
	if err := checkMemberCount(p, d, len(infos)); err != nil {
		return err
	}
	p.fields = make([]field, len(infos))
	for i, fi := range infos {
		f := &p.fields[order[i]]
		f.name, f.index, f.member = fi.path, fi.idx, -1
		f.key, f.formers = pks[i].key, pks[i].formers
		if p.format == formatKeyed {
			f.member = pks[i].member
		} else if d.hasMembers && d.rawEnc == nil {
			f.member = order[i]
		}
		if err := b.fieldCodec(p, d, fi, f); err != nil {
			return err
		}
	}
	if p.format == formatKeyed && len(p.fields) > 16 {
		p.keys = map[int32]int{}
		for i := range p.fields {
			p.keys[p.fields[i].key] = i
			for _, k := range p.fields[i].formers {
				p.keys[k] = i
			}
		}
	}
	return b.rawLayout(p, d)
}

// declare takes the plan's header, deferrability and type id from the type's layout (if it declares one) and its
// NinoTypeID, and returns the layout (empty when there is none).
func declare(p *plan) (*layoutDecl, error) {
	t := p.typ
	if s, ok := layoutOf(t); ok {
		d, err := parseLayout(s)
		if err != nil {
			return nil, &TagError{Type: t, Detail: err.Error()}
		}
		p.layout = d
	}
	d := p.layout
	if d == nil {
		d = &layoutDecl{}
	}
	p.entry = isEntryType(t)
	if d.track == yes {
		return nil, typeErr(t, "trackReferences=true: reference tracking is not supported by struct tags (use generated code)")
	}
	p.header = d.header
	if d.deferrable == no {
		p.deferrable = false
	}
	if d.hasTypeID {
		p.id, p.hasID = d.typeID, true
	}
	if id, ok := typedID(t); ok {
		if p.hasID && id != p.id {
			return nil, typeErr(t, "NinoTypeID returns 0x%08X but the layout's typeId is 0x%08X", uint32(id), uint32(p.id))
		}
		p.id, p.hasID = id, true
	}
	return d, nil
}

// chooseFormat sets the plan's format (the layout's, else keyed when the tags take names) and checks the tags
// agree with it. It returns the number of fields whose tag takes a position.
func chooseFormat(p *plan, d *layoutDecl, infos []rawFieldInfo) (int, error) {
	t := p.typ
	positions, names := 0, 0
	for _, fi := range infos {
		if fi.tag.name == "" {
			continue
		}
		if fi.tag.position >= 0 {
			positions++
		} else {
			names++
		}
	}
	if positions > 0 && names > 0 {
		return 0, typeErr(t, "positions and names mixed in the nino tags: positional and version-tolerant types take positions, keyed types names")
	}
	p.format = formatPositional
	if d.hasFormat {
		p.format = d.format
	} else if names > 0 {
		p.format = formatKeyed
	}
	if p.format == formatKeyed && positions > 0 {
		return 0, typeErr(t, "a keyed type takes member names in its tags, not positions")
	}
	if p.format != formatKeyed && names > 0 {
		return 0, typeErr(t, "a positional or version-tolerant type takes positions in its tags (nino:\"0\"), not names")
	}
	for _, fi := range infos {
		if len(fi.tag.formers) > 0 && p.format != formatKeyed {
			return 0, &TagError{Type: t, Field: fi.path, Detail: "former names apply to keyed types only"}
		}
	}
	if d.raw == yes && p.format != formatPositional {
		return 0, typeErr(t, "raw=true needs format=Positional")
	}
	return positions, nil
}

// fieldOrder returns the member index of each field (order[i] for infos[i]) and, for a keyed type, its keys: the
// keyed order, else the tags' positions, else the fields' order.
func fieldOrder(p *plan, d *layoutDecl, infos []rawFieldInfo, positions int) ([]int, []pendingKey, error) {
	t := p.typ
	order := make([]int, len(infos))
	pks := make([]pendingKey, len(infos))
	switch {
	case p.format == formatKeyed:
		var err error
		if pks, err = keyedOrder(p, d, infos, order); err != nil {
			return nil, nil, err
		}
	case positions > 0:
		at := map[int]string{}
		for i, fi := range infos {
			if fi.tag.position < 0 {
				return nil, nil, &TagError{Type: t, Field: fi.path, Detail: "no position: in a type with positions every field takes one (or nino:\"-\")"}
			}
			if prev, ok := at[fi.tag.position]; ok {
				return nil, nil, &TagError{Type: t, Field: fi.path, Detail: fmt.Sprintf("position %d is also %s's", fi.tag.position, prev)}
			}
			at[fi.tag.position] = fi.path
			order[i] = fi.tag.position
		}
		for pos := 0; pos < len(infos); pos++ {
			if _, ok := at[pos]; !ok {
				return nil, nil, typeErr(t, "positions have a gap at %d: positions must be 0 to %d (a positional body has no room for a missing member)", pos, len(infos)-1)
			}
		}
	default:
		for i := range infos {
			order[i] = i
		}
	}
	return order, pks, nil
}

// checkMemberCount checks the number of fields against the layout's members, or its raw layout's fields.
func checkMemberCount(p *plan, d *layoutDecl, n int) error {
	t := p.typ
	if d.hasMembers && d.rawEnc == nil && p.format != formatKeyed {
		switch {
		case p.format == formatPositional && n != len(d.members):
			return typeErr(t, "%d fields for the %d members of the layout: a positional body holds every member", n, len(d.members))
		case p.format == formatTolerant && n > len(d.members):
			return typeErr(t, "%d fields for the %d members of the layout", n, len(d.members))
		}
	}
	if d.rawEnc != nil && d.rawEnc.fields != nil && n != len(d.rawEnc.fields) {
		return typeErr(t, "%d fields for the %d fields of the raw layout", n, len(d.rawEnc.fields))
	}
	return nil
}

// fieldCodec builds the codec of field f (its member already set), in the variant its tag and its layout member
// ask for, and checks it against the member's encoding.
func (b *builder) fieldCodec(p *plan, d *layoutDecl, fi rawFieldInfo, f *field) error {
	t := p.typ
	var enc *encoding
	if f.member >= 0 {
		enc = d.members[f.member].enc
	}
	variant := variantDefault
	if fi.tag.utf8 {
		variant = variantUTF8
	}
	if enc != nil && enc.kind == "utf8" && !fi.tag.utf8 {
		variant = variantUTF8 // the layout says utf8: follow it
	}
	if enc != nil && enc.kind == "seq" && len(enc.args) == 1 && enc.args[0].kind == "nullable" {
		variant = variantElementNullable
	}
	if enc != nil {
		switch enc.kind {
		case "grid", "group", "bits", "timezone", "formatter":
			return &TagError{Type: t, Field: fi.path, Detail: fmt.Sprintf("layout member %s is %s: the encoding %s is not supported by struct tags", d.members[f.member].name, enc.text, enc.kind)}
		}
	}
	c, err := b.codec(fi.sf.Type, variant, t)
	if err != nil {
		if te, ok := err.(*TagError); ok && te.Field == "" && te.Type == fi.sf.Type {
			return &TagError{Type: t, Field: fi.path, Detail: te.Detail}
		}
		return err
	}
	f.c = c
	if enc != nil {
		if err := checkEncoding(c, enc); err != nil {
			return &TagError{Type: t, Field: fi.path, Detail: fmt.Sprintf("layout member %s is %s: %v", d.members[f.member].name, enc.text, err)}
		}
	}
	return nil
}

type pendingKey struct {
	key     int32
	formers []int32
	member  int
}

// keyedOrder assigns keys (the layout's, else the hash of the name) and the write order (the layout's member
// order, else the fields' order).
func keyedOrder(p *plan, d *layoutDecl, infos []rawFieldInfo, order []int) ([]pendingKey, error) {
	t := p.typ
	pks := make([]pendingKey, len(infos))
	type ranked struct{ rank, i int }
	ranks := make([]ranked, len(infos))
	owner := map[int32]string{}
	for i, fi := range infos {
		name := fi.tag.name
		if name == "" {
			name = fi.sf.Name
		}
		pk := pendingKey{key: TypeID(name), member: -1}
		for _, f := range fi.tag.formers {
			pk.formers = append(pk.formers, TypeID(f))
		}
		rank := len(d.members) + i
		if d.hasMembers {
			m := -1
			for j := range d.members {
				if d.members[j].name == name {
					if m >= 0 {
						m = -2
						break
					}
					m = j
				}
			}
			if m < 0 {
				m = -1
				for j := range d.members {
					if d.members[j].hasKey && d.members[j].key == pk.key {
						m = j
						break
					}
				}
			}
			if m < 0 {
				return nil, &TagError{Type: t, Field: fi.path, Detail: fmt.Sprintf("no member %q in the layout", name)}
			}
			lm := &d.members[m]
			if lm.hasKey {
				pk.key = lm.key // published keys win over recomputed ones (spec §7.6)
			}
			for _, f := range pk.formers {
				if !slices.Contains(lm.formers, f) {
					return nil, &TagError{Type: t, Field: fi.path, Detail: fmt.Sprintf("former key 0x%08X is not one the layout publishes for %s", uint32(f), lm.name)}
				}
			}
			pk.formers = slices.Clone(lm.formers)
			pk.member = m
			rank = m
		}
		for _, k := range append([]int32{pk.key}, pk.formers...) {
			if prev, ok := owner[k]; ok {
				return nil, &TagError{Type: t, Field: fi.path, Detail: fmt.Sprintf("key 0x%08X is also %s's", uint32(k), prev)}
			}
			owner[k] = fi.path
		}
		pks[i] = pk
		ranks[i] = ranked{rank, i}
	}
	slices.SortStableFunc(ranks, func(a, b ranked) int { return cmp.Compare(a.rank, b.rank) })
	for pos, r := range ranks {
		order[r.i] = pos
	}
	return pks, nil
}

// rawLayout decides whether the type, held by value, is one raw block, and places its fields.
func (b *builder) rawLayout(p *plan, d *layoutDecl) error {
	t := p.typ
	if p.entry || d.raw == no || p.format != formatPositional {
		return nil
	}
	if d.rawEnc != nil {
		e := d.rawEnc
		if len(p.fields) == 0 {
			// An empty struct: .NET writes its one byte.
			if e.size > 1 {
				return typeErr(t, "an empty struct is one byte, the layout says %d", e.size)
			}
			p.raw, p.size, p.align = true, 1, 1
			return nil
		}
		if e.size <= 0 {
			return typeErr(t, "the raw layout has no size (a host-dependent layout, spec §10 item 3)")
		}
		p.raw, p.size, p.align = true, e.size, 1
		for i := range p.fields {
			f := &p.fields[i]
			if !f.c.rawCapable() || f.c.kind == kDTO {
				return &TagError{Type: t, Field: f.name, Detail: fmt.Sprintf("%s cannot be a field of a raw struct", f.c.typ)}
			}
			lf := e.fields[i]
			f.off = lf.off
			if err := checkEncoding(f.c, lf.enc); err != nil {
				return &TagError{Type: t, Field: f.name, Detail: fmt.Sprintf("raw field %s is %s: %v", lf.name, lf.enc.text, err)}
			}
			if f.off+f.c.size > p.size {
				return &TagError{Type: t, Field: f.name, Detail: fmt.Sprintf("at %d, %d bytes do not fit in %d", f.off, f.c.size, p.size)}
			}
			p.align = max(p.align, f.c.align)
		}
		return b.sortRaw(p)
	}
	if d.raw == yes {
		return typeErr(t, "raw=true needs the members of the raw layout (raw(n){...})")
	}
	// Inferred: raw when every field is a fixed-size value (spec §5.1), laid out as CoreCLR does on a 64-bit host
	// with sequential layout (spec §5.2).
	if len(p.fields) == 0 {
		p.raw, p.size, p.align = true, 1, 1
		return nil
	}
	end, align := 0, 1
	for i := range p.fields {
		c := p.fields[i].c
		if !c.rawCapable() {
			if c.size > 0 {
				p.rawErr = fmt.Sprintf("field %s (%s) has no raw layout of its own", p.fields[i].name, c.typ)
			}
			return nil
		}
	}
	for i := range p.fields {
		f := &p.fields[i]
		if f.c.align == 0 {
			return &TagError{Type: t, Field: f.name, Detail: fmt.Sprintf("a raw struct holding %s needs its offsets: declare them in NinoLayout (raw(n){...})", f.c.typ)}
		}
		f.off = roundUp(end, f.c.align)
		end = f.off + f.c.size
		align = max(align, f.c.align)
	}
	p.raw, p.size, p.align = true, roundUp(end, align), align
	return nil
}

func (b *builder) sortRaw(p *plan) error {
	slices.SortStableFunc(p.fields, func(a, b field) int { return cmp.Compare(a.off, b.off) })
	for i := 1; i < len(p.fields); i++ {
		prev := &p.fields[i-1]
		if p.fields[i].off < prev.off+prev.c.size {
			return &TagError{Type: p.typ, Field: p.fields[i].name, Detail: "overlaps " + prev.name}
		}
	}
	return nil
}

func roundUp(n, a int) int { return (n + a - 1) / a * a }

// checkEncoding checks that the codec c writes what the layout's encoding e says.
func checkEncoding(c *codec, e *encoding) error {
	mismatch := func() error { return fmt.Errorf("the Go type %s does not write %s", c.typ, e.text) }
	switch e.kind {
	case "raw":
		switch {
		case c.quirk:
			return nil // .NET publishes these wrong (spec §10, item 1)
		case c.kind == kRawStruct:
			if e.size < 0 {
				if c.size == 1 && len(c.plan.fields) == 0 {
					return nil
				}
				return mismatch()
			}
			if e.size != c.size {
				return fmt.Errorf("%d bytes, the Go type takes %d", e.size, c.size)
			}
			return nil
		case c.rawCapable() && c.kind != kDTO:
			if e.size != c.size {
				return fmt.Errorf("%d bytes, the Go type takes %d", e.size, c.size)
			}
			return nil
		}
	case "string":
		if (c.kind == kString || c.kind == kStringPtr) && !c.utf8 {
			return nil
		}
	case "utf8":
		if (c.kind == kString || c.kind == kStringPtr) && c.utf8 {
			return nil
		}
	case "object":
		if c.kind == kIface {
			return nil
		}
	case "type":
		switch c.kind {
		case kClass, kIface, kSlice, kPrimSlice, kBytes, kSet, kMap, kStride:
			// A class, a polymorphic slot, or a [NinoType] collection (a Go slice or map of its elements).
			return nil
		case kStruct:
			if !c.plan.entry {
				return nil
			}
		}
	case "nullable":
		if c.kind == kNullable && len(e.args) == 1 {
			return checkEncoding(c.elem, e.args[0])
		}
	case "seq":
		if len(e.args) != 1 {
			return mismatch()
		}
		switch c.kind {
		case kBytes:
			if e.args[0].kind == "raw" && e.args[0].size == 1 {
				return nil
			}
		case kSlice, kPrimSlice, kSet:
			ec := c.elem
			if c.kind == kSet {
				ec = c.key
			}
			return checkEncoding(ec, e.args[0])
		case kStride:
			if e.args[0].kind == "raw" && e.args[0].size == c.size {
				return nil
			}
			return fmt.Errorf("a stride of %d bytes, the Go type takes %d", e.args[0].size, c.size)
		}
	case "map":
		if len(e.args) != 2 {
			return mismatch()
		}
		switch {
		case c.kind == kMap:
			if err := checkEncoding(c.key, e.args[0]); err != nil {
				return err
			}
			return checkEncoding(c.elem, e.args[1])
		case c.kind == kSlice && c.elem.kind == kStruct && c.elem.plan.entry:
			fs := c.elem.plan.fields
			if err := checkEncoding(fs[0].c, e.args[0]); err != nil {
				return err
			}
			return checkEncoding(fs[1].c, e.args[1])
		}
	case "pair", "tuple":
		if c.kind == kStruct && c.plan.format == formatPositional && len(c.plan.fields) == len(e.args) {
			for i := range e.args {
				if err := checkEncoding(c.plan.fields[i].c, e.args[i]); err != nil {
					return err
				}
			}
			return nil
		}
	case "datetimeoffset":
		if c.kind == kDTO {
			return nil
		}
	case "bigint":
		if c.kind == kBigInt {
			return nil
		}
	case "version":
		if c.kind == kVersion {
			return nil
		}
	default:
		return fmt.Errorf("the encoding %s is not supported by struct tags", e.kind)
	}
	return mismatch()
}
