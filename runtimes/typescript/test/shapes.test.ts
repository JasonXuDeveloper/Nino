// nino-gen's code for tests/Nino.Codegen.Tests/Shapes.cs (test/generated/Nino.Codegen.Tests.ts) against bytes .NET
// writes for it.
import assert from "node:assert/strict";
import { describe, it } from "node:test";
import { deserializeExact, serialize } from "../src/nino.ts";
import {
  Box, Flags, HoldsNinoNullable, HoldsWideNinoNullable, NinoNullableArgs, NinoNullableArgsCodec, NinoNullableRaw,
  NinoNullableRawCodec, NinoNullableRawWide, NinoNullableRawWideCodec, NullableArgs, NullableArgsCodec, Strides,
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
    const boxed = Object.assign(new Box<(TupleCell | null)[] | null>(), { value: [null, cell] });
    const strides = Object.assign(new Strides(), { cells: [cell, null], boxed });
    const data = serialize(StridesCodec, strides);
    assert.equal(hex(data), want);
    assert.deepEqual(deserializeExact(StridesCodec, data), strides);
  });

  it("reads and writes NinoNullable<T> as T?, inside type arguments too", () => {
    // What .NET writes for the NinoNullableArgs (and the NullableArgs) of ModelTests.cs, NinoNullableArgsHex.
    const want = "0101000000000001000002000000010000000000000001000000000000000000000000000000000000000000000001010700000001020000000000" +
      "00000000000000000000000000000100000001000000000000000000000001020000000100000000000000ffffffffffffffff0000000000000000" +
      "000000000000000002000000010400000000020000000100000001000000000000e03f0200000000";
    const fill = <T extends NinoNullableArgs | NullableArgs>(v: T): T => Object.assign(v, {
      enum: Flags.B,
      enums: [Flags.A, null],
      one: Object.assign(new Box<number | null>(), { value: 7 }),
      array: Object.assign(new Box<(TupleCell | null)[] | null>(), { value: [null, Object.assign(new TupleCell(), { a: 1 })] }),
      list: Object.assign(new Box<(bigint | null)[] | null>(), { value: [-1n, null] }),
      set: new Set<number | null>([4, null]),
      map: new Map<number, number | null>([[1, 0.5], [2, null]]),
    });
    const value = fill(new NinoNullableArgs());
    const data = serialize(NinoNullableArgsCodec, value);
    assert.equal(hex(data), want);
    assert.deepEqual(deserializeExact(NinoNullableArgsCodec, data), value);
    // The same type declared with T?: the same bytes, and either reads the other's.
    const twin = fill(new NullableArgs());
    assert.equal(hex(serialize(NullableArgsCodec, twin)), want);
    assert.deepEqual(deserializeExact(NullableArgsCodec, data), twin);
  });

  it("reads and writes NinoNullable<T> inside a raw struct where .NET holds it", () => {
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
    // A presence byte other than 0 or 1 is rejected there too.
    const bad = wideData.slice();
    bad[1 + 8] = 2;
    assert.throws(() => deserializeExact(NinoNullableRawWideCodec, bad));
  });
});
