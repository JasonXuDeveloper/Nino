---
tags: [skill-creator]
max_turns: 30
timeout_seconds: 900
allowed_tools: [Read, Glob, Grep, Skill, Write, Edit, Bash]
---

I'm writing the authoritative server (.NET 10) for a competitive shooter. Clients send an InputPacket over UDP every tick, serialized with Nino v4: a sequence number, the client's tick, and up to 32 InputFrame values. InputFrame is a plain struct: `int Tick; short MoveX; short MoveY; bool Jump; bool Fire; byte? WeaponSlot;`. Cheaters will send hand-crafted packets. Write Packets.cs (the types), PacketReceiver.cs (a method that takes the received ReadOnlySpan<byte> and returns a validated InputPacket or rejects the packet, without allocating per packet if you can), and SECURITY.md listing which checks Nino does for us and which our code still has to do.
