---
name: Build & Test multi-client workflow for this project
description: User's standard workflow for multiplayer testing — Build & Test with Clients=N, then Build & Publish for separate accounts; shared username quirk on local clients
type: reference
originSessionId: d6951207-22e8-4061-8690-43e2cf33b087
---
User's two-tier multiplayer test workflow:

1. **Build & Test with Clients = 2+** — fastest cycle for initial sync / behavior verification. Caveat: all local clients share the same VRChat username (player IDs differ, but display name is identical), so it's visually hard to tell which window contains which player. Fine for "does sync work" sanity tests; not for testing UX flows that depend on identifying players by name.

2. **Build & Publish to a private instance** — slower cycle but gives separate test accounts with distinct usernames. Used when needed (e.g. UX validation, persistence testing).

VR coverage: user remote-desktops to their PC for a single VR player; can rig two VR players if both headsets are available, but typically one VR + N desktop clients is enough to demonstrate the multiplayer surface. Will rope in other people when they're around for higher fidelity testing.

**How to apply:**
- Default to suggesting Build & Test with Clients=N for first verification of a multiplayer change.
- Only suggest Build & Publish when something specifically requires distinct accounts.
- When writing test plans that involve telling players apart, note the shared-username caveat so the user knows to use playerId / chair-color / position to identify them.
