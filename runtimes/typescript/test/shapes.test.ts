// nino-gen's code for tests/Nino.Codegen.Tests/Shapes.cs (test/generated/Nino.Codegen.Tests.ts) against bytes .NET
// writes for it.
import assert from "node:assert/strict";
import { describe, it } from "node:test";
import { deserializeExact, serialize } from "../src/nino.ts";
import { Box, Strides, StridesCodec, TupleCell, TupleInside } from "./generated/Nino.Codegen.Tests.ts";

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
});
