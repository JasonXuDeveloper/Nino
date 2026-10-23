//! Deferred bodies (the specification's 7.10): class bodies nested 128 deep are written and read later, from a
//! shallow point, so the stack stays bounded however deep the data is.
//!
//! **Writing** keeps references to the deferred objects (`&'a`, borrowed for the whole call) and writes their bodies
//! when the outermost class body closes (the top queue) or at the end of the outermost region (a section).
//!
//! **Reading** cannot keep a reference into the object being built (safe Rust has one owner), so it works in three
//! steps:
//! 1. Where a body is deferred, the slot gets `T::default()` (a placeholder) and the reader queues how to read the
//!    body later ([`Pending`]).
//! 2. Each container the value is read into (a member, an element, an `Option`, a `Box`) logs one [`Step`] for the
//!    deferrals made inside it, as one entry for all of them ([`Reader::trail`]): the steps lead from the object
//!    that owns the queue (the outermost class, or the region's object) down to each placeholder. One entry per
//!    container read, not per deferral, so memory stays proportional to the data.
//! 3. When the queue is read (after the outermost body, or at the end of the outermost region), every body is read
//!    into a value of its own, in queue order; then each value is moved into its placeholder by following the steps,
//!    in reverse order (a body deferred inside another deferred body goes into that body first).

use core::any::Any;
use std::rc::Rc;

use crate::error::{Error, ErrorKind, Result};
use crate::reader::{take_i32, Reader, Region, DEPTH_INLINE};
use crate::traits::NinoBody;
use crate::writer::Writer;

/// How to reach a value from the object or container it was read into. Generated code passes
/// [`Step::Field`]s (see [`crate::field`]); the runtime's containers add the others.
#[derive(Clone)]
pub enum Step {
    /// A member, an `Option`'s or a `Box`'s content, a tuple item, a polymorphic variant's body.
    Field(fn(&mut dyn Any) -> Option<&mut dyn Any>),
    /// An element of a sequence, by index.
    Index(fn(&mut dyn Any, usize) -> Option<&mut dyn Any>, usize),
    /// Anything else (a dictionary value, by its key).
    Dyn(Rc<DynStep>),
}

/// The function of a [`Step::Dyn`].
pub type DynStep = dyn Fn(&mut dyn Any) -> Option<&mut dyn Any>;

impl Step {
    fn apply<'x>(&self, value: &'x mut dyn Any) -> Option<&'x mut dyn Any> {
        match self {
            Step::Field(f) => f(value),
            Step::Index(f, i) => f(value, *i),
            Step::Dyn(f) => f(value),
        }
    }
}

type ReadFn = fn(&mut Reader<'_>) -> Result<Box<dyn Any>>;
type AttachFn = fn(&mut dyn Any, Box<dyn Any>) -> Result<()>;

/// A deferred body: where it was deferred, how to read it, and once read, its value and the deferrals made
/// while reading it (`records` and `log`, absolute ranges).
struct Pending {
    position: usize,
    read: ReadFn,
    attach: AttachFn,
    item: Option<Box<dyn Any>>,
    records: (usize, usize),
    log: (usize, usize),
}

/// One container's step for the deferrals `lo..hi` made inside it.
struct Trail {
    lo: usize,
    hi: usize,
    step: Step,
}

/// The reader's deferred bodies: the top queue or the outermost region's (never both at once, see
/// `Reader::read_class`), and the steps to their placeholders.
#[derive(Default)]
pub(crate) struct ReadDeferrals {
    pending: Vec<Pending>,
    log: Vec<Trail>,
    top_active: bool,
}

fn read_deferred<T: NinoBody>(r: &mut Reader<'_>) -> Result<Box<dyn Any>> {
    let mut value = T::default();
    value.read_body(r)?;
    Ok(Box::new(value))
}

fn attach<T: NinoBody>(slot: &mut dyn Any, item: Box<dyn Any>) -> Result<()> {
    let slot = slot.downcast_mut::<T>().ok_or_else(misplaced)?;
    *slot = *item.downcast::<T>().map_err(|_| misplaced())?;
    Ok(())
}

/// A step that does not lead where it was recorded: a bug, reported rather than a panic.
#[cold]
fn misplaced() -> Error {
    Error::new(ErrorKind::Unsupported, -1)
}

impl ReadDeferrals {
    pub(crate) fn pending_len(&self) -> usize {
        self.pending.len()
    }

    pub(crate) fn log_len(&self) -> usize {
        self.log.len()
    }

    pub(crate) fn defer<T: NinoBody>(&mut self, position: usize, _regions: u32) {
        self.pending.push(Pending {
            position,
            read: read_deferred::<T>,
            attach: attach::<T>,
            item: None,
            records: (0, 0),
            log: (0, 0),
        });
    }

    #[inline]
    pub(crate) fn trail(&mut self, mark: usize, step: Step) {
        let hi = self.pending.len();
        if hi > mark {
            self.log.push(Trail { lo: mark, hi, step });
        }
    }
}

/// The depth a body from a section is read and written at (`NinoDepth.Deferred`).
fn section_depth(depth: u32) -> u32 {
    depth.min(DEPTH_INLINE.saturating_sub(1)).saturating_add(1)
}

/// Reads the body of pending item `i` with `read` (which positions the reader), and records the deferrals made
/// meanwhile as the item's group. False when there is no item `i`.
fn read_item<'d>(
    r: &mut Reader<'d>,
    i: usize,
    read: impl FnOnce(&mut Reader<'d>, ReadFn) -> Result<Box<dyn Any>>,
) -> Result<()> {
    let Some(d) = r.defer.as_deref() else { return Err(misplaced()) };
    let Some(p) = d.pending.get(i) else { return Err(misplaced()) };
    let (body, records, log) = (p.read, d.pending.len(), d.log.len());
    let item = read(r, body)?;
    let Some(d) = r.defer.as_deref_mut() else { return Err(misplaced()) };
    let (records_end, log_end) = (d.pending.len(), d.log.len());
    let Some(p) = d.pending.get_mut(i) else { return Err(misplaced()) };
    p.item = Some(item);
    p.records = (records, records_end);
    p.log = (log, log_end);
    Ok(())
}

/// Every class body is closed: reads the bodies deferred outside regions, which follow here, in order (each at
/// depth 1; those they defer are appended), then puts them in place under `owner`, the outermost object.
pub(crate) fn flush_top<O: Any>(r: &mut Reader<'_>, owner: &mut O) -> Result<()> {
    let (records, log) = match r.defer.as_deref_mut() {
        Some(d) if !d.top_active && !d.pending.is_empty() => {
            d.top_active = true;
            (d.pending.len(), d.log.len())
        }
        _ => return Ok(()),
    };
    let mut i = 0usize;
    let result = loop {
        let more = r.defer.as_deref().is_some_and(|d| i < d.pending.len());
        if !more {
            break Ok(());
        }
        if let Err(e) = read_item(r, i, |r, body| {
            r.depth = 1;
            body(r)
        }) {
            break Err(e);
        }
        i = i.saturating_add(1);
    };
    r.depth = 0;
    if let Some(d) = r.defer.as_deref_mut() {
        d.top_active = false;
    }
    result?;
    put_in_place(r, 0, 0, records, log, owner)
}

/// At the end of the outermost region: reads the bodies deferred inside it from its section, matching each with
/// its entry by key (entries of objects this reader skipped are skipped), then puts them in place under `owner`.
pub(crate) fn end_outer_region<'d, O: Any>(r: &mut Reader<'d>, region: Region<'d>, owner: &mut O) -> Result<()> {
    let start = region.pending_start;
    let (records, log) = match r.defer.as_deref() {
        Some(d) if d.pending.len() > start => (d.pending.len(), d.log.len()),
        _ => return Ok(()),
    };
    let (mut section, mut at, members_at) = region.section();
    if section.is_empty() {
        return Err(Error::of(ErrorKind::InvalidSection));
    }
    let depth = section_depth(r.depth);
    let mut i = start;
    while let Some(position) = r.defer.as_deref().and_then(|d| d.pending.get(i)).map(|p| p.position) {
        loop {
            let key = take_i32(&mut section)?;
            let length = take_i32(&mut section)?;
            let (entry, tail) = usize::try_from(length)
                .ok()
                .and_then(|n| section.split_at_checked(n))
                .ok_or_else(|| Error::length(length))?;
            let entry_at = at.saturating_add(8);
            at = entry_at.saturating_add(entry.len());
            section = tail;
            if (members_at as i128).wrapping_add(i128::from(key)) != position as i128 {
                continue;
            }
            read_item(r, i, |r, body| r.read_entry(entry, entry_at, depth, body))?;
            break;
        }
        i = i.saturating_add(1);
    }
    put_in_place(r, start, region.log_start, records, log, owner)
}

/// Moves the bodies read (pending items from `start`) into their placeholders, and drops them from the queue.
/// `records`/`log` end the deferrals made under `owner` itself (those from `start`/`log_start`).
fn put_in_place<O: Any>(
    r: &mut Reader<'_>,
    start: usize,
    log_start: usize,
    records: usize,
    log: usize,
    owner: &mut O,
) -> Result<()> {
    let Some(d) = r.defer.as_deref_mut() else { return Err(misplaced()) };
    if start > d.pending.len() || log_start > d.log.len() {
        return Err(misplaced());
    }
    let mut pending = d.pending.split_off(start);
    let trails = d.log.split_off(log_start);
    let base = Base { records: start, log: log_start };

    // Last first: what was deferred inside a body comes after it in the queue, and goes into it before it moves.
    for i in (0..pending.len()).rev() {
        let Some(p) = pending.get_mut(i) else { return Err(misplaced()) };
        let Some(mut value) = p.item.take() else { return Err(misplaced()) };
        let (records, log) = (p.records, p.log);
        let mut cursor = log.1;
        walk(&mut *value, &trails, &base, &mut cursor, log.0, records.0, records.1, &mut pending)?;
        if let Some(p) = pending.get_mut(i) {
            p.item = Some(value);
        }
    }
    let mut cursor = log;
    walk(owner, &trails, &base, &mut cursor, log_start, start, records, &mut pending)
}

/// Where the split-off queue and log start in absolute indices.
struct Base {
    records: usize,
    log: usize,
}

/// Puts the items of `lo..hi` (absolute) in place under `value`, following the log entries from `cursor` back to
/// `log_lo` (absolute) that lie inside that range: an entry leads to a container holding some of them, and an item
/// no entry leads further for is `value` itself.
#[allow(clippy::too_many_arguments)]
fn walk(
    value: &mut dyn Any,
    trails: &[Trail],
    base: &Base,
    cursor: &mut usize,
    log_lo: usize,
    lo: usize,
    hi: usize,
    pending: &mut [Pending],
) -> Result<()> {
    let mut end = hi;
    loop {
        let child = if *cursor > log_lo {
            let index = cursor.saturating_sub(1).checked_sub(base.log).ok_or_else(misplaced)?;
            trails.get(index).filter(|t| t.lo >= lo && t.hi <= end)
        } else {
            None
        };
        let leaves_from = child.map_or(lo, |t| t.hi);
        for record in (leaves_from..end).rev() {
            let index = record.checked_sub(base.records).ok_or_else(misplaced)?;
            let p = pending.get_mut(index).ok_or_else(misplaced)?;
            let item = p.item.take().ok_or_else(misplaced)?;
            (p.attach)(&mut *value, item)?;
        }
        let Some(trail) = child else { return Ok(()) };
        *cursor = cursor.saturating_sub(1);
        let inner = trail.step.apply(&mut *value).ok_or_else(misplaced)?;
        walk(inner, trails, base, cursor, log_lo, trail.lo, trail.hi, pending)?;
        end = trail.lo;
    }
}

// ---------------------------------------------------------------------- writing

type WriteFn<'a> = fn(&'a dyn Any, &mut Writer<'a>) -> Result<()>;

/// A deferred object: its body is written later.
#[derive(Clone, Copy)]
pub(crate) struct WriteItem<'a> {
    value: &'a dyn Any,
    write: WriteFn<'a>,
    position: usize,
    // Cycle detection (Brent), as in Nino.Core's Writer.Defer.
    anchor: Option<usize>,
    generation: u32,
}

#[derive(Default)]
pub(crate) struct WriteDeferrals<'a> {
    top: Vec<WriteItem<'a>>,
    region: Vec<WriteItem<'a>>,
    top_active: bool,
    current: Option<usize>,
    current_anchor: Option<usize>,
    current_generation: u32,
}

fn write_deferred<'a, T: NinoBody>(value: &'a dyn Any, w: &mut Writer<'a>) -> Result<()> {
    value.downcast_ref::<T>().ok_or_else(misplaced)?.write_body(w)
}

/// An object's identity, for cycle detection: its address (no zero-sized value can hold a cycle).
fn identity<T>(value: &T) -> Option<usize> {
    if core::mem::size_of::<T>() == 0 {
        None
    } else {
        Some(value as *const T as usize)
    }
}

impl<'a> WriteDeferrals<'a> {
    /// Queues the body of `value`, in the top queue outside regions, else in the region's. Fails on a cycle: each
    /// deferred object is compared with the one whose body it was found in and with the one at the last power of two
    /// of that chain, so any repetition is found in steps proportional to its length.
    pub(crate) fn defer<T: NinoBody>(&mut self, value: &'a T, position: usize, regions: u32) -> Result<()> {
        let generation = self.current_generation;
        let anchor = if generation != 0 && generation.is_power_of_two() { self.current } else { self.current_anchor };
        let id = identity(value);
        if id.is_some() && (id == self.current || id == anchor) {
            return Err(Error::of(ErrorKind::Cycle));
        }
        let item =
            WriteItem { value, write: write_deferred::<T>, position, anchor, generation: generation.saturating_add(1) };
        if regions == 0 {
            self.top.push(item);
        } else {
            self.region.push(item);
        }
        Ok(())
    }

    fn start(&mut self, item: &WriteItem<'a>) {
        self.current = if core::mem::size_of_val(item.value) == 0 {
            None
        } else {
            Some(item.value as *const dyn Any as *const () as usize)
        };
        self.current_anchor = item.anchor;
        self.current_generation = item.generation;
    }
}

/// Every class body is closed: writes the bodies deferred outside regions, in order, each at depth 1 (those they
/// defer are appended).
pub(crate) fn write_top(w: &mut Writer<'_>) -> Result<()> {
    match w.defer.as_deref_mut() {
        Some(d) if !d.top_active && !d.top.is_empty() => d.top_active = true,
        _ => return Ok(()),
    }
    let mut i = 0usize;
    let result = loop {
        let Some(item) = w.defer.as_deref_mut().and_then(|d| {
            let item = d.top.get(i).copied();
            if let Some(item) = &item {
                d.start(item);
            }
            item
        }) else {
            break Ok(());
        };
        i = i.saturating_add(1);
        w.depth = 1;
        if let Err(e) = (item.write)(item.value, w) {
            break Err(e);
        }
    };
    w.depth = 0;
    if let Some(d) = w.defer.as_deref_mut() {
        d.top.clear();
        d.top_active = false;
        d.current = None;
        d.current_anchor = None;
        d.current_generation = 0;
    }
    result
}

/// Ends the outermost region (its length at `at`): when bodies were deferred inside it, writes them as a section of
/// `(key, length, body)` entries and the section's offset, and sets bit 31 of the length. Returns false when there
/// was nothing to write (the caller patches the length).
pub(crate) fn write_section(w: &mut Writer<'_>, at: usize) -> Result<bool> {
    match w.defer.as_deref() {
        Some(d) if !d.region.is_empty() => {}
        _ => return Ok(false),
    }
    let start = at.checked_add(4).ok_or_else(Error::too_large)?;
    let section = w.position();
    let depth = w.depth;
    let saved = w.defer.as_deref().map(|d| (d.current, d.current_anchor, d.current_generation));
    w.regions = 1;
    let mut i = 0usize;
    let result = (|| {
        while let Some(item) = w.defer.as_deref_mut().and_then(|d| {
            let item = d.region.get(i).copied();
            if let Some(item) = &item {
                d.start(item);
            }
            item
        }) {
            i = i.saturating_add(1);
            w.i32(offset(item.position, start)?);
            let length = w.reserve_i32();
            w.depth = section_depth(depth);
            (item.write)(item.value, w)?;
            w.patch_length(length)?;
        }
        Ok(())
    })();
    w.regions = 0;
    w.depth = depth;
    if let Some(d) = w.defer.as_deref_mut() {
        if let Some((current, anchor, generation)) = saved {
            d.current = current;
            d.current_anchor = anchor;
            d.current_generation = generation;
        }
        d.region.clear();
    }
    result?;
    w.i32(offset(section, start)?);
    let total = offset(w.position(), start)? as u32 | crate::reader::SECTION_FLAG;
    w.patch_i32(at, total as i32);
    Ok(true)
}

/// `position - start` as the `i32` the format stores.
fn offset(position: usize, start: usize) -> Result<i32> {
    position.checked_sub(start).and_then(|n| i32::try_from(n).ok()).ok_or_else(Error::too_large)
}

impl core::fmt::Debug for Step {
    fn fmt(&self, f: &mut core::fmt::Formatter<'_>) -> core::fmt::Result {
        match self {
            Step::Field(_) => f.write_str("Step::Field"),
            Step::Index(_, i) => write!(f, "Step::Index({i})"),
            Step::Dyn(_) => f.write_str("Step::Dyn"),
        }
    }
}
