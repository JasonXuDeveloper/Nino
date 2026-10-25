//! The worked examples of the specification (docs/spec/wire-format.md, Appendix A and 7.10), byte for byte.

mod samples;

use nino::{deserialize, deserialize_exact, serialize, type_id, ErrorKind, RawNullables, Utf8};
use samples::*;

fn hex(s: &str) -> Vec<u8> {
    let s: String = s.chars().filter(|c| !c.is_whitespace() && *c != '|').collect();
    (0..s.len()).step_by(2).map(|i| u8::from_str_radix(&s[i..i + 2], 16).unwrap()).collect()
}

#[test]
fn a1_small() {
    let v = Some(Small { a: 1, b: Some("a".into()) });
    let bytes = serialize(&v).unwrap();
    assert_eq!(bytes, hex("01 01000000 0361"));
    assert_eq!(deserialize_exact::<Option<Small>>(&bytes).unwrap(), v);
    assert_eq!(serialize(&None::<Small>).unwrap(), hex("00"));
    assert_eq!(deserialize::<Option<Small>>(&hex("00")).unwrap(), None);
    // The declared type is not nullable: null is an error.
    assert_eq!(deserialize::<Small>(&hex("00")).unwrap_err().kind(), ErrorKind::UnexpectedNull);
    assert_eq!(type_id("Spec.Small") as u32, 0xF8B7D639);
}

#[test]
fn a2_polymorphism() {
    let v = vec![
        Some(Shape::Circle(Box::new(Circle { id: 1, r: 1.0 }))),
        Some(Shape::Square(Box::new(Square { id: 2, side: 2.0 }))),
        None,
    ];
    let bytes = serialize(&v).unwrap();
    assert_eq!(bytes, hex("03000000 AC2BC0ED 01000000 0000803F 1F27E78F 02000000 00000040 00000000"));
    assert_eq!(deserialize_exact::<Vec<Option<Shape>>>(&bytes).unwrap(), v);
    assert_eq!(Circle::TYPE_ID as u32, 0xEDC02BAC);
    assert_eq!(Square::TYPE_ID as u32, 0x8FE7271F);
    assert_eq!(type_id("Spec.Box<System.Int32>") as u32, 0x6781EE38);
    assert_eq!(type_id("Spec.Open") as u32, 0x8213CC32);

    // An id outside the hierarchy is rejected, with the id in the error.
    let mut bad = bytes.clone();
    bad[4] ^= 1;
    let err = deserialize::<Vec<Option<Shape>>>(&bad).unwrap_err();
    assert_eq!(err.kind(), ErrorKind::UnknownTypeId);
}

#[test]
fn a3_keyed() {
    let v = Some(Keyed { id: 1, name: Some("n".into()), score: 2 });
    let bytes = serialize(&v).unwrap();
    let expected =
        hex("01 26000000 4819C498 04000000 01000000 E2B38530 02000000 036E 4DB14A04 08000000 0200000000000000");
    assert_eq!(bytes, expected);
    assert_eq!(deserialize_exact::<Option<Keyed>>(&bytes).unwrap(), v);

    let null_name = serialize(&Some(Keyed { id: 1, name: None, score: 2 })).unwrap();
    assert_eq!(&null_name[1..5], &hex("25000000")[..]);
    assert_eq!(&null_name[17..26], &hex("E2B38530 01000000 00")[..]);

    // The former name's key is read too; unknown keys are skipped; order does not matter.
    let former = hex("01 20000000 959DA293 02000000 036E 01020304 02000000 AAAA 4819C498 04000000 01000000");
    let read = deserialize_exact::<Option<Keyed>>(&former).unwrap().unwrap();
    assert_eq!(read, Keyed { id: 1, name: Some("n".into()), score: 0 });

    // A member running past its entry is an error.
    let short = hex("01 0C000000 4819C498 02000000 01000000");
    assert!(deserialize::<Option<Keyed>>(&short).is_err());
}

#[test]
fn a3_version_tolerant() {
    let leaf = TolLeaf { a: 1, b: 7 };
    let as_leaf = serialize(&Some(leaf.clone())).unwrap();
    assert_eq!(as_leaf, hex("01 08000000 01000000 07000000"));
    let as_base = serialize(&Some(TolBase::Leaf(Box::new(leaf.clone())))).unwrap();
    assert_eq!(as_base, hex("7730462B 08000000 01000000 07000000"));
    assert_eq!(deserialize_exact::<Option<TolBase>>(&as_base).unwrap(), Some(TolBase::Leaf(Box::new(leaf))));

    // An older reader skips what it does not know.
    let mut r = nino::Reader::new(&as_leaf);
    let old: Option<TolBaseOnly> = r.read().unwrap();
    assert_eq!(old, Some(TolBaseOnly { a: 1 }));
    assert_eq!(r.remaining(), 0);

    // A newer reader defaults what is missing.
    let older = hex("01 04000000 01000000");
    assert_eq!(deserialize_exact::<Option<TolLeaf>>(&older).unwrap(), Some(TolLeaf { a: 1, b: 0 }));

    // A region longer than the data, or a member past the region's end.
    assert_eq!(
        deserialize::<Option<TolLeaf>>(&hex("01 09000000 01000000 07000000")).unwrap_err().kind(),
        ErrorKind::InvalidLength
    );
    assert_eq!(
        deserialize::<Option<TolLeaf>>(&hex("01 06000000 01000000 0700")).unwrap_err().kind(),
        ErrorKind::UnexpectedEnd
    );
    assert!(deserialize::<Option<TolLeaf>>(&hex("01 FFFFFFFF")).is_err());
}

#[test]
fn a4_strings() {
    let v = Some(Strings { def: Some("x".into()), u8: Some(Utf8("é😀".into())) });
    let bytes = serialize(&v).unwrap();
    assert_eq!(bytes, hex("01 0378 04 06 C3A9F09F9880"));
    assert_eq!(deserialize_exact::<Option<Strings>>(&bytes).unwrap(), v);

    let def = |s: Option<&str>| serialize(&s.map(String::from)).unwrap();
    assert_eq!(def(None), hex("00"));
    assert_eq!(def(Some("")), hex("01"));
    assert_eq!(def(Some("abc")), hex("07 616263"));
    assert_eq!(def(Some("é")), hex("03 E9"));
    assert_eq!(def(Some("a中")), hex("04 6100 2D4E"));
    assert_eq!(def(Some("😀")), hex("04 3DD8 00DE"));

    let u8 = |s: Option<&str>| serialize(&s.map(|s| Utf8(s.into()))).unwrap();
    assert_eq!(u8(None), hex("00"));
    assert_eq!(u8(Some("")), hex("01"));
    assert_eq!(u8(Some("abc")), hex("04 03 616263"));

    // A lone surrogate: a String reads it as U+FFFD, a WideString keeps it and writes it back.
    let lone = hex("04 6100 00D8");
    assert_eq!(deserialize_exact::<String>(&lone).unwrap(), "a\u{FFFD}");
    let wide = deserialize_exact::<nino::WideString>(&lone).unwrap();
    assert_eq!(wide.0, vec![0x61, 0xD800]);
    assert_eq!(serialize(&wide).unwrap(), lone);
    // .NET writes "a\uD800" as UTF-8 with U+FFFD: that is what a String holds.
    assert_eq!(u8(Some("a\u{FFFD}")), hex("03 04 61EFBFBD"));
}

#[test]
fn a6_raw_nullables() {
    let v = RawNullables::<i64>(vec![Some(1), None]);
    let bytes = serialize(&v).unwrap();
    assert_eq!(bytes, hex("02000000 01 00000000000000 0100000000000000 00000000000000000000000000000000"));
    assert_eq!(deserialize_exact::<RawNullables<i64>>(&bytes).unwrap(), v);

    let v = RawNullables::<i32>(vec![Some(1), None]);
    assert_eq!(serialize(&v).unwrap(), hex("02000000 01000000 01000000 00000000 00000000"));
    // One T? per element (IList<int?>).
    assert_eq!(serialize(&vec![Some(1i32), None]).unwrap(), hex("02000000 01 01000000 00"));
    assert_eq!(serialize(&Some(7i32)).unwrap(), hex("01 07000000"));
    assert_eq!(serialize(&None::<i64>).unwrap(), hex("00"));

    // Padding and flags are checked.
    assert_eq!(
        deserialize::<RawNullables<i32>>(&hex("01000000 02000000 01000000")).unwrap_err().kind(),
        ErrorKind::InvalidPresence
    );
    // The value bytes of a null element are not looked at (.NET writes zeros there).
    assert_eq!(deserialize_exact::<RawNullables<i32>>(&hex("01000000 00000000 01000000")).unwrap().0, vec![None]);
}

#[test]
fn deep_link_first_131() {
    let chain = LinkFirst::chain(131);
    let bytes = serialize(&chain).unwrap();
    let mut expected = vec![1u8; 129];
    for v in (1..=128i32).rev() {
        expected.extend_from_slice(&v.to_le_bytes());
    }
    expected.extend(hex("01 01 00 83000000 82000000 81000000"));
    assert_eq!(bytes, expected);

    let back: Option<Box<LinkFirst>> = deserialize_exact(&bytes).unwrap();
    assert_eq!(LinkFirst::values(back.as_deref()), (1..=131).collect::<Vec<_>>());
}

#[test]
fn deep_link_first_long() {
    // Every 128 levels go to the end of the previous batch: no recursion past 128 bodies in either direction.
    for n in [127, 128, 129, 255, 256, 257, 1000, 100_000] {
        let chain = LinkFirst::chain(n);
        let bytes = serialize(&chain).unwrap();
        let back: Option<Box<LinkFirst>> = deserialize_exact(&bytes).unwrap();
        assert_eq!(LinkFirst::values(back.as_deref()), (1..=n).collect::<Vec<_>>(), "n = {n}");
    }
}

#[test]
fn deep_link_tol_130() {
    let chain = LinkTol::chain(130);
    let bytes = serialize(&chain).unwrap();
    assert_eq!(bytes.len(), 1183);
    assert_eq!(&bytes[..23], &hex("01 9A040080 01000000 01 73040000 02000000 01 6A040000")[..]);
    let tail = hex("05000000 80000000 01 7C040000 12000000 0E000000 81000000 01 05000000 82000000 00 7C040000");
    assert_eq!(&bytes[bytes.len() - tail.len()..], &tail[..]);

    let back: Option<Box<LinkTol>> = deserialize_exact(&bytes).unwrap();
    assert_eq!(LinkTol::values(back.as_deref()), (1..=130).collect::<Vec<_>>());
}

#[test]
fn deep_link_tol_long() {
    for n in [1, 128, 129, 130, 256, 257, 300, 1000, 20_000] {
        let chain = LinkTol::chain(n);
        let bytes = serialize(&chain).unwrap();
        let back: Option<Box<LinkTol>> = deserialize_exact(&bytes).unwrap();
        assert_eq!(LinkTol::values(back.as_deref()), (1..=n).collect::<Vec<_>>(), "n = {n}");
        assert_eq!(serialize(&back).unwrap(), bytes);
    }
}

#[test]
fn deep_keyed_130() {
    let chain = LinkKeyed::chain(130);
    let bytes = serialize(&chain).unwrap();
    assert_eq!(&bytes[..14], &hex("01 BA0C0080 3A11A6C9 680C0000 01")[..]);
    assert_eq!(&bytes[bytes.len() - 4..], &hex("7C0C0000")[..]);
    let back: Option<Box<LinkKeyed>> = deserialize_exact(&bytes).unwrap();
    assert_eq!(LinkKeyed::values(back.as_deref()), (1..=130).collect::<Vec<_>>());
    for n in [129, 400, 5000] {
        let chain = LinkKeyed::chain(n);
        let bytes = serialize(&chain).unwrap();
        let back: Option<Box<LinkKeyed>> = deserialize_exact(&bytes).unwrap();
        assert_eq!(LinkKeyed::values(back.as_deref()), (1..=n).collect::<Vec<_>>());
    }
}

#[test]
fn deep_tree_in_lists() {
    for (depth, width) in [(130, 1), (130, 4), (300, 3), (600, 2)] {
        let tree = Tree::comb(depth, width);
        let bytes = serialize(&Some(tree.clone())).unwrap();
        let back: Option<Tree> = deserialize_exact(&bytes).unwrap();
        assert!(back.as_ref() == Some(&tree), "depth {depth} width {width}");
        assert_eq!(serialize(&back).unwrap(), bytes);
        // The tree's own drop recurses: forget the deep ones instead of overflowing the test's stack.
        std::mem::forget(back);
        std::mem::forget(tree);
    }
}

#[test]
fn deep_polymorphic() {
    for n in [1, 128, 129, 130, 700] {
        let chain = Some(Node::chain(n));
        let bytes = serialize(&chain).unwrap();
        let back: Option<Node> = deserialize_exact(&bytes).unwrap();
        assert!(back == chain, "n = {n}");
        assert_eq!(serialize(&back).unwrap(), bytes);
        std::mem::forget(back);
        std::mem::forget(chain);
    }
}

#[test]
fn deep_non_deferrable() {
    // Inline up to 512 open bodies, then an error, writing and reading alike.
    let ok = Rec::chain(512);
    let bytes = serialize(&ok).unwrap();
    assert_eq!(bytes.len(), 513);
    assert!(deserialize_exact::<Option<Box<Rec>>>(&bytes).is_ok());
    let deep = Rec::chain(513);
    assert_eq!(serialize(&deep).unwrap_err().kind(), ErrorKind::TooDeep);
    let mut bytes = vec![1u8; 513];
    bytes.push(0);
    assert_eq!(deserialize::<Option<Box<Rec>>>(&bytes).unwrap_err().kind(), ErrorKind::TooDeep);
}

#[test]
fn deep_inline_deferrable_is_read_lazily() {
    // 1 000 000 presence bytes of a deferrable chain, then nothing: deferred, not recursed into, and an error.
    let bytes = vec![1u8; 1_000_000];
    assert_eq!(deserialize::<Option<Box<LinkFirst>>>(&bytes).unwrap_err().kind(), ErrorKind::UnexpectedEnd);
}

#[test]
fn deep_in_region_needs_a_section() {
    let chain = LinkTol::chain(130);
    let mut bytes = serialize(&chain).unwrap();
    // Clear the section flag: the queued bodies have nowhere to come from.
    bytes[4] &= 0x7F;
    assert!(deserialize::<Option<Box<LinkTol>>>(&bytes).is_err());
}
