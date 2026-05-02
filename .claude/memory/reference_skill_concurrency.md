---
name: vrchat-udonsharp skill concurrency convention
description: Concurrent edits to the shared claude-skills repo are serialized via skill_edit.py (OS file lock); direct Edit/Write are denied
type: reference
---

The `vrchat-udonsharp` skill at `C:\Users\progr\.claude\skills\vrchat-udonsharp\` is junctioned into `D:\J\Work\Dev\Claude\claude-skills\` — its own git repo — and is shared across all of the user's VRChat projects. Multiple Claude threads (across projects) may try to edit it at the same time. The harness occasionally crashes mid-edit, so the locking scheme has to be **stale-lock-tolerant**.

## Mechanism: OS-level file lock via `skill_edit.py`

A wrapper script at `D:\J\Work\Dev\Claude\claude-skills\skill_edit.py` (committed in the repo) holds an exclusive OS lock on `<repo>/.write.lock` for the full read-edit-write-commit cycle. The OS releases the lock when the process exits — **including on crash** — so there is no sidecar lock file to clean up.

Inside the lock the script:
1. Verifies the working tree is clean (otherwise refuses — orphan changes from a prior crashed thread need human review).
2. Applies the requested ops (replace / replace_all / append / write / create / delete) using atomic per-file writes (`tempfile` + `os.replace`).
3. Runs `git add` + `git commit` with the supplied message.
4. Releases the lock by closing the lock fd (or the OS does it on crash).

## The deny rule that enforces this

`~/.claude/settings.json` has `permissions.deny` rules covering both the junction path and the canonical clone path, in three forms each (POSIX `//d/...`, Windows-forward `D:/...`, Windows-backslash `D:\...`) for `Edit` and `Write`. With `deny` (not `ask`):
- Any direct `Edit`/`Write` tool call against the skill paths fails outright.
- There is **no** "yes, don't ask again" button to fat-finger — the rule cannot be bypassed in the UI.
- The only allowed channel is `python skill_edit.py` via Bash.

## How to invoke (cookbook)

Single-op shorthand, JSON via Bash heredoc:

```bash
python "D:/J/Work/Dev/Claude/claude-skills/skill_edit.py" <<'OPS'
{
  "mode": "replace",
  "file": "D:/J/Work/Dev/Claude/claude-skills/vrchat-udonsharp/SKILL.md",
  "old": "<exact existing text>",
  "new": "<replacement text>",
  "commit_message": "vrchat-udonsharp: <what + why>"
}
OPS
```

If the JSON would contain backticks, embedded `<<EOF` markers, or other characters that fight with shell heredoc rules, write the JSON to a file via the `Write` tool first and pipe it:

```bash
python "D:/J/Work/Dev/Claude/claude-skills/skill_edit.py" \
  < ".claude/transient/skill_edit_input.json"
```

Batch mode for multi-file or multi-op changes in one commit:

```json
{
  "ops": [
    {"mode": "replace", "file": "...", "old": "...", "new": "..."},
    {"mode": "append",  "file": "...", "content": "..."}
  ],
  "commit_message": "..."
}
```

## Edge cases / failure modes

- **Dirty tree on entry**: script bails with a "resolve first" message. Inspect with `git -C "D:/J/Work/Dev/Claude/claude-skills" status` / `diff` and either commit the orphan work (if it looks complete and well-formed — quite possibly real content from a crashed thread) or `git restore` to discard. Then re-run.
- **`old` not unique** (in `replace` mode): script bails with the match count. Use a longer surrounding context to make `old` unique, or switch to `replace_all` if all matches genuinely need replacing.
- **Concurrent thread holds the lock**: this script blocks at lock acquire (default 180-second timeout). The other thread wraps up, commits, releases; this thread proceeds.
- **Editing `skill_edit.py` itself**: invoke the script on itself with `mode: "write"` and full new content. The deny rule blocks the `Edit` tool from touching it.
- **Bash shortcuts** (`echo > file`, `sed -i`, etc.) bypass both the deny rule AND the lock. **Don't use them.** No technical safeguard catches it; the convention has to hold.

## Background — why not the simpler approaches

Considered and rejected:
- `permissions.ask` — has a "yes, don't ask again" UI button that can be fat-fingered, removing the safety. Not a real serialization mechanism.
- Sidecar `.lock` files with PID/timestamp staleness checks — leak when the harness crashes; complex to make right; OS-level locks give all of this for free.
- Git as the coordination point alone — `git commit` is journaling, not serialization. Disjoint Edits work; same-region Edits / `Write`-tool overwrites can clobber.

The OS-level file lock + atomic file replace + dirty-tree precondition is the smallest mechanism that actually serializes concurrent threads without leaving stale state on crash.

**How to apply:** Whenever the user asks you to update the `vrchat-udonsharp` skill (or any other skill in `claude-skills/`), use `skill_edit.py` via Bash. Do not use the `Edit` or `Write` tool directly — those calls will be denied at the harness level. If you do attempt it and get denied, that's the safety net working; switch to the script.
