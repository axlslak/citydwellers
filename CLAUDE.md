# CLAUDE.md — City Dwellers session bootstrap

City Dwellers is an Anarchy Online municipal bot suite (C#, .NET Framework 4.8):
one supervising host plus five plugins. This repository carries its own durable
memory so a new session can resume **without the owner retelling the project**.

Read this file first, then follow it. It does not replace `AGENTS.md`; it points
you at it and adds what a Claude Code session specifically needs.

## 1. Mandatory first reads

In this order, before any non-trivial change:

1. `AGENTS.md` — handoff rules and owner directions
2. `RECOVERY.md` — canonical recovery entry point (key `CITYDWELLERS-RECOVER-V1`)
3. `docs/REPOSITORY_COORDINATION.md` — cross-session write rules
4. `memory/CURSOR.json` — the repository-wide writer lock
5. `memory/JOURNAL.jsonl` — append-only transaction log (read the tail)
6. `memory/PROTOCOL.md` — write-ahead transaction rules
7. `docs/PROJECT_STATE.md` — current-state checkpoint (read recent sessions first)
8. `docs/PROJECT_HISTORY.md` — condensed engineering history
9. `memory/MANIFEST.json` + `memory/README.md` — encrypted conversation memories

Encrypted memories need a password the owner supplies; it is deliberately never
stored here. Memories tagged `boot-required` are part of a normal boot — ask for
the password rather than skipping them.

## 2. The writer lock — not optional

- `[INVARIANT]` Exactly one session may write at a time. `memory/CURSOR.json`
  locks the **whole repository**, not a directory or a project.
- `[INVARIANT]` `master` is the single integration branch. Never force push.
- An `in_progress` cursor means another writer or a crash to recover — it is not
  permission to start unrelated work. Inspect and plan freely; do not write.

Writer sequence: fetch → confirm cursor `idle` → append journal `BEGIN` and set
cursor `in_progress` → **commit and publish that marker before implementing** →
make one focused change → update state/history → append exactly one terminal
record (`COMMIT`, `ABORT`, `SUPERSEDE`) → cursor back to `idle` → publish.

## 3. Owner standing directions

- `[OWNER-DIRECTION]` **Always persist durable state. Do not ask permission to
  save.** The owner has lost long AI conversations to context limits; that is why
  this system exists. Journal, state, history and recovery notes are written as a
  matter of course, not on request. Err toward over-recording.
- `[OWNER-DIRECTION]` The owner (Kavey) builds and live-tests. You write and
  review code. Warn before long or tool-heavy investigation.
- `[OWNER-DIRECTION]` Mark work resolved once committed and published; do not
  wait for the owner to confirm a rebuild. Reopen if they report a problem.
- `[OWNER-DIRECTION]` The in-game changelog
  (`plugins/CityManager/CityManager.Changelog.cs`) takes **only** wording the
  owner supplies verbatim. Never infer, invent, date or paraphrase entries.
- The owner prefers Linux/server-first operation and reproducible restore; they
  use Windows VMs only where AOSharp forces it. Prefer headless, pinned,
  path-independent solutions over Visual-Studio-only convenience.

## 4. Build and test boundary

Claude Code web/remote containers have **no .NET toolchain** (no `dotnet`,
`mono`, `msbuild`, `csc`, `nuget`) and the projects target .NET Framework 4.8.
You therefore cannot compile or live-test this project. Say so plainly rather
than implying validation you did not perform. Source review, API/IL reasoning,
project XML checks and `git diff --check` are the available validation.

Never claim a build succeeded, a test passed, or behavior was live-verified
unless the owner supplied that evidence.

## 5. Journal rules — and how they were broken once

- Exactly one valid JSON object per line. `seq` strictly increasing, never reused.
- Existing lines are append-only; correct mistakes with a later `SUPERSEDE`.
- Record semantic transactions, not shell commands. Never journal credentials,
  account data or private logs.

`[HAZARD]` In September 2026 a session read the journal through a tool that
**truncated its output**, then wrote the truncated text back as the file. That
destroyed records seq79-86 and left literal tool output (`Warning: truncated
output…`) as file content. It happened twice; the second instance survived seven
days. Session 135 restored the lost records from the clean copy at `f888aca`.

**Never write a file back from content a tool may have truncated.** Edit the
journal by appending, or by a script that reads the file directly from disk and
validates every line parses before writing. Validate after writing:

```bash
python3 -c "
import json
s=[json.loads(l)['seq'] for l in open('memory/JOURNAL.jsonl') if l.strip()]
assert all(s[i]<s[i+1] for i in range(len(s)-1)), 'seq not increasing'
print('OK', len(s), 'records', min(s), '-', max(s))"
```

## 6. Safety and publication constraints

- Never commit credentials, account secrets, private logs, or private
  third-party material. This repository is public.
- `InfoHelper` is permanently excluded.
- The owner's runtime `data/` snapshot (member names, alts, logs, ledgers) is
  supplied for analysis only — **analyse it, never commit it**.
- `data/items.json` is a 424 MB third-party dump. Never commit it.
- `[DO-NOT-GUESS]` The Grid→Serenity handoff is deliberately unmapped. Return
  route-unavailable; never invent a coordinate and never route to a tower site.
- `[INVARIANT]` Cloak automation is **enable-only**. Auto-enabling a forgotten
  cloak is recoverable; auto-disabling can trigger hostile raid activity.

## 7. Status vocabulary

Use these in state/history/journal: `[VERIFIED]`, `[VERIFIED-LIVE]`,
`[IMPLEMENTED]`, `[DECISION]`, `[INVARIANT]`, `[OPEN]`, `[HISTORICAL]`,
`[CHAT-ONLY]`, `[SUPERSEDED]`, `[DO-NOT-USE]`, `[OWNER-DIRECTION]`.

`[CHAT-ONLY]` matters: a commit mentioned only in conversation is **not real**
until Git confirms it. Two such commits (`6585617`, `91aeae6`) were once
believed published and never existed. Always verify against Git.

## 8. Practical notes

- Clones may be **shallow**. `git log -S` will then point at the oldest visible
  commit as if it were the origin of a change. Run
  `git rev-parse --is-shallow-repository` and `git fetch --unshallow` before
  making any claim about history.
- Component layout: `executables/CityDwellers` (host), `plugins/CityManager`,
  `plugins/CityBankers`, `plugins/CityBuddies`, `plugins/CityFlipper`,
  `plugins/CityBuffers`, `shared/`, `bankers/shared/`.
- Build and runtime layout: `BUILDING.md`. Topic docs live in `docs/`.
