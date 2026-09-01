# City Banker — Persistent Project State

Initialized: 2026-09-01

This is City Banker's compact restart checkpoint. It must contain verified
current architecture, decisions, invariants, hazards, and the exact next task
as the project develops; it is not a transcript.

## Recovery and repository

- Recovery key: `CITYBANKER-RECOVER-V1`.
- Repository: `axlslak/citydwellers`; integration branch: `master`.
- Canonical entry point: `RECOVERY.md`.
- `[INVARIANT]` Read `docs/REPOSITORY_COORDINATION.md` and obey the global
  `memory/CURSOR.json` writer lock before changing any file.
- `[OWNER-DIRECTION]` Two GPT sessions may work on City Banker, but only one
  session may write anywhere in the repository at a time.
- `[OWNER-DIRECTION]` City Banker and City Dwellers may share useful ideas and
  findings. Record validated cross-project knowledge in
  `docs/SHARED_ENGINEERING.md` with its evidence and compatibility boundary.
- `[INVARIANT]` City Banker's project-specific decisions and progress belong
  in this file and `docs/citybanker/PROJECT_HISTORY.md`, not in the City
  Dwellers checkpoint.

## Current verified state

- `[OPEN]` The first City Banker work transaction must inspect the repository
  and record its real code location, architecture, constraints, build/test
  boundary, and immediate task. Do not infer or invent them from City Dwellers.
- `[OPEN]` No City Banker implementation claim has yet been recorded in this
  checkpoint.
