# Repository Coordination

This repository contains City Dwellers and its cousin project City Banker.
Kavey permits sessions to share useful engineering knowledge and guarantees
that only one session will write at a time.

## Global rules

- `[INVARIANT]` Exactly one writer exists across the entire repository. Chat
  mode, Work mode, different projects, and different machines do not create
  separate write scopes.
- `[INVARIANT]` `master` is the single integration branch. Never force push.
- `[INVARIANT]` `memory/CURSOR.json` is the repository-wide writer lock and
  `memory/JOURNAL.jsonl` is the shared append-only transaction history.
- Readers may inspect, analyze, and discuss any committed content. They do not
  edit files, create commits, or publish while the cursor is `in_progress`.
- An interrupted transaction is recovered, explicitly aborted, or superseded
  before unrelated work begins.

## Writer sequence

1. Fetch/rebase `master` and confirm local `HEAD` matches `origin/master`.
2. Read the cursor and newest journal records. Proceed only if the cursor is
   `idle`, or if this session is explicitly recovering its active transaction.
3. Append a journal `BEGIN` with `project` set to `citydwellers`, `citybanker`,
   or `repository`; set the cursor to `in_progress` with the same project and
   transaction.
4. Commit and publish that marker before implementation.
5. Make a focused change and update the active project's state/history. Put
   validated cross-project findings in `docs/SHARED_ENGINEERING.md`.
6. Before final publication, fetch again. If the remote changed unexpectedly,
   stop and reconcile rather than overwriting it.
7. Publish the implementation, append exactly one terminal journal record
   (`COMMIT`, `ABORT`, or `SUPERSEDE`), return the cursor to `idle`, and publish
   the seal.

If usage limits interrupt a writer after step 4, the committed cursor tells the
next session exactly what to resume. The next writer does not guess that the
operation completed.

## Project routing

| Project | Checkpoint | History | Recovery card |
|---|---|---|---|
| City Dwellers | `docs/PROJECT_STATE.md` | `docs/PROJECT_HISTORY.md` | `RECOVERY_CARD.txt` |
| City Banker | `docs/citybanker/PROJECT_STATE.md` | `docs/citybanker/PROJECT_HISTORY.md` | `CITYBANKER_RECOVERY_CARD.txt` |

City Dwellers Chat and Work modes are equal Git writers; their labels only
describe the intended size of the task. Both obey the same sequence above.

## Sharing boundary

Kavey authorizes sharing ideas, findings, and compatible implementation
patterns between City Dwellers and City Banker. A shared note must identify its
source project, evidence or commit, maturity, and compatibility assumptions.
The receiving project verifies those assumptions before adopting it.

Never put credentials, private raw logs, private account data, or private
third-party material in the repository or shared notes.
