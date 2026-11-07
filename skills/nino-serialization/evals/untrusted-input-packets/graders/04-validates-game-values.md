---
type: llm
focus: trace
weight: 1
---

PacketReceiver itself validates the frame count (at most 32, or NinoSettings.MaxCollectionLength set accordingly) and at least one game value range (tick, move axes or weapon slot), and SECURITY.md says Nino does not validate such values
