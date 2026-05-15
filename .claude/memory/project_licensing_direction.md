---
name: VrcWorldTx licensing direction
description: License stance for the public-repo release — open, no warranty, source-side attribution required, world-side attribution appreciated, fork-friendly.
type: project
originSessionId: planning-2026-05-12
---

Decided 2026-05-12 in a planning thread. License file not yet written — these are the design constraints for whoever writes it.

## Direction

- **Open / can-do-anything** for use in VRChat worlds (commercial or not).
- **No warranty / no responsibility** for downstream use.
- **Attribution propagation is required on the source side** — derivative source repos / forks must keep the attribution. Provide a pre-shipped attribution file in the repo so forks don't need to author anything; extending the file with additional lines for further forks is the expected pattern.
- **World-side attribution is appreciated but not required** — worlds that already list components/credits are encouraged to mention this one; not gated.
- **Forks preferred over copies** so traceback works via GitHub. The attribution file should state the canonical repo URL up-front.
- **Up-front README note**: the simplest compliance route is to fork and not remove the attribution file. Anything else is optional.

## How to apply

When writing the LICENSE / NOTICE / attribution file:
- Pick a permissive base (MIT-like) but add the source-side attribution clause.
- The attribution file itself should be the canonical "what to keep" artifact, separate from LICENSE.
- README should point forks at "easy path = keep this file" before any legalese.
- Worth checking sister projects (`VrcBoardBoundTactics`, `PurplePicturePalace`) for any existing license pattern before drafting; match conventions if one exists.

## Cross-references

- `project_roadmap.md` — licensing file is part of item #7 (README/demo/license ongoing).
