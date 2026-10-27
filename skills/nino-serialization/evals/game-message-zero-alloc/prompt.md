---
tags: [skill-creator]
max_turns: 30
timeout_seconds: 900
allowed_tools: [Read, Glob, Grep, Skill, Write, Edit, Bash]
---

I'm writing a Unity multiplayer game (server is .NET 10, client is Unity 2022 with IL2CPP). Using the Nino serializer v4, write the C# types for a PlayerSnapshot message: player id (int), name (string), position and velocity (a small struct of three floats), and an inventory whose items are either a Weapon (damage) or a Potion (heal amount) - the client must get the right subclass back. Then write a MessageCodec class that serializes snapshots into a buffer it reuses across calls and deserializes on the client into an existing snapshot instance, so steady-state messages don't allocate. Put the code in Messages.cs and MessageCodec.cs.
