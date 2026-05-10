---
name: Known issues in v2 chair (post-multiplayer integration)
description: Open issues observed after the v2 sync integration landed — scale rendering, body collision-bork, HUD scaling
type: project
originSessionId: d6951207-22e8-4061-8690-43e2cf33b087
---
After v2 multiplayer sync landed (commit `965981e` / fixup `3c0730c`, verified 2026-05-10), the user reported four open issues in Build & Test. Captured here so future threads can pick up without re-asking.

## 1. Scale-display updates slowly (the "first issue")

**Symptom:** "Scale updates only ever so often — maybe 3 to 5 seconds — unsure." Not yet diagnosed which surface is lagging.

**Open question for diagnosis:** is the lag in
- (a) the HUD readout text (the "Eye height / Baseline / Ratio / Clamp" lines on the chair's scale-display panel), or
- (b) the avatar's visible scale lagging behind the grip-spread motion, or
- (c) the *remote* avatar's scale lagging on a watching client (different code path — VRChat's player-eye-height sync rate, not our script)?

Investigation should ask the user which surface they're seeing before chasing root causes.

**Why:** Both surfaces exist and have different update paths. Diagnosing without confirming surface wastes a round.

**How to apply:** Resume this issue first. Ask the user which surface is lagging if it's not clear from the convo. Then likely causes per surface:
- HUD: check `Update()` is actually running each frame for the local owner (not gated out by `_isSeatedLocal` flipping); check `Text.text` writes aren't being missed.
- Local avatar scale: check `SetAvatarEyeHeightByMeters` is being called every frame in `SolveTwoHand` and not throttled by `OnAvatarEyeHeightChanged` re-baselining.
- Remote scale: VRChat's standard player-sync rate is what it is — limited control. May just be the rate. Document and move on if so.

## 2. HUD panel doesn't track player scale / distance correctly

**Symptom:** The scale-display panel doesn't visually scale and reposition with the player's avatar scale, so when the player grows / shrinks the panel ends up wrong size / wrong distance.

**Context:** The current `Update()` in the chair sets `scaleDisplayPanelTransform.localScale = _hudPanelBaseScale * ratio` to keep it apparent-constant. That handles scale but may not handle distance — if the panel is parented to the chair root and the player shrinks, the panel stays at the same world position (correct in world space) but appears tiny/far in the player's view because the player's eyes are now closer to the floor.

**Why:** HUD is a development-debug aid. The user has flagged it will likely be removed or made optional in the released component; full polish is not required, just enough that it doesn't get in the way during dev.

**How to apply:** Low priority unless it's blocking debugging. If addressed, consider:
- Parenting the panel to a head-following empty transform instead of the chair root.
- Or scaling the panel by `ratio^2` or similar to compensate for both the eye-height shift and the apparent-distance shift.
- Or accepting that the debug HUD looks rough at extreme scales and moving on.

## 3. Avatar scales away from player at a crossover scale point

**Symptom:** "The case where the avatar appears to scale away from the player — this seems to occur at the same scaling crossover scale point."

**Context:** Discussed in prior sessions but never fully diagnosed. The "crossover scale point" suggests a specific eye-height threshold where VRChat's avatar IK / mesh rendering breaks coherence — the visual avatar detaches from the player's actual position/scale. The skill's "Avatar scaling APIs split into Player-Controlled vs World-Authoritative" entry notes that outside the ~`[0.1m, 100m]` "safe" rendering range, "avatar mesh visually plateaus, IK breaks." Likely the same phenomenon, but the specific threshold the user is hitting hasn't been measured.

**Why:** This is a VRChat / avatar IK behavior, not a script bug. Workarounds (clamp to safe range, swap mesh-only avatars at extreme scales, etc.) are world-level concerns.

**How to apply:** If asked to investigate: measure the crossover eye-height empirically in Build & Test, compare to the avatar's `AvatarScalingSettings` clamp and to the skill's documented "safe range," and decide whether to tighten the chair's `minScale` / `maxScale` defaults to keep users inside the coherent zone. Don't expect to fix VRChat's IK.

## 4. Body "borks" at large scales — appears displaced from seat as if colliding

**Symptom:** "The body seems to bork as if hitting something and being moved to a position not the same as the player — almost like a collision force out of body. Depends on how much one scaled — get really big, then the body borks position at a larger scale."

**Context:** Scaled-player collision interaction with world geometry. When the avatar is large, its collision capsule is correspondingly large; if the chair has translated the player into geometry that the small-scale avatar would have cleared, the physics layer pushes the avatar out, displacing it from the station seat. This is the inverse of issue 3 in some sense (3 is rendering; 4 is physics).

**Why:** Scaled colliders against fixed world geometry. May be addressable by tuning `Immobilize` interaction with collision, or by checking if there's an API to disable player-collider influence while seated.

**How to apply:** Likely related to the `VRCStation` player-collider behavior. Worth checking SDK docs for whether seated players have collision overridden, and whether scaling the eye height correspondingly scales the collider. Empirical test: scale up gradually in an open area vs. near a wall and see if the bork only triggers near geometry. If geometry-triggered, the workaround is "don't translate seated players into walls" — a design constraint, not a script fix.

## Cross-references

- `project_v2_sync_architecture.md` — multiplayer architecture; explains the sync layer that landed in v2.
- `project_overview.md` — top-level project context (component, not world).
- `Docs/v1-handoff.md`, `Docs/v2-no-station-handoff.md` — historical iteration notes; v2-no-station's VERDICT block documents why TeleportTo path was rejected.
- vrchat-udonsharp skill, entries "Avatar scaling APIs..." and "Stations block TeleportTo..." for adjacent context.
