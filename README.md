# VRChatWorldSpaceTransformChair

A drop-in **VRChat UdonSharp world component** that lets a seated VR player "grab the world" — translate, rotate, and scale their view by gripping with one or both controllers. Multiplayer-aware: each player gets their own auto-allocated chair, and movement syncs across clients so remote viewers see seated avatars at the right pose.

> **Status: in development.** The core concept is working — multi-client testing confirms per-player allocation, position + rotation sync, and full 3D tilt all behave correctly. Known issues remain (see [Known issues](#known-issues)); the component is not yet recommended for production use.

## What it is

A `UdonSharpBehaviour` (`VRChatWorldSpaceTransformChair`) paired with a `VRCStation`. Sit in the chair, then:

- **One-hand grip** — rigid rotate + translate of the world around the gripped hand.
- **Two-hand grip** — rigid rotate + translate on the inter-hand axis, plus avatar-eye-height scaling driven by the inter-hand distance ratio. Makes you bigger or smaller.

Inspired by the "grab the world" interaction in *Half-Life: Alyx*, *Demeo*, and similar VR titles.

## Multiplayer

The chair root carries `VRC.SDK3.Components.VRCPlayerObject`, so VRChat automatically instantiates one chair per joining player with that player as owner. Each player can only sit in their own chair (`Interact` is gated on `Networking.IsOwner`).

Sync mechanics:

- Chair pose (`Vector3` + `Quaternion`) is `[UdonSynced]` with manual `RequestSerialization` from the owner — throttled to a configurable max rate during motion (~10 Hz default), and dropped to zero serializations during idle (chair pose hasn't materially changed). Bandwidth scales with actual use.
- Remote viewers lerp their local copy of the chair toward the synced pose each frame; the station is in `Immobilize` mode, so the seated remote avatar follows automatically via `stationEnterPlayerLocation`. Full 3D rotation is preserved (no yaw-clamp like `TeleportTo`).
- Avatar scale propagates via VRChat's standard player-eye-height networking — no extra sync work needed.
- `OnPlayerJoined` pushes a fresh serialize from the owner so late joiners see current state immediately.

## Layout

```
Unity/VRChatWorldSpaceTransformChair/         Unity 2022.3.x VRChat world project
  Assets/Local/                               Authored content (everything outside Local/ is vendored SDK / UdonSharp)
    Scripts/                                  VRChatWorldSpaceTransformChair.cs (+ .asset / .meta)
    Editor/                                   Editor menu — "Create New Chair Instance in Scene"
    Scenes/DevScene001.unity                  Dev scaffolding scene; not a shipping deliverable
Docs/                                         Hand-off notes and spec
```

## Quick start

1. Open the Unity project (`Unity/VRChatWorldSpaceTransformChair/`). The repo is set up against VRChat Worlds SDK 3.10.x.
2. **Tools → VRChat World-Space Transform Chair → Create New Chair Instance in Scene** drops a fully-wired chair template into the scene (`VRCPlayerObject`, `VRCStation`, trigger collider, seat / exit transforms, the U# behaviour, scale HUD).
3. Drag into `Assets/Local/Prefabs/` if you want a reusable prefab.
4. Build & Test with 2+ clients to verify the multiplayer behaviour.

## API — callbacks for receiver scripts

The chair can drive a single optional `UdonBehaviour` callback target with three events. Wire your receiver UB into the chair's **Callbacks → Callback Target** Inspector slot. The chair calls methods on the receiver by name; missing methods are silent no-ops (VRChat's `SendCustomEvent` semantics).

**Local-only, post-work, on the acting (seated) player's machine.** Cross-client propagation is the receiver's responsibility if it needs it. Naming convention details: see `.claude/memory/project_api_conventions.md`.

### Events

| Method on receiver | When it fires | Carries pose / eye-height? |
|---|---|---|
| `VrcWorldTx__Entered()` | Once, immediately after `OnStationEntered` finishes its setup work. Unconditional. | **Yes** — Param fields set, Old = New = entry-state (zero delta at entry). |
| `VrcWorldTx__Exited()` | Once, immediately after `OnStationExited` finishes its restore work. Unconditional. | **No** — only `Param__SourceStation`. Chair pose ≠ post-exit player pose (VRChat moves the player out), so reporting it would mislead. Consumers needing post-exit state read it directly from `Networking.LocalPlayer`. |
| `VrcWorldTx__TxChanged()` | Each time pose or eye-height changes past the configured epsilons since the last fire — change-only, min-interval-throttled. **Opt-in.** | **Yes** — full Old + New delta. |

### Opt-in for `TxChanged`

Declare a public bool on the receiver:

```csharp
public bool VrcWorldTx__Config__IncludeTxChangedCalls = true;
```

The chair reads this once on `OnStationEntered` (cached for the seated duration; toggling mid-session applies on the next entry). If absent / false, `TxChanged` is skipped entirely — no per-frame param-field writes, no `SendCustomEvent`.

### Param fields the chair sets on the receiver before each `SendCustomEvent`

Declare these on the receiver in the same UdonBehaviour. The chair writes via `SetProgramVariable`; only declared-and-named fields land, the rest no-op.

| Field | Type | Set for | Meaning |
|---|---|---|---|
| `VrcWorldTx__Param__SourceStation` | `UdonBehaviour` | every call | The chair UB itself. Receiver can call back into it if useful. |
| `VrcWorldTx__Param__OldPos` | `Vector3` | `Entered`, `TxChanged` | `chairTransform.position` at the previous fire (or entry-pose on `Entered`). |
| `VrcWorldTx__Param__OldRot` | `Quaternion` | `Entered`, `TxChanged` | `chairTransform.rotation` at the previous fire (or entry-pose on `Entered`). |
| `VrcWorldTx__Param__OldEyeHeight` | `float` (meters) | `Entered`, `TxChanged` | Player eye height at the previous fire (or entry-baseline on `Entered`). |
| `VrcWorldTx__Param__NewPos` | `Vector3` | `Entered`, `TxChanged` | Current `chairTransform.position`. |
| `VrcWorldTx__Param__NewRot` | `Quaternion` | `Entered`, `TxChanged` | Current `chairTransform.rotation`. |
| `VrcWorldTx__Param__NewEyeHeight` | `float` (meters) | `Entered`, `TxChanged` | Current `Networking.LocalPlayer.GetAvatarEyeHeightAsMeters()`. |

All "Old" + "New" fields are set every `TxChanged` fire even when only one axis changed — receivers can diff cheaply. On `Entered`, **Old = New = entry-state** (zero delta at entry by convention; receivers can diff coherently against the first `TxChanged` that follows).

### Minimal receiver example

The chair writes the param fields via `SetProgramVariable`, which reaches `private` fields on a U# behaviour just fine — keep the API surface fields `private` to avoid Inspector clutter unless you specifically want them visible:

```csharp
using UdonSharp;
using UnityEngine;
using VRC.Udon;

public class MyChairListener : UdonSharpBehaviour
{
    // Written by the chair via SetProgramVariable. Private keeps them out of the
    // Inspector and out of your class's external API.
    private UdonBehaviour VrcWorldTx__Param__SourceStation;
    private Vector3 VrcWorldTx__Param__OldPos;
    private Vector3 VrcWorldTx__Param__NewPos;
    private Quaternion VrcWorldTx__Param__OldRot;
    private Quaternion VrcWorldTx__Param__NewRot;
    private float VrcWorldTx__Param__OldEyeHeight;
    private float VrcWorldTx__Param__NewEyeHeight;

    // Read by the chair on station entry via GetProgramVariable. Private is fine
    // here too. Default-true if you always want TxChanged; flip to false to opt out.
    private bool VrcWorldTx__Config__IncludeTxChangedCalls = true;

    public void VrcWorldTx__Entered()  { Debug.Log("seated"); }
    public void VrcWorldTx__Exited()   { Debug.Log("exited"); }

    public void VrcWorldTx__TxChanged()
    {
        float ratio = VrcWorldTx__Param__NewEyeHeight / Mathf.Max(0.001f, VrcWorldTx__Param__OldEyeHeight);
        Debug.Log("chair moved; size ratio = " + ratio.ToString("F3"));
    }
}
```

If you need a particular field to be Inspector-editable (e.g. a user-facing Config flag), declare it plain `public` instead — `SetProgramVariable` / `GetProgramVariable` reach those too. `[NonSerialized] public` works as well but is slightly fragile: UdonSharp's compile-time and reflection paths disagree about whether `private` / `[NonSerialized]` fields should be exported, and the empirically-working behaviour relies on the reflection path. Plain `public` is the only access modifier both paths agree on — safe choice if you want maximum future-proofing.

### Tuning the change-detect epsilons

On the chair Inspector under **Callbacks (public VrcWorldTx__ API)**:

- `txChangedMinInterval` — minimum seconds between consecutive `TxChanged` fires (default 0.1).
- `txChangedPosEpsilon` — meters of pos delta required to fire (default 0.001).
- `txChangedRotEpsilon` — degrees of rot delta required to fire (default 0.1).
- `txChangedEyeHeightEpsilon` — meters of eye-height delta required to fire (default 0.001).

### Bundled receiver helpers

Two ready-to-use UdonSharpBehaviours ship in [`Assets/Local/Scripts_Extras/`](Unity/VRChatWorldSpaceTransformChair/Assets/Local/Scripts_Extras/) — drop either into your scene and wire it into the chair's `callbackTarget` slot:

- **`VrcWorldTxDebugLogger`** — logs each callback to the console with a configurable `[tag] ` prefix and per-event toggles. The TxChanged opt-in is exposed as a UI-friendly `Subscribe To TxChanged` checkbox (copied to the underlying API field on `Start`). Use as a smoke-test target while building.
- **`VrcWorldTxRebroadcaster`** — takes a `targets[]` array of downstream `UdonBehaviour`s and forwards every event to each one in order. Lets you fan one chair out to multiple receivers without building your own propagator. `SourceStation` passes through unchanged so downstream receivers see the original chair as source.

## Known issues

- **Avatar scale → mesh / IK rendering** sometimes detaches the visual avatar from the player at certain scale crossover points; appears related to VRChat's own IK / mesh handling at extreme scales, not the script's transform math.
- **Body "bork" at large scales** — at sufficiently large scale-up, the avatar can end up briefly displaced from the seated position, as if hitting an invisible collision and being pushed. Pattern suggests scaled-player collision interaction with world geometry; under investigation.
- **HUD panel** doesn't fully track player scale yet — the scale-display panel is a development debug aid and will likely be removed or made optional in the released component.

## Status & roadmap

The single-user "grab the world" interaction has been stable since `v1` (commit `559dcc7`). Multiplayer sync via `VRCPlayerObject` + UdonSynced landed in `v2` (commit `965981e`, 2026-05-10), verified across 3 desktop clients. Remaining work is focused on smoothing the known issues above and tightening the component's public surface for drop-in use in third-party worlds.

This repo will likely be made fully public once the known issues are resolved and the API is stable; for now, treat it as a working-but-rough reference implementation.
