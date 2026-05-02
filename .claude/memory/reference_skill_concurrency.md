---
name: vrchat-udonsharp skill concurrency convention
description: How concurrent edits to the shared vrchat-udonsharp skill are gated — permissions.ask in global settings + git-immediate-commit discipline
type: reference
---

The `vrchat-udonsharp` skill at `C:\Users\progr\.claude\skills\vrchat-udonsharp\` is junctioned into `D:\J\Work\Dev\Claude\claude-skills\vrchat-udonsharp\` — its own git repo — and is shared across all of the user's VRChat projects. Multiple Claude threads (across projects) may be reading or modifying it at the same time. The harness occasionally crashes mid-edit, so any locking scheme has to be **stale-lock-tolerant**.

## Setup: human-as-throttle via permissions.ask

`~/.claude/settings.json` (global, applies cross-project) has a `permissions.ask` rule that gates `Edit` and `Write` on both the junction path and the canonical path under `claude-skills/`. Effect: every Edit/Write call into the skill triggers a Claude Code permission prompt. The user becomes the natural serialization point — if two threads race, the user can defer one until the other completes (or has been read-back to verify).

**Why this design (over a sidecar lock or a hook):**
- No `.lock` files → impossible to leak a stale lock if the harness crashes mid-edit. Any lock-based scheme would need timeout/heartbeat logic the user wants to avoid.
- Plain `permissions.ask` works in the desktop app (which is what the user runs); hooks would also work but `permissions.ask` is simpler and the user accepted "ask" as the friction level.
- Hooks could potentially suppress the "Yes, don't ask again" button (docs unclear), but `permissions.ask` cannot — see caveat below.

## ⚠ Caveat: "Yes, don't ask again" button

The standard `permissions.ask` prompt UI includes a "Yes, don't ask again" / "always allow" button. If clicked, the rule is bypassed for the rest of the session and the safety is gone. **Never click it for these patterns.** If you do by accident: edit `~/.claude/settings.json` (or the session's local override) to restore the rule, or just restart the session.

If this becomes a real risk, swap to a `PreToolUse` hook that returns `permissionDecision: "ask"` — hooks fire on every invocation regardless of past approvals (cannot be "always allowed" past).

## ⚠ Caveat: Bash bypasses these rules

The rules gate `Edit` and `Write` only. Bash commands (`cp`, `sed`, `tee`, `git checkout`, etc.) running on the skill files would NOT trigger the prompt. Convention: **always edit the skill via the `Edit` tool**, never via Bash. If a multi-file refactor of the skill is ever needed, surface that to the user explicitly so they can serialize manually.

## Edit protocol (when modifying the skill)

For every edit:

1. `git -C "D:/J/Work/Dev/Claude/claude-skills" status` — verify clean tree (or only your own staged work). If another thread has uncommitted changes you didn't author, stop and surface to the user — possible concurrent edit.
2. `Read` the file to get the freshest content (don't rely on a snapshot from earlier in the conversation).
3. `Edit` the file. The user gets prompted; on approval, the edit lands.
4. **Immediately** commit: `git -C "D:/J/Work/Dev/Claude/claude-skills" add <files>` + `git -C "D:/J/Work/Dev/Claude/claude-skills" commit -m "..."`. Small, focused commits — easier to bisect / revert.
5. If your commit fails because another thread committed first, re-read the file, re-apply the edit on top, commit again.

**Why "immediately" commit:** the window between Edit and commit is the race-prone region. Keep it small. The commit is essentially journaling — it doesn't need a separate prompt.

## Files location reminder

- Junction (use this path in tool calls — what other parts of memory point at): `C:\Users\progr\.claude\skills\vrchat-udonsharp\SKILL.md`
- Canonical / git-tracked: `D:\J\Work\Dev\Claude\claude-skills\vrchat-udonsharp\SKILL.md`
- Both `Edit` patterns are in the ask rules so either path triggers the prompt.

**How to apply:** When the user asks you to update the skill, follow the Edit protocol above. Expect (and surface to the user) the permission prompt — that's the throttle, not a bug.
