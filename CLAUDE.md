# VRChatWorldSpaceTransformChair

A reusable **VRChat UdonSharp component** (chair / seating-derived behaviour) intended to be drop-in usable across multiple VRChat worlds. The repo will likely be made public; a small demo world may ship alongside the component.

## Layout

- `Unity/VRChatWorldSpaceTransformChair/` — Unity 2022.3.x VRChat world project. Also accessible via `L:\VRChatWorldSpaceTransformChair__Unity\` (junctioned to dodge Windows MAX_PATH limits).
- `Unity/VRChatWorldSpaceTransformChair/Assets/Local/` — **our** authored content (scripts, prefabs, scenes, generated assets). Everything outside `Local/` is SDK / UdonSharp / 3rd-party imports — leave it alone unless explicitly updating a dependency.
  - `Assets/Local/Scenes/DevScene001.unity` — initial near-empty dev scene.
- `.claude/memory/` — curated project-context notes for Claude Code, junctioned from `C:\Users\progr\.claude\projects\D--J-Work-Dev-VRC-Components-VRChatWorldSpaceTransformChair\memory\`. Tracked in git so context survives across machines and collaborators.

## When working on Udon code

Always consult the `vrchat-udonsharp` skill first — it captures hard Udon constraints (no `new MyClass()`, no `Camera.main`, the `.cs` + `.asset` + `.asset.meta` triple required for new behaviours, etc.). When you discover a new Udon gotcha mid-task, add it to that skill before moving on.

The skill lives at `C:\Users\progr\.claude\skills\vrchat-udonsharp\` (a junction into `D:\J\Work\Dev\Claude\claude-skills\vrchat-udonsharp\` — its own git repo). Multiple Claude threads may be modifying that skill concurrently; see `.claude/memory/reference_skill_concurrency.md` for the coordination convention.

## Component vs. world

This project is primarily a **component**, not a published world. That changes a few defaults relative to the sister projects (`VrcBoardBoundTactics`, `PurplePicturePalace`):

- Public API and prefab boundaries matter — assume third-party worlds will consume the component, so name/path/serialization stability is a first-class concern.
- The dev scene exists to exercise the component; treat scene contents as scaffolding, not shippable content.
- A demo world (if/when shipped) should be a minimal showcase, not the main deliverable.

## Conventions

- Local authoring under `Assets/Local/`. Generated artifacts under `Assets/Local/Generated/` (regenerable; don't hand-edit).
- Branch: `main`.
- Commit style: short imperative subject + body where useful, mirroring the sister projects.
