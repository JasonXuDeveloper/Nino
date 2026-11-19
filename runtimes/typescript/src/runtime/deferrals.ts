import { NinoError } from "./error.ts";

/**
 * Open class bodies after which the next one is deferred, as `NinoDepth.Inline` in .NET: its slot gets the instance
 * now and its body is written and read later, from a shallow point (see "Deep nesting" in
 * docs/v4-design/binary-format.md), so a long list or deep tree, or data forged to nest deeply, costs a bounded stack.
 */
export const inlineDepth = 128;
/** Open bodies after which a body that cannot be deferred fails, as `NinoDepth.Max` in .NET. */
export const maxDepth = 512;
/** Set in the length of a region followed by a section of the bodies deferred inside it. */
export const sectionFlag = -0x80000000;

/** A body deferred until the stack is shallow again. */
export interface NinoDeferred<S> {
  value: unknown;
  body: (state: S, value: never) => void;
  /** Where it was deferred: keys its entry when it is in a region's section. */
  position: number;
  // Writer only, to find cycles (see NinoWriter.defer).
  anchor: unknown;
  generation: number;
}

/** The deferred bodies of one call: outside any region, and inside the outermost region open. */
export class NinoDeferrals<S> {
  top: NinoDeferred<S>[] = [];
  topHead = 0;
  region: NinoDeferred<S>[] = [];
  regionHead = 0;
  /** Whether `top` is being written or read. */
  topActive = false;
  // Writer only: the deferred object whose body is being written, for cycle detection.
  current: unknown = null;
  currentAnchor: unknown = null;
  currentGeneration = 0;

  add(item: NinoDeferred<S>, regions: number): void {
    (regions === 0 ? this.top : this.region).push(item);
  }
}

export function tooDeep(): NinoError {
  return new NinoError(`data nested deeper than ${maxDepth} bodies.`);
}
