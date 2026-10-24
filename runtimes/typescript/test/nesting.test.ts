// Deep nesting: past 128 open class bodies a body is deferred (after the top-level object, or in a section at the end
// of the outermost region), so any depth costs a bounded stack; bodies that cannot be deferred, and structs that hold
// themselves, fail past 512; cycles fail. The byte layout is checked against .NET by tests/Nino.Codegen.Integration.Tests.
import assert from "node:assert/strict";
import { describe, it } from "node:test";
import { deserialize, NinoError, serialize } from "../src/nino.ts";
import {
  chain, Chain, ChainCodec, int32, Nest, NestCodec, Record, RecordCodec, tolerant, TolerantCodec, values,
} from "./codecs.ts";

const range = (n: number) => Array.from({ length: n }, (_, i) => i + 1);

describe("deep nesting", () => {
  it("writes and reads chains of any length with a bounded stack", () => {
    for (const length of [1, 127, 128, 129, 130, 256, 1000, 100000]) {
      const bytes = serialize(ChainCodec, chain(length));
      assert.deepEqual(values(deserialize(ChainCodec, bytes)), range(length), `length ${length}`);
    }
  });

  it("writes no more bytes when it defers: one presence byte and one int per link", () => {
    for (const length of [100, 129, 1000]) assert.equal(serialize(ChainCodec, chain(length)).length, 1 + 5 * length);
  });

  it("writes and reads deep version tolerant chains, deferred into the outermost region's section", () => {
    for (const length of [127, 128, 129, 300, 20000]) {
      const bytes = serialize(TolerantCodec, tolerant(length));
      assert.deepEqual(values(deserialize(TolerantCodec, bytes)), range(length), `length ${length}`);
      // The outermost region's length has the section flag once anything was deferred.
      const flagged = (new DataView(bytes.buffer, bytes.byteOffset).getInt32(1, true) & 0x80000000) !== 0;
      assert.equal(flagged, length > 128, `length ${length}`);
    }
  });

  it("reads data nested in place deeper than any writer nests it, with a bounded stack", () => {
    const bytes = [1];
    for (let i = 1; i <= 200000; i++) bytes.push(...int32(i), i < 200000 ? 1 : 0);
    assert.deepEqual(values(deserialize(ChainCodec, Uint8Array.from(bytes))), range(200000));
  });

  it("rejects a region nested in place past 128 (a writer would have written a section)", () => {
    let body: number[] = [0];
    for (let i = 300; i > 0; i--) {
      const members = [...int32(i), ...body];
      body = [1, ...int32(members.length), ...members];
    }
    assert.throws(() => deserialize(TolerantCodec, Uint8Array.from(body)), /missing deferred bodies/);
  });

  it("writes and reads classes that cannot be deferred in place, up to 512 bodies", () => {
    const record = (n: number) => {
      let head: Record | null = null;
      for (let i = n; i > 0; i--) head = Object.assign(new Record(), { value: i, next: head });
      return head;
    };
    assert.deepEqual(values(deserialize(RecordCodec, serialize(RecordCodec, record(512)))), range(512));
    assert.throws(() => serialize(RecordCodec, record(513)), NinoError);
    const forged = [1];
    for (let i = 1; i <= 100000; i++) forged.push(...int32(i), i < 100000 ? 1 : 0);
    assert.throws(() => deserialize(RecordCodec, Uint8Array.from(forged)), NinoError);
  });

  it("counts structs that hold themselves, up to 512", () => {
    const nest = (n: number) => {
      let v = Object.assign(new Nest(), { value: n });
      for (let i = n - 1; i > 0; i--) v = Object.assign(new Nest(), { value: i, inner: [v] });
      return v;
    };
    assert.equal(deserialize(NestCodec, serialize(NestCodec, nest(512))).value, 1);
    assert.throws(() => serialize(NestCodec, nest(513)), NinoError);
    const forged: number[] = [];
    for (let i = 1; i < 100000; i++) forged.push(...int32(i), ...int32(1));
    forged.push(...int32(100000), ...int32(-1));
    assert.throws(() => deserialize(NestCodec, Uint8Array.from(forged)), NinoError);
  });

  it("rejects cycles, short or long, instead of writing forever", () => {
    for (const length of [1, 2, 127, 128, 129, 1000]) {
      const head = chain(length);
      let last = head;
      while (last.next !== null) last = last.next;
      last.next = head;
      assert.throws(() => serialize(ChainCodec, head), NinoError, `length ${length}`);
    }
    const struct = new Nest();
    struct.inner = [struct];
    assert.throws(() => serialize(NestCodec, struct), NinoError);
  });

  it("writes again after a cycle failed", () => {
    const head = chain(300);
    head.next!.next = head;
    assert.throws(() => serialize(ChainCodec, head), NinoError);
    assert.deepEqual(values(deserialize(ChainCodec, serialize(ChainCodec, chain(300)))), range(300));
  });

  it("rejects truncated deep data at every length", () => {
    for (const bytes of [serialize(ChainCodec, chain(300)), serialize(TolerantCodec, tolerant(300))]) {
      const codec = bytes.length === 1 + 5 * 300 ? ChainCodec : TolerantCodec;
      for (let cut = 0; cut < bytes.length; cut++)
        assert.throws(() => deserialize(codec as typeof ChainCodec, bytes.subarray(0, cut)), NinoError, `cut ${cut}`);
    }
  });

  it("rejects forged sections", () => {
    const bytes = serialize(TolerantCodec, tolerant(200)), view = new DataView(bytes.buffer, bytes.byteOffset);
    const length = view.getInt32(1, true) & 0x7fffffff, offsetAt = 5 + length - 4;
    const forge = (at: number, value: number) => {
      const copy = bytes.slice();
      new DataView(copy.buffer).setInt32(at, value, true);
      return copy;
    };
    const members = view.getInt32(offsetAt, true);
    for (const forged of [
      forge(offsetAt, -1), forge(offsetAt, length), forge(offsetAt, 0x7fffffff), // the section's offset
      forge(1, 0x80000003), forge(1, 0x80000000 | (length + 1)), forge(1, -1), // the region's length
      forge(5 + members + 4, 0x7fffffff), forge(5 + members + 4, -1), // an entry's length
      forge(5 + members, 12345), // an entry's key: no entry for the body
    ])
      assert.throws(() => deserialize(TolerantCodec, forged), NinoError);
  });

  it("checks a section before reading any member, as .NET, Go and Rust do", () => {
    const bytes = serialize(TolerantCodec, tolerant(300)), view = new DataView(bytes.buffer, bytes.byteOffset);
    const length = view.getInt32(1, true) & 0x7fffffff, offsetAt = 5 + length - 4;
    const members = view.getInt32(offsetAt, true), first = 5 + members;
    const second = first + 8 + view.getInt32(first + 4, true);
    assert.ok(second < offsetAt, "two entries at least");
    const forge = (at: number, value: number) => {
      const copy = bytes.slice();
      new DataView(copy.buffer).setInt32(at, value, true);
      return copy;
    };
    const cases: [string, Uint8Array, RegExp][] = [
      ["no entry (the offset at the section's end)", forge(offsetAt, length - 4), /invalid offset of deferred bodies/],
      ["the members read as the section", forge(offsetAt, 0), /invalid section of deferred bodies/],
      ["an entry keyed past itself", forge(first, members + 1), /invalid section of deferred bodies/],
      ["keys not increasing", forge(second, view.getInt32(first, true)), /invalid section of deferred bodies/],
      ["a negative key", forge(first, -1), /invalid section of deferred bodies/],
      ["an entry cut by the offset", forge(first + 4, offsetAt - first - 8 + 1), /invalid section of deferred bodies/],
    ];
    for (const [name, forged, error] of cases) assert.throws(() => deserialize(TolerantCodec, forged), error, name);
    // A flag on a region inside another: only the outermost region holds a section.
    const inner = serialize(TolerantCodec, tolerant(2)), at = 1 + 4 + 4 + 1;
    const flagged = inner.slice();
    new DataView(flagged.buffer).setInt32(at, new DataView(inner.buffer, inner.byteOffset).getInt32(at, true) | 0x80000000,
      true);
    assert.throws(() => deserialize(TolerantCodec, flagged), /region inside another region/);
  });

  it("leaves no state behind in the shared writer when a deep value fails", () => {
    const head = chain(5000);
    let last: Chain = head;
    while (last.next !== null) last = last.next;
    last.next = head.next!.next;
    assert.throws(() => serialize(ChainCodec, head), NinoError);
    assert.equal(serialize(ChainCodec, chain(1)).length, 6);
  });
});
