---
type: llm
focus: trace
weight: 1
---

SECURITY.md (or a code comment) says that in the default mode a bool field or Nullable flag inside a raw struct such as InputFrame is not checked, so a byte other than 0 or 1 is undefined behavior or corrupt state, and that NinoSettings.Untrusted rejects it
