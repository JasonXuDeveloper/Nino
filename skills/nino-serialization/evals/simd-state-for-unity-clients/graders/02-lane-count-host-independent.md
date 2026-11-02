---
type: llm
focus: trace
weight: 1
---

The code never assumes a fixed Vector<float>.Count (no hard-coded 4 or 8 floats per vector): lanes per group are a multiple of 16, or the math loops in Vector<float>.Count chunks with a scalar remainder
