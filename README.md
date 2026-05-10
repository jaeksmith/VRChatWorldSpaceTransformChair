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

## Known issues

- **Avatar scale → mesh / IK rendering** sometimes detaches the visual avatar from the player at certain scale crossover points; appears related to VRChat's own IK / mesh handling at extreme scales, not the script's transform math.
- **Body "bork" at large scales** — at sufficiently large scale-up, the avatar can end up briefly displaced from the seated position, as if hitting an invisible collision and being pushed. Pattern suggests scaled-player collision interaction with world geometry; under investigation.
- **HUD panel** doesn't fully track player scale yet — the scale-display panel is a development debug aid and will likely be removed or made optional in the released component.

## Status & roadmap

The single-user "grab the world" interaction has been stable since `v1` (commit `559dcc7`). Multiplayer sync via `VRCPlayerObject` + UdonSynced landed in `v2` (commit `965981e`, 2026-05-10), verified across 3 desktop clients. Remaining work is focused on smoothing the known issues above and tightening the component's public surface for drop-in use in third-party worlds.

This repo will likely be made fully public once the known issues are resolved and the API is stable; for now, treat it as a working-but-rough reference implementation.
