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

### Round 6 — Interact-hover fix; clamp-layer comments corrected; no-station path scoped as separate behaviour

**Selection-box-on-chair fix:** when the seated player shrinks, their interact-ray often hits the chair's BoxCollider trigger from inside, lighting up the Interact-hover highlight at unexpected scales. Added optional `interactCollider` field on the chair component; auto-disabled on station entry, re-enabled on exit. The `Create New Chair Instance` editor menu wires the BoxCollider into this slot automatically; the existing hand-tweaked chair can have it wired manually if desired.

**Clamp layers re-corrected.** The earlier draft of the script's clamp-layer comment overstated the role of the radial-puppet `[0.2, 5]` widening. Per VRChat docs, those bounds are clamps on the *parameter* of `SetAvatarEyeHeightMinimum/MaximumByMeters` and they set the radial-puppet UI's endpoints. They DO NOT gate `SetAvatarEyeHeightByMeters` (the script-driven API). Practical bounds for `SetAvatarEyeHeightByMeters` are roughly `[0.01, 10000]m` absolute / `[0.1, 100]m` "safe" rendering range. The actual clamps a player experiences come from (a) any `AvatarScalingSettings` UB in the scene, and (b) the avatar-mesh rendering plateauing at extreme small scales. Comment block in the script rewritten to reflect this accurately.

**`alwaysEnforceAvatarEyeHeight` does NOT exist** in the current Worlds SDK (verified by string-searching the SDK DLL and all SDK .cs files; zero references). ChatGPT recommends it; that's wrong for our SDK version. Captured in the skill so future threads don't try to use it.

**No-station path scoped as a SEPARATE behaviour.** The next milestone — drop the station, drive player position via `Immobilize(true) + SetGravityStrength(0) + per-frame TeleportTo(... lerpOnRemote: false)` — will be a sibling UdonSharpBehaviour, NOT a modification of the chair version. Both will live in the dev scene side-by-side for A/B comparison. Hand-off with full rationale, mechanism, and what-to-copy-from-the-chair lives in `Docs/v2-no-station-handoff.md`. The chair version stays as the known-good fallback.

### Round 5 — Editor menu Text-detachment fix; HUD readout clarified; clamp-layer doc

**Bug fixed:** the `Create New Chair Instance` menu from Round 4 produced a `DisplayText` GameObject that ended up at scene root (detached from `ScaleDisplayPanel`) with anomalous transform values (e.g. `localPosition.z` around 700). Diagnosis: the menu's prior code used `new GameObject(name)` (which creates with `Transform`), then `SetParent(panel)`, then `AddComponent<RectTransform>()`. The `Transform → RectTransform` swap that AddComponent performs has been observed to break the parent relationship in this specific call order, leaving the child at scene root with transform values inherited from the swap.

**Fix:** create both panel and text GameObjects with `new GameObject(name, typeof(RectTransform))` so `RectTransform` is the original transform component — no swap, no detachment. Also adopted the user's empirically-verified working canvas values:

- Panel `localScale = 0.003` (was 0.001)
- Panel `sizeDelta = (100, 70)` (was 400×250) — together: world footprint ~30×21cm
- `CanvasScaler.dynamicPixelsPerUnit = 50` (default 1) — text crispness
- Text `fontSize = 12`, `alignment = MiddleLeft`, wrap on (better for the 4-line readout)

**HUD readout clarified.** The display now shows:

```
Eye height: 1.500 m
Baseline:   1.700 m  (entry)
Ratio:      0.882x
Clamp: [0.300, 3.000] m
  source: AvatarScalingSettings
```

`source` is either `AvatarScalingSettings` (the wired world UB resolved the bounds) or `minScale*baseline (fallback)` (no UB wired — relative bounds from the script's minScale/maxScale fields).

**Three clamp layers — the part that was confusing.** The script-level comment block explains this in detail; in short:

| # | Layer | Range source | What it does |
|---|---|---|---|
| 1 | Player API bounds | We widen to VRChat's API max `[0.2, 5]` on station entry; restore on exit | Per-player; default values can clamp at ~0.5–2.5m and silently floor `SetAvatarEyeHeightByMeters`. We widen so this doesn't surprise-clamp our calls. |
| 2 | This script's clamp | `_effectiveMinEyeHeight` / `_effectiveMaxEyeHeight` resolved at station entry from either `avatarScalingSettings` UB (if wired) or `baseline * minScale/maxScale` (if not) | What we ASK for in `SolveTwoHand`. Shown as `Clamp: [...]` on the HUD with `source` indicating which path. |
| 3 | World `AvatarScalingSettings` UB | Its `minimumHeight` / `maximumHeight` fields | Listens on `OnAvatarEyeHeightChanged` and re-clamps any change. Final word. |

When you wire the `avatarScalingSettings` field on the chair to your scene's UB, layers 2 and 3 share a source so they don't fight. The new "Create New Chair" tool searches the scene for a UB whose name (or program-asset name) is `AvatarScalingSettings` and auto-wires it; the auto-wire is reported in the post-create dialog.

The `[0.2, 5]` constants in the script for layer 1 are NOT duplicating any other value — they're VRChat's hard API range for `SetAvatarEyeHeightMinimumByMeters` / `SetAvatarEyeHeightMaximumByMeters`. We pick the most permissive values the API allows so layer 1 never floors our calls. The actual clamp the user experiences comes from layers 2 and 3.

### Round 4 — Round 3's editor menu was buggy; replaced with a single "Create New Chair Instance" menu

**What broke:** the `Add or Refresh Scale Display Panel on Selection` menu from Round 3 had a path where, on a freshly-created GameObject, `AddComponent<Canvas>` either didn't auto-add `RectTransform` reliably or the subsequent `GetComponent<RectTransform>()` returned null in the user's environment. The downstream `rt.sizeDelta = ...` then NRE'd silently, leaving the panel as a Canvas-only single-node with no Text child.

**Replacement:** a single `Tools → VRChat World-Space Transform Chair → Create New Chair Instance in Scene` menu. Differences from the broken one:

- **Builds a fresh chair from scratch.** Doesn't operate on selection in any way that could modify a working chair. Even if the user's existing chair is selected, the menu only places the new one 2m to its right.
- **DOES NOT TOUCH any existing chair.** Stated explicitly in the menu's confirmation dialog and as a code comment. No `chairRoot.Find(...)` lookups against the user's chair root, no AddComponent on the user's chair, no field writes to the user's chair.
- **Adds `RectTransform` explicitly BEFORE any UI component**, sidestepping the auto-conversion path that broke the previous tool.
- **Re-applies RectTransform values after Canvas init** in case Canvas's internal init resets them.
- **Wires the chair's UdonSharpBehaviour via `UdonSharpUndo.AddComponent`**, not plain AddComponent — per skill, plain AddComponent looks like it works but the proxy↔UdonBehaviour link isn't initialised, and `CopyProxyToUdon` then throws ArgumentNullException with "Value cannot be null. Parameter name: key" in the formatter.
- **Pre-flight checks** that the UdonSharpProgramAsset exists and `GetSerializedUdonProgramAsset() != null` (compile finished) before touching anything.

What the new menu builds (all under one new GameObject):

- Root `VRChatWorldSpaceTransformChair (auto)` at selection-position + 2m right (or world origin)
- `BoxCollider` (trigger, 1×1.5×1, center y=0.75) — Interact zone
- `VRCStation` configured with `PlayerMobility=Immobilize`, `disableStationExit=false`, `seated=true`, station enter/exit transforms wired
- `Seat` child (entry point) at local `(0, 0, 0)`
- `Exit` child at local `(0, 0, 0.6)` so the player lands 60cm in front on Jump-out
- `VRChatWorldSpaceTransformChair` UdonSharpBehaviour, fields wired:
  - `station` ← VRCStation on root
  - `chairTransform` ← root transform
  - `scaleDisplayText` ← built Text component
  - `scaleDisplayPanelTransform` ← built panel transform
  - `avatarScalingSettings` ← scene UB named `AvatarScalingSettings` if found, else null (script falls back to relative `minScale`/`maxScale`)
- `ScaleDisplayPanel` child with `RectTransform` (added first), `Canvas` (world-space), `CanvasScaler`, `GraphicRaycaster`, `VRC.SDK3.Components.VRCUiShape` — local pos `(0, 1.4, 0.7)`, identity rotation (canvas's readable -Z naturally points back at the seated player), localScale `0.001`, sizeDelta `400×250` (renders ~0.4×0.25m)
- `DisplayText` grandchild: `RectTransform` stretch-to-fill, `Text` with default font, fontSize 28, white, center-aligned

**Existing chair untouched.** If the new chair works, drag it to `Assets/Local/Prefabs` to make it a prefab, or just keep it as a scene instance. If the new chair DOESN'T work, delete it; nothing else changed.

The Round 3 manual recipe in the doc still applies as a reference for what the menu builds.

### Round 3 (post-Round-2 VR test) — found the real scale floor; UI panel automation; world-settings reference

**Key finding:** the avatar stops scaling at ~0.5x not because of our script's clamp or `AvatarScalingSettings.minimumHeight`, but because **the player has its own `Min/MaxEyeHeightByMeters` bounds** (`SetAvatarEyeHeightMinimumByMeters` / `SetAvatarEyeHeightMaximumByMeters`) that clamp the result of `SetAvatarEyeHeightByMeters` regardless of what we ask. Default min is roughly 0.5m. Distinct from the world-level `AvatarScalingSettings`, this is per-player, set via the player API.

**Fix:** on station entry, save the player's existing min/max via `Get*` companions, then widen to VRChat's API range `[0.2, 5]` so our `SetAvatarEyeHeightByMeters` calls aren't silently floored. On station exit, restore the saved values so we leave the player in the state we found them.

**Single source of truth for clamp bounds:** new optional `avatarScalingSettings` field (typed `VRC.Udon.UdonBehaviour`). Wire the scene's `AvatarScalingSettings` UdonBehaviour to this slot and the script reads `minimumHeight` / `maximumHeight` (absolute meters) on entry and uses those as our clamp. If unwired, falls back to `baseline * minScale` / `baseline * maxScale` (relative-bounds path). Single source of truth: change `AvatarScalingSettings` and our script picks it up; or leave the world component unwired and use the relative defaults for prefab-only deployments.

**HUD auto-scale:** new optional `scaleDisplayPanelTransform` field (typed `Transform`). If wired, the script captures the panel's authored `localScale` once at `Start` and per-frame sets `localScale = baseScale * (currentEyeHeight / baselineEyeHeight)`. Effect: the panel stays roughly apparent-constant in the player's view across the full scale range.

**UI setup automation:** new editor menu under `Tools → VRChat World-Space Transform Chair → Add or Refresh Scale Display Panel on Selection`. Select the chair root, click the menu, and it builds:

- A `ScaleDisplayPanel` child GameObject
- World-space `Canvas` + `CanvasScaler` + `GraphicRaycaster` + `VRC.SDK3.Components.VRCUiShape`
- A `DisplayText` child with a sized `UnityEngine.UI.Text` that fills the canvas
- LocalPos `(0, 1.4, 0.7)` from chair root (chest height, 70 cm in front), localScale `0.001` (so 400×250 sizeDelta → 0.4 × 0.25 m), localRotation identity (the canvas's readable -Z naturally points back at the seated player when placed in chair-local +Z)
- Auto-wires both `scaleDisplayText` and `scaleDisplayPanelTransform` on the chair component via the proxy → CopyProxyToUdon path

A companion `Remove Scale Display Panel from Selection` menu unwires + deletes if you want to reset.

**Re-running the menu refreshes defaults (position, size, font, alignment) without re-creating the panel** — useful if you tweaked things and want a clean reset, or if the script was updated and the menu now sets newer defaults.

**Inspector display additions:** the panel now also shows the active `Bounds: [min, max]` so you can immediately see whether the source is `AvatarScalingSettings` (absolute meters from world) or the relative-fallback (`baseline * minScale` / `baseline * maxScale`).

### Round 2 (post-math-fix VR test) — scale display + scale clamp diagnostics + v2 sketch

**What's known about the scaling stack now (from VRChat docs + SDK source):**

- `SetAvatarEyeHeightByMeters` is the **World-Authoritative** scaling API. It has **no documented numeric bounds** — VRChat's docs say nothing about `[0.2, 5]` for this method. Those bounds belong to a *different* set of methods (`SetAvatarEyeHeightMinimumByMeters` / `Maximum*`, the player-controlled radial-puppet bounds).
- `AvatarScalingSettings` is a **sample UdonBehaviour** shipped in `Packages/com.vrchat.worlds/Samples/UdonExampleScene/UdonProgramSources/AvatarScalingSettings.asset`. It's not an SDK-internal hard gate — it's example code. It listens on `OnAvatarEyeHeightChanged` and re-calls `SetAvatarEyeHeightByMeters` to clamp into its `minimumHeight` / `maximumHeight`. Those are regular UdonBehaviour fields, accept any float.
- That means: if you have `AvatarScalingSettings` in your scene with `minimumHeight = 0.17` and your script asks for `0.05`, the component will undo your call on the next event tick. Silent clamp.

**Diagnostic UI: a `scaleDisplayText` field on the script.** Wire a world-space `UI.Text` to it and the script writes current eye height + scale ratio + baseline every frame. So you can SEE what's happening without guessing.

**Wiring the scale display panel in Unity (superseded by Round 3 editor menu — kept for reference):**

> **Use the menu instead.** Round 3 added `Tools → VRChat World-Space Transform Chair → Add or Refresh Scale Display Panel on Selection`. Select the chair root, click the menu, the panel is built and wired in one shot.
>
> If you want to do it manually, the corrected recipe is:
> 1. Add a child `ScaleDisplayPanel` under the chair root.
> 2. On the panel: `Canvas` (RenderMode=WorldSpace), `CanvasScaler`, `GraphicRaycaster`, `VRC.SDK3.Components.VRCUiShape` (concrete, not the abstract `VRC_UiShape` base).
> 3. Position: localPosition `(0, 1.4, 0.7)`, localRotation **identity** (NOT `(0, 180, 0)` as I'd previously suggested — the canvas's readable -Z naturally points back at the seated player when the canvas is placed at chair-local +Z), localScale `(0.001, 0.001, 0.001)`. RectTransform sizeDelta: `(400, 250)`.
> 4. Add a `Text` child (Legacy UI.Text). Anchor stretch-to-fill with zero offsets. Default font, fontSize 28, alignment middle-center, white.
> 5. Drag the Text into the chair's `Scale Display Text` slot, and the panel's Transform into the `Scale Display Panel Transform` slot.

**Diagnostic recipe for the "view scales but avatar doesn't" symptom:**

1. Read the panel BEFORE gripping → confirms baseline reads correctly.
2. Grip + spread hands progressively → watch eye height number drop.
3. Note the value where the number STOPS dropping — that's your effective floor (script `minScale`, world `AvatarScalingSettings.minimumHeight`, or VRChat itself).
4. To find which clamp is firing: lower `minScale` on the script (Inspector) and see if floor moves. If yes, script clamp was active; lower it further until something else takes over. Then lower world's `AvatarScalingSettings.minimumHeight`. If the floor STILL doesn't move, you've found a VRChat-side limit (or the avatar's mesh just stops looking different).
5. For maximum: same process upward.

If you find that the panel value DOES change (eye height keeps dropping) but your VR view continues to "shrink" past where you expect, the explanation is probably perspective: as you shrink, the camera moves closer to your own (already-shrunk) hands, so they fill more of your field of view. They're not actually growing — but they look bigger because the camera is right next to them. A larger floor (`minScale = 0.15` say) keeps you out of that regime.

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

## v2 sketch: no-station + Immobilize + per-frame TeleportTo

The current station-based design has one architectural cost: the station's transform is computed locally on each client (not networked), so other players see your seated avatar at the prefab's *original* world pose, not where the chair-with-driver has moved you. Networking the chair transform would fix it but adds complexity.

There's a cleaner alternative worth exploring in v2: drop the station entirely, use VRChat's free-locomotion override APIs:

```csharp
// On enter "grip mode":
_localPlayer.Immobilize(true);              // joystick locomotion off
_localPlayer.SetGravityStrength(0f);        // float in place

// In LateUpdate, instead of moving chairTransform:
_localPlayer.TeleportTo(targetPos, targetRot,
    VRC.SDKBase.VRC_SceneDescriptor.SpawnOrientation.Default,
    /* lerpOnRemote */ false);              // critical for 60Hz teleport

// On exit:
_localPlayer.SetGravityStrength(1f);
_localPlayer.Immobilize(false);
```

**Why this is interesting:**
- **Player position auto-syncs to remote clients** via standard player-position networking. No chair-transform sync needed for multiplayer parity.
- VRChat docs explicitly support per-frame teleport: *"When you teleport very often or across very short distances, consider setting `lerpOnRemote` to `false`."* Without that flag, remote clients get a smooth-lerp visual that compounds badly at 60Hz; with it, remote sees the same instant position as local.
- No "seated pose" vs "standing pose" question — the player isn't in any station, just held in place.
- Trigger surface can be anything you want (button, hand gesture, HUD toggle), not "click the chair."

**Why it's deferred, not done:**
- **Stations block teleport.** *"Stations can prevent teleportation."* If the player is in any station, `TeleportTo` no-ops. So this is mutually exclusive with the current station-based path — you migrate, you don't add.
- **Per-frame teleport hasn't been smoke-tested by us.** The docs claim it works; community usage exists; but verification on actual hardware (any motion-sickness from blink? networking spam? edge cases around extreme positions?) is its own iteration.
- **Solver math is the same.** What changes is just the "where do I write the player's pose" call. Roughly: replace `chairTransform.SetPositionAndRotation(...)` with `_localPlayer.TeleportTo(...)`. Anchors remain in world space, T computation is identical.

**Suggested spike before refactor:** small standalone test world with one button that toggles `Immobilize(true) + SetGravityStrength(0)` on a player and one that calls `TeleportTo(somewhere, lerpOnRemote=false)` from a per-frame `Update`. Verify smoothness, no-stutter, multiplayer parity. If the spike feels good, port the chair logic. If it doesn't, fall back to chair-transform networking instead.

## Things deferred from the spec to v2+

- The above no-station / TeleportTo migration (largest win: free multiplayer position sync)
- Networking the chair transform (only relevant if the no-station path doesn't pan out)
- Desktop / mobile control schemes (currently the solver no-ops outside VR; mobile/desktop players can sit but the gesture controls don't run)
- Visual feedback for grip state
- Per-hand independent rotation contributions
