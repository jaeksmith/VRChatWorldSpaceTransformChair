# v1 hand-off — what I did vs. what you need to do in Unity

Status: code skeleton written, prefab not built (needs Unity).

## What's in the repo now

- `Assets/Local/Scripts/VRChatWorldSpaceTransformChair.cs` — the UdonSharpBehaviour. Implements the two-hand similarity-transform solver: scale + rotate + translate the chair so that on each frame in the gripped state, the player's hand pair "lands back on" the anchored hand pair.
- `Assets/Local/Scripts/VRChatWorldSpaceTransformChair.asset` + `.asset.meta` — UdonSharpProgramAsset companion. Empty-but-valid; UdonSharp will populate `serializedUdonProgramAsset`, `scriptVersion`, etc. on first compile.
- `Assets/Local/Prefabs/` — empty folder placeholder for the chair prefab.
- `Docs/spec.md` — the original v1 spec (the .md you wrote in chat-mode), copied into the repo for reference.

The behaviour supports BOTH single-hand and two-hand grips. One hand = drag + rotate (rigid). Both hands = drag + rotate + scale (similarity). Switching between modes (one→two, two→one, left→right) re-snapshots the anchor mid-grip so the transition is jump-free.

## What I did NOT do (needs Unity / your hands)

### 1. Build the chair prefab in `Assets/Local/Prefabs/`

Hierarchy per the spec:

```
VRChatWorldSpaceTransformChair (root)         ← the GameObject we move/rotate
├── ChairMesh                                  ← optional visual
├── EnterButton                                ← Collider (IsTrigger) + the UdonBehaviour for Interact
└── Station                                    ← VRCStation component
    └── EntryExitPoint                         ← Transform; assign as VRCStation.stationEnterPlayerLocation / stationExitPlayerLocation
```

Wiring on the UdonBehaviour:
- `station` → drag the Station child's VRCStation component
- `chairTransform` → drag the prefab root (the thing we move). The Station and entry point must be descendants of this.
- `minScale` / `maxScale` → defaults 0.1 / 10.0 are fine to start
- (no other fields — both single-hand and two-hand modes are always on)

The `Interact()` button: easiest is to put the UdonBehaviour on the same GameObject as the Collider you want clickable, OR on a child of EnterButton, OR have a tiny separate UdonBehaviour on EnterButton that calls `station.UseStation(Networking.LocalPlayer)`. The current script's `Interact()` already does this — just put it on the GameObject with the Collider.

VRCStation Inspector field worth flagging: **`Disable Station Exit` must be UNCHECKED.** If on, Jump won't fire `OnStationExited` and the player has no way out. This has caught us once; double-check it.

### 2. Configure the `AvatarScalingSettings` UdonBehaviour on the scene

Avatar scaling is gated by an `AvatarScalingSettings` UdonBehaviour, NOT by a checkbox on the Scene Descriptor (despite what older docs may suggest). On the scene's descriptor GameObject, find the sibling `AvatarScalingSettings` component and:

- Leave `disableAvatarScaling` UNCHECKED (false = scaling allowed; true = world overrides off).
- Set `minHeight` / `maxHeight` to absolute meters that match what the script will compute for itself: roughly `entryEyeHeight * 0.1` (≈ 0.16 m for a default avatar) and `entryEyeHeight * 10` (≈ 16 m). Otherwise the world's tighter clamp will silently override the script's clamp and the user will hit limits earlier than expected.

If you forget this, the symptoms are silent: `SetAvatarEyeHeightByMeters` returns cleanly, no error, no log, no scale change.

### 3. Smoke test

Drop the prefab into `Assets/Local/Scenes/DevScene001.unity`. Hit Play with VRChat ClientSim — note that ClientSim runs as a **desktop client only**, so the grip-driven gesture path is NOT exercisable there (no controllers, no `InputGrab`, `IsUserInVR()` returns false). ClientSim is fine for testing Interact / station entry / station exit; the actual gesture solver needs a VR session. Expected:
- Click Interact → seated.
- One grip + move hand → chair drags, rotates with the hand (no scale change).
- Both grips + move hands apart → world appears to grow (player shrinks, eye height drops).
- Both grips + rotate hand axis → view rotates around the midpoint between your hands.
- Both grips + translate hands → chair pans, view pans with it.
- Switch one→two or release one mid-grip → no jump, anchor re-baselines from the new grip set.
- Release → state holds.
- Jump → exit, eye height returns to baseline.

## Iteration log

### Round 1 (initial VR test) — math fix for jitter + half-rotation gain

**Symptom:** During grip, the chair shows fast small jitter. Translation gain feels ~1:1 but rotation gain feels ~0.5 (180° hand-axis turn produces ~90° world rotation). Holding hands still during a sustained rotation, jitter appears to grow rather than stay bounded.

**Diagnosis:** The seated player's playspace follows the chair (because the station drives the player's body to the seat, and the playspace re-aligns so the head ends up there). So `GetTrackingData(Left/RightHand).position` shifts every time we move the chair, even if the user's physical controllers haven't moved. The original math computed the per-frame transform `T` that maps `current_hand_world → anchor_hand_world` and applied it to `chair_at_grip` (the snapshot pose). Walking through one cycle:

- Frame N: hands rotated 30° from anchor in world → solver R = -30° → chair set to `chair_at_grip * R` (-30° rotation).
- Frame N+1: playspace caught up to the new chair pose, so hand_world now reads back at the anchor axis. Solver R = identity → chair set to `chair_at_grip * I = chair_at_grip`. **Chair jumps back to grip pose, playspace jumps back, hands read 30° off again.**
- The chair toggles 60Hz between grip pose and grip-pose-times-T. Visible average of "full transform" and "identity" frames is half — exactly the reported rotation gain. Held hands → toggling = jitter.

**Fix:** Apply T to the chair's *current* pose, not `chair_at_grip`. Then T per frame is the small residual to bring hands back to anchor; once playspace catches up, T → identity, system stays put. Stable, full gain.

Also dropped the scale-folded-into-chair-offset thing. Avatar scaling pivots around the head, so it doesn't shift hand world positions; pure rotation+translation goes to the chair, scale goes only to eye height.

Added an EMA on hand positions (`inputSmoothing` field, defaults to 0 = raw input) for residual VR controller noise.

### Things that may still need iteration

1. **Residual VR tracking jitter after the math fix.** Dial `inputSmoothing` up (try 0.3, 0.6, 0.85) until the held-still feel is acceptable. Smoothing trades latency for steadiness; for "rigid 1:1" feel the lowest setting that kills jitter is best.
2. **Solver feels wrong axis-wise.** Full 3D rotation lets the player tilt sideways, which can be disorienting. If so, project the rotation onto vertical axis (yaw) only, or constrain pitch. Easy follow-up.
3. **Eye-height feedback loop.** `OnAvatarEyeHeightChanged` fires for any change including ones we drive. Currently I early-return while gripping. If VRChat's eye-height delivery is delayed-by-a-frame, an event from the previous frame's `Set` could land *after* release and confuse the baseline. If you see weird scale jumps right after releasing both grips, this is the suspect.
4. **Multiplayer chair drift.** v1 doesn't sync `chairTransform`. Other players will see your *seated avatar position computed from the original (un-driven) station transform*, even though your local client sees your avatar where the chair has moved you. Avatar scale IS auto-synced (eye height propagates). So in multiplayer you'll appear to other players as "scaled correctly but stuck where the chair originally was." Networking the chair transform is v2 work.

## Things deferred from the spec to v2+

- Networking (chair transform sync to other players)
- Desktop / mobile control schemes (currently the solver no-ops outside VR; mobile/desktop players can sit but the gesture controls don't run)
- Visual feedback for grip state
- Hand-input smoothing
- Per-hand independent rotation contributions
