---
tags: [skill-creator]
max_turns: 30
timeout_seconds: 900
allowed_tools: [Read, Glob, Grep, Skill, Write, Edit, Bash]
---

Our .NET 8 server runs the flocking simulation with System.Numerics SIMD. Right now FlockState is a class with `Vector<float>[] Positions`, `Vector<float> Weights` and `List<Agent> Agents`, where Agent is an unsafe struct `{ int Id; float Speed; bool Active; delegate* unmanaged<void> OnHit; }`. We want to send FlockState snapshots with Nino v4 to our Unity 2022 clients (IL2CPP, Android Arm64 phones and Windows x64, Burst installed) and also save snapshots to disk so replays from older builds still load. Write FlockState.cs with the Nino declarations so it works on every machine, keeping the server's SIMD math possible, and NOTES.md explaining what you changed and why.
