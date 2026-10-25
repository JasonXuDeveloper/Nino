// Loaded before the tests (node --import, see package.json) to run the whole suite on the runtime's other paths:
// NINO_TEST_RUNTIME=browser evaluates the runtime without `Buffer`, as a browser or Deno has it, so text goes through
// the WebAssembly kernels; NINO_WASM=0 turns the kernels off too, so the JavaScript paths they replace run instead.
const global = globalThis as { Buffer?: unknown };
const saved = global.Buffer;
if (process.env.NINO_TEST_RUNTIME === "browser") delete global.Buffer;
try {
  // The runtime looks for Buffer once, when it is evaluated: here, before any test imports it.
  const { useWasm } = await import("../src/nino.ts");
  if (process.env.NINO_WASM === "0") useWasm(false);
} finally {
  global.Buffer = saved;
}
