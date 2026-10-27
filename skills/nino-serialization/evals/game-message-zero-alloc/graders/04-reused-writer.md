---
type: llm
focus: trace
weight: 1
---

MessageCodec keeps one NinoArrayBufferWriter (or other IBufferWriter<byte>) as a field, calls ResetWrittenCount() (or an equivalent reset) before each message, and serializes with NinoSerializer.Serialize(value, writer) rather than the byte[]-returning overload
