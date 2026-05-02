---
name: User profile
description: User context — VRChat world creator, multi-project Udon developer, prefers terse + autonomous workflow
type: user
---

The user is actively building VRChat worlds with UdonSharp. This is one of several Udon projects:
- Sister projects (full worlds): `D:\J\Work\Dev\VRC\GenAiTests\VrcBoardBoundTactics\`, `D:\J\Work\Dev\VRC\Group\PurplePicturePalace\` — useful for cross-referencing setup conventions, gitignore patterns, scene-setup pipelines.
- THIS project: a reusable **component** rather than a full world. Different defaults: API/prefab stability matters; demo scene is scaffolding, not the deliverable.

User skills/preferences:
- Hands-on with the Unity Editor + VRChat SDK pipeline (knows about Library/Temp ignore, VPM, ClientSim, the path-length quirk).
- Maintains the `vrchat-udonsharp` skill at `C:\Users\progr\.claude\skills\vrchat-udonsharp\` (also a checked-in repo) — wants new constraints/gotchas added to the skill as they're discovered.
- Prefers being given working scaffolding to iterate from rather than back-and-forth design questions for a clear-enough brief.
- Time-limited; values momentum and large autonomous steps over consultation.

**How to apply:** When writing UdonSharp code, ALWAYS consult the `vrchat-udonsharp` skill first. When you discover a new Udon constraint or surprising VRChat-SDK behaviour mid-task, propose adding it to that skill before moving on (the user explicitly wants this loop closed). Default to picking sensible defaults and documenting them rather than asking.
