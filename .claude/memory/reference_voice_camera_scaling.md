---
name: Voice & camera scaling with avatar size — research notes
description: Quick research on whether/how the chair component can adjust voice falloff and camera near/far clip to track avatar scale. Surfaced 2026-05-11; user plans to mod/test the following day.
type: reference
originSessionId: f5b1ac9c-1e69-407c-805c-0d2c0c7a643f
---
User flagged two side items during the 2026-05-11 testing session, after confirming the avatar-IK issue (project_known_issues_v2 #3). Both relate to "things that don't auto-scale with the player when the chair scales them." Quick research notes here so tomorrow's session starts informed.

## 1. Voice falloff distance — YES, script-controllable

VRChat exposes a full set of voice tuning APIs on `VRCPlayerApi`. Per docs / SDK:

- `SetVoiceDistanceNear(float)` — meters at which voice is at full volume (default ~0)
- `SetVoiceDistanceFar(float)` — meters at which voice fades to zero (default ~25)
- `SetVoiceGain(float)` — overall voice gain multiplier (default 15dB)
- `SetVoiceLowpass(bool)` — apply highband filter past the lowpass distance (default true)
- `SetVoiceVolumetricRadius(float)` — radius inside which voice is full volume regardless of direction (default 0)
- Plus `Get*` companions for each.

### The per-instance gotcha

Per VRChat docs (paraphrased): "These methods need to be called on the same instance per player to remain consistent across instances. To do this, properly set them up on every player join through `OnPlayerJoined`."

Meaning: each client maintains its OWN view of voice settings PER player. If client A sets `playerB.SetVoiceDistanceFar(50)`, only client A hears playerB at 50m falloff — other clients still use whatever value they set. For consistency, EVERY client must call the same setter values for each player.

### Implementation pattern for scale-tracking voice

For the chair to scale voice with the player's current avatar size:

1. **Where**: each client iterates all players each frame (or on a throttle), reads each player's `GetAvatarEyeHeightAsMeters()` (callable on any player, not just local), computes a scale ratio against a chosen reference (e.g., 1.4m default), and applies the scaled voice distances on that player.
2. **Why per-client polling and not UdonSynced**: scale is already in the player's standard sync — no need for our own sync layer. Polling once or twice a second is plenty (voice falloff isn't time-critical).
3. **Reference baseline**: a single world-wide "default voice distance at default avatar height" — public field on the chair script, e.g., `voiceDistanceFarAtBaseline = 25f`. Apply `targetFar = voiceDistanceFarAtBaseline * (playerEyeHeight / referenceEyeHeight)`.
4. **On player join / leave**: register/unregister. Reset to default on un-scaling exit.

### Design questions to decide tomorrow

- Should the chair script own voice-distance management, or is it really a world-level concern (and a separate `VoiceDistanceScaler` UB)? Argues for a separate component: voice distance applies to ALL players in the world, not just those using the chair. Cross-cuts.
- If kept in the chair: clean-up on station exit — restore original voice distances? Or leave them?
- Min/max clamp for safety — at ratio=0.01 the voice would fade out at 0.25m, which makes the player effectively muted; at ratio=100 they'd be heard 2.5km away. Reasonable to clamp.

**Likely outcome:** Spike it inside the chair component first for testing, but factor it out into a separate `VRChatPlayerScaleVoice` (or similar) component once it's known to work, since the concern is broader than just the chair.

## 2. Camera near/far clip plane — NO, NOT script-controllable

Per vrchat-udonsharp skill's Camera and Input entry:
- `Camera.main` is **"Method is not exposed to Udon"**.

There is no `VRCPlayerApi.SetCameraNearClipPlane(...)` or equivalent. The player's main viewing camera is not exposed at all for Udon manipulation.

### What you CAN do

- The world author sets near/far clip on the **Scene Descriptor's "Reference Camera"** in the Unity Editor. These values are baked into the world at upload and apply globally — no per-player or per-scale variation. This is what the user is currently doing (manually picking values that span the scale range).
- An Inspector-assigned `Camera` reference on an UdonSharpBehaviour CAN have its `nearClipPlane` / `farClipPlane` mutated at runtime — but that's for purpose-built cameras (render-to-texture, mirrors, security cameras), NOT the player's view camera.

### Why this is a real limitation

The user's current workaround is "set near small and far huge to cover the full scale range." Downsides:
- Z-buffer precision suffers when the near/far ratio is extreme. Z-fighting on distant surfaces becomes more likely.
- At extreme small-scale, anything closer than `near` is invisible — so near has to be tiny for the player to see anything nearby when shrunk.
- At extreme large-scale, anything farther than `far` is invisible — so far has to be huge.
- Both ends of the cost: poorer depth precision throughout normal play.

### Honest answer for tomorrow

There's no clean fix. Workarounds the world author can consider:
- Choose a tighter scale range (`minScale` / `maxScale`) that doesn't require extreme clip settings.
- Live with the precision tradeoff and pick clip values empirically.
- Investigate if VRChat has internal scale-aware clip adjustment ("things might not work well beyond this point" message at ratio 0.1 suggests they're aware) — but it's not script-driven; it's whatever VRChat decides to do.

## Cross-references

- `project_known_issues_v2.md` — issue #3 diagnosis (avatar-IK quality dominates) closed in the same session; this doc captures the new directions surfaced as a result.
- `vrchat-udonsharp` skill, "Camera and Input" entry — confirms `Camera.main` is unavailable.
- VRChat creator docs: https://creators.vrchat.com/worlds/udon/players/player-voice/

## Status

**Not yet implemented.** Research-and-plan stage. Implementation thread sequenced after callback scaffold per `project_roadmap.md`.

## Planned implementation (decided 2026-05-12 in planning thread)

Three modes exposed as an Inspector enum on the chair:

- **`Off`** — chair never touches voice settings.
- **`DistanceBased`** — chair scales `SetVoiceDistanceFar` (and optionally Near / VolumetricRadius) with the seated player's avatar scale ratio, using the per-client polling pattern in section 1 above. Voice logic reads scale directly inside the chair — it does NOT subscribe to the chair's own `VrcWorldTx__TxChanged` callback (callbacks are external-consumer API only; see `project_api_conventions.md`).
- **`SwitchToGlobal`** — chair switches the seated player to a "globally audible" voice config (effectively very large far distance + appropriate gain) on enter, restores on exit.

**Restore on exit is required**, symmetric to the existing `restoreAvatarHeightOnExit` config. The restore path lives in `OnStationExited` (or whatever single chokepoint already handles avatar-height restore) so all exit routes — chair API exit, station Jump-exit, menu Respawn-triggered station exit — converge on one code path.

**Cross-client gotcha still applies**: per section 1, voice settings are per-client-per-player. The chair's mode-switching logic has to run on EVERY client, not just the seated player's, to keep the seated player audible to others correctly. Late-join handling: when a new player joins while someone is seated, the new client must apply the same voice mods to that seated player.

**Min/max clamps**: TBD at implementation. The ratio-clamping discussion in section 1 still stands (ratio=0.01 effectively mutes; ratio=100 broadcasts to 2.5km). Default clamps should be sane production values.

**Public API mirroring**: voice mode + restore-on-exit flag follow the project naming convention in `project_api_conventions.md` (e.g. Inspector fields named per project style, any new callbacks named `VrcWorldTx__*`).
