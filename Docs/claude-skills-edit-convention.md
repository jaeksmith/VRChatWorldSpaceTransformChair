# Editing the shared `claude-skills/` repo

Multiple Claude threads (across different VRC projects on the same machine) can edit the shared `claude-skills` repo at the same time. Without coordination, two threads racing on the same file silently produce interleaved edits or lost work, and crash mid-edit can corrupt files. The mechanism below serializes concurrent threads without leaving stale state on crash.

**Mechanism: `skill_edit.py` at the root of the `claude-skills` repo** holds an OS-level exclusive file lock on `<repo>/.write.lock` for the full read-edit-write-commit cycle. The OS releases the lock when the process exits — including on crash — so there is no sidecar lock file to clean up. The script applies edits with atomic per-file writes (tempfile + `os.replace`), then `git add` + `git commit`. It refuses to run if the working tree is dirty (catches orphan changes from a previously crashed thread — surface for human review rather than auto-resolving).

## Required `settings.json`

At `~/.claude/settings.json` (or merge into existing) — `permissions.deny` rules block direct `Edit`/`Write` against the skill paths so the script is the only write channel. With `deny` (not `ask`), there is no "yes, don't ask again" button to fat-finger:

```json
{
  "permissions": {
    "deny": [
      "Edit(//c/Users/progr/.claude/skills/**)",
      "Write(//c/Users/progr/.claude/skills/**)",
      "Edit(C:/Users/progr/.claude/skills/**)",
      "Write(C:/Users/progr/.claude/skills/**)",
      "Edit(C:\\Users\\progr\\.claude\\skills\\**)",
      "Write(C:\\Users\\progr\\.claude\\skills\\**)",
      "Edit(//d/J/Work/Dev/Claude/claude-skills/**)",
      "Write(//d/J/Work/Dev/Claude/claude-skills/**)",
      "Edit(D:/J/Work/Dev/Claude/claude-skills/**)",
      "Write(D:/J/Work/Dev/Claude/claude-skills/**)",
      "Edit(D:\\J\\Work\\Dev\\Claude\\claude-skills\\**)",
      "Write(D:\\J\\Work\\Dev\\Claude\\claude-skills\\**)"
    ]
  }
}
```

Adjust paths if your clone lives elsewhere. The triplet (POSIX `//d/...`, Windows-forward `D:/...`, Windows-backslash `D:\...`) covers all the path forms Claude Code might use for the same file via the junction or canonical path.

## Invocation pattern

JSON op on stdin. Supported modes: `replace` / `replace_all` / `append` / `write` / `create` / `delete`. Batchable for multi-file edits in one commit.

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

If JSON content has backticks or other characters that fight with shell heredoc rules, write the JSON to a temp file via the `Write` tool first and pipe it:

```bash
python "D:/J/Work/Dev/Claude/claude-skills/skill_edit.py" \
  < "/path/to/skill_edit_input.json"
```

Batch form for multiple ops in one transaction:

```json
{
  "ops": [
    {"mode": "replace", "file": "...", "old": "...", "new": "..."},
    {"mode": "append",  "file": "...", "content": "..."}
  ],
  "commit_message": "..."
}
```

## Hard rules

- **Do NOT** use Bash shortcuts (`echo > file`, `sed -i`, `tee`, etc.) on skill files — they bypass both the deny rule and the lock. The convention is: edits to the skill go through `skill_edit.py`, period.
- **If a Claude thread tries `Edit`/`Write` directly on the skill path and gets denied, that's the safety net working** — switch to the script.
- **If the script bails on a dirty working tree**, it means a prior thread crashed mid-edit. Inspect with `git -C <repo> status` / `git diff` and either commit the orphan work (if it looks complete and well-formed — quite possibly real content from a crashed thread) or `git restore` to discard. Then retry.
- **Editing `skill_edit.py` itself**: invoke the script on itself with `mode: "write"` and full new content (the deny rule blocks the `Edit` tool from touching it).

The same convention is documented in the `claude-skills` repo's own README (under "Concurrent edits — use `skill_edit.py`") so a freshly-cloned repo on a new machine carries the documentation with it.
