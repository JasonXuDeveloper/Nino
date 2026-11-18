// nino-gen's code for runtimes/csharp/tests/Nino.Codegen.Tests/Shapes.cs (test/generated/Nino.Codegen.Tests.ts) against bytes .NET
// writes for it.
import assert from "node:assert/strict";
import { describe, it } from "node:test";
import { deserializeExact, deserializeInto, NinoError, serialize, type NinoNullable, type NullableSlot } from "../src/nino.ts";
import { Grid, GridCodec } from "./generated/Nino.Conformance.ts";
import {
  Box, Flags, HoldsNinoNullable, HoldsWideNinoNullable, NinoNullableArgs, NinoNullableArgsCodec, NinoNullableRaw,
  NinoNullableRawCodec, NinoNullableRawWide, NinoNullableRawWideCodec, NullableGrids, NullableGridsCodec,
  NullableSlots, NullableSlotsCodec, Strides,
  StridesCodec, TupleCell, TupleInside,
} from "./generated/Nino.Codegen.Tests.ts";

function hex(bytes: Uint8Array): string {
  return Array.from(bytes, (b) => b.toString(16).padStart(2, "0")).join("");
}

describe("Shapes", () => {
  it("writes T?[] of a raw struct at the stride .NET writes", () => {
    // TupleCell is aligned to 4 by the auto layout of the (byte, short) it holds: the flag, three bytes of padding,
    // then the value; as a member and as a type argument.
    const want = "01" + "02000000" + "01000000" + "0100000002000000fcff0300" + "00".repeat(16) +
      "01" + "02000000" + "00".repeat(16) + "01000000" + "0100000002000000fcff0300";
    const cell = Object.assign(new TupleCell(), {
      a: 1,
      inside: Object.assign(new TupleInside(), { a: 2, t: [3, -4] as [number, number] }),
    });
    const boxed = Object.assign(new Box<NullableSlot<TupleCell>[] | null>(), { value: [null, cell] });
    const strides = Object.assign(new Strides(), { cells: [cell, null], boxed });
    const data = serialize(StridesCodec, strides);
    assert.equal(hex(data), want);
    assert.deepEqual(deserializeExact(StridesCodec, data), strides);
  });

  it("reads and writes a raw T? as a slot in every sequence, and as a single value elsewhere", () => {
    // What .NET writes for the NullableSlots of ModelTests.cs, NullableSlotsHex: a slot of a fixed size as the element
    // of a queue, a stack, a linked list, a sorted set, a list interface, nested and a set inside type arguments; a
    // presence byte and the value in a dictionary and a tuple.
    const want = "010200000001000000050000000000000000000000020000000000000000000000000000000000000001000000000000000700000000000000" +
      "020000000000010903000000000000000000000001000000ffffffff0100000003000000020000000100000001000000000000000000000000" +
      "00000000000000000000000000000003000000010000000100000001000000ffffffff00000000010200000001000000040000000000000000" +
      "000000010100000002000000000000000000000000000000000000000100000000000000feffffffffffffff02000000010000000002000000" +
      "010700000000000000010100000003000000010800000000000000020000000100000000020000000109000000";
    // Queue<int?> {5, null}: the two slots of an int?[]. Dictionary<int, long?> {1: null, 2: 7}: single values.
    const queue = "0200000001000000050000000000000000000000", dictionary = "02000000010000000002000000010700000000000000";
    const value = Object.assign(new NullableSlots(), {
      queue: [5, null],
      stack: [null, 7n],
      linked: [null, 9],
      sorted: new Set<NullableSlot<number>>([null, -1, 3]),
      readOnly: [Object.assign(new TupleCell(), { a: 1 }), null],
      nested: [[1], null, []],
      boxedSet: Object.assign(new Box<Set<NullableSlot<number>> | null>(), {
        value: new Set<NullableSlot<number>>([4, null]),
      }),
      boxedNested: Object.assign(new Box<(Set<NullableSlot<bigint>> | null)[] | null>(), {
        value: [new Set<NullableSlot<bigint>>([null, -2n])],
      }),
      map: new Map<number, bigint | null>([[1, null], [2, 7n]]),
      boxedMap: Object.assign(new Box<Map<number, bigint | null> | null>(), {
        value: new Map<number, bigint | null>([[3, 8n]]),
      }),
      tuples: [[1, null], [2, 9]],
    });
    // The declared types say which form a member has, and are the values themselves: nothing wraps a slot.
    const slots: NullableSlot<number>[] | null = value.queue;
    const single: Map<number, bigint | null> | null = value.map;
    assert.deepEqual([slots, single?.get(2)], [[5, null], 7n]);
    const data = serialize(NullableSlotsCodec, value);
    assert.equal(hex(data), want);
    assert.equal(hex(data.subarray(1, 21)), queue);
    assert.equal(hex(data.subarray(215, 237)), dictionary);
    assert.deepEqual(deserializeExact(NullableSlotsCodec, data), value);
    // A presence byte other than 0 or 1 is rejected in every sequence: the queue's first slot (at 5), the sorted
    // set's second (at 77), the set's inside a type argument (at 158).
    for (const at of [5, 77, 158]) {
      const bad = data.slice();
      bad[at] = 2;
      assert.throws(() => deserializeExact(NullableSlotsCodec, bad), NinoError, `byte ${at}`);
    }
    // A set of slots rejects an element read twice: the sorted set's third slot made the second (-1).
    const twice = data.slice();
    twice.set(data.subarray(77, 85), 85);
    assert.throws(() => deserializeExact(NullableSlotsCodec, twice), NinoError);
  });

  it("reads and writes NinoNullable<T> as its memory, inside type arguments too", () => {
    // What .NET writes for the NinoNullableArgs of ModelTests.cs, NinoNullableArgsHex: a presence and a value of a T
    // each, wherever it is.
    const want = "0101000000000000000000000000010000020000000100000000000000010000000000000000000000000000000000000000000000010000000000" +
      "000000000000010000000000000000000000010100000007000000010100000000000000000000000000000000000100000000000f000000000000" +
      "0001020000000000000000000000000000000000000000000000000000000100000000000000000000000100000000000000000000000102000000" +
      "0100000000000000ffffffffffffffff00000000000000000000000000000000020000000100000004000000000000000000000002000000010000" +
      "000100000000000000000000000000e03f020000000000000000000000000000000000000009010000000000000000000000000000003322110055" +
      "4477668899aabbccddeeff";
    const cell = () => Object.assign(new TupleCell(), { a: 1 });
    const value = Object.assign(new NinoNullableArgs(), {
      enum: Flags.B,
      enums: [Flags.A, null],
      cell: cell(),
      one: Object.assign(new Box<number | null>(), { value: 7 }),
      wide: Object.assign(new Box<string | null>(), { value: "1.5" }),
      array: Object.assign(new Box<(TupleCell | null)[] | null>(), { value: [null, cell()] }),
      list: Object.assign(new Box<(bigint | null)[] | null>(), { value: [-1n, null] }),
      set: new Set<number | null>([4, null]),
      map: new Map<number, number | null>([[1, 0.5], [2, null]]),
      tuple: [9, "00112233-4455-6677-8899-aabbccddeeff"],
    });
    // The members are declared with the runtime's alias of T | null: the same values, and nothing at run time.
    const one: Box<NinoNullable<number>> | null = value.one;
    const declared: NinoNullable<Flags> = value.enum;
    const plain: bigint | null = declared;
    assert.equal(one?.value, 7);
    assert.equal(plain, value.enum);
    const data = serialize(NinoNullableArgsCodec, value);
    assert.equal(hex(data), want);
    assert.deepEqual(deserializeExact(NinoNullableArgsCodec, data), value);
    // What .NET's untrusted mode rejects: a byte above the presence's first (One, a type argument, at 78), and a
    // byte in a value that is absent (the second of Enums, at 37).
    for (const at of [78 + 3, 37 + 8]) {
      const bad = data.slice();
      bad[at] = 1;
      assert.throws(() => deserializeExact(NinoNullableArgsCodec, bad), /NinoNullable/, `byte ${at}`);
    }
  });

  it("writes rectangular arrays of raw nullable slots as .NET writes them", () => {
    // NullableGridsHex in ModelTests.cs.
    const expected = "0101000000020000000100000005000000000000000000000001000000020000000100000000000000000000000000000000000000010000000000" +
      "0000070000000000000002000000010000000000000000000000000000000000000001000000010000000000000000000000";
    const value = Object.assign(new NullableGrids(), {
      grid: { lengths: [1, 2], data: [5, null] },
      cube: { lengths: [1, 2, 1], data: [null, 7n] },
      cells: { lengths: [2, 1], data: [null, Object.assign(new TupleCell(), { a: 1 })] },
    });
    const data = serialize(NullableGridsCodec, value);
    assert.equal(hex(data), expected);
    assert.deepEqual(deserializeExact(NullableGridsCodec, data), value);
    const grid = value.grid;
    assert.equal(deserializeInto(NullableGridsCodec, data, value).grid, grid);
    for (const at of [9, 53]) {
      const bad = data.slice();
      bad[at] = 0xff;
      assert.throws(() => deserializeExact(NullableGridsCodec, bad), NinoError);
    }
  });

  it("uses typed storage for numeric rectangular arrays without inserting a length before the elements", () => {
    const value = new Grid();
    value.Values = { lengths: [2, 2], data: Int32Array.of(1, -2, 3, -4) };
    assert.equal(hex(serialize(GridCodec, value)), "01020000000200000001000000feffffff03000000fcffffff");
    for (const count of [0, 4, 32]) {
      value.Values = { lengths: [2, count / 2], data: Int32Array.from({ length: count }, (_, i) => i - count) };
      assert.deepEqual(deserializeExact(GridCodec, serialize(GridCodec, value)), value);
    }
    value.Values = { lengths: [65_536, 0], data: new Int32Array(0) };
    assert.deepEqual(deserializeExact(GridCodec, serialize(GridCodec, value)), value);
    value.Values = { lengths: [2, 2], data: Int32Array.of(1) };
    assert.throws(() => serialize(GridCodec, value), NinoError);
  });

  it("reads and writes NinoNullable<T> inside a raw struct, its memory there too", () => {
    // What .NET writes for the values of ModelTests.cs (NinoNullableRawHex, NinoNullableRawWideHex): the presence is
    // a whole T (1 in its first byte), then the value: at 4 for an int, at 16 for a decimal.
    const hold = (a: number, b: number | null) => Object.assign(new HoldsNinoNullable(), { a, b });
    const narrow = Object.assign(new NinoNullableRaw(), { one: hold(1, 7), many: [hold(2, null), hold(3, -1)] });
    const data = serialize(NinoNullableRawCodec, narrow);
    assert.equal(hex(data), "01010000000100000007000000020000000200000000000000000000000300000001000000ffffffff");
    assert.deepEqual(deserializeExact(NinoNullableRawCodec, data), narrow);

    const holdWide = (a: number, b: string | null) => Object.assign(new HoldsWideNinoNullable(), { a, b });
    const wide = Object.assign(new NinoNullableRawWide(), { one: holdWide(1, "1.5"), many: [holdWide(2, null)] });
    const wideData = serialize(NinoNullableRawWideCodec, wide);
    assert.equal(hex(wideData),
      "0101000000000000000100000000000000000000000000000000000100000000000f000000000000000100" +
      "000002000000000000000000000000000000000000000000000000000000000000000000000000000000");
    assert.deepEqual(deserializeExact(NinoNullableRawWideCodec, wideData), wide);
    // A presence byte other than 0 or 1 is rejected there too, and so are a byte above it and a byte in a value
    // that is absent (the value of Many[0].B, at 45 + 8 + 16).
    for (const [at, byte] of [[1 + 8, 2], [1 + 8 + 15, 1], [45 + 8 + 16 + 4, 1]]) {
      const bad = wideData.slice();
      bad[at] = byte;
      assert.throws(() => deserializeExact(NinoNullableRawWideCodec, bad), `byte ${at}`);
    }
  });
});
