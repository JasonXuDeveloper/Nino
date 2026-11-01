//! Reference tracking (the specification's 7.9): objects of a type declared with `trackReferences` are written
//! behind a reference header, an `i32`: 0 for null, -1 for an object not written before (its body follows), or
//! `n > 0` for the `n`-th object of a tracked type in the payload (from 1, in the order of their headers).
//!
//! Such an object is a [`Shared<T>`]: one value any number of places hold, which may hold itself (a cycle). A
//! reader records each new object as it creates it, before its body is read, so a body can refer to the object
//! holding it; a writer numbers each object by its address.
//!
//! Three forms, generated code picks one:
//! - a tracked class without subtypes: the header, then the body ([`Reader::read_shared`], [`Writer::write_shared`]);
//! - a tracked class with subtypes or interface: the header, then (for -1) the type id and the body
//!   ([`Reader::ref_header`], [`Reader::back_ref`], [`Reader::read_shared_new`], [`Writer::shared_header`],
//!   [`Writer::write_shared_body`]);
//! - a slot that does not track (`object`, an untracked base) holding a tracked type: the type id, then the header,
//!   then the body for -1, and null is a type id of 0, never a header of 0 ([`Reader::read_shared_after_id`]).
//!
//! Hostile data: a header is checked against the objects read so far (a back reference never names a later one),
//! and the object it names must be of a type the slot allows ([`Reader::shared_from`]); every new object takes at
//! least its 4 header bytes, so the table grows with the data and no faster.
//!
//! Memory: the objects one read creates belong to that read's arena, which frees all of them at once, cycles
//! included (as .NET's collector does). A `Shared` the reader stores inside one of those objects refers to its
//! target weakly; one it stores anywhere else (the value returned, a struct or collection outside every tracked
//! object) keeps the arena alive, and so does any clone. When the last of those is dropped, or when the read fails,
//! the arena and every object in it are freed. A graph built by hand with [`Shared::new`] is reference counted like
//! an `Rc`.

#![allow(unsafe_code)] // one block: Shared::get on a handle inside an arena's object (SAFETY there)

use core::any::Any;
use core::cell::{OnceCell, RefCell};
use core::fmt;
use std::rc::{Rc, Weak};

use crate::error::{Error, ErrorKind, Result};
use crate::reader::{Reader, INLINE_DEPTH, MAX_DEPTH};
use crate::traits::{NinoRead, NinoReadBody, NinoWrite, NinoWriteBody};
use crate::writer::Writer;

/// The objects of tracked types one read created, in the order of their headers (a back reference `n` is the
/// `n`-th): it owns them, so dropping it frees every one, whatever cycles they form.
#[derive(Default)]
pub(crate) struct Arena {
    objects: RefCell<Vec<Rc<dyn Any>>>,
}

impl Arena {
    pub(crate) fn len(&self) -> usize {
        self.objects.borrow().len()
    }
}

/// An object of a tracked type: shared by every place that holds it (a clone is the same object), possibly
/// holding itself. Its value is set once: by the reader once its body is read, or by [`Shared::new`] or
/// [`Shared::fill`] (create one empty to build a cycle, then fill it).
///
/// A `Shared` from a read keeps that read's objects alive (see the module documentation): the ones the reader
/// put inside its objects do not, so a graph read from data is freed when the last `Shared` outside it is dropped,
/// cycles included. A clone always keeps them alive.
///
/// Equality is identity (two `Shared`s are equal when they are the same object): comparing values could go round
/// a cycle forever. `Debug` prints the address.
pub struct Shared<T>(Link<T>);

enum Link<T> {
    /// Keeps the object alive, and the arena of the read that made it (if a read did).
    Strong(Rc<OnceCell<T>>, Option<Rc<Arena>>),
    /// Inside an object of the arena, which owns the target: reachable only through a `Strong` link's arena.
    Weak(Weak<OnceCell<T>>, Weak<Arena>),
}

impl<T> Shared<T> {
    /// A new object holding `value`.
    pub fn new(value: T) -> Self {
        Self(Link::Strong(Rc::new(OnceCell::from(value)), None))
    }

    /// A new object without its value yet ([`Shared::fill`] sets it): one a value can hold before it exists.
    pub fn empty() -> Self {
        Self(Link::Strong(Rc::new(OnceCell::new()), None))
    }

    /// Sets the value of an object made by [`Shared::empty`]; returns it back when the object has one already.
    pub fn fill(&self, value: T) -> core::result::Result<(), T> {
        match &self.0 {
            Link::Strong(cell, _) => cell.set(value),
            Link::Weak(cell, _) => match cell.upgrade() {
                Some(cell) => cell.set(value),
                None => Err(value),
            },
        }
    }

    /// The value (`None` for an object made by [`Shared::empty`] and not filled).
    #[inline]
    pub fn get(&self) -> Option<&T> {
        match &self.0 {
            Link::Strong(cell, _) => cell.get(),
            Link::Weak(cell, _) => {
                if cell.strong_count() == 0 {
                    return None;
                }
                // SAFETY: the object is alive (its strong count is not 0), and stays alive while `self` is borrowed.
                // A weak link is made only by the reader, for a `Shared` it stores inside an object of its arena,
                // and `Shared` hands out no `&mut` and no way to move one out of an object (a clone is strong). So
                // `self` is borrowed only through a `&` to that object's value, which in turn comes from `get` on a
                // `Shared` borrowed for at least as long: following that chain back ends at a strong link, whose
                // arena (which holds every object strongly and never releases one) outlives the borrow. While the
                // arena itself is being dropped, its objects are freed one at a time and none frees another, so an
                // object a `Drop` impl reaches this way is either alive for that call or seen as dead above. The
                // `OnceCell` is never written once its value is set, and `get` reads it as `OnceCell::get` would.
                unsafe { (*cell.as_ptr()).get() }
            }
        }
    }

    /// Whether `a` and `b` are the same object.
    pub fn ptr_eq(a: &Self, b: &Self) -> bool {
        a.address() == b.address()
    }

    fn address(&self) -> usize {
        match &self.0 {
            Link::Strong(cell, _) => Rc::as_ptr(cell) as *const () as usize,
            Link::Weak(cell, _) => cell.as_ptr() as *const () as usize,
        }
    }
}

impl<T> Clone for Shared<T> {
    /// The same object, kept alive by the clone (with the objects of its read).
    fn clone(&self) -> Self {
        match &self.0 {
            Link::Strong(cell, arena) => Self(Link::Strong(Rc::clone(cell), arena.clone())),
            Link::Weak(cell, arena) => match (cell.upgrade(), arena.upgrade()) {
                (Some(cell), Some(arena)) => Self(Link::Strong(cell, Some(arena))),
                _ => Self(Link::Weak(cell.clone(), arena.clone())),
            },
        }
    }
}

impl<T> Default for Shared<T> {
    fn default() -> Self {
        Self::empty()
    }
}

impl<T> PartialEq for Shared<T> {
    fn eq(&self, other: &Self) -> bool {
        Self::ptr_eq(self, other)
    }
}

impl<T> Eq for Shared<T> {}

impl<T> fmt::Debug for Shared<T> {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "Shared({:#x})", self.address())
    }
}

#[cold]
#[inline(never)]
fn invalid(header: i32) -> Error {
    Error::new(ErrorKind::InvalidReference, header)
}

impl Reader<'_> {
    /// Reads a reference header: 0 (null), -1 (a new object) or the number of an object read before; any other
    /// value fails with [`ErrorKind::InvalidReference`].
    #[inline]
    pub fn ref_header(&mut self) -> Result<i32> {
        let header = self.i32()?;
        let known = self.defer.as_deref().and_then(|d| d.arena.as_deref()).map_or(0, Arena::len);
        if header < -1 || (header > 0 && header as u32 as usize > known) {
            return Err(invalid(header));
        }
        Ok(header)
    }

    /// The object a back reference `header` (from [`Reader::ref_header`], > 0) names: generated code passes it to
    /// [`Reader::shared_from`] for each type the slot allows.
    pub fn back_ref(&self, header: i32) -> Result<Rc<dyn Any>> {
        let index = usize::try_from(header).ok().and_then(|n| n.checked_sub(1)).ok_or_else(|| invalid(header))?;
        let arena = self.defer.as_deref().and_then(|d| d.arena.as_deref());
        arena.and_then(|a| a.objects.borrow().get(index).cloned()).ok_or_else(|| invalid(header))
    }

    /// The object a back reference names ([`Reader::back_ref`]) as a `Shared<T>`, or `None` when it is of another
    /// type: the `Shared` to store where the reader is now (see [`Shared`]).
    pub fn shared_from<T: 'static>(&self, object: &Rc<dyn Any>) -> Option<Shared<T>> {
        let cell = Rc::clone(object).downcast::<OnceCell<T>>().ok()?;
        Some(self.link(cell))
    }

    /// The object a back reference names, which must be a `Shared<T>`.
    fn back_shared<T: 'static>(&self, header: i32) -> Result<Shared<T>> {
        self.shared_from(&self.back_ref(header)?).ok_or_else(|| invalid(header))
    }

    /// A `Shared` for `cell`, an object of this read's arena: weak inside a tracked object's body, else strong.
    fn link<T>(&self, cell: Rc<OnceCell<T>>) -> Shared<T> {
        let d = self.defer.as_deref();
        let arena = d.and_then(|d| d.arena.as_ref());
        match d {
            Some(d) if d.objects_open > 0 => {
                Shared(Link::Weak(Rc::downgrade(&cell), arena.map_or_else(Weak::new, Rc::downgrade)))
            }
            _ => Shared(Link::Strong(cell, arena.cloned())),
        }
    }

    /// Reads a tracked class without subtypes: its reference header, then its body for a new object.
    pub fn read_shared<T: NinoReadBody>(&mut self) -> Result<Option<Shared<T>>> {
        match self.ref_header()? {
            0 => Ok(None),
            -1 => self.read_shared_new().map(Some),
            header => self.back_shared(header).map(Some),
        }
    }

    /// Reads a tracked class in a slot that does not track, after its type id: its reference header (not 0: null
    /// was the type id), then its body for a new object.
    pub fn read_shared_after_id<T: NinoReadBody>(&mut self) -> Result<Shared<T>> {
        match self.ref_header()? {
            -1 => self.read_shared_new(),
            0 => Err(invalid(0)),
            header => self.back_shared(header),
        }
    }

    /// Creates a new object of a tracked class, records it, and reads its body into it (after a header of -1 and,
    /// where there is one, its type id). Counted and deferred as [`Reader::read_class`] does.
    pub fn read_shared_new<T: NinoReadBody>(&mut self) -> Result<Shared<T>> {
        let cell = Rc::new(OnceCell::<T>::new());
        let d = self.deferrals();
        d.arena.get_or_insert_with(Default::default).objects.borrow_mut().push(cell.clone());
        let shared = self.link(cell);
        let depth = self.depth;
        if depth >= INLINE_DEPTH {
            if T::DEFERRABLE {
                let position = self.position();
                self.deferrals().defer_shared::<T>(position);
                return Ok(shared);
            }
            if depth.saturating_add(self.structs) >= MAX_DEPTH {
                return Err(Error::of(ErrorKind::TooDeep));
            }
        }
        let position = self.position();
        let (mark, log_mark) = match self.defer.as_deref() {
            Some(d) => (d.pending_len(), d.log_mark()),
            None => (0, 0),
        };
        self.depth = depth.saturating_add(1);
        // Every Shared read into the body (or into bodies deferred inside it) is inside this object.
        let d = self.deferrals();
        d.objects_open = d.objects_open.saturating_add(1);
        let mut value = T::default();
        let read = value.read_body(self);
        self.depth = depth;
        let read = read.and_then(|()| match depth == 0 && self.regions == 0 {
            true => crate::defer::flush_top(self, &mut value),
            false => Ok(()),
        });
        let d = self.deferrals();
        d.objects_open = d.objects_open.saturating_sub(1);
        read?;
        if self.defer_mark() > mark {
            // Bodies inside it were deferred: it waits for them in the queue.
            return self.deferrals().hold(position, mark, log_mark, value).map(|()| shared);
        }
        shared.fill(value).map_err(|_| Error::of(ErrorKind::Unsupported))?;
        Ok(shared)
    }
}

impl<'a> Writer<'a> {
    /// Writes the reference header of `value`: its number for an object written before, else -1, and then returns
    /// true: the caller writes the rest (type id, body).
    pub fn shared_header<T>(&mut self, value: &Shared<T>) -> bool {
        let refs = &mut self.defer.get_or_insert_with(Default::default).refs;
        let next = i32::try_from(refs.len()).unwrap_or(i32::MAX).saturating_add(1);
        match refs.entry(value.address()) {
            std::collections::hash_map::Entry::Occupied(e) => {
                let n = *e.get();
                self.i32(n);
                false
            }
            std::collections::hash_map::Entry::Vacant(e) => {
                e.insert(next);
                self.i32(-1);
                true
            }
        }
    }

    /// Writes the body of a tracked object, after its header (and type id): one made by [`Shared::empty`] and never
    /// filled cannot be written.
    pub fn write_shared_body<T: NinoWriteBody>(&mut self, value: &'a Shared<T>) -> Result<()> {
        self.write_class(value.get().ok_or_else(Error::null)?)
    }

    /// Writes a tracked class without subtypes: its reference header, then its body for a new object.
    pub fn write_shared<T: NinoWriteBody>(&mut self, value: Option<&'a Shared<T>>) -> Result<()> {
        match value {
            None => {
                self.i32(0);
                Ok(())
            }
            Some(v) if self.shared_header(v) => self.write_shared_body(v),
            Some(_) => Ok(()),
        }
    }
}

/// A tracked class without subtypes, as a member: its reference header, then its body for a new object.
impl<T: NinoReadBody> NinoRead for Shared<T> {
    const MAY_DEFER: bool = true;
    const MIN_SIZE: usize = 4;

    fn read(r: &mut Reader<'_>) -> Result<Self> {
        r.read_shared()?.ok_or_else(Error::null)
    }

    fn read_option(r: &mut Reader<'_>) -> Result<Option<Self>> {
        r.read_shared()
    }
}

impl<T: NinoWriteBody> NinoWrite for Shared<T> {
    fn write<'a>(&'a self, w: &mut Writer<'a>) -> Result<()> {
        w.write_shared(Some(self))
    }

    fn write_option<'a>(value: Option<&'a Self>, w: &mut Writer<'a>) -> Result<()> {
        w.write_shared(value)
    }
}

/// serde sees the value (a graph with a cycle cannot be serialized this way); deserializing makes a new object.
#[cfg(feature = "serde")]
impl<T: serde::Serialize> serde::Serialize for Shared<T> {
    fn serialize<S: serde::Serializer>(&self, s: S) -> core::result::Result<S::Ok, S::Error> {
        self.get().serialize(s)
    }
}

#[cfg(feature = "serde")]
impl<'de, T: serde::Deserialize<'de>> serde::Deserialize<'de> for Shared<T> {
    fn deserialize<D: serde::Deserializer<'de>>(d: D) -> core::result::Result<Self, D::Error> {
        Ok(Option::<T>::deserialize(d)?.map_or_else(Self::empty, Self::new))
    }
}
