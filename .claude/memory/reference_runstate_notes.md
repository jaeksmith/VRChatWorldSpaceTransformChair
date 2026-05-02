---
name: Per-run-state notes (crash recovery scratchpad)
description: Convention for writing transient run-state notes under .claude/transient/ so context survives harness crashes
type: reference
---

The user's machine is currently flaky — Claude Code's harness can die abruptly mid-task with no warning, losing visible thread context. This makes long autonomous runs expensive: re-checking and rebuilding state can take longer than the original work.

## Convention

When a task is non-trivial (more than a couple of minutes of work, or spans multiple long-running tools), keep a short **run-state note** at:

```
.claude/transient/runstate.md
```

This directory is **gitignored** (see `.gitignore`'s `.claude/transient/` rule).

The note should be terse — just enough that a fresh Claude thread restarted by the user with "you died, please continue" can pick up where you left off without re-doing investigation:

```
# Run state (last update: <timestamp>)

Goal: <what we're doing>
Done: <bullets of completed steps>
Doing: <current step>
Next: <queued steps>
Open questions / blockers: <if any>
Key paths/identifiers we've established: <so re-search isn't needed>
```

## When to update

- **At meaningful checkpoints**, not every turn (otherwise it's pure overhead).
- After completing a sub-step that took non-trivial investigation/searching.
- Before kicking off a long-running tool/agent.
- When pivoting direction.

## When NOT to update

- For short, single-tool-call tasks.
- For purely conversational replies.
- Per-turn just to keep it "current" — that's noise.

## When to clear

- At end-of-thread, after the last user-visible response, write an empty note (or a one-liner "task X completed at <ts>") rather than leaving stale state.
- A new task in a continuing thread can either overwrite or append-with-divider, whichever is clearer.

**Why:** This is a write-mostly file — readers are only future Claude instances after a crash. Cost of keeping it up-to-date is small if updates are checkpoint-driven; cost of *not* having it after a crash is potentially many minutes of re-work.

**How to apply:** If the user reports "you died — continue what you were doing," check `.claude/transient/runstate.md` first before re-investigating.
