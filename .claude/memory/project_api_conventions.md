---
name: VrcWorldTx API naming + callback contract
description: Project-wide public API naming convention for the chair component and the receiver-side callback contract (single target, local-only, post-work, change-only). Binds all future feature work.
type: project
originSessionId: planning-2026-05-12
---

Decided 2026-05-12 in a planning thread (no code yet). These bind future feature work — every new Inspector field / callback / param landed in the chair must follow this convention so the public API stays stable.

## Naming prefixes

- **Methods on the receiver UB**: `VrcWorldTx__<EventName>` — e.g. `VrcWorldTx__Entered`, `VrcWorldTx__Exited`, `VrcWorldTx__TxChanged`.
- **Param fields the chair sets on the receiver before each `SendCustomEvent`**: `VrcWorldTx__Param__<ParamName>` — shared across calls where overlapping (`VrcWorldTx__Param__SourceStation` is set for every call). The full `VrcWorldTx__` prefix is part of the field name on the receiver — no shorthand in actual declarations.
- **Config fields the receiver declares on itself to opt in to optional callbacks**: `VrcWorldTx__Config__<FlagName>` — e.g. `VrcWorldTx__Config__IncludeTxChangedCalls`. Always use the full prefix; chair reads these once on enter / on assign.

## Callback events

- `VrcWorldTx__Entered` — fires unconditionally on station enter, post-work.
- `VrcWorldTx__Exited` — fires unconditionally on station exit, post-work.
- `VrcWorldTx__TxChanged` — single combined event for pose-or-size change. Opt-in via `VrcWorldTx__Config__IncludeTxChangedCalls`. Fires when ANY of pos/rot/eye-height changed past an epsilon since last fire (change-only). Rate-limited via Inspector-visible minimum interval on the chair.

Size and position tend to change together in practice, so a single combined event is cleaner than separate Resized + Moved (one change-detect path, one Config flag, one set of param-field writes per fire).

## Callback contract

- **Single callback target** (UdonBehaviour field on the chair). If consumers need fan-out, they put a propagator component in the slot. (Propagator does not exist yet — build only if demand surfaces.)
- **Local-only firing** — chair fires callbacks on the acting player's machine only. Consumer is responsible for any cross-client propagation.
- **Post-work** — callback fires after the chair has completed the underlying state change.
- **Change-only for `TxChanged`** — fires only if pos/rot/eye-height changed past an epsilon since last fire. (`Entered` / `Exited` fire unconditionally on the corresponding station event.)
- **Configurable rate for `TxChanged`** — Inspector-visible minimum interval (default suggestion: 0.1s; finalize at implementation). Caps callback frequency for high-rate motion.
- **No method-existence precheck** (Udon has no `HasMethod` reflection). Two safety mechanisms: (a) `SendCustomEvent` is a silent no-op when the method is missing; (b) receivers opt into optional events via `VrcWorldTx__Config__*` flags so the chair can skip building param payloads when nobody cares.

## Param-field table (chair sets these before `SendCustomEvent`)

| Call | Param fields set (full receiver-side field names) |
|------|------------------|
| `VrcWorldTx__Entered` | `VrcWorldTx__Param__SourceStation`, `VrcWorldTx__Param__OldPos` (Vector3), `VrcWorldTx__Param__OldRot` (Quaternion), `VrcWorldTx__Param__OldEyeHeight` (float, meters), `VrcWorldTx__Param__NewPos` (Vector3), `VrcWorldTx__Param__NewRot` (Quaternion), `VrcWorldTx__Param__NewEyeHeight` (float, meters) — **Old = New = entry-state** (no delta at entry; zero-delta convention) |
| `VrcWorldTx__Exited` | `VrcWorldTx__Param__SourceStation` only |
| `VrcWorldTx__TxChanged` | `VrcWorldTx__Param__SourceStation`, `VrcWorldTx__Param__OldPos` (Vector3), `VrcWorldTx__Param__OldRot` (Quaternion), `VrcWorldTx__Param__OldEyeHeight` (float, meters), `VrcWorldTx__Param__NewPos` (Vector3), `VrcWorldTx__Param__NewRot` (Quaternion), `VrcWorldTx__Param__NewEyeHeight` (float, meters) |

Notes on the `TxChanged` / `Entered` payloads:
- **Eye height in meters**, matching `GetAvatarEyeHeightAsMeters` and the project's existing vocab (`restoreAvatarHeightOnExit`, `OnAvatarEyeHeightChanged`). Consumer computes their own ratio against any baseline they want.
- **Pos / Rot are the station's `chairTransform` world pose** (the externally observable "seat in the world" surface — same value being `[UdonSynced]` for cross-client parity). For attached players this is functionally the player pose; consumers wanting the player pose specifically should read from `VRCPlayerApi` themselves. (Internal field name `chairTransform` is legacy and will rename to `stationTransform` in the Chair → Station migration; see `project_roadmap.md`.)
- Transform deltas are `Vector3 + Quaternion`, not `Transform` component references (refs compare-equal before/after — same object).
- All "Old" + "New" fields are set every call even when only one axis changed — receivers can diff cheaply.
- **Entered convention: `Old = New`** (both set to the entry pose / entry eye-height). The receiver sees a zero delta on Entered and can diff coherently against the first `TxChanged` that follows (its `Old` will equal the entry `New`). This also overwrites any stale Old/New values a prior session may have left on the same receiver's fields.
- `VrcWorldTx__Param__SourceStation` is typed `UdonBehaviour` on the receiver, holding the station UB itself (`this`). Receiver can call back into it if useful.

Why `Exited` is intentionally minimal (only `SourceStation`):
- The chair's pose at exit ≠ the player's pose after exit — VRChat repositions the player to the station's exit transform when they leave. Reporting `chairTransform.position` here would mislead consumers expecting "where the character is now."
- Eye-height post-restore (when `restoreAvatarHeightOnExit` is true) does match the character, but covering eye-height-only and not pose is asymmetric. Worse than covering nothing.
- Consumers that need post-exit state can read it directly: `Networking.LocalPlayer.GetPosition()` / `GetRotation()` / `GetAvatarEyeHeightAsMeters()` from the `Exited` handler.

## Inbound API (external scripts → station)

Methods external world scripts can call on the station UB. Exact names finalize at implementation; the **local-only vs any-player scope is the firm contract**.

### Local-only mutating methods

Each station is a per-player `VRCPlayerObject` instance — each player owns their own (`project_v2_sync_architecture.md`). Mutations only have effect when called on the local player's own station UB; calling them on a remote player's station instance no-ops architecturally (ownership-enforced, not just a runtime check).

- `Enter()` — local player attaches to their own station, capturing current pose / eye-height per the enter-mode config.
- `Exit()` — local player detaches from their own station. Restore path (avatar height, voice mods, anything else state-mutating) runs in `OnStationExited` and converges with all other exit routes.
- `SetEyeHeight(float meters)` / `ResetEyeHeight()` — set or restore avatar eye height while attached. "Reset" = restore to pre-enter value (matches `restoreAvatarHeightOnExit` semantics).
- `SetPose(Vector3 pos, Quaternion rot)` / `ResetPose()` — set or restore station world pose while attached. Could split into Pos + Rot variants at implementation if useful.

(Names above use the existing project vocab `EyeHeight` / `Pose` for consistency with the callback param fields. Alternative splits OK if discovered useful at implementation.)

### Any-player query methods

Station state is `[UdonSynced]` per the v2 architecture — synced pose (and eye-height, if added to the synced set) is visible cross-client. Query methods read synced state and work on **any** station instance, local or remote.

- `IsAttached()` — bool. (Term "Attached" preferred over "Seated" — generalizes beyond chair semantics, matches the Station naming direction.)
- `GetAttachedPlayer()` — `VRCPlayerApi` or null.
- `GetCurrentEyeHeight()` — float, meters.
- `GetCurrentPose()` — Vector3 + Quaternion (or `GetCurrentPos` / `GetCurrentRot` split).

### Naming note

Public API uses **Station**, not Chair (see `project_roadmap.md` naming migration item). The `VrcWorldTx` prefix is name-agnostic (`Tx` = Transform) and stays.

## Voice + callbacks relationship

Voice logic is **internal to the chair** (reads scale directly, no callback subscription) — the chair does not consume its own emitted events. Callbacks are a **public-facing surface for third-party world scripts**. Voice and callbacks share underlying change-detection plumbing but are otherwise independent features.

## How to apply

When implementing any of voice / permissions / entry-exit / debug-self-check / etc., always:
1. Match the naming prefixes above for any public-facing field, callback, or config.
2. Update the param-field table here if a new callback or new param is added.
3. Mirror this table in the README's API section.

## Cross-references

- `project_roadmap.md` — sequencing puts this scaffold BEFORE voice.
- `vrchat-udonsharp` skill — Udon SendCustomEvent semantics, no runtime reflection.
