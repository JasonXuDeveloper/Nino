// nino-gen's code for tests/Nino.Codegen.Tests/Shapes.cs (test/generated/Nino.Codegen.Tests.ts) against bytes .NET
// writes for it.
import assert from "node:assert/strict";
import { describe, it } from "node:test";
import { deserializeExact, serialize } from "../src/nino.ts";
import {
  Box, Flags, HoldsNinoNullable, HoldsWideNinoNullable, NinoNullableArgs, NinoNullableArgsCodec, NinoNullableRaw,
  NinoNullableRawCodec, NinoNullableRawWide, NinoNullableRawWideCodec, Strides, StridesCodec, TupleCell, TupleInside,
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
    const boxed = Object.assign(new Box<(TupleCell | null)[] | null>(), { value: [null, cell] });
    const strides = Object.assign(new Strides(), { cells: [cell, null], boxed });
    const data = serialize(StridesCodec, strides);
    assert.equal(hex(data), want);
    assert.deepEqual(deserializeExact(StridesCodec, data), strides);
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
