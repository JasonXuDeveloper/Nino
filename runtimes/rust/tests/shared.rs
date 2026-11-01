//! Reference tracking's memory (src/shared.rs): the objects one read creates belong to its arena and are freed with
//! it, cycles included, whether the read succeeds or fails; a clone of a `Shared` from inside the graph keeps the
//! graph alive. Each test counts the `Node`s alive on its own thread.

#![allow(clippy::unwrap_used, clippy::indexing_slicing)]

use std::cell::Cell;

use nino::{NinoReadBody, NinoWriteBody, Reader, Shared, Writer};

thread_local! {
    static ALIVE: Cell<isize> = const { Cell::new(0) };
}

fn alive() -> isize {
    ALIVE.with(Cell::get)
}

/// Counts the values that hold one.
struct Counted;

impl Default for Counted {
    fn default() -> Self {
        ALIVE.with(|a| a.set(a.get() + 1));
        Counted
    }
}

impl Drop for Counted {
    fn drop(&mut self) {
        ALIVE.with(|a| a.set(a.get() - 1));
    }
}

/// A tracked class: `[NinoType(trackReferences: true)] class Node { int Value; Node Next; List<Node> Others; }`.
#[derive(Default)]
struct Node {
    value: i32,
    next: Option<Shared<Node>>,
    others: Vec<Option<Shared<Node>>>,
    _counted: Counted,
}

impl NinoReadBody for Node {
    fn read_body(&mut self, r: &mut Reader<'_>) -> nino::Result<()> {
        self.value = r.read()?;
        r.read_member_into(&mut self.next, nino::field!(Self, next))?;
        r.read_member_into(&mut self.others, nino::field!(Self, others))?;
        Ok(())
    }
}

impl NinoWriteBody for Node {
    fn write_body<'a>(&'a self, w: &mut Writer<'a>) -> nino::Result<()> {
        w.write(&self.value)?;
        w.write_shared(self.next.as_ref())?;
        w.write(&self.others)
    }
}

fn hex(s: &str) -> Vec<u8> {
    let s: String = s.split_whitespace().collect();
    (0..s.len()).step_by(2).map(|i| u8::from_str_radix(&s[i..i + 2], 16).unwrap()).collect()
}

/// Node 1 (value 7) whose next is itself and whose others are a new node 2 (value 8, next node 1, no others) and
/// node 2 again: two cycles.
fn cyclic() -> Vec<u8> {
    hex("ffffffff 07000000 01000000 02000000   ffffffff 08000000 01000000 00000000   02000000")
}

fn read(bytes: &[u8]) -> nino::Result<Option<Shared<Node>>> {
    nino::deserialize_exact(bytes)
}

#[test]
fn a_cyclic_graph_read_is_freed_with_its_last_shared() {
    let bytes = cyclic();
    for _ in 0..100 {
        let root = read(&bytes).unwrap().unwrap();
        let node = root.get().unwrap();
        assert_eq!(node.value, 7);
        assert!(Shared::ptr_eq(node.next.as_ref().unwrap(), &root));
        let two = node.others[0].as_ref().unwrap();
        assert_eq!(two.get().unwrap().value, 8);
        assert!(Shared::ptr_eq(two.get().unwrap().next.as_ref().unwrap(), &root));
        assert!(Shared::ptr_eq(node.others[1].as_ref().unwrap(), two));
        assert_eq!(alive(), 2);
        // Written back the same, cycles and all.
        assert_eq!(nino::serialize(&Some(root.clone())).unwrap(), bytes);
        drop(root);
        assert_eq!(alive(), 0, "every node of the read is freed");
    }
}

#[test]
fn a_clone_from_inside_the_graph_keeps_it_alive() {
    let root = read(&cyclic()).unwrap().unwrap();
    let two = root.get().unwrap().others[0].clone().unwrap();
    drop(root);
    assert_eq!(alive(), 2);
    let one = two.get().unwrap().next.as_ref().unwrap();
    assert_eq!(one.get().unwrap().value, 7);
    assert!(Shared::ptr_eq(one.get().unwrap().next.as_ref().unwrap(), one));
    drop(two);
    assert_eq!(alive(), 0);
}

#[test]
fn a_failed_read_frees_what_it_made() {
    let bytes = cyclic();
    for n in 0..bytes.len() {
        assert!(read(&bytes[..n]).is_err(), "prefix {n}");
        assert_eq!(alive(), 0, "prefix {n}");
    }
    // A bad back reference after a cycle was made.
    let mut bad = bytes.clone();
    bad[bytes.len() - 4] = 3;
    assert!(read(&bad).is_err());
    assert_eq!(alive(), 0);
}

#[test]
fn a_graph_built_by_hand_is_reference_counted() {
    let leaf = Shared::new(Node { value: 2, ..Node::default() });
    let root = Shared::new(Node {
        value: 1,
        next: Some(leaf.clone()),
        others: vec![Some(leaf.clone()), None],
        ..Node::default()
    });
    let bytes = nino::serialize(&Some(root.clone())).unwrap();
    drop((root, leaf));
    assert_eq!(alive(), 0);
    let back = read(&bytes).unwrap().unwrap();
    let node = back.get().unwrap();
    assert!(Shared::ptr_eq(node.next.as_ref().unwrap(), node.others[0].as_ref().unwrap()));
    assert!(node.others[1].is_none());
    drop(back);
    assert_eq!(alive(), 0);
}

/// `n` nodes, each the next of the one before, the last one's next the first: deeper than 128, so the bodies past
/// it are deferred and read later. Written from a chain built by hand (no cycle, so nothing leaks), whose last
/// body's null next becomes a back reference to the first node.
fn ring(n: i32) -> Vec<u8> {
    const LAST: i32 = 0x5A5A_5A5A;
    let mut next = None;
    for i in (0..n).rev() {
        let value = if i == n - 1 { LAST } else { i };
        next = Some(Shared::new(Node { value, next, ..Node::default() }));
    }
    let mut bytes = nino::serialize(&next).unwrap();
    let at = bytes.windows(4).position(|w| w == LAST.to_le_bytes()).unwrap();
    bytes[at..at + 4].copy_from_slice(&(n - 1).to_le_bytes());
    assert_eq!(bytes[at + 4..at + 8], 0i32.to_le_bytes());
    bytes[at + 4..at + 8].copy_from_slice(&1i32.to_le_bytes());
    bytes
}

#[test]
fn a_deep_ring_with_deferred_bodies_is_freed() {
    let bytes = ring(300);
    assert_eq!(alive(), 0);
    let root = read(&bytes).unwrap().unwrap();
    assert_eq!(alive(), 300);
    let mut at = root.clone();
    for i in 0..300 {
        assert_eq!(at.get().unwrap().value, i);
        at = at.get().unwrap().next.clone().unwrap();
    }
    assert!(Shared::ptr_eq(&at, &root));
    drop(at);
    assert_eq!(nino::serialize(&Some(root.clone())).unwrap(), bytes);
    drop(root);
    assert_eq!(alive(), 0, "every node of the read is freed");
}

/// A tracked class whose `Drop` reads the object it refers to, while the arena frees them one by one.
#[derive(Default)]
struct Peek {
    next: Option<Shared<Peek>>,
    seen: std::rc::Rc<Cell<usize>>,
}

impl Drop for Peek {
    fn drop(&mut self) {
        if let Some(next) = self.next.as_ref().and_then(Shared::get) {
            self.seen.set(self.seen.get() + 1 + usize::from(next.next.is_some()));
        }
    }
}

impl NinoReadBody for Peek {
    fn read_body(&mut self, r: &mut Reader<'_>) -> nino::Result<()> {
        r.read_member_into(&mut self.next, nino::field!(Self, next))
    }
}

#[test]
fn a_drop_that_reads_another_object_sees_it_alive_or_not_at_all() {
    // Peek 1 -> Peek 2 -> Peek 1.
    let bytes = hex("ffffffff ffffffff 01000000");
    let root = nino::deserialize_exact::<Option<Shared<Peek>>>(&bytes).unwrap().unwrap();
    drop(root);
}
