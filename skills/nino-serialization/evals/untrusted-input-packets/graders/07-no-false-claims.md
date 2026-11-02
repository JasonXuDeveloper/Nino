---
type: llm
focus: trace
weight: 1
---

Every statement the outputs make about how Nino behaves is true per the Nino v4 source; in particular InputFrame, a plain unmanaged struct holding a byte?, is still copied as one raw block (a Nullable field does not make it member by member), and MaxCollectionLength applies in every mode, not only with Untrusted
