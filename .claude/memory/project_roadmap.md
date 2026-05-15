---
name: VrcWorldTx feature roadmap and sequencing
description: Priority order for outstanding feature work as of 2026-05-12 planning thread. Top: chair/sync hygiene. Then callbacks scaffold (load-bearing for everything else). Then voice. Then permissions / entry-exit / debug self-check.
type: project
originSessionId: planning-2026-05-12
---

Decided 2026-05-12 in a planning thread. Subject to change as work uncovers infrastructure friction — edit this list rather than silently expand scope.

## Priority order

1. **In-world chair functionality + multiplayer-sync stability** — highest priority on an ongoing basis. Don't break what works.
2. ~~**Callback scaffolding + naming convention**~~ — **DONE 2026-05-15.** See `project_api_conventions.md` for the contract that landed.
   - All three callbacks wired: `VrcWorldTx__Entered` (carries entry pose+eye-height, Old=New convention), `VrcWorldTx__Exited` (SourceStation only, by design), `VrcWorldTx__TxChanged` (change-only + min-interval gated, full Old+New payload). All fire local-only on the seated player's client.
   - Two bundled sample receivers ship in `Assets/Local/Scripts_Extras/`: `VrcWorldTxDebugLogger` (per-event log toggles + `[tag] ` prefix), `VrcWorldTxRebroadcaster` (fan-out array). Both use `private` API-surface fields per the field-accessibility verification (skill commit `6506c75`).
   - Build & Test smoke-test verified 2026-05-15 — Entered + TxChanged stream correctly, Exited fires, eye-height baseline captured on entry.
3. **Voice support** — three modes: `{ off, distance-based, switch-to-global }`. Implementation notes in `reference_voice_camera_scaling.md`. Restore-on-exit symmetric to `restoreAvatarHeightOnExit`. Voice logic is INTERNAL to the chair (reads scale directly, does not subscribe to its own `VrcWorldTx__TxChanged` event).
4. **Permissions on the use-button** — API-controllable, likely via a check callback on the receiver. Provide a sample component (the debug panel itself may serve as that example).
5. **Entry/exit "supports"** — config-driven, not a full plugin system:
   - Enter modes: `InPlace` (default — capture current player pos/scale), `TeleportToAnchor`, `ExternalProvided` (receiver populates `VrcWorldTx__Param__EnterPos` / `VrcWorldTx__Param__EnterRot` / `VrcWorldTx__Param__EnterEyeHeight` before chair reads). **Naming**: full `VrcWorldTx__` prefix on every receiver-facing field, no exceptions (see `project_api_conventions.md`). Use `EnterEyeHeight` in meters for consistency with the existing `Param__OldEyeHeight` / `Param__NewEyeHeight` vocab — not `EnterScale` ratio.
   - Exit modes: `ReturnToEntry`, `LeaveInPlace`, `TeleportToAnchor`, `ExternalProvided`.
   - Propagator component for fan-out is "build only if demand surfaces."
6. **Debug panel self-check** — on enable, scan local connectivity / setup; alert at top of panel + log. Setup-aid feature.
7. **README + demo scene + licensing file** — ongoing alongside feature work, not a single deliverable. README structure: quick desc → quick setup → quick test notes → config → API → misc/details. Build & Test default-avatar caveat up-front in test notes (see `project_known_issues_v2.md` #3).

## Naming migration: Chair → Station

Decided 2026-05-14. The user's preferred public-presentation term is **Station** (matches VRChat's station concept, generalizes beyond chair semantics — the component is more about a multiplayer-synced station than about chair-ness specifically).

- **Going forward** (effective immediately, including the callback-scaffold thread): new public-facing names use Station. Examples: `VrcWorldTx__Param__SourceStation` (not `SourceChair`), `IsAttached` (not `IsSeated`), the inbound-API section in `project_api_conventions.md` already uses Station vocab.
- **Future rename pass** (separate dedicated thread, no scheduled timing): sweep the codebase to replace `Chair` → `Station` in class names (`VRChatWorldSpaceTransformChair` → `VRChatWorldSpaceTransformStation`), internal field names (`chairTransform` → `stationTransform`), comments, prefab names. The repo itself may also rename — coordinate via the public-API stability policy when it lands.
- **Not changing**: the `VrcWorldTx` project prefix (Tx = Transform, name-agnostic).
- **Memory files**: existing docs that use "chair" language are left as-is until the rename pass — they describe past state truthfully. New docs and new sections use Station.

This is a naming policy item, not a feature. Sequenced "any time after callback scaffold lands"; do NOT block callback scaffold on it (the scaffold already uses Station-friendly naming).

## Review / eval items (observed behavior to look at, not yet diagnosed)

These are noted from the 2026-05-12 planning thread and have not been reproduced or investigated in this thread — they need a Build & Test pass to characterize before deciding feature/bug status.

- **Two-hand grip rotation axis count** — when both grips are pressed, rotation appears to apply on only 1 or 2 axes rather than full 3-axis. Should be full freedom of rotation in the two-grip case. Confirm current behavior, then fix if it really is restricted.
- **Jitter when both grips pressed** — observed jitter in the two-grip state. Unclear whether it's the original/only jitter source or interacts with input smoothing. Review whether input smoothing is enabled in that path, whether the jitter is per-frame numerical noise, and what tweaks (smoothing constants, update order, sync timing) might help.
- **Remote-side callbacks as an opt-in mode** (added 2026-05-15) — current callback contract is local-only by design (fire only on the acting/seated player's client). If/when a use case surfaces for cross-client callbacks (e.g. a world script that wants to react on every client when ANY player enters their chair, not just the local player), evaluate adding an opt-in mode that also fires on remote viewers' clients. Considerations to review at that time: (a) which events make sense to fan out (`TxChanged` per-frame on N clients × M players gets expensive fast — likely needs rate-limiting); (b) whether to read the local-only-vs-remote-too flag from the chair Inspector or from a Config flag on the receiver; (c) how this interacts with the planned `IsAttachedToLocalPlayer()` inbound query (receivers might still want to filter by "is this my player" inside their handler). Not blocking any current work; revisit when a real consumer surfaces.

## Out of scope decisions (so far)

- **Panic-exit button**: not adding. VRChat's Jump-exit and menu Respawn cover the user-recovery side; what matters is that `OnStationExited` rigorously restores any chair-applied state (avatar height, voice mods, anything else) regardless of exit path. All exit routes converge on the same restore code path.
- **Propagator / fan-out component**: deferred until demand. Single callback target for now.
- **Separate `Resized` + `Moved` callbacks**: merged into single `VrcWorldTx__TxChanged` — size and position tend to change together in practice; one change-detect path is simpler.
- **Per-frame `TxChanged` callbacks** without rate limiting: not allowed by contract — change-only + configurable rate is baseline.
- **Voice subscribing to its own callbacks**: rejected. Voice logic is internal to the chair; callbacks are external public API only.

## How to apply

- Pick the next work item from this list when spawning a new thread.
- Update this file when an item closes (move to a "done" section or strikethrough, keep history).
- If a thread discovers infra it needs that isn't listed here, edit this list rather than silently expanding scope.

## Cross-references

- `project_api_conventions.md` — public API naming + callback contract (consumed by every item below #1).
- `reference_voice_camera_scaling.md` — voice implementation research + decided 3-mode plan.
- `project_licensing_direction.md` — license stance for the public release.
- `project_known_issues_v2.md` — existing known issues feeding into README test-notes section.
