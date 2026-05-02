---
name: Project overview
description: VRChatWorldSpaceTransformChair is a reusable VRChat UdonSharp seating component, not a full world
type: project
---

**Project type: COMPONENT, not a full world.** This distinguishes it from sister projects `VrcBoardBoundTactics` and `PurplePicturePalace`, which are published worlds.

The deliverable is one (or a small set of) UdonSharp script(s) packaged for drop-in reuse in third-party VRChat worlds. The Unity sub-project under `Unity/VRChatWorldSpaceTransformChair/` exists to develop, test, and showcase the component.

**Why:** The user wants this to be a sharable building-block — likely public on GitHub. A demo world may be published alongside it, but the component itself is the main artifact.

**How to apply:**
- API/prefab boundaries (script names, public field names, prefab paths, serialization layout) are first-class concerns — renaming or restructuring breaks downstream consumers.
- Default to multiplayer-correct designs (UdonSynced where required, master/owner authority patterns) so consumers don't have to retrofit networking.
- Keep the surface area narrow and well-named; avoid leaking internal-helper behaviours into the component's public scripts folder.
- The dev scene `Assets/Local/Scenes/DevScene001.unity` is **scaffolding for testing**, not part of the shipped component.
- The actual purpose of the chair behaviour ("WorldSpaceTransform") is TBD — wait for the user's brief in a follow-up message before committing to a design.
