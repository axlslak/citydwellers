# Agent Handoff Instructions

City Dwellers keeps durable project memory in this repository so a new AI/coding session can resume without depending on a long chat transcript.

Before making a non-trivial change:

1. Read `docs/PROJECT_STATE.md` for the current architecture, invariants, open work, and known hazards.
2. Read the relevant entries in `docs/PROJECT_HISTORY.md` when a decision needs historical context.
3. Treat Git and reproducible test evidence as authoritative. A commit mentioned only in a chat is not considered real until it can be found in the repository.
4. Do not silently resurrect a superseded or rejected approach. Record why a replacement was chosen.
5. After a meaningful implementation, test, discovery, or design decision, update `docs/PROJECT_STATE.md` and/or `docs/PROJECT_HISTORY.md` in the same work session.
6. Mark uncertain recovered information explicitly. Do not convert old-chat recollection into a verified fact without checking code, Git, or logs.

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

These files are a restart checkpoint, not a verbatim conversation archive. Keep them dense, factual, and useful to the next session.