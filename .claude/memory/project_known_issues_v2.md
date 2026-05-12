---
name: Known issues in v2 chair (post-multiplayer integration)
description: Open issues observed after the v2 sync integration landed — scale rendering, body collision-bork, HUD scaling
type: project
originSessionId: d6951207-22e8-4061-8690-43e2cf33b087
---
After v2 multiplayer sync landed (commit `965981e` / fixup `3c0730c`, verified 2026-05-10), the user reported four open issues in Build & Test. Captured here so future threads can pick up without re-asking.

## 1. Scale-display updates slowly — DIAGNOSED 2026-05-11: VRChat-side cross-client scale sync (not script)

**Symptom (original):** "Scale updates only ever so often — maybe 3 to 5 seconds — unsure."

**RESOLUTION (verified 2026-05-11):** The lag was on the REMOTE client side — VRChat's standard player networking sync rate for cross-client avatar scale, which we do not drive and cannot control. `SetAvatarEyeHeightByMeters` only sets the local player's scale; cross-client visibility goes through VRChat's player sync channel (not our UdonSynced channel). The local HUD reads `GetAvatarEyeHeightAsMeters()` each frame and is live.

**Hygiene fix landed:** Added a value-different gate on `SetAvatarEyeHeightByMeters` in `SolveTwoHand` so we don't fire the setter (and the `OnAvatarEyeHeightChanged` event) when the value didn't change. Doesn't affect remote lag — VRChat owns that — but avoids needless local event traffic. The radial-puppet bound setters and `SetManualAvatarScalingAllowed` are local-only client state, no broadcast.

**Status:** Closed-as-VRChat-limitation. Document the cross-client lag in the README if/when the component ships publicly.

## 2. HUD panel doesn't track player scale / distance correctly — FIXED 2026-05-11

**Symptom (original):** The scale-display panel doesn't visually scale and reposition with the player's avatar scale, so when the player grows / shrinks the panel ends up wrong size / wrong distance ("pressed against face at large scale, floating overhead at small scale").

**RESOLUTION (2026-05-11):** `Update()` now scales BOTH `localScale` AND `localPosition` by ratio (was only `localScale` before). Captured `_hudPanelBasePosition` alongside the existing `_hudPanelBaseScale` at `Start`. Treats the panel as a geometrically-similar transform around the chair root — so as the avatar shrinks, the panel moves closer to (and stays at the right eye-relative height of) the smaller player. User confirms: "better, but not quite perfect — livable."

**Related polish landed same session:**
- Panel widened 1.7x (100→170) and slightly taller (90→110) to fit the added diagnostic lines (Target, Offset).
- Text alignment changed from `MiddleLeft` to `MiddleCenter` per user preference.
- Both editor menu and the dev scene's existing panel were updated; editor menu sizing comment also updated.
- New Target line (top of readout) shows the value we last passed to `SetAvatarEyeHeightByMeters` so divergence from `Eye height:` is visible at a glance.
- New Offset line (bottom) shows `_localPlayer.GetPosition() - station.stationEnterPlayerLocation.position` for diagnosing the "body knocked out of seat" symptom.

**Status:** Closed. May revisit if "not quite perfect" turns into a specific complaint later.

## 3. Avatar scales away from player at a crossover scale point — DIAGNOSED 2026-05-11: per-avatar IK quality

**Symptom (original report):** "The case where the avatar appears to scale away from the player — this seems to occur at the same scaling crossover scale point."

**Symptom (refined 2026-05-11):** With Build & Test's default test avatar (the standard robot), the shift triggers at ratio ~0.8 (very mild scale-down). The user discovered the shift threshold also varies with hand height — belly-height hands hit it at ~0.5, lower hands sooner, higher hands later — and described it as "feels like my hand hitting something." Hypothesis at the time: avatar IK strain when real-world hand position is far from the shrunken avatar's natural reach.

**RESOLUTION (verified 2026-05-11):** Uploaded a regular humanoid avatar and tried the same scaling. The avatar handles down to ratio 0.1 cleanly (VRChat's standard "things might not work well beyond this point" warning fires at that point, mild torso drift forward, hands still tracked correctly). **The earlier threshold-at-0.8 was the Build & Test default avatar's IK breaking, not anything in our script or VRChat at the API level.**

**Implication:** Per-avatar IK quality dominates the "safe scaling range" — the documented [0.1m, 100m] range is a rough guide, but the actual usable range for a given avatar can be much narrower (the test robot's was ~[0.8, ~5]) or much wider (regular avatars are at least [0.1, ~5+]). The script's defaults of `minScale=0.1, maxScale=10` are reasonable for production avatars; the chair can't fix a poorly-rigged avatar.

**How to apply:** If a user reports avatar-detachment at moderate scales, FIRST ask what avatar they're testing in — if it's the Build & Test default test avatar, ask them to retry with a real uploaded avatar. The behavior is most likely avatar-IK, not script. (Skill entry added: "Avatar IK quality dominates the 'safe scaling range' — Build & Test's default avatar is unusually bad.")

## 4. Body "borks" at large scales — DIAGNOSED 2026-05-11: avatar feet larger than floor square at extreme upscales

**Symptom (original):** "The body seems to bork as if hitting something and being moved to a position not the same as the player — almost like a collision force out of body. Depends on how much one scaled — get really big, then the body borks position at a larger scale."

**RESOLUTION (verified 2026-05-11, regular avatar):** The bork at moderate upscales was the same avatar-IK issue as #3 (test avatar in Build & Test). At GENUINELY gigantic scales (multiple multiples of baseline), the user still gets knocked out of the chair — but the user observed plausibly because their giant avatar's feet were larger than the floor square the chair sits on. Not a script bug; expected behavior of scaled colliders vs. fixed geometry.

**Why:** Scaled-player collision interaction with world geometry. When the avatar is huge, its collision capsule is huge; intrudes into floor/world geometry; VRChat physics pushes the avatar (and, by extension, the seated position) out of the seat. Setting the chair hierarchy to Walkthrough layer didn't help, per 2026-05-11 testing — which suggests the collision is at the WORLD geometry layer (floor, walls), not the chair's own colliders.

**How to apply:** Document the bound as a known limit. Tightening `maxScale` to keep the avatar's feet inside whatever world floor area the chair sits in is a per-world tuning concern. The chair component can't generically fix this — the world author decides how big the floor is.

**Status:** Closed-as-expected. May re-open if the user finds the bork happens at scales where the floor IS big enough.

## Cross-references

- `project_v2_sync_architecture.md` — multiplayer architecture; explains the sync layer that landed in v2.
- `project_overview.md` — top-level project context (component, not world).
- `Docs/v1-handoff.md`, `Docs/v2-no-station-handoff.md` — historical iteration notes; v2-no-station's VERDICT block documents why TeleportTo path was rejected.
- vrchat-udonsharp skill, entries "Avatar scaling APIs..." and "Stations block TeleportTo..." for adjacent context.
