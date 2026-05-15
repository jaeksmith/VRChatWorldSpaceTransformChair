---
name: Bundled sample receivers in Scripts_Extras
description: The project ships small ready-to-use UdonSharp samples under Assets/Local/Scripts_Extras/ — separate folder from the main chair script. Convention + initial inventory + when to add more.
type: project
originSessionId: callback-scaffold-2026-05-15
---

The chair ships **bundled sample receivers** under `Unity/VRChatWorldSpaceTransformChair/Assets/Local/Scripts_Extras/`, separate from the main chair behaviour in `Assets/Local/Scripts/`. This is intentional: world authors who consume the component can leave the samples in place (they're tiny + low-risk) or delete the whole `Scripts_Extras/` folder if they don't want them. Either way, the main behaviour in `Scripts/` is what they actually wire up.

## Initial inventory (landed 2026-05-15)

- **`VrcWorldTxDebugLogger`** — receiver that logs each VrcWorldTx callback to the console. Configurable `[tag] ` prefix. Per-event log toggles. Internal `subscribeToTxChanged` UI checkbox copied to the API-named `VrcWorldTx__Config__IncludeTxChangedCalls` field in `Start()` (since the API field name is ugly in the Inspector). Doubles as a smoke-test target.
- **`VrcWorldTxRebroadcaster`** — receiver that fans events out to an `UdonBehaviour[] targets` array. Each target gets the same Param fields + the same `SendCustomEvent` in array order. Null entries skipped. Same `subscribeToTxChanged` Inspector mirror as the logger. Covers the "single callback target" limitation in the callback contract without committing to a full propagator.

Both use `private` for their API-surface fields (Param + Config) — verified to work via SetProgramVariable / GetProgramVariable across UBs (see `vrchat-udonsharp` skill → "Field accessibility for cross-UB SetProgramVariable").

## Convention for adding new samples

When a future feature thread (voice / permissions / entry-exit / etc.) ships a "here's how a world author wires this up" example component:

- New `.cs` file in `Assets/Local/Scripts_Extras/<Name>.cs`.
- Paired `.asset` (UdonSharpProgramAsset) + `.asset.meta` with a 32-char hex GUID (per the `vrchat-udonsharp` skill creation checklist).
- Name with the `VrcWorldTx` prefix when it consumes the public callback API (e.g. `VrcWorldTxLocalScaleClampSample`). For helpers that don't ride the callback API (e.g. a "permissions decider" sample), name however reads cleanly.
- Mention the new sample in the README's "Bundled receiver helpers" subsection.
- Keep each sample MINIMAL — they're examples, not features. If a sample's Inspector surface grows beyond ~5 fields, consider whether the feature really belongs in `Scripts/` instead.

## How to apply

When adding the next feature, ask whether shipping a tiny sample alongside makes sense (often yes — first-class examples drive adoption). Drop it in `Scripts_Extras/`, follow the existing file pattern.
