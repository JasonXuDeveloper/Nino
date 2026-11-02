---
type: llm
focus: trace
weight: 1
---

NOTES.md explains that Vector<T> cannot be serialized because its size is chosen by the CPU (16, 32 or 64 bytes, so the x64 server and Arm64 clients disagree), naming NINO029 or the equivalent compile-time error
