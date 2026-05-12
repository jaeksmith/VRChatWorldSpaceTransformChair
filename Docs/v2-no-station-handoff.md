# v2 hand-off — non-chair (no-station) version

> **VERDICT (post-investigation): NON-VIABLE.** The body of this doc is preserved for context, but the design it proposes does not work. `TeleportTo` silently clamps the Quaternion rotation argument to yaw (Y-axis heading) only — pitch and roll are dropped. Empirically verified by a one-shot test (per-frame `TeleportTo(GetPosition(), Quaternion.Euler(0, 0, 130), …)` while Immobilized: player stayed upright, facing forward, no roll). VRChat keeps standing players upright as a design rule and there's no API to override that. The implementation built per this doc compiled and ran but felt wobbly — root cause was the per-frame solver computing position offsets assuming full 3D rotation would land, while only yaw actually landed; the mismatch visible as `(R − yaw(R)) · roomScaleHandOffset` lever-arm wobble (~10cm/frame for a few degrees of incidental hand pitch/roll on a 1.4m foot-to-hand lever). The implementation was deleted. The `vrchat-udonsharp` skill's TeleportTo entry was updated with this finding so future threads on any project skip the dead end.
>
> **What to use instead:** the chair version (`VRCStation`-driven). The chair gets full 3D rotation because the station tilts the playspace in 3D. Trade-off acknowledged in the body below — multiplayer position sync — remains a real architectural cost of the chair path; addressing it requires manually networking the chair transform. That work is its own milestone, not a v2-replacement task.
>
> Read this if you're the next Claude thread starting work on the no-station variant.
> The chair version is committed and working. Don't touch it.

## What we're building

A second UdonSharpBehaviour that does the same "grab the world" thing the chair does — translate / rotate / scale the player's world view by gripping with VR controllers — but **without using a `VRCStation`**. The chair version stays as-is for comparison; both versions live side-by-side in the dev scene.

## Why a separate behaviour, side-by-side

- The chair version works (commit `559dcc7` was the "feels good" point; later commits added scale-display HUD, player-bounds widening, interact-hover suppression).
- It has one architectural cost worth eliminating: the station's transform is **not** networked. Other players see your seated avatar at the prefab's *original* world pose, not where the chair-with-driver moved you. To fix, we'd either network the chair transform manually (adds complexity) OR drop the station entirely and use `TeleportTo`, which auto-syncs player position via standard player networking.
- Side-by-side keeps the working version as a known-good fallback in case the no-station path runs into runtime issues (per-frame `TeleportTo` is officially supported but we haven't smoke-tested it on hardware ourselves).

## Mechanism

Per VRChat docs (verified in this project's prior research):

| API | Purpose |
|---|---|
| `VRCPlayerApi.Immobilize(true)` | Lock locomotion (joystick / WASD). VR room-scale walking still works, which is fine. |
| `VRCPlayerApi.SetGravityStrength(0f)` | Stop falling. Player floats in place between teleports. |
| `VRCPlayerApi.TeleportTo(pos, rot, default, lerpOnRemote: false)` | Move the player. Per-frame is officially supported with `lerpOnRemote=false` — without that flag, remote clients smooth-lerp, which compounds badly at 60Hz. |
| `VRCPlayerApi.SetManualAvatarScalingAllowed(false)` | Disable radial puppet. Same as chair version. |
| `VRCPlayerApi.SetAvatarEyeHeightByMeters(h)` | Drive scale. Same as chair version. |

**Stations and TeleportTo are mutually exclusive** — VRChat docs: *"Stations can prevent teleportation."* If the player is in any station, `TeleportTo` no-ops. So this design must NOT use a station; the entry/exit surface is something else.

## Architecture decisions to make in v2

1. **Entry/exit trigger.** No station to "sit in". Options:
   - World-space button (Interact-clickable) that toggles grip mode on/off.
   - Two buttons (Enter, Exit).
   - A held-grip-when-not-in-mode gesture (e.g., hold both grips for 1s).
   - Per-player HUD toggle.
   Suggest: a single Interact button that toggles. Mirrors the chair's "click to enter" but doesn't require sitting.

2. **What to call the new behaviour.** Tentative: `VRChatWorldSpaceTransformGripMode` (drops the "Chair" suffix since there's no chair). Lives at `Assets/Local/Scripts/VRChatWorldSpaceTransformGripMode.cs`.

3. **What to wire in the prefab.** Just the trigger button + the HUD panel. No station, no Seat/Exit transforms, no chair root that the user moves through.

4. **HUD panel attachment.** With a chair, we parented the panel to the chair root (which moved with the player). Without a chair, the panel needs to follow the player's head. Either:
   - Per-frame: position the panel via `GetTrackingData(Head).position + offset`. Simple.
   - Parent the panel to a head-bone-following empty Transform via the SDK's `VRC_PlayerObjectAssigner` or similar.
   Suggest the per-frame approach for v2 simplicity.

## Math: keep the same solver

The math the chair version uses is **identical** for the no-station case. The proof from the chair's iteration log: solver computes per-frame `T` mapping `current_hand_world → anchor_hand_world`, applies T to "player's current pose". The chair version's "player pose" is `chairTransform`; the no-station version's "player pose" is the local player itself.

Concretely, where the chair version does:

```csharp
chairTransform.SetPositionAndRotation(newPos, newRot);
```

…the no-station version does:

```csharp
_localPlayer.TeleportTo(newPos, newRot,
    VRC.SDKBase.VRC_SceneDescriptor.SpawnOrientation.Default,
    /* lerpOnRemote */ false);
```

Reading the player's current pose for the per-frame delta:

```csharp
Vector3 currentPlayerPos = _localPlayer.GetPosition();
Quaternion currentPlayerRot = _localPlayer.GetRotation();
```

And `T` is applied to those, with the same midpoint-pivot formula as the chair version.

**Why the same math works:** both station-driven and TeleportTo-driven movements shift the player's playspace_origin in tandem (so the player's head ends up at the new position). The "playspace follows the chair" feedback loop the chair version had to solve manifests the same way for TeleportTo: per-frame hand world readings shift when we teleport. The math fix (`T` applied to current pose, not snapshot pose) handles both cases identically.

## What to copy from the chair script verbatim

- `using` directives.
- The header comment block describing solver, math, and clamp layers.
- All anchor-snapshot fields and `SnapshotAnchors()`.
- `InputGrab` handler.
- `OnAvatarEyeHeightChanged` handler.
- `Update()` HUD readout.
- `SolveOneHand()` and `SolveTwoHand()` — the offset/rotation math is unchanged. Only the "where do I write the result" call changes (player teleport vs `chairTransform.SetPositionAndRotation`).
- `inputSmoothing` EMA filter.
- Player-bounds widening (defensive belt-and-braces) and `SetManualAvatarScalingAllowed`.
- `avatarScalingSettings` reference + clamp resolution.
- `scaleDisplayText` / `scaleDisplayPanelTransform` HUD wiring (with the per-frame head-following positioning since there's no chair to parent under).

## What to delete or change relative to the chair

- `public VRCStation station;` — remove.
- `public Transform chairTransform;` — remove. Replace with reading `_localPlayer.GetPosition()` / `GetRotation()` directly.
- `public Collider interactCollider;` — keep but its purpose changes. It's the **enter-grip-mode** button collider, not a station-trigger. Could disable while in grip mode if desired.
- `Interact()` override — instead of `station.UseStation(p)`, set `_inGripMode = true; _localPlayer.Immobilize(true); _localPlayer.SetGravityStrength(0f); ...`.
- `OnStationEntered` / `OnStationExited` — remove. Replace with `EnterGripMode()` / `ExitGripMode()` methods called from the toggle.
- Exit path — instead of `station.ExitStation`, the user clicks the toggle again or hits a separate exit button. Restore `Immobilize(false)`, `SetGravityStrength(1f)`, `SetManualAvatarScalingAllowed(true)`, baseline eye height, player-bounds-widen restore.

## Edge cases to think about

1. **Initial player pose.** When grip mode enters, snapshot the player's current world pose as the "zero" position. Future TeleportTos compute deltas from this.
2. **Avatar swap mid-session.** Same handling as chair: `OnAvatarEyeHeightChanged` re-baselines `_baselineEyeHeight` when not actively gripping. Mid-grip swaps still go stale; recovery via release-and-regrip.
3. **Player jumps mid-mode.** Locomotion is locked by `Immobilize(true)`, so jump shouldn't fire. But `InputJump` overrides may still trigger; verify in testing.
4. **Player tries to walk in their physical room.** VR room-scale walking moves the player relative to the playspace_origin. In a station, the avatar stays at the seat; with `Immobilize(true)` only, the avatar may follow real-room walking. Verify and decide whether to "anchor" position more aggressively (e.g., re-teleport to the held-position every frame, even when not gripping).

## Files for the next thread to read first

- `Docs/v1-handoff.md` — full iteration log for the chair version. The Round 1–6 sections explain the solver math and the gotchas already discovered.
- `Unity/.../Assets/Local/Scripts/VRChatWorldSpaceTransformChair.cs` — the working chair script. Source for copying the solver math.
- The `vrchat-udonsharp` skill — entries on `TeleportTo`, `Immobilize`, `SetGravityStrength`, station-blocks-teleport rule, playspace-follows-station feedback loop, avatar scaling APIs, RectTransform creation-order gotcha. All directly relevant.
- `Docs/spec.md` — the original spec the user wrote. Still the source of truth for "what the user wants this to feel like."

## What NOT to touch

- The chair script (`VRChatWorldSpaceTransformChair.cs`) — leave alone for side-by-side comparison.
- The chair prefab (`Prefabs/VRChatWorldSpaceTransformChair.orig.prefab` and `.auto.prefab`).
- The dev scene's chair instance.

## Suggested first step in the new thread

1. Read this doc + the v1 handoff + the chair script.
2. Sketch the new behaviour's class structure and Inspector field layout in chat — get user confirmation before writing code.
3. Decide on the trigger surface (button vs gesture).
4. Add a new editor menu item alongside the chair's "Create New Chair Instance" — `Create New No-Station Grip Trigger in Scene`. Builds the new prefab.
5. Implement the new script. Lift solver math from the chair verbatim; swap the writeout call.
6. Drop both prefabs in the dev scene. A/B test in VR.
