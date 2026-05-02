---
name: vrchat-udonsharp skill concurrency convention
description: How to safely modify the vrchat-udonsharp skill when other Claude threads might be editing it concurrently
type: reference
---

The `vrchat-udonsharp` skill at `C:\Users\progr\.claude\skills\vrchat-udonsharp\` is junctioned into `D:\J\Work\Dev\Claude\claude-skills\vrchat-udonsharp\` — its own git repo — and is shared across all of the user's VRChat projects. Multiple Claude threads (across projects) may be reading or modifying it at the same time. The harness occasionally crashes mid-edit, so any locking scheme has to be **stale-lock-tolerant**.

## Convention

**Reads** are unrestricted — concurrent readers don't conflict on Windows filesystem.

**Writes** use git as the coordination point (no sidecar lock files → no stale-lock cleanup needed if a thread dies):

1. Before edit: `git -C <skill-repo> status` should show a clean tree (or only your own in-progress changes from this thread). If another thread has uncommitted changes, that's a sign of a racing edit — pause and re-check or surface to the user.
2. Make the edit.
3. Immediately commit: `git -C <skill-repo> add <files>` + `git -C <skill-repo> commit -m "..."`. Small focused commits are easier to bisect/revert than batched ones.
4. If `git commit` fails because another thread committed first, the working tree may need a pull/merge or rebase — re-read the file and re-apply the edit on top of the latest content.

## Why this approach

- **No sidecar lock files** → impossible to leave a stale lock if Claude's harness crashes mid-edit.
- **Git already tracks atomic content commits**, with built-in conflict detection if two threads race to modify the same hunk.
- **Trivial recovery**: if a thread dies between steps 2 and 3, the working tree just has uncommitted changes — visible on next `git status`, easy to inspect and either commit or discard.

## What NOT to do

- Don't introduce `.lock` files or `flock`-based schemes. They will leak when the harness dies.
- Don't batch many edits into one commit; that increases the window where two threads can race.
- Don't rely on filesystem mtime for "did someone else change this?" — git diff is more reliable.

**How to apply:** Before any edit to `SKILL.md` (or any file under `claude-skills/`), check `git status` in that repo, do the edit, commit immediately. If you find pre-existing uncommitted changes you didn't author, treat that as a possible concurrent edit — read the diff, decide if it's compatible, and surface to the user if uncertain.
