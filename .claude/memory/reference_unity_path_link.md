---
name: Unity path link
description: Unity project directory is junctioned from L:\VRChatWorldSpaceTransformChair__Unity\ to dodge Windows path-length limits
type: reference
---

`D:\J\Work\Dev\VRC\Components\VRChatWorldSpaceTransformChair\Unity\VRChatWorldSpaceTransformChair\` is junctioned from `L:\VRChatWorldSpaceTransformChair__Unity\` to keep absolute Unity asset paths short — Unity (and parts of the VRChat SDK build pipeline) trip over deeply-nested Windows MAX_PATH limits.

Both paths resolve to the same files; you can read/write either way.

**Why:** Unity tooling on Windows hits 260-char path limits inside `Library/` and during build. Shortening the prefix to `L:\` keeps the deepest paths well under that limit.

**How to apply:** Prefer the project-relative path `Unity/VRChatWorldSpaceTransformChair/...` when referring to files in conversation/docs (it works regardless of which mount the user opened the project from). When invoking Unity-specific CLI tools that need short absolute paths, the `L:` form is a safe fallback.

## Editor-tool gotcha: `Application.dataPath` returns the *opened* path

When the user opens the Unity project via the L: junction, `Application.dataPath` is `L:\VRChatWorldSpaceTransformChair__Unity\Assets`. Walking up with `../../` to find the project repo root yields `L:\` — which is just the junction's mount point, not the actual repo. `Path.GetFullPath` does NOT resolve junctions in .NET / Mono.

If/when an editor tool needs to find the repo root, it should not naïvely walk up from `Application.dataPath`. Either cache the absolute repo path in `EditorPrefs` (with a "Set..." menu item to update it), or pass it in explicitly.
