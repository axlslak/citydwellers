# City Dwellers Durable Recovery Protocol

Protocol id: `CITYDWELLERS-RECOVER-V1`

The recovery system is organized like a checkpoint plus write-ahead log:

- `docs/PROJECT_STATE.md` is the compact current-state checkpoint.
- `docs/PROJECT_HISTORY.md` is the condensed engineering history.
- `memory/JOURNAL.jsonl` is the append-only semantic transaction log.
- `memory/CURSOR.json` is the replaceable pointer to current/incomplete work.
- `memory/MANIFEST.json` indexes encrypted session memories.
- `memory/conversations/` preserves richer, encrypted historical reasoning.
- Git commits are the authoritative atomic implementation record.

## Transaction protocol

Before a non-trivial, long-running, or risky operation:

1. Append a `BEGIN` record to `memory/JOURNAL.jsonl`.
2. Set `memory/CURSOR.json` to `in_progress`, naming the transaction, base
   commit, task, and exact resume instruction.
3. Commit this recovery marker. Publish it before starting when repository
   access permits.

During the operation:

1. Keep source changes focused.
2. After a meaningful implementation phase, live test, discovery, or design
   decision, update state/history and append a `CHECKPOINT` when losing the
   new evidence would force the owner to repeat work.
3. Never journal passwords, account data, private logs, or unapproved private
   dependencies.

At the end:

1. Verify the implementation and repository state.
2. Update `PROJECT_STATE.md` and `PROJECT_HISTORY.md`.
3. Create or extend an encrypted session memory when the conversation has
   accumulated reasoning that is not adequately represented by state/history.
4. Commit the implementation/memory changes.
5. Append exactly one terminal journal record: `COMMIT`, `ABORT`, or
   `SUPERSEDE`. A `COMMIT` names the implementation commit it seals.
6. Set the cursor to `idle`, pointing to the last completed transaction and
   next task.
7. Commit the seal and publish all commits.

## Journal rules

- JSON Lines: exactly one valid JSON object per line.
- `seq` is strictly increasing and never reused.
- Existing lines are append-only. Correct mistakes with a later `SUPERSEDE`
  entry rather than rewriting history.
- The journal records semantic operations, not every shell command or file
  read. Git already records file-level changes; the journal records intent,
  boundary, outcome, evidence, and recovery position.
- Supported phases are `BEGIN`, `CHECKPOINT`, `COMMIT`, `ABORT`, and
  `SUPERSEDE`.
- A `BEGIN` without a later terminal record is an interrupted transaction.

## Cursor rules

The cursor is intentionally small and replaceable. It must answer:

- Which numbered session is current?
- Is work `idle` or `in_progress`?
- What transaction is active or last completed?
- Which commit is the recovery base/checkpoint?
- What should the next session do first?

The cursor is not history. The append-only journal and Git preserve history.

## Keeping growth manageable

Do not require every future session to reread an infinite archive.

- State/history are periodically compacted checkpoints, never raw transcripts.
- Rich encrypted memories remain available for audit and historical questions.
- `memory/MANIFEST.json` may mark records as boot-required or historical.
- A new checkpoint memory may summarize earlier sessions while preserving the
  older encrypted records unchanged.
- A normal restart reads the checkpoint, journal since that checkpoint, and
  boot-required encrypted memories. Older memories are decrypted on demand.

This preserves detail without making recovery time grow without bound.
