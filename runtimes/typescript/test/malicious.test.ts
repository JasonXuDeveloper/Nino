// Data that is not what a writer writes: truncated at every offset, forged lengths and counts, bad presence bytes,
// unknown type ids, and random or mutated bytes (fast-check). Every read must throw a NinoError (never read past the end, return garbage, allocate by a forged
// count, or loop), as Nino's Reader does in .NET.
import assert from "node:assert/strict";
import { describe, it } from "node:test";
import fc from "fast-check";
import { deserialize, deserializeExact, NinoError, NinoReader, NinoRegistry, serialize, type NinoCodec } from "../src/nino.ts";
import { chain, ChainCodec, hex, int32 } from "./codecs.ts";

class Item {
  id = 0;
  name: string | null = null;
  tags: string[] | null = null;
  scores: Map<string | null, number> | null = null;
  flags: Set<number> | null = null;
  maybe: bigint | null = null;
  when = new Date(0);
  price = "0";
  key = "00000000-0000-0000-0000-000000000000";
  blob: Uint8Array | null = null;
  note: string | null = null;
}

/** A codec of everything at once, as generated code reads and writes it. */
const ItemCodec: NinoCodec<Item> = {
  read: (r) => {
    const v = new Item();
    v.id = r.i32();
    v.name = r.string();
    v.tags = r.array((r) => r.string()!);
    v.scores = r.map((r) => r.string(), (r) => r.f64());
    v.flags = r.set((r) => r.u16());
    v.maybe = r.nullable((r) => r.i64());
    v.when = r.dateTime();
    v.price = r.decimal();
    v.key = r.guid();
    v.blob = r.bytes();
    v.note = r.utf8();
    return v;
  },
  write: (w, v) => {
    w.i32(v.id);
    w.string(v.name);
    w.array(v.tags, (w, s) => w.string(s));
    w.map(v.scores, (w, k) => w.string(k), (w, x) => w.f64(x));
    w.set(v.flags, (w, x) => w.u16(x));
    w.nullable(v.maybe, (w, x) => w.i64(x));
    w.dateTime(v.when);
    w.decimal(v.price);
    w.guid(v.key);
    w.bytes(v.blob);
    w.utf8(v.note);
  },
};

function item(): Item {
  return Object.assign(new Item(), {
    id: 7, name: "Zoë 名字 " + "x".repeat(40), tags: ["a", "", "ünï", "😀".repeat(20)],
    scores: new Map([["__proto__", 1.5], [null, -2]]), flags: new Set([1, 65535]), maybe: -5n,
    when: new Date(Date.UTC(2026, 0, 2)), price: "-12.3400", key: "0f8fad5b-d9cb-469f-a165-70867728950e",
    blob: Uint8Array.of(1, 2, 3), note: "naïve text",
  });
}

describe("malicious data", () => {
  it("reads back what it writes", () => {
    assert.deepEqual(deserialize(ItemCodec, serialize(ItemCodec, item())), item());
  });

  it("throws a NinoError for data truncated at every offset", () => {
    const bytes = serialize(ItemCodec, item());
    for (let cut = 0; cut < bytes.length; cut++)
      assert.throws(() => deserialize(ItemCodec, bytes.slice(0, cut)), NinoError, `cut at ${cut}`);
  });

  it("checks every count and length against the bytes left before allocating", () => {
    const huge = [0x7fffffff, 0x10000000, 1 << 24, 0x7ffffff0];
    for (const count of huge) {
      const forged = Uint8Array.from(int32(count));
      const reader = () => new NinoReader(forged);
      assert.throws(() => reader().array((r) => r.u8()), NinoError);
      assert.throws(() => reader().set((r) => r.u8()), NinoError);
      assert.throws(() => reader().map((r) => r.u8(), (r) => r.u8()), NinoError);
      assert.throws(() => reader().bytes(), NinoError);
      assert.throws(() => reader().string(), NinoError);
      assert.throws(() => reader().region(), NinoError);
      assert.throws(() => reader().take(count), NinoError);
    }
    for (const count of [-2, -100, -0x80000000]) {
      const reader = () => new NinoReader(Uint8Array.from(int32(count)));
      assert.throws(() => reader().array((r) => r.u8()), NinoError);
      assert.throws(() => reader().bytes(), NinoError);
      assert.throws(() => reader().length(), NinoError);
      assert.throws(() => reader().region(), NinoError);
    }
  });

  it("does not take a count of elements that each need a byte for more than the bytes there are", () => {
    // 1000 elements, 999 bytes.
    const forged = Uint8Array.from([...int32(1000), ...new Array(999).fill(0)]);
    assert.throws(() => new NinoReader(forged).array((r) => r.u8()), NinoError);
    assert.throws(() => new NinoReader(forged).map((r) => r.u8(), (r) => r.u8()), NinoError);
  });

  it("rejects presence bytes other than 0 and 1", () => {
    for (let flag = 2; flag < 256; flag += 17) {
      assert.throws(() => new NinoReader(Uint8Array.of(flag)).notNull(), NinoError);
      assert.throws(() => deserialize(ChainCodec, Uint8Array.of(flag, 0, 0, 0, 0, 0)), NinoError);
      assert.throws(() => new NinoReader(Uint8Array.of(flag)).bool(), NinoError);
      assert.throws(() => new NinoReader(Uint8Array.of(0, flag)).boolAt(1), NinoError);
      assert.throws(() => new NinoReader(Uint8Array.of(flag, 7, 0, 0, 0)).flagAt(0), NinoError);
    }
    assert.equal(new NinoReader(Uint8Array.of(1)).bool(), true);
    assert.equal(new NinoReader(Uint8Array.of(0)).bool(), false);
  });

  it("rejects type ids it does not know, and type ids of types not allowed there", () => {
    class A {}
    class B {}
    const types = new NinoRegistry();
    types.add(1, A, () => new A(), () => {});
    types.add(2, B, () => new B(), () => {});
    for (const id of [3, -1, 0x7fffffff, -0x80000000])
      assert.throws(() => deserialize(types.codec([1, 2]), Uint8Array.from(int32(id))), NinoError);
    assert.throws(() => deserialize(types.codec([1]), Uint8Array.from(int32(2))), NinoError);
    assert.ok(deserialize(types.codec([1, 2]), Uint8Array.from(int32(2))) instanceof B);
  });

  it("rejects a member that runs past the end of its region", () => {
    const r = new NinoReader(hex("02000000 01020304"));
    const end = r.region();
    r.i32();
    assert.throws(() => r.endRegion(end), NinoError);
  });

  it("rejects a known keyed member whose value leaves bytes in its entry, and skips unknown ones whole", () => {
    // Keyed { int A (key 1); string B (key 2) }, read as generated code reads it.
    const read = (data: number[]) => {
      const r = new NinoReader(Uint8Array.from(data)), v = { a: 0, b: null as string | null };
      const end = r.region();
      while (r.pos < end) {
        const key = r.i32(), member = r.region();
        switch (key) {
          case 1:
            v.a = r.i32();
            r.endMember(member);
            continue;
          case 2:
            v.b = r.string();
            r.endMember(member);
            continue;
        }
        r.endRegion(member);
      }
      r.endRegion(end);
      assert.equal(r.pos, data.length);
      return v;
    };
    const body = (...entries: number[][]) => {
      const members = entries.flat();
      return [...int32(members.length), ...members];
    };
    const entry = (key: number, value: number[]) => [...int32(key), ...int32(value.length), ...value];
    const b = [...int32(~2), 0x68, 0x69];
    assert.deepEqual(read(body(entry(2, b), entry(9, [1, 2, 3]), entry(1, int32(7)))), { a: 7, b: "hi" });
    assert.throws(() => read(body(entry(1, [...int32(7), 0]))), /bytes left over at the end of a keyed member/);
    assert.throws(() => read(body(entry(2, [...b, 0x21]))), /bytes left over at the end of a keyed member/);
    // A value read past its entry: the string's length claims a byte of the next entry.
    const long = body(entry(2, b), entry(1, int32(7)));
    long[4 + 8] = ~3 & 0xff;
    assert.throws(() => read(long), /runs past the end/);
  });

  it("leaves the bytes after a value unread, as .NET does", () => {
    const bytes = serialize(ChainCodec, chain(3)), padded = new Uint8Array(bytes.length + 10).fill(0xff);
    padded.set(bytes);
    assert.equal(deserialize(ChainCodec, padded)!.value, 1);
  });

  // Property tests (fast-check): random bytes, and valid data with bytes changed, inserted or cut out.
  const valid = serialize(ItemCodec, item()), chainBytes = serialize(ChainCodec, chain(200));
  const mutated = (bytes: Uint8Array) => fc.tuple(
    fc.array(fc.tuple(fc.nat(), fc.integer({ min: 0, max: 255 })), { minLength: 1, maxLength: 8 }),
    fc.option(fc.tuple(fc.nat(), fc.uint8Array({ maxLength: 8 }))),
    fc.option(fc.tuple(fc.nat(), fc.nat({ max: 16 }))),
  ).map(([changes, insert, cut]) => {
    let data = bytes.slice();
    for (const [at, value] of changes) data[at % data.length] = value;
    if (insert !== null) {
      const at = insert[0] % (data.length + 1);
      data = Uint8Array.from([...data.subarray(0, at), ...insert[1], ...data.subarray(at)]);
    }
    if (cut !== null) {
      const at = cut[0] % (data.length + 1);
      data = Uint8Array.from([...data.subarray(0, at), ...data.subarray(at + cut[1])]);
    }
    return data;
  });
  const inputs = fc.oneof(fc.uint8Array({ maxLength: 300 }), mutated(valid), mutated(chainBytes));
  const codecs = [ItemCodec, ChainCodec] as NinoCodec<unknown>[];

  it("reads random and mutated bytes without anything but a NinoError", () => {
    fc.assert(fc.property(inputs, (data) => {
      for (const codec of codecs) {
        try {
          deserialize(codec, data);
        } catch (e) {
          if (!(e instanceof NinoError)) throw e;
        }
      }
    }), { numRuns: 3000 });
  });

  it("writes back whatever it accepts, and reads that to the same value", () => {
    fc.assert(fc.property(inputs, (data) => {
      for (const codec of codecs) {
        let value: unknown;
        try {
          value = deserialize(codec, data);
        } catch (e) {
          if (e instanceof NinoError) continue;
          throw e;
        }
        assert.deepEqual(deserializeExact(codec, serialize(codec, value)), value);
      }
    }), { numRuns: 3000 });
  });
});
