# Agent Handoff Instructions

City Dwellers keeps durable project memory in this repository so a new AI/coding session can resume without depending on a long chat transcript.

If the user supplies `CITYDWELLERS-RECOVER-V1`, begin with `RECOVERY.md` and
follow it exactly. It is the canonical recovery entry point.

Before making a non-trivial change:

1. Read `memory/CURSOR.json`. An `in_progress` cursor is a crash-recovery
   condition, not permission to assume the prior operation succeeded.
2. Read `memory/JOURNAL.jsonl` and `memory/PROTOCOL.md`.
3. Read `docs/PROJECT_STATE.md` for the current architecture, invariants, open work, and known hazards.
4. Read the relevant entries in `docs/PROJECT_HISTORY.md` when a decision needs historical context.
5. Treat Git and reproducible test evidence as authoritative. A commit mentioned only in a chat is not considered real until it can be found in the repository.
6. Do not silently resurrect a superseded or rejected approach. Record why a replacement was chosen.
7. Record a durable journal `BEGIN` and `in_progress` cursor before long or
   non-trivial work. Finish it with `COMMIT`, `ABORT`, or `SUPERSEDE`.
8. After a meaningful implementation, test, discovery, or design decision, update `docs/PROJECT_STATE.md` and/or `docs/PROJECT_HISTORY.md` in the same work session.
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
