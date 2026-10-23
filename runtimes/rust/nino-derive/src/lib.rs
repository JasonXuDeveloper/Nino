//! `#[derive(NinoRead, NinoWrite)]`: the codecs Nino's generator writes for a `[NinoType]`, from attributes on a
//! Rust struct or enum. The expansion is plain calls into `nino-serialization` (no reflection, nothing at run time that
//! hand-written code would not do); see `runtimes/rust/README.md` for the attributes.
//!
//! On a struct, `#[nino(...)]` takes:
//! - `format = "plain" | "tolerant" | "keyed"`: the .NET `NinoFormat` (`Positional`, `VersionTolerant`, `Keyed`).
//! - `kind = "class" | "open" | "struct" | "raw"`: a sealed class (presence byte), a class that is not sealed (its
//!   type id), a struct (no header) or an unmanaged struct written as raw memory in the canonical layout
//!   (`pack = n`, `size = n` as `[StructLayout]`).
//! - `id = "Namespace.Type"` or `id = 0x...`: the type id, for subtypes read behind one (polymorphic enums).
//! - `deferrable = false`: a type .NET builds through a constructor taking its members (a record).
//! - `layout = "..."`: the type's published `[NinoTypeLayout]`, checked against the struct at compile time.
//! - `crate = "path"`: where the runtime is, when it is not `nino`.
//!
//! On a field: `index = n` (position in the .NET member order), `name = "CSharpName"`, `key = "Name"` and
//! `former = "OldName"` (keyed members), `utf8` (`[NinoUtf8]`), `skip`.
//!
//! On an enum (a polymorphic declared type): one variant per concrete type, each holding it (or a `Box` of it);
//! `#[nino(id = ...)]` on a variant overrides the type's own id.

use proc_macro::TokenStream;
use proc_macro2::{Span, TokenStream as Tokens};
use quote::{format_ident, quote, quote_spanned};
use syn::spanned::Spanned;
use syn::{parse_macro_input, Data, DeriveInput, Error, Fields, GenericArgument, LitInt, LitStr, PathArguments, Type};

/// `#[derive(NinoRead)]`: reading the type in Nino's wire format.
#[proc_macro_derive(NinoRead, attributes(nino))]
pub fn derive_read(input: TokenStream) -> TokenStream {
    let input = parse_macro_input!(input as DeriveInput);
    expand(&input, Side::Read).unwrap_or_else(Error::into_compile_error).into()
}

/// `#[derive(NinoWrite)]`: writing the type in Nino's wire format.
#[proc_macro_derive(NinoWrite, attributes(nino))]
pub fn derive_write(input: TokenStream) -> TokenStream {
    let input = parse_macro_input!(input as DeriveInput);
    expand(&input, Side::Write).unwrap_or_else(Error::into_compile_error).into()
}

#[derive(Clone, Copy, PartialEq)]
enum Side {
    Read,
    Write,
}

#[derive(Clone, Copy, PartialEq, Debug)]
enum Format {
    Plain,
    Tolerant,
    Keyed,
}

#[derive(Clone, Copy, PartialEq, Debug)]
enum Kind {
    /// A sealed class: a presence byte, then the body.
    Class,
    /// A class that is not sealed: its type id (0 = null), then the body.
    Open,
    Struct,
    Raw,
}

/// The type id: a name hashed by the runtime's `type_id`, or a number.
enum Id {
    Name(LitStr),
    Value(i32),
}

struct TypeOpts {
    format: Option<(Format, Span)>,
    kind: Option<(Kind, Span)>,
    id: Option<Id>,
    deferrable: bool,
    layout: Option<LitStr>,
    krate: Option<syn::Path>,
    pack: Option<usize>,
    size: Option<usize>,
}

struct FieldOpts {
    index: Option<(u32, Span)>,
    name: Option<String>,
    key: Option<(String, Span)>,
    former: Vec<(String, Span)>,
    utf8: Option<Span>,
    skip: bool,
}

/// A member: a field written on the wire.
struct Member<'a> {
    ident: &'a syn::Ident,
    ty: &'a Type,
    opts: FieldOpts,
    /// The .NET member name (`name`, or the field's name in PascalCase).
    cs_name: String,
}

fn type_opts(attrs: &[syn::Attribute]) -> syn::Result<TypeOpts> {
    let mut o = TypeOpts {
        format: None,
        kind: None,
        id: None,
        deferrable: true,
        layout: None,
        krate: None,
        pack: None,
        size: None,
    };
    for attr in attrs.iter().filter(|a| a.path().is_ident("nino")) {
        attr.parse_nested_meta(|meta| {
            if meta.path.is_ident("format") {
                let s: LitStr = meta.value()?.parse()?;
                let f = match s.value().as_str() {
                    "plain" | "positional" => Format::Plain,
                    "tolerant" | "version_tolerant" => Format::Tolerant,
                    "keyed" => Format::Keyed,
                    _ => return Err(Error::new(s.span(), "format is \"plain\", \"tolerant\" or \"keyed\"")),
                };
                o.format = Some((f, s.span()));
            } else if meta.path.is_ident("kind") {
                let s: LitStr = meta.value()?.parse()?;
                let k = match s.value().as_str() {
                    "class" => Kind::Class,
                    "open" => Kind::Open,
                    "struct" => Kind::Struct,
                    "raw" => Kind::Raw,
                    _ => return Err(Error::new(s.span(), "kind is \"class\", \"open\", \"struct\" or \"raw\"")),
                };
                o.kind = Some((k, s.span()));
            } else if meta.path.is_ident("id") {
                o.id = Some(parse_id(&meta)?);
            } else if meta.path.is_ident("deferrable") {
                let b: syn::LitBool = meta.value()?.parse()?;
                o.deferrable = b.value;
            } else if meta.path.is_ident("layout") {
                o.layout = Some(meta.value()?.parse()?);
            } else if meta.path.is_ident("crate") {
                let s: LitStr = meta.value()?.parse()?;
                o.krate = Some(s.parse()?);
            } else if meta.path.is_ident("pack") {
                let n: LitInt = meta.value()?.parse()?;
                let v: usize = n.base10_parse()?;
                if !v.is_power_of_two() || v > 128 {
                    return Err(Error::new(n.span(), "pack is a power of two up to 128, as [StructLayout(Pack = n)]"));
                }
                o.pack = Some(v);
            } else if meta.path.is_ident("size") {
                let n: LitInt = meta.value()?.parse()?;
                o.size = Some(n.base10_parse()?);
            } else {
                return Err(meta.error(
                    "unknown nino attribute: expected format, kind, id, deferrable, layout, crate, pack or size",
                ));
            }
            Ok(())
        })?;
    }
    Ok(o)
}

fn parse_id(meta: &syn::meta::ParseNestedMeta) -> syn::Result<Id> {
    let value = meta.value()?;
    if value.peek(LitStr) {
        let s: LitStr = value.parse()?;
        if s.value().is_empty() {
            return Err(Error::new(s.span(), "a type id name is not empty"));
        }
        Ok(Id::Name(s))
    } else {
        let n: LitInt = value.parse()?;
        let v: i64 = n.base10_parse()?;
        let v = i32::try_from(v)
            .or_else(|_| u32::try_from(v).map(|u| u as i32))
            .map_err(|_| Error::new(n.span(), "a type id is a 32-bit integer"))?;
        if v == 0 {
            return Err(Error::new(n.span(), "a type id is not 0 (0 is null on the wire)"));
        }
        Ok(Id::Value(v))
    }
}

fn field_opts(field: &syn::Field) -> syn::Result<FieldOpts> {
    let mut o = FieldOpts { index: None, name: None, key: None, former: Vec::new(), utf8: None, skip: false };
    for attr in field.attrs.iter().filter(|a| a.path().is_ident("nino")) {
        attr.parse_nested_meta(|meta| {
            if meta.path.is_ident("index") {
                let n: LitInt = meta.value()?.parse()?;
                o.index = Some((n.base10_parse()?, n.span()));
            } else if meta.path.is_ident("name") {
                let s: LitStr = meta.value()?.parse()?;
                o.name = Some(s.value());
            } else if meta.path.is_ident("key") {
                let s: LitStr = meta.value()?.parse()?;
                o.key = Some((s.value(), s.span()));
            } else if meta.path.is_ident("former") {
                let s: LitStr = meta.value()?.parse()?;
                o.former.push((s.value(), s.span()));
            } else if meta.path.is_ident("utf8") {
                o.utf8 = Some(meta.path.span());
            } else if meta.path.is_ident("skip") {
                o.skip = true;
            } else {
                return Err(meta.error("unknown nino field attribute: expected index, name, key, former, utf8 or skip"));
            }
            Ok(())
        })?;
    }
    Ok(o)
}

/// FNV-1a over UTF-16 code units, low byte first; 0 becomes 1 (the runtime's `type_id`).
fn fnv(name: &str) -> i32 {
    let mut h: u32 = 0x811C_9DC5;
    for unit in name.encode_utf16() {
        h = (h ^ u32::from(unit & 0xFF)).wrapping_mul(0x0100_0193);
        h = (h ^ u32::from(unit >> 8)).wrapping_mul(0x0100_0193);
    }
    if h == 0 {
        1
    } else {
        h as i32
    }
}

fn pascal(ident: &syn::Ident) -> String {
    let s = ident.to_string();
    let s = s.strip_prefix("r#").unwrap_or(&s);
    s.split('_').filter(|p| !p.is_empty()).map(|p| p[..1].to_uppercase() + &p[1..]).collect()
}

/// The last path segment's name and its first generic type argument.
fn segment(ty: &Type) -> Option<(String, Option<&Type>)> {
    let Type::Path(p) = ty else { return None };
    let seg = p.path.segments.last()?;
    let arg = match &seg.arguments {
        PathArguments::AngleBracketed(a) => a.args.iter().find_map(|g| match g {
            GenericArgument::Type(t) => Some(t),
            _ => None,
        }),
        _ => None,
    };
    Some((seg.ident.to_string(), arg))
}

/// Rejects types the wire format has no encoding for, naming the field.
fn check_type(ty: &Type, field: &syn::Ident) -> syn::Result<()> {
    let bad = |why: &str| Err(Error::new(ty.span(), format!("field `{field}`: {why}")));
    match ty {
        Type::Reference(_) => bad("references cannot be read into; use an owned type"),
        Type::Ptr(_) | Type::BareFn(_) | Type::TraitObject(_) | Type::ImplTrait(_) | Type::Never(_) => {
            bad("this type has no Nino encoding")
        }
        Type::Slice(_) => bad("use Vec<T> for a sequence"),
        Type::Array(a) => check_type(&a.elem, field),
        Type::Tuple(t) => t.elems.iter().try_for_each(|e| check_type(e, field)),
        Type::Paren(p) => check_type(&p.elem, field),
        Type::Group(g) => check_type(&g.elem, field),
        Type::Path(p) => {
            if let Some(seg) = p.path.segments.last() {
                match seg.ident.to_string().as_str() {
                    "usize" | "isize" => {
                        return bad("usize and isize have no fixed size on the wire; use u64/i64 (or u32/i32)")
                    }
                    "char" => {
                        return bad(
                            "a Rust char is not a .NET char: use u16 for a UTF-16 code unit (C# char) or nino::Rune",
                        )
                    }
                    "str" => return bad("use String"),
                    _ => {}
                }
                if let PathArguments::AngleBracketed(a) = &seg.arguments {
                    for g in &a.args {
                        if let GenericArgument::Type(t) = g {
                            check_type(t, field)?;
                        }
                    }
                }
            }
            Ok(())
        }
        _ => Ok(()),
    }
}

/// The `[NinoTypeLayout]` string, parsed.
struct Layout {
    format: Format,
    header: String,
    raw: bool,
    track: bool,
    type_id: i32,
    /// name, encoding, key, former keys.
    members: Vec<(String, String, Option<i32>, Vec<i32>)>,
    raw_body: String,
}

fn split_top(s: &str, sep: char) -> Vec<String> {
    let (mut parts, mut depth, mut cur) = (Vec::new(), 0i32, String::new());
    for c in s.chars() {
        match c {
            '(' | '<' | '{' | '[' => depth += 1,
            ')' | '>' | '}' | ']' => depth -= 1,
            _ => {}
        }
        if c == sep && depth == 0 {
            parts.push(std::mem::take(&mut cur));
        } else {
            cur.push(c);
        }
    }
    parts.push(cur);
    parts
}

fn parse_hex(s: &str) -> Option<i32> {
    u32::from_str_radix(s.strip_prefix("0x")?, 16).ok().map(|v| v as i32)
}

fn parse_layout(lit: &LitStr) -> syn::Result<Layout> {
    let text = lit.value();
    let err = |m: &str| Error::new(lit.span(), format!("layout: {m}"));
    let mut get = std::collections::HashMap::new();
    for part in text.split("; ") {
        let (k, v) = part.split_once('=').ok_or_else(|| err("expected key=value parts separated by \"; \""))?;
        get.insert(k.to_string(), v.to_string());
    }
    let field = |k: &str| get.get(k).cloned().ok_or_else(|| err(&format!("no {k}=")));
    let format = match field("format")?.as_str() {
        "Positional" => Format::Plain,
        "VersionTolerant" => Format::Tolerant,
        "Keyed" => Format::Keyed,
        f => return Err(err(&format!("unknown format {f}"))),
    };
    let raw = field("raw")? == "true";
    let body = field("members")?;
    let mut members = Vec::new();
    if !raw && !body.is_empty() {
        for entry in split_top(&body, ';') {
            if entry.is_empty() {
                continue;
            }
            let mut parts = entry.splitn(3, ':');
            let name = parts.next().unwrap_or_default().to_string();
            let _ty = parts.next().ok_or_else(|| err(&format!("member {entry}: no type")))?;
            let mut enc = parts.next().ok_or_else(|| err(&format!("member {entry}: no encoding")))?.to_string();
            let (mut key, mut former) = (None, Vec::new());
            if let Some(i) = enc.find(":key=") {
                let rest = enc[i + 5..].to_string();
                enc.truncate(i);
                let (k, f) = match rest.split_once(":former=") {
                    Some((k, f)) => (k.to_string(), Some(f.to_string())),
                    None => (rest, None),
                };
                key = Some(parse_hex(&k).ok_or_else(|| err(&format!("member {name}: bad key {k}")))?);
                if let Some(f) = f {
                    for x in f.split(',') {
                        former.push(parse_hex(x).ok_or_else(|| err(&format!("member {name}: bad former key {x}")))?);
                    }
                }
            }
            members.push((name, enc, key, former));
        }
    }
    Ok(Layout {
        format,
        header: field("header")?,
        raw,
        track: field("trackReferences")? == "true",
        type_id: parse_hex(&field("typeId")?).ok_or_else(|| err("bad typeId"))?,
        members,
        raw_body: body,
    })
}

fn expand(input: &DeriveInput, side: Side) -> syn::Result<Tokens> {
    let opts = type_opts(&input.attrs)?;
    let krate = opts.krate.clone().unwrap_or_else(|| syn::parse_quote!(::nino));
    match &input.data {
        Data::Struct(s) => expand_struct(input, s, opts, &krate, side),
        Data::Enum(e) => expand_enum(input, e, opts, &krate, side),
        Data::Union(u) => Err(Error::new(u.union_token.span, "a union has no Nino encoding")),
    }
}

// ------------------------------------------------------------------------------------------------- structs

fn expand_struct(
    input: &DeriveInput,
    data: &syn::DataStruct,
    opts: TypeOpts,
    krate: &syn::Path,
    side: Side,
) -> syn::Result<Tokens> {
    let layout = opts.layout.as_ref().map(parse_layout).transpose()?;
    if let Some(l) = &layout {
        if l.track {
            return Err(Error::new(
                opts.layout.as_ref().map_or(Span::call_site(), |s| s.span()),
                "reference tracking (trackReferences=true) is not supported by this runtime",
            ));
        }
    }
    // Format and kind: from the attributes, else from the layout, else plain class.
    let format = match (opts.format, &layout) {
        (Some((f, span)), Some(l)) if f != l.format => {
            return Err(Error::new(span, format!("the layout says format {:?}", l.format)));
        }
        (Some((f, _)), _) => f,
        (None, Some(l)) => l.format,
        (None, None) => Format::Plain,
    };
    let layout_kind = layout.as_ref().map(|l| match (l.raw, l.header.as_str()) {
        (true, _) => Kind::Raw,
        (false, "None") => Kind::Struct,
        (false, "TypeId") => Kind::Open,
        _ => Kind::Class,
    });
    let kind = match (opts.kind, layout_kind) {
        (Some((k, span)), Some(lk)) if k != lk => {
            return Err(Error::new(span, format!("the layout says kind {lk:?}")));
        }
        (Some((k, _)), _) => k,
        (None, Some(lk)) => lk,
        (None, None) => Kind::Class,
    };
    if kind == Kind::Raw && format != Format::Plain {
        return Err(Error::new(Span::call_site(), "a raw struct has no format: it is its memory"));
    }
    if kind != Kind::Raw && (opts.pack.is_some() || opts.size.is_some()) {
        return Err(Error::new(Span::call_site(), "pack and size apply to kind = \"raw\" only"));
    }

    let fields: Vec<&syn::Field> = match &data.fields {
        Fields::Named(n) => n.named.iter().collect(),
        Fields::Unit => Vec::new(),
        Fields::Unnamed(u) => {
            return Err(Error::new(u.span(), "tuple structs have no member names: use named fields"));
        }
    };
    let mut members = Vec::new();
    for f in &fields {
        let fo = field_opts(f)?;
        let ident = f.ident.as_ref().ok_or_else(|| Error::new(f.span(), "unnamed field"))?;
        if fo.skip {
            if fo.index.is_some() || fo.key.is_some() || fo.utf8.is_some() {
                return Err(Error::new(
                    ident.span(),
                    format!("field `{ident}`: a skipped field takes no other attribute"),
                ));
            }
            continue;
        }
        check_type(&f.ty, ident)?;
        if let Some(span) = fo.utf8 {
            let ok = match segment(&f.ty) {
                Some((s, _)) if s == "String" => true,
                Some((s, Some(inner))) if s == "Option" => matches!(segment(inner), Some((i, _)) if i == "String"),
                _ => false,
            };
            if !ok {
                return Err(Error::new(span, format!("field `{ident}`: utf8 applies to String or Option<String>")));
            }
            if kind == Kind::Raw {
                return Err(Error::new(span, format!("field `{ident}`: a raw struct holds no strings")));
            }
        }
        if format != Format::Keyed {
            if let Some((_, span)) = fo.key.as_ref().or(fo.former.first()) {
                return Err(Error::new(*span, format!("field `{ident}`: key and former apply to format = \"keyed\"")));
            }
        }
        let cs_name = fo.name.clone().unwrap_or_else(|| pascal(ident));
        members.push(Member { ident, ty: &f.ty, opts: fo, cs_name });
    }
    // The .NET member order: by index when given (all or none), else declaration order.
    let indexed = members.iter().filter(|m| m.opts.index.is_some()).count();
    if indexed != 0 && indexed != members.len() {
        let m = members.iter().find(|m| m.opts.index.is_none()).map_or(Span::call_site(), |m| m.ident.span());
        return Err(Error::new(m, "every field (or none) has an index"));
    }
    if indexed != 0 {
        members.sort_by_key(|m| m.opts.index.map(|i| i.0));
        for w in members.windows(2) {
            if w[0].opts.index.map(|i| i.0) == w[1].opts.index.map(|i| i.0) {
                let span = w[1].opts.index.map_or(Span::call_site(), |i| i.1);
                return Err(Error::new(span, format!("field `{}`: index used twice", w[1].ident)));
            }
        }
    }
    // Keys: unique, former names included.
    let mut keys: Vec<(i32, Vec<i32>)> = Vec::new();
    if format == Format::Keyed {
        let mut seen: std::collections::HashMap<i32, String> = std::collections::HashMap::new();
        for m in &members {
            let (key_name, span) = m.opts.key.clone().unwrap_or((m.cs_name.clone(), m.ident.span()));
            let key = fnv(&key_name);
            let mut all = vec![(key, key_name, span)];
            all.extend(m.opts.former.iter().map(|(n, s)| (fnv(n), n.clone(), *s)));
            for (k, n, s) in &all {
                if let Some(other) = seen.insert(*k, n.clone()) {
                    return Err(Error::new(
                        *s,
                        format!(
                            "field `{}`: key \"{n}\" (0x{:08X}) is already the key of \"{other}\"",
                            m.ident, *k as u32
                        ),
                    ));
                }
            }
            keys.push((key, all.iter().skip(1).map(|x| x.0).collect()));
        }
    }
    let mut checks = Tokens::new();
    if let Some(l) = &layout {
        checks = check_layout(
            l,
            opts.layout.as_ref().map_or(Span::call_site(), |s| s.span()),
            &members,
            &keys,
            kind,
            krate,
            &input.ident,
        )?;
    }
    let type_id = match (&opts.id, &layout) {
        (Some(Id::Name(s)), _) => quote!(#krate::type_id(#s)),
        (Some(Id::Value(v)), _) => quote!(#v),
        (None, Some(l)) if l.type_id != 0 && kind != Kind::Raw => {
            let v = l.type_id;
            quote!(#v)
        }
        _ => quote!(0),
    };

    let name = &input.ident;
    if kind == Kind::Raw {
        if !input.generics.params.is_empty() {
            return Err(Error::new(input.generics.span(), "a raw struct is not generic"));
        }
        return Ok(raw_struct(name, &members, &opts, krate, side, checks));
    }
    let (impl_g, ty_g, where_g) = input.generics.split_for_impl();
    let mut preds: Vec<Tokens> = where_g.map(|w| w.predicates.iter().map(|p| quote!(#p)).collect()).unwrap_or_default();
    for p in input.generics.type_params() {
        let id = &p.ident;
        preds.push(quote!(#id: 'static));
    }
    let bound = match side {
        Side::Read => quote!(#krate::NinoRead),
        Side::Write => quote!(#krate::NinoWrite),
    };
    // Bounds only for fields that mention a type parameter (a bound on a recursive type would never resolve).
    let params: Vec<String> = input.generics.type_params().map(|p| p.ident.to_string()).collect();
    for m in members.iter().filter(|m| m.opts.utf8.is_none()) {
        let ty = m.ty;
        let mentions = quote!(#ty).into_iter().any(|t| mentions_param(t, &params));
        if mentions {
            preds.push(quote!(#ty: #bound));
        }
    }
    let where_clause = quote!(where #(#preds,)*);
    let deferrable = opts.deferrable;
    let body = match side {
        Side::Read => read_body(&members, &keys, format, krate),
        Side::Write => write_body(&members, &keys, format, krate),
    };
    let tokens = match (side, kind) {
        (Side::Read, Kind::Class) => quote! {
            impl #impl_g #krate::NinoReadBody for #name #ty_g #where_clause {
                const DEFERRABLE: bool = #deferrable;
                const TYPE_ID: i32 = #type_id;
                #[inline]
                fn read_body(&mut self, r: &mut #krate::Reader<'_>) -> #krate::Result<()> {
                    #body
                    ::core::result::Result::Ok(())
                }
            }
            impl #impl_g #krate::NinoRead for #name #ty_g #where_clause {
                const MAY_DEFER: bool = true;
                #[inline]
                fn read(r: &mut #krate::Reader<'_>) -> #krate::Result<Self> {
                    r.read_sealed_value()
                }
                #[inline]
                fn read_option(r: &mut #krate::Reader<'_>) -> #krate::Result<::core::option::Option<Self>> {
                    r.read_sealed()
                }
            }
        },
        (Side::Write, Kind::Class) => quote! {
            impl #impl_g #krate::NinoWriteBody for #name #ty_g #where_clause {
                const DEFERRABLE: bool = #deferrable;
                const TYPE_ID: i32 = #type_id;
                #[inline]
                fn write_body<'a>(&'a self, w: &mut #krate::Writer<'a>) -> #krate::Result<()> {
                    #body
                    ::core::result::Result::Ok(())
                }
            }
            impl #impl_g #krate::NinoWrite for #name #ty_g #where_clause {
                #[inline]
                fn write<'a>(&'a self, w: &mut #krate::Writer<'a>) -> #krate::Result<()> {
                    w.write_sealed(::core::option::Option::Some(self))
                }
                #[inline]
                fn write_option<'a>(
                    value: ::core::option::Option<&'a Self>,
                    w: &mut #krate::Writer<'a>,
                ) -> #krate::Result<()> {
                    w.write_sealed(value)
                }
            }
        },
        (Side::Read, Kind::Open) => quote! {
            impl #impl_g #krate::NinoReadBody for #name #ty_g #where_clause {
                const DEFERRABLE: bool = #deferrable;
                const TYPE_ID: i32 = #type_id;
                #[inline]
                fn read_body(&mut self, r: &mut #krate::Reader<'_>) -> #krate::Result<()> {
                    #body
                    ::core::result::Result::Ok(())
                }
            }
            impl #impl_g #krate::NinoRead for #name #ty_g #where_clause {
                const MAY_DEFER: bool = true;
                const MIN_SIZE: usize = 4;
                #[inline]
                fn read(r: &mut #krate::Reader<'_>) -> #krate::Result<Self> {
                    Self::read_option(r)?.ok_or_else(|| #krate::Error::of(#krate::ErrorKind::UnexpectedNull))
                }
                /// Its type id (0 = null), then its body: a class that is not sealed, read as itself.
                #[inline]
                fn read_option(r: &mut #krate::Reader<'_>) -> #krate::Result<::core::option::Option<Self>> {
                    let id = r.i32()?;
                    if id == 0 {
                        return ::core::result::Result::Ok(::core::option::Option::None);
                    }
                    if id != <Self as #krate::NinoReadBody>::TYPE_ID {
                        return ::core::result::Result::Err(#krate::Error::new(#krate::ErrorKind::UnknownTypeId, id));
                    }
                    r.read_class().map(::core::option::Option::Some)
                }
            }
            const _: () = assert!(#type_id != 0, "kind = \"open\" needs a type id: #[nino(id = ...)] or a layout");
        },
        (Side::Write, Kind::Open) => quote! {
            impl #impl_g #krate::NinoWriteBody for #name #ty_g #where_clause {
                const DEFERRABLE: bool = #deferrable;
                const TYPE_ID: i32 = #type_id;
                #[inline]
                fn write_body<'a>(&'a self, w: &mut #krate::Writer<'a>) -> #krate::Result<()> {
                    #body
                    ::core::result::Result::Ok(())
                }
            }
            impl #impl_g #krate::NinoWrite for #name #ty_g #where_clause {
                #[inline]
                fn write<'a>(&'a self, w: &mut #krate::Writer<'a>) -> #krate::Result<()> {
                    w.i32(<Self as #krate::NinoWriteBody>::TYPE_ID);
                    w.write_class(self)
                }
                #[inline]
                fn write_option<'a>(
                    value: ::core::option::Option<&'a Self>,
                    w: &mut #krate::Writer<'a>,
                ) -> #krate::Result<()> {
                    match value {
                        ::core::option::Option::Some(v) => v.write(w),
                        ::core::option::Option::None => {
                            w.i32(0);
                            ::core::result::Result::Ok(())
                        }
                    }
                }
            }
        },
        (Side::Read, _) => {
            let tys: Vec<&Type> = members.iter().filter(|m| m.opts.utf8.is_none()).map(|m| m.ty).collect();
            let min = if format == Format::Plain {
                let parts = members.iter().map(|m| match m.opts.utf8 {
                    Some(_) => quote!(4usize),
                    None => {
                        let ty = m.ty;
                        quote!(<#ty as #krate::NinoRead>::MIN_SIZE)
                    }
                });
                quote!(0usize #(.saturating_add(#parts))*)
            } else {
                quote!(4usize)
            };
            quote! {
                impl #impl_g #krate::NinoReadBody for #name #ty_g #where_clause {
                    const TYPE_ID: i32 = #type_id;
                    #[inline]
                    fn read_body(&mut self, r: &mut #krate::Reader<'_>) -> #krate::Result<()> {
                        #body
                        ::core::result::Result::Ok(())
                    }
                }
                impl #impl_g #krate::NinoRead for #name #ty_g #where_clause {
                    const MAY_DEFER: bool = false #(|| <#tys as #krate::NinoRead>::MAY_DEFER)*;
                    const MIN_SIZE: usize = #min;
                    /// A struct: its members, no header. Counted towards the depth limit, as it may hold itself.
                    #[inline]
                    fn read(r: &mut #krate::Reader<'_>) -> #krate::Result<Self> {
                        r.enter_struct()?;
                        let mut value = <Self as ::core::default::Default>::default();
                        let result = #krate::NinoReadBody::read_body(&mut value, r);
                        r.exit_struct();
                        result.map(|()| value)
                    }
                }
            }
        }
        (Side::Write, _) => quote! {
            impl #impl_g #krate::NinoWriteBody for #name #ty_g #where_clause {
                const TYPE_ID: i32 = #type_id;
                #[inline]
                fn write_body<'a>(&'a self, w: &mut #krate::Writer<'a>) -> #krate::Result<()> {
                    #body
                    ::core::result::Result::Ok(())
                }
            }
            impl #impl_g #krate::NinoWrite for #name #ty_g #where_clause {
                #[inline]
                fn write<'a>(&'a self, w: &mut #krate::Writer<'a>) -> #krate::Result<()> {
                    #krate::NinoWriteBody::write_body(self, w)
                }
            }
        },
    };
    // Layout checks once, on the read side (or the write side when only it is derived, both is harmless).
    Ok(quote! {
        #tokens
        const _: () = { #checks };
    })
}

fn mentions_param(t: proc_macro2::TokenTree, params: &[String]) -> bool {
    match t {
        proc_macro2::TokenTree::Ident(i) => params.iter().any(|p| i == p),
        proc_macro2::TokenTree::Group(g) => g.stream().into_iter().any(|t| mentions_param(t, params)),
        _ => false,
    }
}

/// The expression reading member `m` (into `self`).
fn read_member(m: &Member, krate: &syn::Path) -> Tokens {
    let field = m.ident;
    let ty = m.ty;
    if m.opts.utf8.is_some() {
        let optional = matches!(segment(ty), Some((s, _)) if s == "Option");
        return if optional {
            quote!(r.utf8()?)
        } else {
            quote!(r.utf8()?.ok_or_else(|| #krate::Error::of(#krate::ErrorKind::UnexpectedNull))?)
        };
    }
    quote_spanned! {ty.span()=>
        r.read_in::<#ty>(|p: &mut dyn ::core::any::Any| -> ::core::option::Option<&mut dyn ::core::any::Any> {
            ::core::option::Option::Some(&mut p.downcast_mut::<Self>()?.#field as &mut dyn ::core::any::Any)
        })?
    }
}

fn write_member(m: &Member) -> Tokens {
    let field = m.ident;
    let ty = m.ty;
    if m.opts.utf8.is_some() {
        let optional = matches!(segment(ty), Some((s, _)) if s == "Option");
        return if optional {
            quote!(w.utf8(self.#field.as_deref())?;)
        } else {
            quote!(w.utf8(::core::option::Option::Some(&self.#field))?;)
        };
    }
    quote_spanned!(ty.span()=> w.write(&self.#field)?;)
}

fn read_body(members: &[Member], keys: &[(i32, Vec<i32>)], format: Format, krate: &syn::Path) -> Tokens {
    match format {
        Format::Plain => {
            let reads = members.iter().map(|m| {
                let f = m.ident;
                let e = read_member(m, krate);
                quote!(self.#f = #e;)
            });
            quote!(let _ = &r; #(#reads)*)
        }
        Format::Tolerant => {
            let reads = members.iter().map(|m| {
                let f = m.ident;
                let e = read_member(m, krate);
                quote!(if r.has_more() { self.#f = #e; })
            });
            quote! {
                let body = r.begin_region()?;
                #(#reads)*
                r.end_region(body, self)?;
            }
        }
        Format::Keyed => {
            let arms = members.iter().zip(keys).map(|(m, (key, former))| {
                let f = m.ident;
                let e = read_member(m, krate);
                quote! {
                    if key == #key #(|| key == #former)* {
                        self.#f = #e;
                        r.end_member(member, self)?;
                        continue;
                    }
                }
            });
            quote! {
                let body = r.begin_region()?;
                while r.has_more() {
                    let key = r.i32()?;
                    let member = r.begin_region()?;
                    #(#arms)*
                    r.end_region(member, self)?;
                }
                r.end_region(body, self)?;
            }
        }
    }
}

fn write_body(members: &[Member], keys: &[(i32, Vec<i32>)], format: Format, _krate: &syn::Path) -> Tokens {
    match format {
        Format::Plain => {
            let writes = members.iter().map(write_member);
            quote!(let _ = &w; #(#writes)*)
        }
        Format::Tolerant => {
            let writes = members.iter().map(write_member);
            quote! {
                let body = w.begin_region();
                #(#writes)*
                w.end_region(body)?;
            }
        }
        Format::Keyed => {
            let writes = members.iter().zip(keys).map(|(m, (key, _))| {
                let wm = write_member(m);
                quote! {
                    w.i32(#key);
                    let member = w.begin_region();
                    #wm
                    w.end_region(member)?;
                }
            });
            quote! {
                let body = w.begin_region();
                #(#writes)*
                w.end_region(body)?;
            }
        }
    }
}

/// Compile-time checks of the struct against its published layout: member names and order, keys, string
/// encodings, sizes of primitives; for a raw struct, its size and field offsets.
fn check_layout(
    l: &Layout,
    span: Span,
    members: &[Member],
    keys: &[(i32, Vec<i32>)],
    kind: Kind,
    krate: &syn::Path,
    name: &syn::Ident,
) -> syn::Result<Tokens> {
    let err = |m: String| Err(Error::new(span, format!("layout: {m}")));
    let header_ok = match kind {
        Kind::Class => l.header == "NullFlag",
        Kind::Open => l.header == "TypeId",
        Kind::Struct | Kind::Raw => l.header == "None",
    };
    if !header_ok {
        return err(format!("header={} does not match kind {kind:?}", l.header));
    }
    if kind == Kind::Raw {
        // raw(N){A:byte@0:raw(1),...}: the size and each field's offset, checked by the compiler.
        let Some(rest) = l.raw_body.strip_prefix("raw(") else {
            return Ok(Tokens::new());
        };
        let (n, fields) = rest.split_once(')').unwrap_or((rest, ""));
        let size: usize = n.parse().map_err(|_| Error::new(span, "layout: bad raw size"))?;
        let mut asserts = vec![quote!(assert!(<#name as #krate::Raw>::SIZE == #size, "layout: the raw size differs");)];
        let fields = fields.trim_start_matches('{').trim_end_matches('}');
        let parsed: Vec<(String, usize)> = if fields.is_empty() {
            Vec::new()
        } else {
            split_top(fields, ',')
                .iter()
                .filter_map(|f| {
                    let (name, rest) = f.split_once(':')?;
                    let (_, rest) = rest.split_once('@')?;
                    let (off, _) = rest.split_once(':')?;
                    Some((name.to_string(), off.parse().ok()?))
                })
                .collect()
        };
        if parsed.len() != members.len() {
            return err(format!("{} raw fields, the struct has {}", parsed.len(), members.len()));
        }
        for (i, ((name, off), m)) in parsed.iter().zip(members).enumerate() {
            if *name != m.cs_name {
                return Err(Error::new(m.ident.span(), format!("layout: field {i} is `{name}`, not `{}`", m.cs_name)));
            }
            let msg = format!("layout: field `{}` is not at offset {off}", m.ident);
            asserts.push(quote!(assert!(OFFSETS[#i] == #off, #msg);));
        }
        return Ok(quote!(#(#asserts)*));
    }
    if l.members.len() != members.len() {
        return err(format!(
            "{} members ({}), the struct has {}",
            l.members.len(),
            l.members.iter().map(|m| m.0.as_str()).collect::<Vec<_>>().join(", "),
            members.len()
        ));
    }
    let mut asserts = Vec::new();
    for (i, ((name, enc, key, former), m)) in l.members.iter().zip(members).enumerate() {
        let here = m.ident.span();
        if *name != m.cs_name {
            return Err(Error::new(
                here,
                format!("layout: member {i} is `{name}`; this field is `{}` (#[nino(name = ...)] or index)", m.cs_name),
            ));
        }
        let utf8 = enc == "utf8";
        if utf8 != m.opts.utf8.is_some() {
            return Err(Error::new(
                here,
                format!(
                    "layout: field `{}` is written as {enc}{}",
                    m.ident,
                    if utf8 { ": add #[nino(utf8)]" } else { "" }
                ),
            ));
        }
        if let (Some(k), Some((mine, my_former))) = (key, keys.get(i)) {
            if k != mine {
                return Err(Error::new(
                    here,
                    format!(
                        "layout: field `{}` has key 0x{:08X}, not 0x{:08X} (#[nino(key = ...)])",
                        m.ident, *k as u32, *mine as u32
                    ),
                ));
            }
            if former != my_former {
                return Err(Error::new(here, format!("layout: field `{}` has other former keys", m.ident)));
            }
        }
        // raw(n) for a Rust primitive: the size must match.
        if let (Some(n), Some((prim, _))) = (enc.strip_prefix("raw(").and_then(|r| r.strip_suffix(')')), segment(m.ty))
        {
            if let (Ok(n), true) = (n.parse::<usize>(), PRIMITIVES.contains(&prim.as_str())) {
                let ty = m.ty;
                let msg = format!("layout: field `{}` is {n} bytes", m.ident);
                asserts.push(quote!(assert!(<#ty as #krate::Raw>::SIZE == #n, #msg);));
            }
        }
    }
    Ok(quote!(#(#asserts)*))
}

const PRIMITIVES: [&str; 12] = ["i8", "u8", "i16", "u16", "i32", "u32", "i64", "u64", "f32", "f64", "bool", "i128"];

/// An unmanaged struct written as its memory in the canonical layout (the specification, 5.2).
fn raw_struct(
    name: &syn::Ident,
    members: &[Member],
    opts: &TypeOpts,
    krate: &syn::Path,
    side: Side,
    checks: Tokens,
) -> Tokens {
    let n = members.len();
    let tys: Vec<&Type> = members.iter().map(|m| m.ty).collect();
    let fields: Vec<&syn::Ident> = members.iter().map(|m| m.ident).collect();
    let idx: Vec<usize> = (0..n).collect();
    let pack = opts.pack.unwrap_or(usize::MAX);
    let min_size = opts.size.unwrap_or(0);
    let layout_fn = format_ident!("__nino_raw_layout_{}", name.to_string().to_lowercase());
    let layout = quote! {
        #[doc(hidden)]
        #[allow(non_snake_case, clippy::all)]
        const fn #layout_fn() -> ([usize; #n], usize, usize) {
            let pack = #pack;
            let mut offsets = [0usize; #n];
            let mut end = 0usize;
            let mut align = 1usize;
            #(
                let a = if <#tys as #krate::Raw>::ALIGN < pack { <#tys as #krate::Raw>::ALIGN } else { pack };
                let off = (end + a - 1) / a * a;
                offsets[#idx] = off;
                if off + <#tys as #krate::Raw>::SIZE > end { end = off + <#tys as #krate::Raw>::SIZE; }
                if a > align { align = a; }
            )*
            let mut size = (end + align - 1) / align * align;
            if size < #min_size { size = #min_size; }
            if size == 0 { size = 1; }
            (offsets, size, align)
        }
    };
    match side {
        Side::Read => quote! {
            #layout
            impl #krate::Raw for #name {
                const SIZE: usize = #layout_fn().1;
                const ALIGN: usize = #layout_fn().2;
                fn decode_raw(bytes: &[u8]) -> #krate::Result<Self> {
                    const OFFSETS: [usize; #n] = #layout_fn().0;
                    let bytes = bytes.get(..<Self as #krate::Raw>::SIZE).ok_or_else(|| #krate::Error::of(#krate::ErrorKind::UnexpectedEnd))?;
                    let _ = &OFFSETS;
                    let value = Self {
                        #( #fields: <#tys as #krate::Raw>::decode_raw(bytes.get(OFFSETS[#idx]..).unwrap_or_default())?, )*
                    };
                    // Padding is zero (what .NET writes): written back, the bytes are the same.
                    let mut check = [0u8; <Self as #krate::Raw>::SIZE];
                    #krate::Raw::encode_raw(&value, &mut check);
                    if check[..] != *bytes {
                        return ::core::result::Result::Err(#krate::Error::of(#krate::ErrorKind::InvalidBits));
                    }
                    ::core::result::Result::Ok(value)
                }
                fn encode_raw(&self, out: &mut [u8]) {
                    const OFFSETS: [usize; #n] = #layout_fn().0;
                    let _ = (&OFFSETS, &out);
                    #(
                        if let ::core::option::Option::Some(o) = out.get_mut(OFFSETS[#idx]..) {
                            #krate::Raw::encode_raw(&self.#fields, o);
                        }
                    )*
                }
            }
            impl #krate::NinoRead for #name {
                const MIN_SIZE: usize = <Self as #krate::Raw>::SIZE;
                #[inline]
                fn read(r: &mut #krate::Reader<'_>) -> #krate::Result<Self> {
                    <Self as #krate::Raw>::decode_raw(r.take(<Self as #krate::Raw>::SIZE)?)
                }
            }
            const _: () = {
                const OFFSETS: [usize; #n] = #layout_fn().0;
                let _ = &OFFSETS;
                #checks
            };
        },
        Side::Write => quote! {
            impl #krate::NinoWrite for #name {
                #[inline]
                fn write<'a>(&'a self, w: &mut #krate::Writer<'a>) -> #krate::Result<()> {
                    let out = w.reserve(<Self as #krate::Raw>::SIZE);
                    #krate::Raw::encode_raw(self, out);
                    ::core::result::Result::Ok(())
                }
            }
        },
    }
}

// ------------------------------------------------------------------------------------------------- enums

fn expand_enum(
    input: &DeriveInput,
    data: &syn::DataEnum,
    opts: TypeOpts,
    krate: &syn::Path,
    side: Side,
) -> syn::Result<Tokens> {
    if opts.format.is_some() || opts.kind.is_some() || opts.layout.is_some() || opts.id.is_some() {
        return Err(Error::new(
            input.ident.span(),
            "an enum is a polymorphic slot: format, kind, layout and id go on its variants' types",
        ));
    }
    if !input.generics.params.is_empty() {
        return Err(Error::new(input.generics.span(), "a polymorphic enum is not generic"));
    }
    if data.variants.is_empty() {
        return Err(Error::new(input.ident.span(), "a polymorphic enum has at least one variant"));
    }
    let name = &input.ident;
    let mut ids = Vec::new();
    let mut variants = Vec::new();
    let mut inners = Vec::new();
    let mut boxed = Vec::new();
    for v in &data.variants {
        let ty = match &v.fields {
            Fields::Unnamed(u) if u.unnamed.len() == 1 => &u.unnamed[0].ty,
            _ => {
                return Err(Error::new(
                    v.ident.span(),
                    format!("variant `{}`: one unnamed field, the type it holds (`{}(Box<T>)`)", v.ident, v.ident),
                ))
            }
        };
        let (inner, is_box) = match segment(ty) {
            Some((s, Some(inner))) if s == "Box" => (inner, true),
            _ => (ty, false),
        };
        let mut id = None;
        for attr in v.attrs.iter().filter(|a| a.path().is_ident("nino")) {
            attr.parse_nested_meta(|meta| {
                if meta.path.is_ident("id") {
                    id = Some(parse_id(&meta)?);
                    Ok(())
                } else {
                    Err(meta.error("a variant takes only id"))
                }
            })?;
        }
        let body_trait = match side {
            Side::Read => quote!(#krate::NinoReadBody),
            Side::Write => quote!(#krate::NinoWriteBody),
        };
        ids.push(match id {
            Some(Id::Name(s)) => quote!(#krate::type_id(#s)),
            Some(Id::Value(v)) => quote!(#v),
            None => quote_spanned!(inner.span()=> <#inner as #body_trait>::TYPE_ID),
        });
        variants.push(&v.ident);
        inners.push(inner);
        boxed.push(is_box);
    }
    let names: Vec<String> = variants.iter().map(|v| v.to_string()).collect();
    let mut asserts = Vec::new();
    for (i, id) in ids.iter().enumerate() {
        let msg =
            format!("variant `{}`: its type has no type id (#[nino(id = ...)] on the type or the variant)", names[i]);
        asserts.push(quote!(assert!(#id != 0, #msg);));
        for (j, other) in ids.iter().enumerate().skip(i + 1) {
            let msg = format!("variants `{}` and `{}` have the same type id", names[i], names[j]);
            asserts.push(quote!(assert!(#id != #other, #msg);));
        }
    }
    let tokens = match side {
        Side::Read => {
            let reads = variants.iter().zip(&inners).zip(&boxed).zip(&ids).map(|(((v, inner), b), id)| {
                let make = if *b {
                    quote!(Self::#v(::std::boxed::Box::new(r.read_class::<#inner>()?)))
                } else {
                    quote!(Self::#v(r.read_class::<#inner>()?))
                };
                quote!(if id == #id { #make } else)
            });
            let bodies = variants.iter().zip(&boxed).map(|(v, b)| {
                if *b {
                    quote!(Self::#v(x) => &mut **x as &mut dyn ::core::any::Any,)
                } else {
                    quote!(Self::#v(x) => x as &mut dyn ::core::any::Any,)
                }
            });
            quote! {
                impl #krate::NinoRead for #name {
                    const MAY_DEFER: bool = true;
                    const MIN_SIZE: usize = 4;
                    fn read(r: &mut #krate::Reader<'_>) -> #krate::Result<Self> {
                        Self::read_option(r)?.ok_or_else(|| #krate::Error::of(#krate::ErrorKind::UnexpectedNull))
                    }
                    /// The type id (0 = null), then the body of that type: only the types of this enum.
                    fn read_option(r: &mut #krate::Reader<'_>) -> #krate::Result<::core::option::Option<Self>> {
                        let id = r.i32()?;
                        if id == 0 {
                            return ::core::result::Result::Ok(::core::option::Option::None);
                        }
                        let mark = r.mark();
                        let value = #(#reads)* {
                            return ::core::result::Result::Err(#krate::Error::new(#krate::ErrorKind::UnknownTypeId, id));
                        };
                        r.trail(mark, #krate::Step::Field(|p| {
                            let v = p.downcast_mut::<Self>()?;
                            ::core::option::Option::Some(match v { #(#bodies)* })
                        }));
                        ::core::result::Result::Ok(::core::option::Option::Some(value))
                    }
                }
            }
        }
        Side::Write => {
            let writes = variants.iter().zip(&boxed).zip(&ids).map(|((v, b), id)| {
                let x = if *b { quote!(&**x) } else { quote!(x) };
                quote!(Self::#v(x) => { w.i32(#id); w.write_class(#x) })
            });
            quote! {
                impl #krate::NinoWrite for #name {
                    fn write<'a>(&'a self, w: &mut #krate::Writer<'a>) -> #krate::Result<()> {
                        match self { #(#writes)* }
                    }
                    fn write_option<'a>(
                        value: ::core::option::Option<&'a Self>,
                        w: &mut #krate::Writer<'a>,
                    ) -> #krate::Result<()> {
                        match value {
                            ::core::option::Option::Some(v) => v.write(w),
                            ::core::option::Option::None => {
                                w.i32(0);
                                ::core::result::Result::Ok(())
                            }
                        }
                    }
                }
            }
        }
    };
    Ok(quote! {
        #tokens
        const _: () = { #(#asserts)* };
    })
}
