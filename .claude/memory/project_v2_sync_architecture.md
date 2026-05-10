---
name: V2 multiplayer-sync architecture (verified)
description: Per-player VRCPlayerObject + UdonSynced pose + per-frame stationEnterPlayerLocation drive — proven viable for the chair on 2026-05-10
type: project
originSessionId: d6951207-22e8-4061-8690-43e2cf33b087
---
The v2 multiplayer-sync path is **proven viable**. Verified on 2026-05-10 in Build & Test with 3 desktop clients via the V2SyncSpike test rig (Tests 1 and 2 in `Assets/Local/Scripts/V2SyncSpike.cs`, deleted after integration).

**Architecture:**
- Each player owns a per-player chair, allocated automatically via `VRC.SDK3.Components.VRCPlayerObject` on the template root.
- Owner script writes chair pose into `[UdonSynced] Vector3 _syncedPos; Quaternion _syncedRot;`, throttled `RequestSerialization()` (~10Hz worked smoothly).
- Owner side: writes seat transform immediately, no lerp (precision matters for the seated player driving their own pose).
- Remote side: lerps local seat transform toward synced values (0.2 per-frame lerp factor was visually smooth).
- Remote-rendered seated avatars track `stationEnterPlayerLocation` for full 3D position AND rotation (including roll) — VRChat does NOT clamp seated-avatar rotation the way TeleportTo clamps standing-player rotation to yaw.
- Per-player chairs spawn at the same template position; offset the ROOT (not just children) by `playerId * spacing` so the BoxCollider on root stays aligned with the visible mesh — children follow root automatically. Offset is computed deterministically on every client, no need to sync the root pose.

**Why:** Why this matters. The chair (v1) has a known multiplayer cost — chair transform isn't networked, so remote clients see the seated avatar at the prefab's original world pose. v2 fixes this without falling back to the architecturally-blocked TeleportTo path (yaw clamp) or to a manual master-handout pool.

**How to apply:** Use this pattern when integrating sync into `VRChatWorldSpaceTransformChair.cs`. Specifically:
- Add `[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]` on the class.
- Add UdonSynced pose fields. Owner writes after solver runs; remote viewers lerp.
- Wrap the chair prefab in a `VRCPlayerObject` template; auto-spawn replaces any pool/manager logic.
- Gate `Interact()` on `Networking.IsOwner(gameObject)` — clicking another player's chair is a no-op (they own their own copy).
- Per-player root offset only needed if multiple chairs would visually overlap; for the real chair, the player can grip-move it anywhere, so initial spawn position matters less, but a small offset still avoids "all chairs stacked at template" on first load.

**Source references** (in case the spike is undeleted at memory-read time):
- Reference implementation: Cyan's `WallWalkingExamples/StationController.cs` (own-position written to syncObj relative to origin, remote reads syncObj and writes to stationPos).
- VRC Library wiki "Repositioning Remote Players" — canonical writeup of the desync-station pattern.
