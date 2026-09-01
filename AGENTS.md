# Agent Handoff Instructions

This repository keeps durable project memory for City Dwellers and City Banker
so a new AI/coding session can resume without depending on a long chat
transcript.

If the user supplies `CITYDWELLERS-RECOVER-V1` or
`CITYBANKER-RECOVER-V1`, begin with `RECOVERY.md` and follow it exactly. It is
the canonical recovery entry point.

## Repository-wide writer lock

Read `docs/REPOSITORY_COORDINATION.md` before making any change.

- `[INVARIANT]` Exactly one session may write anywhere in this repository at a
  time. This includes City Dwellers Chat mode, City Dwellers Work mode, and
  every City Banker session.
- `[INVARIANT]` `memory/CURSOR.json` is the repository-wide writer lock. It is
  not scoped to a project, process, directory, or ChatGPT mode.
- `[INVARIANT]` `master` is the single integration branch. Do not create
  session branches as a substitute for acquiring the lock, and never force
  push.
- A session may inspect, explain, and plan while another transaction is
  `in_progress`, but it must remain read-only unless it is recovering that
  exact transaction.
- Every new journal record should identify `project` as `citydwellers`,
  `citybanker`, or `repository`.

Before making a non-trivial change:

1. Fetch/rebase `master`, then read `memory/CURSOR.json`. An `in_progress`
   cursor is either another active writer or a crash-recovery condition, not
   permission to begin unrelated work.
2. Read `memory/JOURNAL.jsonl`, `memory/PROTOCOL.md`, and
   `docs/REPOSITORY_COORDINATION.md`.
3. For City Dwellers, read `docs/PROJECT_STATE.md` and relevant entries in
   `docs/PROJECT_HISTORY.md`. For City Banker, read
   `docs/citybanker/PROJECT_STATE.md` and relevant entries in
   `docs/citybanker/PROJECT_HISTORY.md`.
4. Identify the active project in the journal `BEGIN` and cursor.
5. Treat Git and reproducible test evidence as authoritative. A commit mentioned only in a chat is not considered real until it can be found in the repository.
6. Do not silently resurrect a superseded or rejected approach. Record why a replacement was chosen.
7. Record a durable journal `BEGIN` and `in_progress` cursor before long or
   non-trivial work. Finish it with `COMMIT`, `ABORT`, or `SUPERSEDE`.
8. After a meaningful implementation, test, discovery, or design decision,
   update the active project's state and/or history files in the same work
   session.
9. Mark uncertain recovered information explicitly. Do not convert old-chat recollection into a verified fact without checking code, Git, or logs.

## Conversation-specific memory

If the user refers to an old development conversation by number, title, or id (for example `conversation #1: AOLite Config JSON Format`), do not rely only on model memory. Read `memory/MANIFEST.json`, locate the matching encrypted recovery record, follow `memory/README.md`, and decrypt it using the password supplied by the user. Use that recovered text as historical discussion context; Git/code/test evidence still wins for current implementation truth.

Passwords for encrypted conversation memories are deliberately not stored in this public repository.

## Journaling scope

Journal semantic transactions and recovery boundaries, not every command.
Record intent, outcome, evidence, blockers, and the exact resume point. Never
put credentials or private raw logs in the public journal. See
`memory/PROTOCOL.md` for the write-ahead transaction rules.

## Durable status vocabulary

Use these labels where useful:

- `[VERIFIED]` confirmed by current Git/code/logs/tests.
- `[DECISION]` deliberate design choice that future work should preserve unless intentionally changed.
- `[INVARIANT]` behavior or constraint that must remain true.
- `[OPEN]` unfinished work.
- `[HISTORICAL]` true of an earlier stage but not necessarily current.
- `[CHAT-ONLY]` claimed in a past conversation but not independently verified.
- `[SUPERSEDED]` replaced by a later implementation or decision.
- `[DO-NOT-USE]` known bad/rejected approach.

## Safety / publication constraints

- Never commit credentials, account secrets, private logs, or private third-party material.
- `InfoHelper` is explicitly excluded from this public repository.
- Prefer reproducible dependency restore over hard-coded developer-machine paths.

The state/history files are compact restart checkpoints. The encrypted `memory/` records preserve conversation-specific historical context. Keep both useful to future sessions.

## Owner build/test boundary

- Unless Kavey explicitly asks otherwise, write and review the code but do not
  spend the Work-session usage window compiling, running test suites, or
  attempting live AO tests. Kavey owns builds and live testing and will return
  the resulting logs.
- Warn Kavey before a tool-heavy or potentially long investigation. Prefer
  focused repository inspection and coherent code changes over speculative
  environment work.
- Distill session memory to decisions, invariants, evidence, hazards, and the
  exact resume point. Do not preserve small talk or repetitive command history
  merely because it occurred.
