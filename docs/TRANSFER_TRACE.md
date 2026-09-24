# Transfer trace (phase 1 instrumentation)

Added in session 234 to answer one question: where does the time go when ten
ordinary items move from Central into worker storage. It measures; it changes no
trade or storage behaviour.

## Reading a trace

On Manager, as an administrator:

- `trace` — index of retained spans, newest last.
- `trace <n>` — write span *n* to the runtime log.
- `trace <transaction|batch|attempt>` — write every span matching that id. Both
  sides of one transfer share a batch id, so a batch selector writes Central's
  span and the worker's together, which is how they should be read.

A timeline is written to the **runtime log**, not to chat, and the chat reply is
only a confirmation with a character count. A ten-item span is roughly 15 KB, far
past any proven organization or tell blob size; replying it would silently cut it
and lose the measurement. Look for `TRACE SPAN BEGIN` in the log. The chat index is
capped at twelve lines for the same reason.

`[INVARIANT]` If a span reports `Truncated: true`, its timeline is incomplete and
must not be used as a measurement. The index and the dump confirmation both say so.
Fix the tracer and re-run instead.

Retention is a bounded in-memory ring of the last 64 spans on Manager. It is
convenience, not an archive: it is lost on restart. Current state is therefore
**live structured capture implemented; durable capture only when an external
syslog sink is configured** (`Syslog` in `citydwellers.json` — `Enabled`, `Host`,
`Port`, `Transport`). Durable JSON diagnostics in general is a separate problem and
is not solved by this session.

## Why it is shaped this way

- `[INVARIANT]` Durations are monotonic. Every elapsed value comes from
  `Stopwatch`. `StartedUtc` is carried once per span for correlation only. An
  operator clock change must not be able to invent or erase measured time.
- One span emits **one** event, not one per stage. The `ServiceEvents` relay is a
  serialized named-pipe round trip per event behind a 256-item bounded queue;
  per-stage emission would drop the data it exists to capture. Stages accumulate
  in the span and ship on `End`. A failed span ships immediately, because the
  run-up to a failure is the most useful trace there is.
- `[DECISION]` Trace rows are **not** in `BusinessTables`. That mapper is driven
  off `AccountingState`, so a trace row would join custody commits and a
  telemetry write could fail a custody transaction. Diagnostics never gate
  custody.
- Nothing is written to disk, per the session 192 storage boundary.
- Every `TradeTrace` entry point tolerates a null span and swallows its own
  exceptions. A broken tracer must not fail a transfer.

## Span shape

`Kind` is `dispatch` (Central) or `worker`. Correlation: `TransactionId`,
`BatchId`, `AttemptId`, `Source`, `Destination`, `Role`, `ManifestCount`.
Outcome: `Outcome` (`completed`, `failed`, `superseded`, `abandoned`), `Error`,
`TotalMs`, `Truncated`.

Each `Stages[]` entry carries `Name`, `AtMs` (monotonic ms since span start),
`SinceMs` (since the previous stage), and where relevant `Attempt`, `Detail`,
`AoId`, `Ql`, `Occurrence`. Empty fields are suppressed, so an ordinary action
stage serializes to three fields.

Wait totals and stage deltas are additive: closing a wait before recording an
action means nothing is counted twice.

### Classifying a wait

A wait stage is named `wait:<condition>` and carries:

| Field | Meaning |
| --- | --- |
| `WaitedMs` | how long the condition stayed unmet |
| `Observations` | how many times the machine evaluated it |
| `FirstLookMs` / `LastLookMs` | first and last look that found the condition **unsatisfied** |
| `MaxLookGapMs` | largest interval between consecutive unsatisfied looks |

`[INVARIANT]` `LastLookMs` is the *last unsatisfied* look, never the satisfying
one. `Wait()` is only called on a not-yet-satisfied path; the satisfying
observation calls `Mark()`, which closes the wait without touching it. Were the
satisfying look recorded there, the blind spot would collapse toward zero and
falsely exonerate our own polling — the one thing the field exists to expose.

`[INVARIANT]` A wait must never wrap a synchronous call. A blocking operation has
no unsatisfied looks, so its blind spot would equal its whole duration and read as
our latency. Bracket those with two marks instead — which is why the accounting
commit is `commit.begin` / `commit.end` and its duration is `commit.end`'s
`SinceMs`.

`WaitedMs` alone **cannot** tell you whose latency it is. The discriminator is the
**blind spot**, `AtMs - LastLookMs`: the window in which the condition may already
have been satisfied with nobody looking.

- Small blind spot, small `MaxLookGapMs` → we were watching closely; the wait is
  genuinely **external** (AO, IPC, or SQL, depending on the condition).
- Large blind spot or large `MaxLookGapMs` → we were not looking; that time is
  **our polling cadence**, not AO's latency. `wait:settle-timer-500ms` and
  `wait:ipc-retry-floor-1000ms` are **artificial delay** by construction.

So each wait resolves to one of: external/AO, our polling, artificial delay, IPC,
SQL/accounting, or unknown. Do not attribute a wait without checking its blind spot.

`WaitTotals` gives the same grouped by condition: `TotalMs`, `Episodes`,
`Observations`, `MaxEpisodeMs`, `MaxLookGapMs`, `BlindSpotMs`, ordered by total
descending.

### Summing waits

`[INVARIANT]` A span has at most one open wait: a different condition closes the
previous one, and any `Mark` closes whatever is open. So **within one span**, waits
cannot overlap and their sum is critical-path waiting with no double counting.

This does **not** hold across spans. Central and the worker run concurrently, so
adding a wait from each double-counts real wall time. Report the two spans'
critical-path waiting separately.

`[INVARIANT]` `TotalMs` is taken from the final stage, inside the span lock, before
any serialization, relay or storage. A span never includes the cost of shipping
itself, and no stage can read above the span total.

## Counters

Per span, so two designs can be compared without re-reading either:
`ao_operations`, `ipc_round_trips`, `accounting_commits`, `waits`, `retries`,
`bag_bank_to_inventory`, `bag_inventory_to_bank`, `bag_opens`, `items_placed`.

## Stages

Central (`dispatch`): `batch.eligible`, `worker.prepare.requested`,
`worker.prepare.ready` / `.refused`, `worker.prepared`, `trade.open.requested`,
`trade.open.observed`, `item.add.requested`, `item.add.observed`,
`item.add.resent`, `manifest.complete`, `accept.requested`, `trade.finished` /
`trade.declined`, `posttrade.evidence.complete`, `storage.accepted`.

Worker (`worker`): `trade.open.observed`, `manifest.complete`,
`accept.requested`, `trade.finished` / `trade.declined`,
`posttrade.evidence.complete`, `storage.received`, then per item
`bag.selected`, `bag.out.requested`, `bag.out.observed`, `bag.open.requested`,
`bag.open.observed`, `item.move.requested`, `item.move.observed`,
`commit.begin`, `commit.end` (or `commit.failed`), `bag.return.requested`,
`bag.return.observed`, and finally `storage.batch.complete`.

`bag.selected` is emitted **per item**, not per bag, because that is what the
current design does: it reselects and re-extracts the destination bag for every
item. A ten-item batch landing in one bag should therefore show ten selections
and ten bank round trips. That is the shape under investigation, stated by the
machine rather than asserted by a reviewer.

## Named timers

Constants that could dominate real AO latency are named in the trace so they are
unmistakable:

- `wait:settle-timer-500ms` — deliberate settle delay in the post-trade receipt,
  *after* the inventory delta already matched exactly.
- `wait:ipc-retry-floor-1000ms` — minimum gap between worker preparation
  attempts.
- `item.add.resent` with `Attempt` — the 1200ms `AddItem` resend
  (`InternalAddItemRetryMilliseconds`, up to `InternalAddItemMaxAttempts`). If a
  healthy batch shows none of these, that timer costs nothing and is not what
  makes transfer slow.

## Not instrumented

- Stage 6 as *Central* sees it. Central has no observation of the trade opening
  on the worker; `trade.open.observed` in the worker span is that fact, measured
  on the side that can see it. Comparing the two spans requires the two clients'
  monotonic origins, which are not comparable — only their own deltas are.
- Stage 14, an extra IPC acknowledgement in the accept path. Session 225 removed
  it from the ordinary dispatch and withdrawal paths, so on this path there is
  nothing left to measure. The cautious recovery-return path still has one and is
  not covered here.
- Time inside AO's own client update loop, and time inside `ManagerAccounting`
  beyond the commit begin/end bracket.
