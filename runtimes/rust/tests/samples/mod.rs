//! Sample `[NinoType]`s with hand-written codecs, in the shape the Scriban templates are meant to produce: the
//! types of the specification's examples (namespace `Spec`), so their bytes can be checked against it.
#![allow(dead_code)]

use nino::{field, type_id, Error, ErrorKind, NinoBody, NinoRead, NinoWrite, Reader, Result, Step, Utf8, Writer};

// ---------------------------------------------------------------------- [NinoType] sealed class Small

/// `[NinoType] public sealed class Small { public int A; public string B; }`
#[derive(Debug, Default, Clone, PartialEq)]
pub struct Small {
    pub a: i32,
    pub b: Option<String>,
}

impl NinoBody for Small {
    fn read_body(&mut self, r: &mut Reader<'_>) -> Result<()> {
        self.a = r.i32()?;
        self.b = r.read()?;
        Ok(())
    }

    fn write_body<'a>(&'a self, w: &mut Writer<'a>) -> Result<()> {
        w.i32(self.a);
        w.write(&self.b)
    }
}

impl NinoRead for Small {
    fn read(r: &mut Reader<'_>) -> Result<Self> {
        r.read_sealed_value()
    }

    fn read_option(r: &mut Reader<'_>) -> Result<Option<Self>> {
        r.read_sealed()
    }
}

impl NinoWrite for Small {
    fn write<'a>(&'a self, w: &mut Writer<'a>) -> Result<()> {
        w.write_sealed(Some(self))
    }

    fn write_option<'a>(value: Option<&'a Self>, w: &mut Writer<'a>) -> Result<()> {
        w.write_sealed(value)
    }
}

// ---------------------------------------------------------------------- positional class with a run of raw members

/// `sealed class Mixed { int A; byte B; long C; string D; short E; short F; }`
#[derive(Debug, Default, Clone, PartialEq)]
pub struct Mixed {
    pub a: i32,
    pub b: u8,
    pub c: i64,
    pub d: Option<String>,
    pub e: i16,
    pub f: i16,
}

impl NinoBody for Mixed {
    fn read_body(&mut self, r: &mut Reader<'_>) -> Result<()> {
        // A run of fixed-size members: one length check.
        let run = r.array::<13>()?;
        self.a = i32::from_le_bytes([run[0], run[1], run[2], run[3]]);
        self.b = run[4];
        self.c = i64::from_le_bytes([run[5], run[6], run[7], run[8], run[9], run[10], run[11], run[12]]);
        self.d = r.read()?;
        let run = r.array::<4>()?;
        self.e = i16::from_le_bytes([run[0], run[1]]);
        self.f = i16::from_le_bytes([run[2], run[3]]);
        Ok(())
    }

    fn write_body<'a>(&'a self, w: &mut Writer<'a>) -> Result<()> {
        w.i32(self.a);
        w.u8(self.b);
        w.i64(self.c);
        w.write(&self.d)?;
        w.i16(self.e);
        w.i16(self.f);
        Ok(())
    }
}

impl NinoRead for Mixed {
    fn read(r: &mut Reader<'_>) -> Result<Self> {
        r.read_sealed_value()
    }

    fn read_option(r: &mut Reader<'_>) -> Result<Option<Self>> {
        r.read_sealed()
    }
}

impl NinoWrite for Mixed {
    fn write<'a>(&'a self, w: &mut Writer<'a>) -> Result<()> {
        w.write_sealed(Some(self))
    }

    fn write_option<'a>(value: Option<&'a Self>, w: &mut Writer<'a>) -> Result<()> {
        w.write_sealed(value)
    }
}

// ---------------------------------------------------------------------- polymorphism

/// `[NinoType] public abstract class Shape { public int Id; }` with its subtypes: the declared type `Shape` reads
/// a type id, restricted to these.
#[derive(Debug, Clone, PartialEq)]
pub enum Shape {
    Circle(Box<Circle>),
    Square(Box<Square>),
}

/// `[NinoType] public sealed class Circle : Shape { public float R; }`
#[derive(Debug, Default, Clone, PartialEq)]
pub struct Circle {
    pub id: i32,
    pub r: f32,
}

/// `[NinoType] public sealed class Square : Shape { public float Side; }`
#[derive(Debug, Default, Clone, PartialEq)]
pub struct Square {
    pub id: i32,
    pub side: f32,
}

impl Circle {
    pub const TYPE_ID: i32 = type_id("Spec.Circle");
}

impl Square {
    pub const TYPE_ID: i32 = type_id("Spec.Square");
}

impl NinoBody for Circle {
    fn read_body(&mut self, r: &mut Reader<'_>) -> Result<()> {
        let run = r.array::<8>()?;
        self.id = i32::from_le_bytes([run[0], run[1], run[2], run[3]]);
        self.r = f32::from_le_bytes([run[4], run[5], run[6], run[7]]);
        Ok(())
    }

    fn write_body<'a>(&'a self, w: &mut Writer<'a>) -> Result<()> {
        w.i32(self.id);
        w.f32(self.r);
        Ok(())
    }
}

impl NinoBody for Square {
    fn read_body(&mut self, r: &mut Reader<'_>) -> Result<()> {
        self.id = r.i32()?;
        self.side = r.f32()?;
        Ok(())
    }

    fn write_body<'a>(&'a self, w: &mut Writer<'a>) -> Result<()> {
        w.i32(self.id);
        w.f32(self.side);
        Ok(())
    }
}

/// A sealed class read by itself: presence byte, then the body (`Option<Circle>`).
impl NinoRead for Circle {
    fn read(r: &mut Reader<'_>) -> Result<Self> {
        r.read_sealed_value()
    }

    fn read_option(r: &mut Reader<'_>) -> Result<Option<Self>> {
        r.read_sealed()
    }
}

impl NinoWrite for Circle {
    fn write<'a>(&'a self, w: &mut Writer<'a>) -> Result<()> {
        w.write_sealed(Some(self))
    }

    fn write_option<'a>(value: Option<&'a Self>, w: &mut Writer<'a>) -> Result<()> {
        w.write_sealed(value)
    }
}

impl NinoRead for Square {
    fn read(r: &mut Reader<'_>) -> Result<Self> {
        r.read_sealed_value()
    }

    fn read_option(r: &mut Reader<'_>) -> Result<Option<Self>> {
        r.read_sealed()
    }
}

impl NinoWrite for Square {
    fn write<'a>(&'a self, w: &mut Writer<'a>) -> Result<()> {
        w.write_sealed(Some(self))
    }

    fn write_option<'a>(value: Option<&'a Self>, w: &mut Writer<'a>) -> Result<()> {
        w.write_sealed(value)
    }
}

impl NinoRead for Shape {
    const MIN_SIZE: usize = 4;

    fn read(r: &mut Reader<'_>) -> Result<Self> {
        Self::read_option(r)?.ok_or_else(|| Error::of(ErrorKind::UnexpectedNull))
    }

    /// The type id (0 = null), then the body of that type: only the subtypes of `Shape`.
    fn read_option(r: &mut Reader<'_>) -> Result<Option<Self>> {
        let id = r.i32()?;
        Ok(Some(match id {
            0 => return Ok(None),
            Circle::TYPE_ID => Shape::Circle(Box::new(r.read_class()?)),
            Square::TYPE_ID => Shape::Square(Box::new(r.read_class()?)),
            _ => return Err(Error::new(ErrorKind::UnknownTypeId, id)),
        }))
    }
}

impl NinoWrite for Shape {
    fn write<'a>(&'a self, w: &mut Writer<'a>) -> Result<()> {
        match self {
            Shape::Circle(v) => {
                w.i32(Circle::TYPE_ID);
                w.write_class(&**v)
            }
            Shape::Square(v) => {
                w.i32(Square::TYPE_ID);
                w.write_class(&**v)
            }
        }
    }

    fn write_option<'a>(value: Option<&'a Self>, w: &mut Writer<'a>) -> Result<()> {
        match value {
            Some(v) => v.write(w),
            None => {
                w.i32(0);
                Ok(())
            }
        }
    }
}

// ---------------------------------------------------------------------- keyed and version tolerant

/// `[NinoType(format: NinoFormat.Keyed)] sealed class Keyed { int Id; [NinoFormerName("Title")] string Name; long Score; }`
#[derive(Debug, Default, Clone, PartialEq)]
pub struct Keyed {
    pub id: i32,
    pub name: Option<String>,
    pub score: i64,
}

impl Keyed {
    const ID: i32 = type_id("Id");
    const NAME: i32 = type_id("Name");
    const TITLE: i32 = type_id("Title");
    const SCORE: i32 = type_id("Score");
}

impl NinoBody for Keyed {
    fn read_body(&mut self, r: &mut Reader<'_>) -> Result<()> {
        let body = r.begin_region()?;
        while r.has_more() {
            let key = r.i32()?;
            let member = r.begin_region()?;
            match key {
                Self::ID => self.id = r.read()?,
                Self::NAME | Self::TITLE => self.name = r.read()?,
                Self::SCORE => self.score = r.read()?,
                _ => {
                    r.end_region(member, self)?;
                    continue;
                }
            }
            r.end_member(member, self)?;
        }
        r.end_region(body, self)
    }

    fn write_body<'a>(&'a self, w: &mut Writer<'a>) -> Result<()> {
        let body = w.begin_region();
        w.i32(Self::ID);
        let m = w.begin_region();
        w.i32(self.id);
        w.end_region(m)?;
        w.i32(Self::NAME);
        let m = w.begin_region();
        w.write(&self.name)?;
        w.end_region(m)?;
        w.i32(Self::SCORE);
        let m = w.begin_region();
        w.i64(self.score);
        w.end_region(m)?;
        w.end_region(body)
    }
}

impl NinoRead for Keyed {
    fn read(r: &mut Reader<'_>) -> Result<Self> {
        r.read_sealed_value()
    }

    fn read_option(r: &mut Reader<'_>) -> Result<Option<Self>> {
        r.read_sealed()
    }
}

impl NinoWrite for Keyed {
    fn write<'a>(&'a self, w: &mut Writer<'a>) -> Result<()> {
        w.write_sealed(Some(self))
    }

    fn write_option<'a>(value: Option<&'a Self>, w: &mut Writer<'a>) -> Result<()> {
        w.write_sealed(value)
    }
}

/// `[NinoType(format: VersionTolerant)] sealed class TolLeaf : TolBase { int B; }` (`TolBase { int A; }`).
#[derive(Debug, Default, Clone, PartialEq)]
pub struct TolLeaf {
    pub a: i32,
    pub b: i32,
}

impl TolLeaf {
    pub const TYPE_ID: i32 = type_id("Spec.TolLeaf");
}

impl NinoBody for TolLeaf {
    fn read_body(&mut self, r: &mut Reader<'_>) -> Result<()> {
        let body = r.begin_region()?;
        if r.has_more() {
            self.a = r.read()?;
        }
        if r.has_more() {
            self.b = r.read()?;
        }
        r.end_region(body, self)
    }

    fn write_body<'a>(&'a self, w: &mut Writer<'a>) -> Result<()> {
        let body = w.begin_region();
        w.i32(self.a);
        w.i32(self.b);
        w.end_region(body)
    }
}

impl NinoRead for TolLeaf {
    fn read(r: &mut Reader<'_>) -> Result<Self> {
        r.read_sealed_value()
    }

    fn read_option(r: &mut Reader<'_>) -> Result<Option<Self>> {
        r.read_sealed()
    }
}

impl NinoWrite for TolLeaf {
    fn write<'a>(&'a self, w: &mut Writer<'a>) -> Result<()> {
        w.write_sealed(Some(self))
    }

    fn write_option<'a>(value: Option<&'a Self>, w: &mut Writer<'a>) -> Result<()> {
        w.write_sealed(value)
    }
}

/// `TolBase` read by an older version that knows only `A`: the region's other bytes are skipped.
#[derive(Debug, Default, Clone, PartialEq)]
pub struct TolBaseOnly {
    pub a: i32,
}

impl NinoBody for TolBaseOnly {
    fn read_body(&mut self, r: &mut Reader<'_>) -> Result<()> {
        let body = r.begin_region()?;
        if r.has_more() {
            self.a = r.read()?;
        }
        r.end_region(body, self)
    }

    fn write_body<'a>(&'a self, w: &mut Writer<'a>) -> Result<()> {
        let body = w.begin_region();
        w.i32(self.a);
        w.end_region(body)
    }
}

impl NinoRead for TolBaseOnly {
    fn read(r: &mut Reader<'_>) -> Result<Self> {
        r.read_sealed_value()
    }

    fn read_option(r: &mut Reader<'_>) -> Result<Option<Self>> {
        r.read_sealed()
    }
}

/// `TolBase` as a declared (non-sealed) type: a type id, then a `TolLeaf` (the only subtype here).
#[derive(Debug, Clone, PartialEq)]
pub enum TolBase {
    Leaf(Box<TolLeaf>),
}

impl NinoRead for TolBase {
    fn read(r: &mut Reader<'_>) -> Result<Self> {
        Self::read_option(r)?.ok_or_else(|| Error::of(ErrorKind::UnexpectedNull))
    }

    fn read_option(r: &mut Reader<'_>) -> Result<Option<Self>> {
        let id = r.i32()?;
        Ok(Some(match id {
            0 => return Ok(None),
            TolLeaf::TYPE_ID => TolBase::Leaf(Box::new(r.read_class()?)),
            _ => return Err(Error::new(ErrorKind::UnknownTypeId, id)),
        }))
    }
}

impl NinoWrite for TolBase {
    fn write<'a>(&'a self, w: &mut Writer<'a>) -> Result<()> {
        match self {
            TolBase::Leaf(v) => {
                w.i32(TolLeaf::TYPE_ID);
                w.write_class(&**v)
            }
        }
    }

    fn write_option<'a>(value: Option<&'a Self>, w: &mut Writer<'a>) -> Result<()> {
        match value {
            Some(v) => v.write(w),
            None => {
                w.i32(0);
                Ok(())
            }
        }
    }
}

// ---------------------------------------------------------------------- strings

/// `[NinoType] sealed class Strings { string Def; [NinoUtf8] string U8; }`
#[derive(Debug, Default, Clone, PartialEq)]
pub struct Strings {
    pub def: Option<String>,
    pub u8: Option<Utf8>,
}

impl NinoBody for Strings {
    fn read_body(&mut self, r: &mut Reader<'_>) -> Result<()> {
        self.def = r.read()?;
        self.u8 = r.read()?;
        Ok(())
    }

    fn write_body<'a>(&'a self, w: &mut Writer<'a>) -> Result<()> {
        w.write(&self.def)?;
        w.write(&self.u8)
    }
}

impl NinoRead for Strings {
    fn read(r: &mut Reader<'_>) -> Result<Self> {
        r.read_sealed_value()
    }

    fn read_option(r: &mut Reader<'_>) -> Result<Option<Self>> {
        r.read_sealed()
    }
}

impl NinoWrite for Strings {
    fn write<'a>(&'a self, w: &mut Writer<'a>) -> Result<()> {
        w.write_sealed(Some(self))
    }

    fn write_option<'a>(value: Option<&'a Self>, w: &mut Writer<'a>) -> Result<()> {
        w.write_sealed(value)
    }
}

// ---------------------------------------------------------------------- deep nesting

/// `sealed LinkFirst { LinkFirst Next; int V; }`: a recursive class, deferred 128 deep.
#[derive(Debug, Default, PartialEq)]
pub struct LinkFirst {
    pub next: Option<Box<LinkFirst>>,
    pub v: i32,
}

impl LinkFirst {
    /// A chain of `n` nodes, V = 1..n.
    pub fn chain(n: i32) -> Option<Box<Self>> {
        let mut head = None;
        for v in (1..=n).rev() {
            head = Some(Box::new(Self { next: head, v }));
        }
        head
    }

    /// The V of each node, without recursion.
    pub fn values(mut node: Option<&Self>) -> Vec<i32> {
        let mut values = Vec::new();
        while let Some(n) = node {
            values.push(n.v);
            node = n.next.as_deref();
        }
        values
    }
}

/// Generated for recursive types: dropping a long chain one node at a time, not by recursion.
impl Drop for LinkFirst {
    fn drop(&mut self) {
        let mut next = self.next.take();
        while let Some(mut node) = next {
            next = node.next.take();
        }
    }
}

impl NinoBody for LinkFirst {
    fn read_body(&mut self, r: &mut Reader<'_>) -> Result<()> {
        self.next = r.read_in(field!(Self, next))?;
        self.v = r.i32()?;
        Ok(())
    }

    fn write_body<'a>(&'a self, w: &mut Writer<'a>) -> Result<()> {
        w.write(&self.next)?;
        w.i32(self.v);
        Ok(())
    }
}

impl NinoRead for LinkFirst {
    const MAY_DEFER: bool = true;

    fn read(r: &mut Reader<'_>) -> Result<Self> {
        r.read_sealed_value()
    }

    fn read_option(r: &mut Reader<'_>) -> Result<Option<Self>> {
        r.read_sealed()
    }
}

impl NinoWrite for LinkFirst {
    fn write<'a>(&'a self, w: &mut Writer<'a>) -> Result<()> {
        w.write_sealed(Some(self))
    }

    fn write_option<'a>(value: Option<&'a Self>, w: &mut Writer<'a>) -> Result<()> {
        w.write_sealed(value)
    }
}

/// `[NinoType(format: VersionTolerant)] sealed LinkTol { int V; LinkTol Next; }`
#[derive(Debug, Default, PartialEq)]
pub struct LinkTol {
    pub v: i32,
    pub next: Option<Box<LinkTol>>,
}

impl LinkTol {
    pub fn chain(n: i32) -> Option<Box<Self>> {
        let mut head = None;
        for v in (1..=n).rev() {
            head = Some(Box::new(Self { v, next: head }));
        }
        head
    }

    pub fn values(mut node: Option<&Self>) -> Vec<i32> {
        let mut values = Vec::new();
        while let Some(n) = node {
            values.push(n.v);
            node = n.next.as_deref();
        }
        values
    }
}

impl Drop for LinkTol {
    fn drop(&mut self) {
        let mut next = self.next.take();
        while let Some(mut node) = next {
            next = node.next.take();
        }
    }
}

impl NinoBody for LinkTol {
    fn read_body(&mut self, r: &mut Reader<'_>) -> Result<()> {
        let body = r.begin_region()?;
        if r.has_more() {
            self.v = r.read()?;
        }
        if r.has_more() {
            self.next = r.read_in(field!(Self, next))?;
        }
        r.end_region(body, self)
    }

    fn write_body<'a>(&'a self, w: &mut Writer<'a>) -> Result<()> {
        let body = w.begin_region();
        w.i32(self.v);
        w.write(&self.next)?;
        w.end_region(body)
    }
}

impl NinoRead for LinkTol {
    const MAY_DEFER: bool = true;

    fn read(r: &mut Reader<'_>) -> Result<Self> {
        r.read_sealed_value()
    }

    fn read_option(r: &mut Reader<'_>) -> Result<Option<Self>> {
        r.read_sealed()
    }
}

impl NinoWrite for LinkTol {
    fn write<'a>(&'a self, w: &mut Writer<'a>) -> Result<()> {
        w.write_sealed(Some(self))
    }

    fn write_option<'a>(value: Option<&'a Self>, w: &mut Writer<'a>) -> Result<()> {
        w.write_sealed(value)
    }
}

/// `[NinoType(format: Keyed)] sealed LinkKeyed { LinkKeyed Next; int V; }`
#[derive(Debug, Default, PartialEq)]
pub struct LinkKeyed {
    pub v: i32,
    pub next: Option<Box<LinkKeyed>>,
}

impl LinkKeyed {
    const V: i32 = type_id("V");
    const NEXT: i32 = type_id("Next");

    pub fn chain(n: i32) -> Option<Box<Self>> {
        let mut head = None;
        for v in (1..=n).rev() {
            head = Some(Box::new(Self { v, next: head }));
        }
        head
    }

    pub fn values(mut node: Option<&Self>) -> Vec<i32> {
        let mut values = Vec::new();
        while let Some(n) = node {
            values.push(n.v);
            node = n.next.as_deref();
        }
        values
    }
}

impl Drop for LinkKeyed {
    fn drop(&mut self) {
        let mut next = self.next.take();
        while let Some(mut node) = next {
            next = node.next.take();
        }
    }
}

impl NinoBody for LinkKeyed {
    fn read_body(&mut self, r: &mut Reader<'_>) -> Result<()> {
        let body = r.begin_region()?;
        while r.has_more() {
            let key = r.i32()?;
            let member = r.begin_region()?;
            match key {
                Self::V => self.v = r.read()?,
                Self::NEXT => self.next = r.read_in(field!(Self, next))?,
                _ => {
                    r.end_region(member, self)?;
                    continue;
                }
            }
            r.end_member(member, self)?;
        }
        r.end_region(body, self)
    }

    fn write_body<'a>(&'a self, w: &mut Writer<'a>) -> Result<()> {
        let body = w.begin_region();
        w.i32(Self::NEXT);
        let m = w.begin_region();
        w.write(&self.next)?;
        w.end_region(m)?;
        w.i32(Self::V);
        let m = w.begin_region();
        w.i32(self.v);
        w.end_region(m)?;
        w.end_region(body)
    }
}

impl NinoRead for LinkKeyed {
    const MAY_DEFER: bool = true;

    fn read(r: &mut Reader<'_>) -> Result<Self> {
        r.read_sealed_value()
    }

    fn read_option(r: &mut Reader<'_>) -> Result<Option<Self>> {
        r.read_sealed()
    }
}

impl NinoWrite for LinkKeyed {
    fn write<'a>(&'a self, w: &mut Writer<'a>) -> Result<()> {
        w.write_sealed(Some(self))
    }

    fn write_option<'a>(value: Option<&'a Self>, w: &mut Writer<'a>) -> Result<()> {
        w.write_sealed(value)
    }
}

/// `[NinoType] sealed class Tree { int Value; List<Tree> Children; }`: deferral inside collections, several
/// deferred bodies per object.
#[derive(Debug, Default, Clone, PartialEq)]
pub struct Tree {
    pub value: i32,
    pub children: Option<Vec<Option<Box<Tree>>>>,
}

impl Tree {
    /// A path `depth` deep, each node with `width` children of which the first continues the path.
    pub fn comb(depth: i32, width: i32) -> Tree {
        let mut node = Tree { value: depth, children: None };
        for d in (0..depth).rev() {
            let mut children = vec![Some(Box::new(node))];
            for k in 1..width {
                children.push(if k % 3 == 2 { None } else { Some(Box::new(Tree { value: -k, children: None })) });
            }
            node = Tree { value: d, children: Some(children) };
        }
        node
    }
}

impl NinoBody for Tree {
    fn read_body(&mut self, r: &mut Reader<'_>) -> Result<()> {
        self.value = r.i32()?;
        self.children = r.read_in(field!(Self, children))?;
        Ok(())
    }

    fn write_body<'a>(&'a self, w: &mut Writer<'a>) -> Result<()> {
        w.i32(self.value);
        w.write(&self.children)
    }
}

impl NinoRead for Tree {
    const MAY_DEFER: bool = true;

    fn read(r: &mut Reader<'_>) -> Result<Self> {
        r.read_sealed_value()
    }

    fn read_option(r: &mut Reader<'_>) -> Result<Option<Self>> {
        r.read_sealed()
    }
}

impl NinoWrite for Tree {
    fn write<'a>(&'a self, w: &mut Writer<'a>) -> Result<()> {
        w.write_sealed(Some(self))
    }

    fn write_option<'a>(value: Option<&'a Self>, w: &mut Writer<'a>) -> Result<()> {
        w.write_sealed(value)
    }
}

/// `[NinoType] abstract class Node { int Id; }` with `sealed class Link : Node { Node Next; }` and
/// `sealed class Leaf : Node { string Name; }`: deferral behind type ids.
#[derive(Debug, Clone, PartialEq)]
pub enum Node {
    Link(Box<Link>),
    Leaf(Box<Leaf>),
}

#[derive(Debug, Default, Clone, PartialEq)]
pub struct Link {
    pub id: i32,
    pub next: Option<Node>,
}

#[derive(Debug, Default, Clone, PartialEq)]
pub struct Leaf {
    pub id: i32,
    pub name: Option<String>,
}

impl Link {
    pub const TYPE_ID: i32 = type_id("Spec.Link");
}

impl Leaf {
    pub const TYPE_ID: i32 = type_id("Spec.Leaf");
}

impl Node {
    /// The body inside the variant, for deferred bodies in it.
    fn body(&mut self) -> &mut dyn core::any::Any {
        match self {
            Node::Link(v) => &mut **v,
            Node::Leaf(v) => &mut **v,
        }
    }

    /// A chain of `n` links ending in a leaf.
    pub fn chain(n: i32) -> Node {
        let mut node = Node::Leaf(Box::new(Leaf { id: n, name: Some("end".into()) }));
        for id in (0..n).rev() {
            node = Node::Link(Box::new(Link { id, next: Some(node) }));
        }
        node
    }
}

impl NinoRead for Node {
    const MAY_DEFER: bool = true;
    const MIN_SIZE: usize = 4;

    fn read(r: &mut Reader<'_>) -> Result<Self> {
        Self::read_option(r)?.ok_or_else(|| Error::of(ErrorKind::UnexpectedNull))
    }

    fn read_option(r: &mut Reader<'_>) -> Result<Option<Self>> {
        let id = r.i32()?;
        let mark = r.mark();
        let value = match id {
            0 => return Ok(None),
            Link::TYPE_ID => Node::Link(Box::new(r.read_class()?)),
            Leaf::TYPE_ID => Node::Leaf(Box::new(r.read_class()?)),
            _ => return Err(Error::new(ErrorKind::UnknownTypeId, id)),
        };
        r.trail(mark, Step::Field(|p| Some(p.downcast_mut::<Node>()?.body())));
        Ok(Some(value))
    }
}

impl NinoWrite for Node {
    fn write<'a>(&'a self, w: &mut Writer<'a>) -> Result<()> {
        match self {
            Node::Link(v) => {
                w.i32(Link::TYPE_ID);
                w.write_class(&**v)
            }
            Node::Leaf(v) => {
                w.i32(Leaf::TYPE_ID);
                w.write_class(&**v)
            }
        }
    }

    fn write_option<'a>(value: Option<&'a Self>, w: &mut Writer<'a>) -> Result<()> {
        match value {
            Some(v) => v.write(w),
            None => {
                w.i32(0);
                Ok(())
            }
        }
    }
}

impl NinoBody for Link {
    fn read_body(&mut self, r: &mut Reader<'_>) -> Result<()> {
        self.id = r.i32()?;
        self.next = r.read_in(field!(Self, next))?;
        Ok(())
    }

    fn write_body<'a>(&'a self, w: &mut Writer<'a>) -> Result<()> {
        w.i32(self.id);
        w.write(&self.next)
    }
}

impl NinoRead for Link {
    const MAY_DEFER: bool = true;

    fn read(r: &mut Reader<'_>) -> Result<Self> {
        r.read_sealed_value()
    }

    fn read_option(r: &mut Reader<'_>) -> Result<Option<Self>> {
        r.read_sealed()
    }
}

impl NinoBody for Leaf {
    fn read_body(&mut self, r: &mut Reader<'_>) -> Result<()> {
        self.id = r.i32()?;
        self.name = r.read()?;
        Ok(())
    }

    fn write_body<'a>(&'a self, w: &mut Writer<'a>) -> Result<()> {
        w.i32(self.id);
        w.write(&self.name)
    }
}

impl NinoRead for Leaf {
    fn read(r: &mut Reader<'_>) -> Result<Self> {
        r.read_sealed_value()
    }

    fn read_option(r: &mut Reader<'_>) -> Result<Option<Self>> {
        r.read_sealed()
    }
}

/// A non-deferrable recursive class (a record in .NET): nested inline past 128, up to 512.
#[derive(Debug, Default, PartialEq)]
pub struct Rec {
    pub next: Option<Box<Rec>>,
}

impl Rec {
    pub fn chain(n: usize) -> Option<Box<Rec>> {
        let mut head = None;
        for _ in 0..n {
            head = Some(Box::new(Rec { next: head }));
        }
        head
    }
}

impl Drop for Rec {
    fn drop(&mut self) {
        let mut next = self.next.take();
        while let Some(mut node) = next {
            next = node.next.take();
        }
    }
}

impl NinoBody for Rec {
    const DEFERRABLE: bool = false;

    fn read_body(&mut self, r: &mut Reader<'_>) -> Result<()> {
        self.next = r.read()?;
        Ok(())
    }

    fn write_body<'a>(&'a self, w: &mut Writer<'a>) -> Result<()> {
        w.write(&self.next)
    }
}

impl NinoRead for Rec {
    fn read(r: &mut Reader<'_>) -> Result<Self> {
        r.read_sealed_value()
    }

    fn read_option(r: &mut Reader<'_>) -> Result<Option<Self>> {
        r.read_sealed()
    }
}

impl NinoWrite for Rec {
    fn write<'a>(&'a self, w: &mut Writer<'a>) -> Result<()> {
        w.write_sealed(Some(self))
    }

    fn write_option<'a>(value: Option<&'a Self>, w: &mut Writer<'a>) -> Result<()> {
        w.write_sealed(value)
    }
}
