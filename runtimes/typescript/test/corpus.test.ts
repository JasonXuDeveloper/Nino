// The conformance corpus (conformance/corpus, or NINO_CORPUS) over the code nino-gen generates for its types
// (test/generated/Nino.Conformance.ts; Nino.Codegen.Tests checks it is what nino-gen writes today): every valid case
// read and written back byte for byte, every malicious case and every strict prefix of a valid case rejected with a
// NinoError. Types TypeScript has no form for (Half, BigInteger, grids...) are left out by the generator with a
// warning; the cases of those types are skipped, and the list of them is checked so none is skipped by mistake.
import assert from "node:assert/strict";
import { existsSync, readFileSync } from "node:fs";
import { join } from "node:path";
import { describe, it } from "node:test";
import { deserialize, deserializeExact, NinoError, serialize, type NinoCodec } from "../src/nino.ts";
import * as generated from "./generated/Nino.Conformance.ts";

const corpus = process.env.NINO_CORPUS ?? join(import.meta.dirname, "..", "..", "..", "conformance", "corpus");
const present = existsSync(join(corpus, "index.json"));
if (!present && process.env.NINO_CORPUS_REQUIRED === "1")
  throw new Error(`NINO_CORPUS_REQUIRED=1 but no corpus at ${corpus}`);

/** The types the generator leaves out of TypeScript (its warnings), and the types holding them. */
const notGenerated = new Set([
  "Conformance.Floats", "Conformance.Scalars", "Conformance.Dates", "Conformance.BigValues",
  "Conformance.Collections", "Conformance.TreeHolder", "Conformance.EmptyStruct", "Conformance.Empties",
  "Conformance.Targets.Grid", "Conformance.Targets.BigSlot", "Conformance.Targets.VersionSlot",
  "Conformance.Tracked", "Conformance.TrackedLeaf", "Conformance.TrackedHolder",
]);

const codecs = generated as unknown as Record<string, NinoCodec<unknown> | undefined>;

/** The generated codec of a .NET type: `<Name>Codec`, named after its last part. */
function codecOf(type: string): NinoCodec<unknown> | undefined {
  return codecs[type.split(/[.+]/).at(-1)! + "Codec"];
}

function json(path: string): any {
  return JSON.parse(readFileSync(path, "utf8"));
}

/**
 * `deserializeExact` from a reader of its own: a codec that deserializes while `deserialize` runs gets a new
 * reader instead of the one `deserialize` reuses.
 */
function read(codec: NinoCodec<unknown>, data: Uint8Array): unknown {
  return deserialize({ read: () => deserializeExact(codec, data), write: codec.write }, new Uint8Array(0));
}

/** `deserializeExact`, which reads a value of up to 4 KiB from a copy in a reader it reuses. */
function deserialized(codec: NinoCodec<unknown>, data: Uint8Array): unknown {
  return deserializeExact(codec, data);
}

/** The cases of a group of the corpus, with their descriptions. */
function cases(group: "valid" | "malicious"): { name: string; c: any }[] {
  if (!present) return [];
  const index = json(join(corpus, "index.json"));
  assert.equal(index.corpusVersion, 1);
  return (index[group] as string[]).map((name) => ({ name, c: json(join(corpus, group, name + ".json")) }));
}

function skipReason(c: any): string | null {
  if (c.optional) return `optional: ${c.optional}`;
  if (notGenerated.has(c.type)) {
    assert.equal(codecOf(c.type), undefined, `${c.type} is generated now: take it out of notGenerated`);
    return `${c.type} is not generated for TypeScript`;
  }
  return null;
}

describe("conformance corpus over generated code", { skip: present ? false : "no corpus (set NINO_CORPUS)" }, () => {
  describe("valid", () => {
    for (const { name, c } of cases("valid")) {
      it(name, (t) => {
        const skip = skipReason(c);
        if (skip) return t.skip(skip);
        const codec = codecOf(c.type);
        assert.ok(codec, `no generated codec for ${c.type}`);
        const data = new Uint8Array(readFileSync(join(corpus, "valid", name + ".bin")));
        const expected = c.roundTrip ? Buffer.from(data).toString("hex") : c.reencoded;
        assert.deepEqual(Buffer.from(serialize(codec, read(codec, data))).toString("hex"), expected);
        assert.deepEqual(Buffer.from(serialize(codec, deserialized(codec, data))).toString("hex"), expected);
      });
    }
  });

  describe("malicious", () => {
    for (const { name, c } of cases("malicious")) {
      it(name, (t) => {
        const skip = skipReason(c);
        if (skip) return t.skip(skip);
        const codec = codecOf(c.type);
        assert.ok(codec, `no generated codec for ${c.type}`);
        const data = new Uint8Array(readFileSync(join(corpus, "malicious", name + ".bin")));
        if (c.expected === "error-or-value") {
          for (const f of [read, deserialized]) {
            try {
              f(codec, data);
            } catch (e) {
              assert.ok(e instanceof NinoError, String(e));
            }
          }
          return;
        }
        assert.throws(() => read(codec, data), NinoError);
        assert.throws(() => deserialized(codec, data), NinoError);
      });
    }
  });

  describe("truncations", () => {
    const list = present ? json(join(corpus, "malicious", "truncations.json")) : { cases: [] };
    for (const c of list.cases as { case: string; type: string; optional?: string }[]) {
      it(c.case, (t) => {
        const skip = skipReason(c);
        if (skip) return t.skip(skip);
        const codec = codecOf(c.type)!;
        const data = new Uint8Array(readFileSync(join(corpus, "valid", c.case + ".bin")));
        for (let n = 0; n < data.length; n++) {
          assert.throws(() => read(codec, data.slice(0, n)), NinoError, `a prefix of ${n} bytes was read`);
          // The reader deserialize reuses still holds the whole value past the prefix: it must not read it.
          deserialized(codec, data);
          assert.throws(() => deserialized(codec, data.subarray(0, n)), NinoError, `a prefix of ${n} bytes was deserialized`);
        }
      });
    }
  });
});
