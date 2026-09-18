# Startup census livelock

Diagnosed from the owner's console run of 2026-09-18T12:11:35+03:00 (build
`source-44377b3a2496`) plus source review. No assistant build or live test.

## Symptom

Seven bankers restart their startup bag audit indefinitely, roughly every
100-145 seconds, and never release a census. Completed audits are discarded.
`Kbinfa` finished a clean audit at 12:13:46 (`opened=110 failed=0`,
`BAG AUDIT COMPLETE`) and that work was destroyed four seconds later.

## Root trigger — two workers blocked on ambiguous bags

At 12:12:09, before any audit could finish:

```
[Kbsupp] Census staging waiting: Ambiguous bag (Container:BB49C56) at bank/1, bank/92.
[Kbarty] Census staging waiting: Ambiguous bag (Container:BB49D3F) at inventory/66, inventory/69.
```

This is `StorageBagPolicy` (session 133) refusing ambiguous evidence, which is
correct behavior. `Kbarty` and `Kbsupp` never appear again in the run: they
never start an audit and never recover on their own.

`[VERIFIED]` These duplicates are new. The 2026-09-11 storage baseline
(`data/storage-baseline.json`, runId `20260911-013435-104b6746`) contains zero
duplicate bag identities across all eight workers (artillery 120, infantry 110,
control 110, support 110, extermination 112, spirit 120, dyna 120, phatz 120).
`[OPEN]` The origin of the new aliases is not established here. Session 133
recorded the same limitation; do not assert a cause without fresh evidence.

## The livelock — why healthy bankers never converge

The amplifier is independent of the ambiguous bags and is a code defect.

1. `PhysicalLedgerReconciliation.ReadCensus` rejects a census when
   `FailedCount != 0` or `OpenedCount != TotalBagCount`. **One** failed bag open
   out of 110 invalidates the entire worker census.
2. On that rejection `StartupCensusGate` (around line 430) requeues a fresh
   audit run and, for any non-central role, sets a 60-second
   `_presenceRetryAfter` and **deletes its own `.presence.json`**. The intent is
   recorded in the source comment: *"An unscannable worker is unavailable, not a
   prerequisite that prevents healthy members from starting indefinitely."*
3. But `Coordinate()` supersedes the **entire collecting cycle** as soon as any
   participant loses presence:

   ```csharp
   bool membersPresent = cycle?.Participants != null && cycle.Participants.All(p => Present(p.Key, p.Value));
   if (cycle?.Phase == "collecting" && (!membersPresent || Requested()))
       cycle.Phase = "superseded";
   ```

4. A new cycle is published. Every banker observing a changed cycle id resets
   its audit state and calls `BagAuditAgent.CancelForRecovery()`
   (`StartupCensusGate.cs:396-402`), producing
   `BAG AUDIT FAILED ... Census superseded or client disconnected` on peers
   whose audits were healthy and in some cases already complete.
5. After 60 seconds the stepped-aside worker republishes presence, rejoins,
   audits, fails again, and deletes presence again.

`[INVARIANT VIOLATED]` A worker intending to remove itself from a cycle instead
destroys that cycle for everyone. Stepping aside and superseding are the same
action, so the system cannot make progress while any single worker keeps
failing.

## Escalation signature

Failure counts climb across rounds and then pin to an exact value:

| Worker | bank | inventory | round 1 | later rounds | steady `opened` |
|---|---|---|---|---|---|
| Kbinfa | 92 | 18 | failed=0 (COMPLETE) | 6, 9, 18, 18, 17 | 92 |
| Kbcont | 92 | 18 | failed=1 | 8, 9, 18, 18 | 92 |
| Kbexte | 94 | 18 | failed=0 | 6, 7, 16, 17, 18 | 94 |
| Kbspirit | 102 | 18 | failed=0 | 15, 16 | 102 |
| Kbdyna | 102 | 18 | failed=0 | 13 | 102 |
| Kbphatz | 102 | 18 | failed=0 | 13, 14 | 102 |

`[VERIFIED]` In the steady-state loop `opened` equals `bankBagCount` exactly for
every worker, and `failed` approaches the inventory bag count. Every bank bag
opens; the inventory-side bags do not. The first round does not show this, so it
is a consequence of repeated aborted rounds rather than the initial state.

`[OPEN]` The mechanism is not proven here. `BagAuditAgent.DefaultBagOpenTimeoutMs`
is 3000 ms while session 117 raised the local bag-move timeout to 15000 ms;
whether aborted staging leaves inventory bags unopenable within 3000 ms needs
the per-run result files under `data/startup-census/` to confirm. Those were not
in the supplied snapshot.

## Not the cause

- The `ArraySerializer` `OutOfMemoryException` on `Apcmanager` at 12:11:38 is the
  longstanding HQ packet variant. Session 122 recorded it as non-blocking;
  Manager continued and reached InPlay. Do not treat it as heap exhaustion.
- `WARNING: Alien file in settings/runtime root: 'data.zip'` is an owner-created
  archive, not a runtime file.
- `SMALL BACKPACK SHORTAGE Kbspirit: have=120; need=156; missing=36` is a real
  capacity warning but does not block the audit and is not part of this loop.

## Fix — session 137

`[IMPLEMENTED]` Source published; owner builds and live-tests.

**Withdrawal is no longer supersession.** `Coordinate()` previously replaced the
cycle whenever any participant lost presence. It now drops that member from
`cycle.Participants` and keeps the same cycle id. A banker only retires its own
audit when it observes a *changed* cycle id (`StartupCensusGate.cs:396-402`), and
only acts on a cycle that `Includes()` it, so mutating the participant set
withdraws one member without disturbing anyone else.

Guards retained:

- An explicit recovery request (`Requested()`) still supersedes. That is a
  deliberate instruction, not an absence.
- A member that already wrote a census for the cycle is **not** withdrawn; its
  physical evidence stands and the existing release check applies.
- Central is mandatory. If Central would be dropped, or nothing would remain,
  the cycle is superseded as before — `CensusApplication.Apply` requires Central
  and would otherwise throw.

**Why a smaller roster is safe.** `CensusApplication.Apply` scopes reconciliation
to `censuses.Select(c => c.Character)`. Anchors outside that scope are skipped
rather than reclassified, and `bundle.Storage.Workers` carries every unaudited
worker's previous state forward unchanged. A cycle that loses a member is
therefore the same condition as one created while that member was offline, which
is already normal: cycles are built from `online` members only. Fail-closed
behavior is untouched — `ReadCensus` still rejects any incomplete census, and an
incomplete census is still never applied.

**Escalating rejection backoff.** A worker whose census is rejected used to
delete its presence for a fixed 60 seconds, then rejoin, fail, and force another
fresh cycle — re-auditing the entire roster every minute. The backoff now
escalates 60s, 120s, 240s, 480s, 960s and resets when the worker completes a
census or reconnects. Each retry is logged with its consecutive rejection count.

**Expected behavior after this change.** In the 12:11 run, the six healthy
bankers would complete and release while `Kbarty` and `Kbsupp` stay withdrawn.
`Kbinfa`'s clean 12:13:46 audit would have been kept rather than discarded.

The two ambiguous bags still need owner action; this fix stops them from taking
the rest of the roster down, it does not resolve them.

## The two ambiguous bags — evidence

`[VERIFIED]` From the owner's `data/storage-state.json`
(`UpdatedUtc 2026-09-18T08:38:05Z`), bag counts against the 2026-09-11 baseline:

| Character | role | bags | baseline | diff | duplicate |
|---|---|---|---|---|---|
| Kbarty | artillery | 121 | 120 | **+1** | `(Container:BB49D3F)` at inventory/66 and inventory/69 |
| Kbsupp | support | 111 | 110 | **+1** | `(Container:BB49C56)` at bank/92 and bank/1 |
| Kbinfa, Kbcont, Kbexte, Kbphatz, Kbdyna, Kbspirit | — | — | — | 0 | none |

Only the two blocked bankers carry an extra bag, and only they have a repeated
identity. The other six match their baseline exactly.

`[VERIFIED]` Both duplicates are repeated observations of one physical bag, not
two bags:

- Kbarty's two entries share **the same handle (316)** and hold an identical set
  of 21 items in identical inner slots.
- Kbsupp's two entries have different handles (114 and 296) but an identical set
  of 9 items. This matches the session 133 note that duplicate containers can
  present different outer addresses.

No item multiset is doubled by this; the duplication is in the observation, not
in physical stock.

`[VERIFIED]` It is not a transient artifact of one session. The persisted state
observed at 08:15/08:38 carries the same identities at the same slots that the
12:11 run reported live. **Restarting the host does not clear it** — the
condition reappears from live observation each session.

Owner action: log the affected character in with the ordinary AO client and
physically move the bag (to a different slot, or into the bank and back), which
forces a fresh server-side observation. `StartupCensusGate` re-evaluates a
staging block when `InventoryLayout()` changes, so the banker retries on its own
afterwards. Do not delete data files to clear the hold.
