#!/usr/bin/env python3
"""Writes tests/conformance/types.rs: Rust codecs for the conformance corpus's sample types, from the `layouts` of
the corpus's cases, in the shape the shared templates are to generate (one struct per [NinoType], NinoReadBody/NinoWriteBody for class
bodies, an enum per polymorphic declared type restricted to its subtypes), plus their JSON form for the test.

    python3 tests/conformance/gen.py <corpus dir> [--serde-rename camelCase|none|<serde rename_all rule>] \
        > tests/conformance/types.rs

Every type also derives serde behind the crate's `serde` feature (`#[cfg_attr(feature = "serde", ...)]`), with its
fields renamed by `--serde-rename` (serde's `rename_all` rules: camelCase by default, as .NET's JSON names; `none`
keeps the Rust names).

What the layouts do not say is a table below, from conformance/Nino.Conformance/Types.cs: subtypes, enums'
underlying types, raw structs' alignment, deferrability (the specification, 10.5) and which string members keep
lone surrogates (WideString; the others use String, to test both).
"""
import glob
import json
import re
import sys

# ---------------------------------------------------------------------------------------------------- not in layouts

ENUMS = {
    "Conformance.E8": "sbyte", "Conformance.EU8": "byte", "Conformance.E16": "short", "Conformance.EU16": "ushort",
    "Conformance.E32": "int", "Conformance.EU32": "uint", "Conformance.E64": "long", "Conformance.EU64": "ulong",
    "Conformance.Permissions": "byte", "Conformance.WideFlags": "ulong",
}

# Declared types written behind a type id: the concrete types a value of each may be (Types.cs).
SUBTYPES = {
    "Conformance.Animal": ["Conformance.Dog", "Conformance.Puppy", "Conformance.Cat", "Conformance.Box<System.Int32>",
                           "Conformance.Box<System.String>", "Conformance.Box<Conformance.Vec2>",
                           "Conformance.Box<System.Collections.Generic.List<Conformance.Point>>"],
    "Conformance.Dog": ["Conformance.Dog", "Conformance.Puppy"],
    "Conformance.IShape": ["Conformance.Square", "Conformance.Circle"],
    "Conformance.Circle": ["Conformance.Circle"],
    "Conformance.Plain": ["Conformance.Plain"],
    "Conformance.TolerantBase": ["Conformance.TolerantBase", "Conformance.TolerantLeaf"],
    "Conformance.Link": ["Conformance.RedLink", "Conformance.BlueLink"],
}

# Built by a constructor taking member values (a record): never deferred.
NOT_DEFERRABLE = {"Conformance.RecordChain"}

# Collections of themselves: `class Tree : List<Tree>`.
SELF_COLLECTIONS = {
    "Conformance.Tree": ("seq", "System.Collections.Generic.List<Conformance.Tree>"),
    "Conformance.DictTree": ("map", "System.Collections.Generic.Dictionary<System.String, Conformance.DictTree>"),
}

WIDE_STRINGS = {("Conformance.Strings", "Text"), ("Conformance.StringCollections", "Array")}

# Reference tracking is optional (corpus README); this runtime leaves it out.
SKIPPED = {"Conformance.Tracked", "Conformance.TrackedLeaf", "Conformance.TrackedHolder"}

# Shapes this hand-written emulation leaves to the code nino-gen writes (tests/generated.rs runs their cases):
# [NinoType] collections, generic types behind a type id, generics nesting themselves, Nullable and KeyValuePair
# type arguments. Their cases are skipped here, by type.
NOT_EMULATED = {"Conformance.BagHolder", "Conformance.GenericSlots", "Conformance.NestHolder", "Conformance.WrappedArgs"}

# Generic closed types used, and their arguments' layouts.
GENERIC_ARGS = {
    "System.Int32": ("int", "raw(4)"),
    "System.String": ("string", "string"),
    "Conformance.Vec2": ("Conformance.Vec2", "type(Conformance.Vec2)"),
    "System.Collections.Generic.List<Conformance.Point>": (
        "System.Collections.Generic.List<Conformance.Point>", "seq(type(Conformance.Point))"),
}

PRIMS = {
    "sbyte": "i8", "byte": "u8", "short": "i16", "ushort": "u16", "int": "i32", "uint": "u32", "long": "i64",
    "ulong": "u64", "float": "f32", "double": "f64", "bool": "bool", "char": "u16", "decimal": "nino::Decimal",
    "System.Guid": "nino::Guid", "System.DateTime": "nino::DateTime", "System.TimeSpan": "nino::TimeSpan",
    "System.DateOnly": "nino::DateOnly", "System.TimeOnly": "nino::TimeOnly", "System.Half": "nino::Half",
    "System.Int128": "i128", "System.UInt128": "u128",
}

# ---------------------------------------------------------------------------------------------------- parsing


def split_top(s, sep=","):
    """Splits at `sep` outside (), <>, {} and []."""
    parts, depth, cur = [], 0, ""
    for c in s:
        if c in "(<{[":
            depth += 1
        elif c in ")>}]":
            depth -= 1
        if c == sep and depth == 0:
            parts.append(cur)
            cur = ""
        else:
            cur += c
    parts.append(cur)
    return [p.strip() for p in parts]


def parse_enc(e):
    """An encoding as (kind, args)."""
    m = re.fullmatch(r"raw(?:\((\d+)\))?(\{.*\})?", e, re.S)
    if m:
        return ("raw", int(m.group(1)) if m.group(1) else None, m.group(2))
    m = re.fullmatch(r"(\w+)(?:\((.*)\))?", e, re.S)
    kind, inner = m.group(1), m.group(2)
    if kind in ("type",):
        return ("type", inner)
    if kind == "grid":
        rank, el = split_top(inner)
        return ("grid", int(rank), parse_enc(el))
    if kind in ("seq", "nullable"):
        return (kind, parse_enc(inner))
    if kind in ("map", "pair", "tuple"):
        return (kind, [parse_enc(x) for x in split_top(inner)])
    return (kind,)


def parse_cs(t):
    """A C# type name as (name, [args]) / ('[]', el) / ('[,]', el)."""
    t = t.strip()
    if t.endswith("[]"):
        return ("[]", parse_cs(t[:-2]))
    if t.endswith("[,]"):
        return ("[,]", parse_cs(t[:-3]))
    if "<" in t:
        i = t.index("<")
        return (t[:i], [parse_cs(a) for a in split_top(t[i + 1:-1])])
    return (t, [])


def cs_name(c):
    if c[0] == "[]":
        return cs_name(c[1]) + "[]"
    if c[0] == "[,]":
        return cs_name(c[1]) + "[,]"
    if c[1]:
        return c[0] + "<" + ", ".join(cs_name(a) for a in c[1]) + ">"
    return c[0]


def parse_layout(text):
    d = {}
    for part in text.split("; "):
        k, v = part.split("=", 1)
        d[k] = v
    members = []
    body = d["members"]
    if d["raw"] == "true":
        d["members"] = body
        return d
    for entry in split_top(body, ";") if body else []:
        if not entry:
            continue
        name, rest = entry.split(":", 1)
        # The type has no ':'; the encoding ends at ':key=' when keyed.
        ty, rest = rest.split(":", 1)
        key = former = None
        m = re.search(r":key=(0x[0-9A-F]+)(?::former=([0-9A-Fx,]+))?$", rest)
        if m:
            key = int(m.group(1), 16)
            former = [int(x, 16) for x in m.group(2).split(",")] if m.group(2) else []
            rest = rest[: m.start()]
        members.append({"name": name, "type": ty, "enc": parse_enc(rest), "key": key, "former": former})
    d["members"] = members
    return d


# ---------------------------------------------------------------------------------------------------- the model

def ident(name):
    """A Rust identifier for a C# type name."""
    n = name.replace("Conformance.Targets.", "T").replace("Conformance.", "")
    n = n.replace("System.Collections.Generic.", "").replace("System.", "")
    return re.sub(r"[^A-Za-z0-9]", "", n.replace("<", "Of").replace(",", "And"))


def snake(name):
    s = re.sub(r"(?<=[a-z0-9])([A-Z])", r"_\1", name).lower()
    keywords = {"type", "box", "struct", "ref", "as", "enum", "fn", "impl", "match", "mod", "move", "self", "static",
                "trait", "use", "where", "loop", "let", "in", "for", "if", "else", "crate", "dyn", "const", "pub"}
    return f"r#{s}" if s in keywords else s


class Gen:
    def __init__(self, layouts, type_ids):
        self.layouts = {k: parse_layout(v) for k, v in layouts.items() if k not in SKIPPED}
        self.type_ids = type_ids
        self.out = []
        self.raw_structs = {}

    def layout(self, name):
        return self.layouts[name]

    def is_class(self, name):
        return self.layout(name)["header"] in ("NullFlag", "TypeId")

    # ------------------------------------------------------------------ Rust types

    def rust(self, cs, enc, owner=None, member=None):
        """The Rust type of a member of C# type `cs` written as `enc`."""
        c = parse_cs(cs) if isinstance(cs, str) else cs
        name = cs_name(c)
        kind = enc[0]
        if kind == "raw":
            if name in PRIMS:
                return PRIMS[name]
            if name in ENUMS:
                return PRIMS[ENUMS[name]]
            if name.startswith("System.Nullable<"):
                raise ValueError(name)
            return self.raw_struct(name)
        if kind == "string":
            return "Option<nino::WideString>" if (owner, member) in WIDE_STRINGS else "Option<String>"
        if kind == "utf8":
            return "Option<nino::Utf8>"
        if kind == "bigint":
            return "nino::BigInteger"
        if kind == "version":
            return "Option<nino::Version>"
        if kind == "datetimeoffset":
            return "nino::DateTimeOffset"
        if kind == "bits":
            return "Option<nino::BitArray>"
        if kind == "object":
            return "Option<AnyObject>"
        if kind == "nullable":
            inner = c[1][0]
            return f"Option<{self.rust(inner, enc[1], owner, member)}>"
        if kind == "seq":
            el = self.element(c)
            if el is not None and cs_name(el).startswith("System.Nullable<") and enc[1][0] == "raw":
                inner = el[1][0]
                return f"Option<nino::RawNullables<{self.rust(inner, ('raw', None, None))}>>"
            if name in SELF_COLLECTIONS:
                return f"Option<{ident(name)}>"
            if c[0].endswith("HashSet"):
                return f"Option<indexmap::IndexSet<{self.key(el, enc[1])}>>"
            return f"Option<Vec<{self.rust(el, enc[1], owner, member)}>>"
        if kind == "map":
            if name in SELF_COLLECTIONS:
                return f"Option<{ident(name)}>"
            k, v = c[1]
            return f"Option<indexmap::IndexMap<{self.key(k, enc[1][0])}, {self.rust(v, enc[1][1])}>>"
        if kind == "grid":
            return f"Option<nino::Grid<{self.rust(c[1], enc[2])}, {enc[1]}>>"
        if kind in ("pair", "tuple"):
            items = [self.rust(a, e) for a, e in zip(c[1], enc[1])]
            return "(" + ", ".join(items) + ("," if len(items) == 1 else "") + ")"
        if kind == "type":
            if enc[1] == "T":
                raise ValueError("open generic")
            target = enc[1]
            if target in SELF_COLLECTIONS:
                return f"Option<{ident(target)}>"
            lay = self.layout(target)
            if lay["raw"] == "true":
                return self.raw_struct(target)
            if lay["header"] == "None":
                return ident(target)
            if lay["header"] == "TypeId":
                return f"Option<{ident(target)}Ref>"
            return f"Option<Box<{ident(target)}>>"
        raise ValueError(kind)

    def key(self, c, enc):
        """A dictionary key or set element: never null."""
        t = self.rust(c, enc)
        return t[len("Option<"):-1] if t.startswith("Option<") and enc[0] in ("string", "utf8") else t

    @staticmethod
    def element(c):
        if c[0] in ("[]",):
            return c[1]
        if c[1]:
            return c[1][0]
        return None

    # ------------------------------------------------------------------ raw structs

    def raw_struct(self, name):
        if name in self.raw_structs:
            return self.raw_structs[name][0]
        lay = self.layout(name)
        body = lay["members"]
        rust = ident(name)
        self.raw_structs[name] = (rust, None)
        if body == "raw":
            fields, size = [], 1  # an empty struct: one byte
        else:
            m = re.fullmatch(r"raw\((\d+)\)\{(.*)\}", body, re.S)
            size = int(m.group(1))
            fields = []
            for f in split_top(m.group(2)):
                fname, rest = f.split(":", 1)
                ftype, rest = rest.split("@", 1)
                off, fenc = rest.split(":", 1)
                fields.append((fname, ftype, int(off), parse_enc(fenc)))
        self.raw_structs[name] = (rust, (name, size, fields))
        return rust

    def leaf_align(self, ftype, fenc):
        if ftype in self.layouts and self.layouts[ftype]["raw"] == "true":
            return self.struct_align(ftype)
        if ftype == "decimal":
            return 8
        if ftype == "System.Guid":
            return 4
        return min(fenc[1], 8)

    def struct_align(self, name):
        _, (_, size, fields) = self.raw_structs[name] if name in self.raw_structs and self.raw_structs[name][1] else (
            None, (None, 1, []))
        if not fields:
            self.raw_struct(name)
            _, (_, size, fields) = self.raw_structs[name]
        aligns = [(off, self.leaf_align(t, e)) for (_, t, off, e) in fields]
        best = 1
        for a in (1, 2, 4, 8):
            if a <= max([x for _, x in aligns] + [1]) and all(off % min(fa, a) == 0 for off, fa in aligns):
                best = a
        return best

    def emit_raw_struct(self, name, size, fields):
        rust = ident(name)
        o = self.out
        o.append(f"/// `{name}`: raw, {size} bytes.")
        o.append("#[derive(Debug, Default, Clone, Copy, PartialEq)]")
        o.append(f"pub struct {rust} {{")
        for fname, ftype, off, fenc in fields:
            o.append(f"    pub {snake(fname)}: {self.rust(ftype, fenc)},")
        o.append("}")
        o.append("")
        o.append(f"impl nino::Raw for {rust} {{")
        o.append(f"    const SIZE: usize = {size};")
        o.append(f"    const ALIGN: usize = {self.struct_align(name)};")
        o.append("")
        o.append("    fn decode_raw(b: &[u8]) -> Result<Self> {")
        o.append(f"        let b = b.get(..{size}).ok_or_else(|| Error::of(ErrorKind::UnexpectedEnd))?;")
        used = [False] * size
        for fname, ftype, off, fenc in fields:
            fsize = self.raw_size(ftype, fenc)
            for i in range(off, off + fsize):
                used[i] = True
        pads = [i for i in range(size) if not used[i]]
        if pads:
            idx = ", ".join(str(i) for i in pads)
            o.append(f"        if [{idx}].iter().any(|&i| b.get(i) != Some(&0)) {{")
            o.append("            return Err(Error::of(ErrorKind::InvalidBits));")
            o.append("        }")
        o.append("        Ok(Self {")
        for fname, ftype, off, fenc in fields:
            rt = self.rust(ftype, fenc)
            o.append(f"            {snake(fname)}: <{rt} as nino::Raw>::decode_raw(b.get({off}..).unwrap_or_default())?,")
        o.append("        })")
        o.append("    }")
        o.append("")
        o.append("    fn encode_raw(&self, out: &mut [u8]) {")
        for fname, ftype, off, fenc in fields:
            o.append(f"        if let Some(o) = out.get_mut({off}..) {{")
            o.append(f"            nino::Raw::encode_raw(&self.{snake(fname)}, o);")
            o.append("        }")
        if not fields:
            o.append("        let _ = out;")
        o.append("    }")
        o.append("}")
        o.append("")
        o.append(f"impl NinoRead for {rust} {{")
        o.append(f"    const MIN_SIZE: usize = {size};")
        o.append("")
        o.append("    fn read(r: &mut Reader<'_>) -> Result<Self> {")
        o.append(f"        <Self as nino::Raw>::decode_raw(&r.array::<{size}>()?)")
        o.append("    }")
        o.append("}")
        o.append("")
        o.append(f"impl NinoWrite for {rust} {{")
        o.append("    fn write<'a>(&'a self, w: &mut Writer<'a>) -> Result<()> {")
        o.append(f"        let mut b = [0u8; {size}];")
        o.append("        nino::Raw::encode_raw(self, &mut b);")
        o.append("        w.array(b);")
        o.append("        Ok(())")
        o.append("    }")
        o.append("}")
        o.append("")
        o.append(f"impl J for {rust} {{")
        o.append("    fn j(&self) -> Value {")
        o.append("        let mut m = Map::new();")
        o.append(f"        m.insert(\"$type\".into(), Value::from({json.dumps(name)}));")
        for fname, ftype, off, fenc in fields:
            o.append(f"        m.insert({json.dumps(fname)}.into(), self.{snake(fname)}.j());")
        o.append("        Value::Object(m)")
        o.append("    }")
        o.append("}")
        o.append("")

    def raw_size(self, ftype, fenc):
        if fenc[0] == "raw" and fenc[1] is not None and ftype not in ("System.Half", "System.Int128",
                                                                      "System.UInt128", "System.TimeOnly"):
            return fenc[1]
        return {"System.Half": 2, "System.Int128": 16, "System.UInt128": 16, "System.TimeOnly": 8}[ftype]

    # ------------------------------------------------------------------ members

    def members(self, name, cs_subst=None):
        lay = self.layout(name)
        out = []
        for m in lay["members"]:
            ty, enc = m["type"], m["enc"]
            if enc == ("type", "T"):
                ty, enc_s = cs_subst
                enc = parse_enc(enc_s)
            out.append(dict(m, rust=self.rust(ty, enc, name, m["name"]), field=snake(m["name"]), enc=enc, cs=ty))
        return out

    def may_defer(self, rust):
        return any(re.search(rf"\b{x}\b", rust) for x in self.class_idents)

    def emit_body(self, name, rust, members, fmt, deferrable):
        o = self.out
        o.append(f"impl NinoReadBody for {rust} {{")
        if not deferrable:
            o.append("    const DEFERRABLE: bool = false;")
            o.append("")
        o.append("    fn read_body(&mut self, r: &mut Reader<'_>) -> Result<()> {")

        def read(m):
            if self.may_defer(m["rust"]):
                return f"r.read_member_into(&mut self.{m['field']}, field!(Self, {m['field']}))?"
            return f"r.read_into(&mut self.{m['field']})?"

        if fmt == "Positional":
            for m in members:
                o.append(f"        {read(m)};")
            if not members:
                o.append("        let _ = r;")
        elif fmt == "VersionTolerant":
            o.append("        let body = r.begin_region()?;")
            for m in members:
                o.append("        if r.has_more() {")
                o.append(f"            {read(m)};")
                o.append("        }")
            o.append("        r.end_region(body, self)?;")
        else:
            o.append("        let body = r.begin_region()?;")
            o.append("        while r.has_more() {")
            o.append("            let key = r.i32()?;")
            o.append("            let member = r.begin_region()?;")
            o.append("            match key as u32 {")
            for m in members:
                keys = " | ".join(f"0x{k:08X}" for k in [m["key"]] + m["former"])
                o.append(f"                {keys} => {read(m)},")
            o.append("                _ => {")
            o.append("                    r.end_region(member, self)?;")
            o.append("                    continue;")
            o.append("                }")
            o.append("            }")
            o.append("            r.end_member(member, self)?;")
            o.append("        }")
            o.append("        r.end_region(body, self)?;")
        o.append("        Ok(())")
        o.append("    }")
        o.append("}")
        o.append("")
        o.append(f"impl NinoWriteBody for {rust} {{")
        if not deferrable:
            o.append("    const DEFERRABLE: bool = false;")
            o.append("")
        o.append("    fn write_body<'a>(&'a self, w: &mut Writer<'a>) -> Result<()> {")
        if fmt == "Positional":
            for m in members:
                o.append(f"        w.write(&self.{m['field']})?;")
            if not members:
                o.append("        let _ = w;")
        elif fmt == "VersionTolerant":
            o.append("        let body = w.begin_region();")
            for m in members:
                o.append(f"        w.write(&self.{m['field']})?;")
            o.append("        w.end_region(body)?;")
        else:
            o.append("        let body = w.begin_region();")
            for m in members:
                o.append(f"        w.i32(0x{m['key']:08X}u32 as i32);")
                o.append("        let member = w.begin_region();")
                o.append(f"        w.write(&self.{m['field']})?;")
                o.append("        w.end_region(member)?;")
            o.append("        w.end_region(body)?;")
        o.append("        Ok(())")
        o.append("    }")
        o.append("}")
        o.append("")

    def emit_json(self, rust, type_name, members):
        o = self.out
        o.append(f"impl J for {rust} {{")
        o.append("    fn j(&self) -> Value {")
        o.append("        let mut m = Map::new();")
        o.append(f"        m.insert(\"$type\".into(), Value::from({json.dumps(type_name)}));")
        for mem in members:
            c = cs_name(parse_cs(mem["cs"]))
            if c in ("byte[]", "sbyte[]"):
                o.append(f"        m.insert({json.dumps(mem['name'])}.into(), hex(self.{mem['field']}.as_deref()));")
            else:
                o.append(f"        m.insert({json.dumps(mem['name'])}.into(), self.{mem['field']}.j());")
        o.append("        Value::Object(m)")
        o.append("    }")
        o.append("}")
        o.append("")

    def emit_struct(self, rust, doc, members):
        o = self.out
        o.append(f"/// {doc}")
        o.append("#[derive(Debug, Default, Clone, PartialEq)]")
        o.append(f"pub struct {rust} {{")
        for m in members:
            o.append(f"    pub {m['field']}: {m['rust']},")
        o.append("}")
        o.append("")

    def emit_class(self, name, closed=None):
        """A concrete class (NullFlag or TypeId header), or a struct with header None."""
        lay = self.layout(name if closed is None else closed[0])
        type_name = name
        rust = ident(name)
        subst = GENERIC_ARGS[closed[1]] if closed else None
        members = self.members(closed[0] if closed else name, subst)
        header = lay["header"]
        fmt = lay["format"]
        self.emit_struct(rust, f"`{type_name}`: {fmt}, header {header}.", members)
        if header == "None":
            # A struct: its body, no header, not counted as a class body.
            o = self.out
            o.append(f"impl NinoRead for {rust} {{")
            if self.may_defer(" ".join(m["rust"] for m in members)):
                o.append("    const MAY_DEFER: bool = true;")
            o.append("    fn read(r: &mut Reader<'_>) -> Result<Self> {")
            o.append("        let mut v = Self::default();")
            o.append("        v.read_into(r)?;")
            o.append("        Ok(v)")
            o.append("    }")
            o.append("")
            o.append("    fn read_into(&mut self, r: &mut Reader<'_>) -> Result<()> {")
            o.append("        self.read_body(r)")
            o.append("    }")
            o.append("}")
            o.append("")
            o.append(f"impl NinoWrite for {rust} {{")
            o.append("    fn write<'a>(&'a self, w: &mut Writer<'a>) -> Result<()> {")
            o.append("        self.write_body(w)")
            o.append("    }")
            o.append("}")
            o.append("")
            self.emit_body(name, rust, members, fmt, True)
            self.emit_json(rust, type_name, members)
            return
        self.emit_body(name, rust, members, fmt, name not in NOT_DEFERRABLE)
        o = self.out
        if header == "NullFlag":
            o.append(f"impl NinoRead for {rust} {{")
            o.append("    const MAY_DEFER: bool = true;")
            o.append("    nino::sealed_read!();")
            o.append("}")
            o.append("")
            o.append(f"impl NinoWrite for {rust} {{")
            o.append("    nino::sealed_write!();")
            o.append("}")
            o.append("")
        tid = self.type_id(type_name)
        o.append(f"impl {rust} {{")
        o.append(f"    pub const TYPE_ID: i32 = 0x{tid:08X}u32 as i32;")
        o.append("}")
        o.append("")
        self.emit_json(rust, type_name, members)

    def type_id(self, name):
        if name in self.type_ids:
            return self.type_ids[name]
        if name in self.layouts:
            return int(self.layouts[name]["typeId"], 16)
        raise KeyError(name)

    def emit_ref(self, base, subtypes, rust):
        """A declared type written behind a type id: an enum of the types it may hold, and nothing else."""
        o = self.out
        o.append(f"/// A `{base}` slot: a type id, then the body of one of these types.")
        o.append("#[derive(Debug, Clone, PartialEq)]")
        o.append(f"pub enum {rust} {{")
        for s in subtypes:
            o.append(f"    {ident(s)}(Box<{ident(s)}>),")
        o.append("}")
        o.append("")
        o.append(f"impl {rust} {{")
        o.append("    fn body(&mut self) -> &mut dyn Any {")
        o.append("        match self {")
        for s in subtypes:
            o.append(f"            Self::{ident(s)}(v) => &mut **v,")
        o.append("        }")
        o.append("    }")
        o.append("}")
        o.append("")
        o.append(f"impl NinoRead for {rust} {{")
        o.append("    const MAY_DEFER: bool = true;")
        o.append("    const MIN_SIZE: usize = 4;")
        o.append("")
        o.append("    fn read(r: &mut Reader<'_>) -> Result<Self> {")
        o.append("        Self::read_option(r)?.ok_or_else(|| Error::of(ErrorKind::UnexpectedNull))")
        o.append("    }")
        o.append("")
        o.append("    fn read_option(r: &mut Reader<'_>) -> Result<Option<Self>> {")
        o.append("        let mut slot = None;")
        o.append("        Self::read_option_into(&mut slot, r)?;")
        o.append("        Ok(slot)")
        o.append("    }")
        o.append("")
        o.append("    fn read_option_into(slot: &mut Option<Self>, r: &mut Reader<'_>) -> Result<()> {")
        o.append("        let id = r.i32()?;")
        o.append("        if id == 0 {")
        o.append("            *slot = None;")
        o.append("            return Ok(());")
        o.append("        }")
        o.append("        let mark = r.defer_mark();")
        o.append("        let reused = match slot {")
        for s in subtypes:
            o.append(f"            Some(Self::{ident(s)}(v)) if id == {ident(s)}::TYPE_ID => {{")
            o.append("                r.read_class_into(&mut **v)?;")
            o.append("                true")
            o.append("            }")
        o.append("            _ => false,")
        o.append("        };")
        o.append("        if !reused {")
        o.append("            *slot = Some(match id {")
        for s in subtypes:
            o.append(f"                {ident(s)}::TYPE_ID => Self::{ident(s)}(Box::new(r.read_class()?)),")
        o.append("                _ => return Err(Error::new(ErrorKind::UnknownTypeId, id)),")
        o.append("            });")
        o.append("        }")
        o.append(f"        r.defer_trail(mark, Step::Field(|p| Some(p.downcast_mut::<{rust}>()?.body())));")
        o.append("        Ok(())")
        o.append("    }")
        o.append("}")
        o.append("")
        o.append(f"impl NinoWrite for {rust} {{")
        o.append("    fn write<'a>(&'a self, w: &mut Writer<'a>) -> Result<()> {")
        o.append("        match self {")
        for s in subtypes:
            o.append(f"            Self::{ident(s)}(v) => {{")
            o.append(f"                w.i32({ident(s)}::TYPE_ID);")
            o.append("                w.write_class(&**v)")
            o.append("            }")
        o.append("        }")
        o.append("    }")
        o.append("")
        o.append("    fn write_option<'a>(value: Option<&'a Self>, w: &mut Writer<'a>) -> Result<()> {")
        o.append("        match value {")
        o.append("            Some(v) => v.write(w),")
        o.append("            None => {")
        o.append("                w.i32(0);")
        o.append("                Ok(())")
        o.append("            }")
        o.append("        }")
        o.append("    }")
        o.append("}")
        o.append("")
        o.append(f"impl J for {rust} {{")
        o.append("    fn j(&self) -> Value {")
        o.append("        match self {")
        for s in subtypes:
            o.append(f"            Self::{ident(s)}(v) => v.j(),")
        o.append("        }")
        o.append("    }")
        o.append("}")
        o.append("")

    def emit_self_collection(self, name):
        kind, cs = SELF_COLLECTIONS[name]
        rust = ident(name)
        o = self.out
        if kind == "seq":
            inner = f"Vec<Option<{rust}>>"
        else:
            inner = f"indexmap::IndexMap<String, Option<{rust}>>"
        o.append(f"/// `{name}`: a collection of itself. Nothing but the depth counter bounds its nesting.")
        o.append("#[derive(Debug, Default, Clone, PartialEq)]")
        o.append(f"pub struct {rust}(pub {inner});")
        o.append("")
        o.append(f"impl NinoRead for {rust} {{")
        o.append("    const MIN_SIZE: usize = 4;")
        o.append("    fn read(r: &mut Reader<'_>) -> Result<Self> {")
        o.append("        Self::read_option(r)?.ok_or_else(|| Error::of(ErrorKind::UnexpectedNull))")
        o.append("    }")
        o.append("    fn read_option(r: &mut Reader<'_>) -> Result<Option<Self>> {")
        o.append("        r.enter_struct()?;")
        o.append(f"        let v = <Option<{inner}>>::read(r);")
        o.append("        r.exit_struct();")
        o.append("        Ok(v?.map(Self))")
        o.append("    }")
        o.append("}")
        o.append("")
        o.append(f"impl NinoWrite for {rust} {{")
        o.append("    fn write<'a>(&'a self, w: &mut Writer<'a>) -> Result<()> {")
        o.append("        self.0.write(w)")
        o.append("    }")
        o.append("    fn write_option<'a>(value: Option<&'a Self>, w: &mut Writer<'a>) -> Result<()> {")
        o.append(f"        <{inner} as NinoWrite>::write_option(value.map(|v| &v.0), w)")
        o.append("    }")
        o.append("}")
        o.append("")
        o.append(f"impl J for {rust} {{")
        o.append("    fn j(&self) -> Value {")
        o.append("        self.0.j()")
        o.append("    }")
        o.append("}")
        o.append("")

    def generate(self, top_types):
        concrete = set()
        for name, lay in self.layouts.items():
            if name.endswith("<T>"):
                continue
            if lay["raw"] == "true":
                continue
            concrete.add(name)
        concrete -= {"Conformance.Animal", "Conformance.IShape", "Conformance.Link"}  # abstract or interfaces
        closed = [n for n in self.type_ids if n.startswith("Conformance.Box<")]
        classes = [n for n in concrete if self.layout(n)["header"] != "None"] + closed
        self.class_idents = {ident(n) for n in classes} | {ident(b) + "Ref" for b in SUBTYPES}
        self.class_idents |= {ident(n) for n in SELF_COLLECTIONS} | {"AnyObject"}
        for name in sorted(concrete):
            self.emit_class(name)
        for name in closed:
            m = re.fullmatch(r"(Conformance\.Box)<(.*)>", name)
            self.emit_class(name, ("Conformance.Box<T>", m.group(2)))
        for base, subs in SUBTYPES.items():
            self.emit_ref(base, subs, ident(base) + "Ref")
        everything = sorted(n for n in concrete if self.layout(n)["header"] != "None") + closed
        self.emit_ref("object", everything, "AnyObject")
        for name in SELF_COLLECTIONS:
            self.emit_self_collection(name)
        while True:
            pending = [(n, v) for n, v in self.raw_structs.items() if v[1] and not v[1][0] in getattr(self, "done", set())]
            if not pending:
                break
            self.done = getattr(self, "done", set())
            for n, (_, (nm, size, fields)) in pending:
                self.done.add(nm)
                self.emit_raw_struct(nm, size, fields)
        self.emit_dispatch(top_types)

    def top(self, name):
        if name in self.layouts and self.layout(name)["header"] == "TypeId":
            return f"Option<{ident(name)}Ref>"
        if name in self.layouts and self.layout(name)["header"] == "None":
            return ident(name)
        return f"Option<Box<{ident(name)}>>"

    def emit_dispatch(self, top_types):
        o = self.out
        o.append("/// The types whose cases tests/generated.rs runs (the code nino-gen writes) and this emulation skips.")
        o.append("pub const NOT_EMULATED: &[&str] = &[")
        for t in sorted(NOT_EMULATED):
            o.append(f"    {json.dumps(t)},")
        o.append("];")
        o.append("")
        o.append("/// Runs `f` with the type a case is read as, or returns `None` for a type this runtime leaves out.")
        o.append("pub fn with_type<R>(name: &str, f: impl Visit<R>) -> Option<R> {")
        o.append("    Some(match name {")
        for t in sorted(top_types):
            if t in SKIPPED or t in NOT_EMULATED:
                continue
            o.append(f"        {json.dumps(t)} => f.visit::<{self.top(t)}>(),")
        o.append("        _ => return None,")
        o.append("    })")
        o.append("}")
        o.append("")


SERDE_RULES = {"lowercase", "UPPERCASE", "PascalCase", "camelCase", "snake_case", "SCREAMING_SNAKE_CASE",
               "kebab-case", "SCREAMING-KEBAB-CASE", "none"}


def serde_attr(rule):
    """The attribute put after each type's derive: serde behind the `serde` feature, named by `rule`."""
    if rule not in SERDE_RULES:
        raise SystemExit(f"--serde-rename: {rule!r} is not one of {sorted(SERDE_RULES)}")
    rename = "" if rule == "none" else f', serde(rename_all = "{rule}")'
    return f'#[cfg_attr(feature = "serde", derive(serde::Serialize, serde::Deserialize){rename})]'


def main():
    args = sys.argv[1:]
    rule = "camelCase"
    if "--serde-rename" in args:
        i = args.index("--serde-rename")
        rule = args[i + 1] if i + 1 < len(args) else ""
        del args[i:i + 2]
    attr = serde_attr(rule)
    corpus = args[0]
    layouts, type_ids, tops = {}, {}, set()
    for f in sorted(glob.glob(f"{corpus}/valid/*.json") + glob.glob(f"{corpus}/malicious/*.json")):
        if f.endswith("truncations.json"):
            continue
        d = json.load(open(f))
        if d["type"] in NOT_EMULATED or d.get("optional"):
            continue
        for k, v in d.get("layouts", {}).items():
            if layouts.get(k, v) != v:
                raise SystemExit(f"two layouts for {k}")
            layouts[k] = v
        for k, v in d.get("typeIds", {}).items():
            type_ids[k] = int(v, 16)
        tops.add(d["type"])
    g = Gen(layouts, type_ids)
    g.generate(tops)
    print("// Generated by tests/conformance/gen.py from the conformance corpus's layouts. Do not edit.")
    print("#![allow(clippy::all, dead_code, non_camel_case_types, unused_imports)]")
    print()
    print("use core::any::Any;")
    print()
    print("use nino::{field, Error, ErrorKind, NinoRead, NinoReadBody, NinoWrite, NinoWriteBody, Reader, Result, Step, Writer};")
    print("use serde_json::{Map, Value};")
    print()
    print("use super::json::{hex, Visit, J};")
    print()
    out = []
    for line in g.out:
        out.append(line)
        if line.startswith("#[derive("):
            out.append(attr)
    print("\n".join(out))


main()
