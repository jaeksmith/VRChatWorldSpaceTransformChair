---
name: Memory junction setup
description: How this project's memory dir is wired between the project repo and claude-code's projects directory
type: reference
---

Source of truth for memory in this project: `D:\J\Work\Dev\VRC\Components\VRChatWorldSpaceTransformChair\.claude\memory\` — this is what gets checked into git.

A **Junction** at `C:\Users\progr\.claude\projects\D--J-Work-Dev-VRC-Components-VRChatWorldSpaceTransformChair\memory` points TO the in-repo dir, so claude-code's standard memory location resolves transparently to the in-repo files. Created with:

```
cmd /c mklink /J ^
  "C:\Users\progr\.claude\projects\D--J-Work-Dev-VRC-Components-VRChatWorldSpaceTransformChair\memory" ^
  "D:\J\Work\Dev\VRC\Components\VRChatWorldSpaceTransformChair\.claude\memory"
```

The `.gitignore` ignores `.claude/*` BUT explicitly un-ignores `.claude/memory/` and its children (see `!.claude/memory/` lines). Same pattern as sister projects `VrcBoardBoundTactics` and `PurplePicturePalace`.

**How to apply:** When writing memory files, write directly to either the in-repo path or the junction'd path — both resolve to the same location. Junction direction matters: the projects-dir entry points TO the repo, not the other way around.
