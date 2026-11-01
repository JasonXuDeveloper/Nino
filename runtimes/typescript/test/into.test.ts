// deserializeInto over generated code (test/generated/Nino.Conformance.ts): objects read in place, arrays keeping
// their storage, maps and sets cleared, polymorphic members reused only for the same class, failed reads.
import assert from "node:assert/strict";
import { describe, it } from "node:test";
import { deserialize, deserializeInto, NinoError, serialize } from "../src/nino.ts";
import {
  BlueLink, Inner, IntMap, IntMapCodec, Middle, Outer, OuterCodec, Point, PointList, PointListCodec, RedLink,
  RedLinkCodec, TolerantChain, TolerantChainCodec,
} from "./generated/Nino.Conformance.ts";

function point(x: number, y: number): Point {
  return Object.assign(new Point(), { X: x, Y: y });
}

function outer(name: string): Outer {
  const middle = Object.assign(new Middle(), {
    First: point(3, 4),
    Inner: Object.assign(new Inner(), { Value: 7n, Text: "t" }),
  });
  return Object.assign(new Outer(), { Name: name, Point: point(1, 2), Middle: middle });
}

describe("deserializeInto", () => {
  it("reads nested objects in place", () => {
    const target = Object.assign(new Outer(), { Name: "old", Point: point(9, 9), Middle: new Middle() });
    const { Point: p, Middle: m } = target;
    m!.Second = point(5, 5);
    const data = outer("new");
    const back = deserializeInto(OuterCodec, serialize(OuterCodec, data), target);
    assert.equal(back, target);
    assert.deepEqual(target, data);
    assert.equal(target.Point, p);
    assert.equal(target.Middle, m);
    assert.equal(m!.Second, null);
  });

  it("keeps an array and reads its elements into the old ones", () => {
    const list = (n: number) => Object.assign(new PointList(), { Values: Array.from({ length: n }, (_, i) => point(i, 10 - i)) });
    const target = list(8);
    const [values, first] = [target.Values!, target.Values![0]];
    deserializeInto(PointListCodec, serialize(PointListCodec, list(3)), target);
    assert.deepEqual(target, list(3));
    assert.equal(target.Values, values);
    assert.equal(target.Values![0], first);
    deserializeInto(PointListCodec, serialize(PointListCodec, list(10)), target);
    assert.deepEqual(target, list(10));
    assert.equal(target.Values, values);
  });

  it("clears a map before filling it", () => {
    const target = Object.assign(new IntMap(), { Values: new Map([[1, 1], [2, 2], [3, 3]]) });
    const map = target.Values;
    const data = Object.assign(new IntMap(), { Values: new Map([[4, 40], [2, 20]]) });
    deserializeInto(IntMapCodec, serialize(IntMapCodec, data), target);
    assert.deepEqual(target, data);
    assert.equal(target.Values, map);
  });

  it("reuses a polymorphic member only for the same class", () => {
    const red = (value: number, next: RedLink | BlueLink | null) => Object.assign(new RedLink(), { Value: value, Next: next });
    const target = red(0, red(1, null));
    const inner = target.Next;
    const same = red(2, red(3, null));
    deserializeInto(RedLinkCodec, serialize(RedLinkCodec, same), target);
    assert.deepEqual(target, same);
    assert.equal(target.Next, inner);
    const other = red(4, Object.assign(new BlueLink(), { Value: 5, Next: null, Note: "b" }));
    deserializeInto(RedLinkCodec, serialize(RedLinkCodec, other), target);
    assert.deepEqual(target, other);
    assert.ok(target.Next instanceof BlueLink);
  });

  it("leaves a value that can be written when the data is cut short", () => {
    const good = serialize(OuterCodec, outer("name"));
    for (let n = 0; n < good.length; n++) {
      const target = outer("x");
      assert.throws(() => deserializeInto(OuterCodec, good.subarray(0, n), target), NinoError, `prefix ${n}`);
      serialize(OuterCodec, target);
      deserializeInto(OuterCodec, good, target);
      assert.deepEqual(target, outer("name"));
    }
    const list = Object.assign(new PointList(), { Values: [point(1, 1)] });
    const longer = serialize(PointListCodec, Object.assign(new PointList(), { Values: [point(1, 2), point(3, 4), point(5, 6)] }));
    assert.throws(() => deserializeInto(PointListCodec, longer.subarray(0, longer.length - 1), list), NinoError);
    assert.ok(list.Values!.every((p) => p instanceof Point), "no holes");
    serialize(PointListCodec, list);
  });

  it("reads bodies deeper than 128 later into the same objects", () => {
    const chain = (n: number, base: number) => {
      let c: TolerantChain | null = null;
      for (let v = n; v >= 1; v--) c = Object.assign(new TolerantChain(), { Value: v + base, Next: c });
      return c!;
    };
    const data = serialize(TolerantChainCodec, chain(400, 0));
    const target = chain(300, 1000);
    deserializeInto(TolerantChainCodec, data, target);
    assert.deepEqual(serialize(TolerantChainCodec, target), data);
    assert.deepEqual(target, deserialize(TolerantChainCodec, data));
  });
});
