// Development-only #263 probe. Both TypedArray.set and memory.copy take the same rotating contents and offsets.
// A wasm-memory upper bound and the extra staging required for ordinary Uint8Array buffers are timed separately.
import { Bench } from "tinybench";
import type { Options, Row } from "./bench.ts";

// (module
//   (memory (export "memory") 1)
//   (func (export "copy") (param i32 i32 i32)
//     local.get 0  local.get 1  local.get 2  memory.copy))
const binary = new Uint8Array([
  0, 97, 115, 109, 1, 0, 0, 0,
  1, 7, 1, 96, 3, 127, 127, 127, 0,
  3, 2, 1, 0,
  5, 3, 1, 0, 1,
  7, 17, 2, 6, 109, 101, 109, 111, 114, 121, 2, 0, 4, 99, 111, 112, 121, 0, 0,
  10, 14, 1, 12, 0, 32, 0, 32, 1, 32, 2, 252, 10, 0, 0, 11,
]);

export async function blockCopyProbe(options: Options): Promise<Row[]> {
  const wasm = new WebAssembly.Instance(new WebAssembly.Module(binary)).exports as {
    memory: WebAssembly.Memory;
    copy: (dst: number, src: number, size: number) => void;
  };
  const rows: Row[] = [];
  const variants = 4;
  for (const size of [2048, 2113, 4096, 65536, 262144, 1048576]) {
    const stride = Math.ceil((size + 4096) / 4096) * 4096;
    const pages = Math.ceil(2 * variants * stride / 65536);
    const growBy = pages - wasm.memory.buffer.byteLength / 65536;
    if (growBy > 0) wasm.memory.grow(growBy);
    const memory = new Uint8Array(wasm.memory.buffer);
    for (const dstOffset of [0, 4]) {
      for (const mode of ["wasm-memory", "ordinary-array"]) {
        const inMemory = mode === "wasm-memory";
        const slots = Array.from({ length: variants }, (_, i) => {
          const from = 2 * i * stride;
          const to = from + stride + 128 * i + dstOffset;
          const input = inMemory ? memory.subarray(from, from + size) : new Uint8Array(size);
          const output = inMemory ? memory.subarray(to, to + size)
            : new Uint8Array(size + 64).subarray(dstOffset, dstOffset + size);
          // Prepare the view outside the timed loop for both paths.
          const wasmOutput = memory.subarray(to, to + size);
          for (let j = 0; j < input.length; j++) input[j] = (j * 73 + i + 1) & 255;
          return { from, to, input, output, wasmOutput };
        });
        type Slot = typeof slots[number];
        const useSet = ({ input, output }: Slot) => output.set(input);
        const useWasm = inMemory
          ? ({ from, to }: Slot) => wasm.copy(to, from, size)
          : ({ from, to, input, output, wasmOutput }: Slot) => {
            memory.set(input, from);
            wasm.copy(to, from, size);
            output.set(wasmOutput);
          };
        for (const [name, copy] of [["set", useSet], ["copy", useWasm]] as const) {
          for (const slot of slots) {
            slot.output.fill(0xa5);
            memory[slot.to - 1] = memory[slot.to + size] = 0xa5;
            copy(slot);
            if (!slot.output.every((v, i) => v === slot.input[i])
              || memory[slot.to - 1] !== 0xa5 || memory[slot.to + size] !== 0xa5)
              throw new Error(`BlockCopy ${mode}/${name}/${size}-offset${dstOffset}`);
          }
          let next = 0;
          const scenario = `BlockCopy/${mode}/${name}/${size}-offset${dstOffset}`;
          const bench = new Bench({ name: scenario, time: options.time ?? 250, warmupTime: options.warmupTime ?? 100 });
          bench.add("copy", () => {
            copy(slots[next]);
            next = (next + 1) % variants;
          });
          await bench.run();
          const result = bench.getTask("copy")!.result;
          if (result.state !== "completed") throw new Error(`${scenario}: ${result.state}`);
          const l = result.latency;
          rows.push({
            scenario, serializer: "Nino", op: "serialize", mean: l.mean, rme: l.rme, p75: l.p75, p99: l.p99,
            hz: 1000 / l.mean, samples: l.samplesCount, bytes: size, variants,
          });
        }
      }
    }
  }
  return rows;
}
