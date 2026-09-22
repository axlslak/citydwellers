# Agent Handoff Instructions

City Dwellers keeps durable project memory in this repository so a new
AI/coding session can resume without depending on a long chat transcript.

If the user supplies `CITYDWELLERS-RECOVER-V1`, begin with `RECOVERY.md` and
follow it exactly. It is the canonical recovery entry point.

## Repository-wide writer lock

Read `docs/REPOSITORY_COORDINATION.md` before making any change.

- `[INVARIANT]` Exactly one session may write in this repository at a time.
  This includes City Dwellers Chat mode and City Dwellers Work mode.
- `[INVARIANT]` `memory/CURSOR.json` is the repository-wide writer lock. It is
  not scoped to a project, process, directory, or ChatGPT mode.
- `[INVARIANT]` `master` is the single integration branch. Do not create
  session branches as a substitute for acquiring the lock, and never force
  push.
- A session may inspect, explain, and plan while another transaction is
  `in_progress`, but it must remain read-only unless it is recovering that
  exact transaction.
- `[OWNER-DIRECTION]` Kavey also guarantees one writer at a time across the
  sibling repository `axlslak/citybankers`. Its recovery key, cursor, journal,
  encrypted memories, and historical project records remain separate and must
  not be copied here.
- `[DECISION]` CityBankers runtime code is now an integrated City Dwellers
  subsystem. Its imported source and future unified-runtime changes live here;
  the sibling repository remains the authoritative pre-import history and
  recovery record unless Kavey explicitly requests another synchronized change.

Before making a non-trivial change:

1. Fetch/rebase `master`, then read `memory/CURSOR.json`. An `in_progress`
   cursor is either another active writer or a crash-recovery condition, not
   permission to begin unrelated work.
2. Read `memory/JOURNAL.jsonl`, `memory/PROTOCOL.md`, and
   `docs/REPOSITORY_COORDINATION.md`.
3. Read `docs/PROJECT_STATE.md` and relevant entries in
   `docs/PROJECT_HISTORY.md`.
4. Identify the active City Dwellers transaction in the journal `BEGIN` and
   cursor.
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

## Owner resolution policy

- Mark completed work resolved as soon as it is committed and published. Do not
  wait for the owner to explicitly say resolved, rebuild, deploy or test.
- Reopen a resolved topic when the owner reports an issue. Delayed testing,
  raids in progress or optional observations do not create an open task.
- Keep validation claims accurate: resolved does not mean live-tested.
  Record what was actually reviewed or tested without making unperformed
  owner tests a pending gate. Do not label unfinished implementation resolved.

## Owner-authored in-game changelog

- `plugins/CityManager/CityManager.Changelog.cs` keeps the complete chronological
  entry list in Git. `#changelog` displays only its last 25 entries.
- Append only wording explicitly provided by Kavey. Do not automatically add
  entries after implementation, infer entries from Git/recovery notes, invent
  dates, or paraphrase the owner's text.
- Do not move this changelog into runtime JSON or the user's data directory.

## Owner rule — no automatic physical audits (session 186)

- First priority when asked what needs fixing: remove automatic audits from normal bot operations. Do not resurrect this recovery strategy.
- Owner clarification: ONE initial startup audit is allowed per banker per host process. After that, physical bag audits/censuses require an explicit administrator request; SC+ alone is not sufficient. Keep the implementation in source, but disable/comment out automatic periodic, reconnect and failure-triggered execution. Existing #restart (Manager only) and #dump remain available.
- A problem on one banker must not audit or disrupt the whole roster. Report the concrete fault to the administrator; do not infer that a CRU donation justifies checking nine bankers.
- Prefer a local relog to refresh distrusted client state. There is no clientless 30-second relog rule. Never relog during a trade. Relogging must not implicitly launch an audit or manufacture proof of container contents.
- Hold only affected uncertain operations and explain the bug; administrator decides whether to authorize a physical audit. Evidence first, not blanket reconciliation.


## Owner relog evidence — session 189

- The owner's30-second logout statement concerns lingering avatar exposure to mobs, not a mandatory clientless reconnect delay. Do not use it as a cooldown or world-settling requirement.
- Owner measured nine consecutive in-play logins in18.184 seconds total (mean1.993 seconds per login), with next login starting about21–23 ms after local disconnect. All domains unloaded. Disconnect exceptions were separate from successful logins; do not repeat the probe's old misleading successful=0 conclusion.
- Prefer affected-only relog when client state is doubted, outside any active trade. Successful reconnect is not proof of unseen container contents. No blanket audit or roster disruption; existing one-startup/admin-only audit policy remains.

## Implemented recovery policy — session 190

- Normal banker recovery uses affected-only relog, never automatic physical audits after the one initial startup attempt. Scanner enforcement consumes a per-banker allowance per host generation before scanning; domain/plugin reload cannot reset it.
- Retain disabled audit code in source. Do not reconnect/restart Manager as part of banker recovery. Do not interpret another banker's fault, missing heartbeat or recovery file as permission to scan/relog peers.
- No relog during a trade; no fixed clientless relog/world-settling wait. Use fresh connection/bank state. Unresolved operation evidence remains local and is reported for administrator review instead of being silently reconciled or audited.
- Explicit administrator audit access remains the separate console-admin bankers-bagaudit mode. There is no new runtime SC+ audit authority. First-startup admission is not permission for a second scan.

## Public multiuser service — session 191

- Preserve bounded public work, per-member spam budgets, receipt ownership and atomic queued/claimed/cancelled IPC decisions. Multiple users must not overwrite each other's recipient, order, donation or pending receipt.
- Four active pickup orders (three items each) are staging capacity, not simultaneous Central trades. One Central trade window remains. Do not claim ten-user throughput without owner live evidence.
- One buffer cast failure must not clear everybody's queue. Keep duplicate/capacity checks atomic and preserve the no-automatic-audit policy under spam and concurrency.
- See docs/MULTIUSER_REVIEW.md for limits and repeatable owner live scenarios. Severe tell backlog pauses fresh public input rather than growing an unbounded rejection backlog; authenticated Manager administrative controls remain reachable.

## Mandatory MySQL persistence — session 192

- Owner direction: no runtime `data` folder, including logs, dumps, alts, caches,
  queues, receipts or generated lists. MySQL is mandatory; no disk fallback.
- Root `citydwellers.json` is administrator bootstrap configuration; compiled
  binaries and immutable GameData/NavMeshes/Buffers assets remain deployment
  inputs. Logical legacy data paths are SQL keys, never physical directories.
- The offline DataMigration utility creates the schema, preserves immutable
  source archives, verifies every byte/hash, seals once and removes only verified
  source files. Reruns must never replace evolved live state with archived data.
- SQL loss must stop the entire host from any AppDomain. Do not swallow a
  persistence failure and continue physical AO operations.
- Preserve atomic custody/accounting and create-only receipt/audit-token rules.
  Host logging uses its independent SQL transaction path to avoid a cross-domain
  logger waiting for the caller's state transaction.
- LoginTry was removed; the measured login evidence remains in history.
- Schema, indexes, migration commands and SQL debugging queries are documented in
  docs/MYSQL_MIGRATION.md, docs/mysql-schema.sql and docs/mysql-diagnostics.sql.

## Owner storage correction — session 200 (supersedes session 192 disk ban)

- Preserve items, stock, transactions/custody, donors/takers, lost and found,
  meaningful history and settings. Do not interpret cleanup as deleting business data.
- Disk `data/citydweller.log`, diagnostic dumps and the existing `items.json`
  catalogue/cache are explicitly permitted. Catalogue SQL conversion is deferred.
- Remove obsolete previous-host census snapshots and completed tell acknowledgements;
  retain pending messages and current-host coordination. No permanent diagnostic archive.
- Do not delete original source/backup data on migration reruns. The supplied SQL dump
  remains untouched. Cleanup is an explicit disposable-category allowlist.
- Fast normal startup and working banking/trading are the priority; not historical
  audit replay, checksums or catalogue redesign.

## Owner simplification correction — session 203

- Database still exists; owner explicitly corrected the earlier deletion statement.
  Preserve business state. Do not reset or reconstruct it from inventory.
- Remove redundant import archives, not move them into another chunk store or
  disk archive. DataMigration is retired; schema 3 no longer needs its tables.
- No automatic incident snapshot/export loop or duplicate service-event persistence.
  Normal disk runtime logs and explicit dumps remain permitted. Optional syslog remains.
- Existing relational ledger/stock and history, custody, lost/found, settings and
  pending messages remain intact. Cleanup uses an explicit disposable allowlist.
- Live SQL document/chunk APIs still exist for other business state. Do not claim
  the entire SQL filesystem has been removed. Continue simplification by replacing
  concrete domain dependencies, never by deleting required state or adding archives.

## Owner architecture correction — session 207

- Manager owns the complete working state in RAM and the ONE database connection.
  Bankers/other clients have no SQL connections. IPC carries requests/events; ordinary
  live decisions and coordination use memory, never SQL/file/network polling loops.
- Load persisted business data once at startup. Write only actual business changes
  and minimal interrupted-transaction recovery. SQL contains relational ledger,
  item/transaction history (including lost/found), and cloak events.
- Remove cd_directories, cd_documents, cd_document_chunks and cd_event_lines after
  preserving business records into their proper relational entities. No replacement
  filesystem, generic JSON/blob store, archive, chunks or checksum/hash gates.
- Tell queues, heartbeat/availability and coordination belong in Manager memory.
  Logs and requested dumps stay outside SQL. Operator edits in phpMyAdmin are valid
  business input on next startup; do not reject records because a stored hash differs.
- User authorized this full change after stopping session206. Its unfinished typed
  heartbeat table patch was explicitly discarded; do not revive incremental polling
  changes as a substitute for the requested architecture.

## Owner rule — duplicate container identities (session 215, revised 216)

- `[INVARIANT]` A container identity is unique by Anarchy Online's design. Two bags reporting the
  same identity is never an addressing quirk, an ambiguous layout, or a bookkeeping matter.
- `[OWNER-DIRECTION]` Never tolerate one. Never deduplicate-and-continue. Never trade, store,
  withdraw, reconcile or account against one. Halt the affected banker only, name the condition,
  and report it to the administrator. The detection and recovery path already exists in code.
- `[DECISION]` Detail does not belong in this repository. Background, the single prior occurrence,
  the evidence, the unresolved cause and the reasoning are in **encrypted conversation memory #5**;
  ask the owner for the password. Public files carry hints and rules; memories carry narrative.
- `[HAZARD]` Session 147 weakened this check so a process could keep running; session 153 restored
  it. A tripwire that is inconvenient is still a tripwire.
- `[DECISION]` Name the condition for what it is. Neutral vocabulary understated it and is what
  made the session 147 error feel reasonable.

## Handoff rule — sparse two-assistant review (sessions 218-222)

The owner works with two assistants. They do not run concurrently and neither owns
the repository. This is how they take turns.

- `[INVARIANT]` `memory/CURSOR.json` is the referee, not either assistant and not
  the owner's memory of who was last in the tree. `idle` means the next isolated
  job is available to whoever asks first. `in_progress` means inspect and plan
  freely, write nothing.
- `[OWNER-DIRECTION]` Publish in three steps, so a crash boundary is unmistakable
  to the other writer: a journal `BEGIN` marker with the cursor set `in_progress`,
  committed and pushed **before** implementing; then the implementation as its own
  commit; then the terminal record naming that implementation commit by hash, with
  the cursor back to `idle`. A seal that does not name its implementation commit
  makes recovery guesswork.
- `[DECISION]` Treat the other assistant's commits exactly as you would treat your
  own older work: inspect them, and let Git, the code and the evidence decide. Do
  not overwrite something because of who authored it, and do not defer to it for
  the same reason.
- `[DECISION]` A review finding is traced before it is accepted or disputed. Say
  plainly which it was. Four rounds across sessions 219-222 each found something
  the previous round missed, including two of this repository's invariants and one
  definite accounting bug — the value is in the independence, which is lost if
  either side simply agrees.
- `[DECISION]` Corrections go in as `SUPERSEDE` records naming the superseded
  `seq`. The journal is append-only; a wrong claim is answered, never edited.
- `[OWNER-DIRECTION]` Credit the review in the record. Who found a thing is part of
  how the next session judges how well it was checked.
