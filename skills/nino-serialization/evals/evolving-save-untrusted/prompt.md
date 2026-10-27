---
tags: [skill-creator]
max_turns: 30
timeout_seconds: 900
allowed_tools: [Read, Glob, Grep, Skill, Write, Edit, Bash]
---

Our game stores save files with Nino 4 and players share saves online, so we load files we didn't write. I need the SaveGame class to keep loading old saves after we add new fields in later versions and after we rename the field 'gold' to 'coins'. Write SaveGame.cs with the right Nino declarations, and SaveLoader.cs that reads a save from a byte[] safely for untrusted input (what settings, what exception to catch). Also write NOTES.md listing which future changes to SaveGame are safe and which would break old saves.
