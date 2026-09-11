# Banker custody safety validation

Status: implementation candidate, not approved for production. Keep the owner's
service stopped. Compile first; no data is to be deleted to bypass a safety hold.

## Revised IPC and service isolation checkpoint

The owner superseded global discrepancy shutdowns; the original gate description
below records an intermediate implementation that still needs replacement.

- Shared named-pipe transport now serves Manager/Flipper/Buddies and banker dispatch.
  Banker reservations are evaluated on the AO update thread, expire after 15 seconds,
  and are acknowledged before a trade opens. An acknowledgment older than 10 seconds
  is not reused. Busy/unreachable destinations get a 3-second retry interval.
- Primary storage outcomes are returned over IPC, correlated by batch and checked
  for expected/stored count consistency. Files remain durable compatibility records;
  other legacy recovery paths and withdrawals have not all migrated to IPC.
- Internal offer staging/acceptance, confirmation, and exact post-trade inventory
  observations use 500 ms settling intervals measured with Stopwatch.
- Sender verification frees Central to trade with another worker while storage
  completes. A destination with an outstanding storage acknowledgment receives no
  further batch. Other destinations and donations with room may proceed.
- Component exit monitoring leaves healthy host services running. To run only
  Manager, Flipper and Buddies after compilation, set top-level BankersEnabled to
  false in citydwellers.json. Default remains true. This does not enable banker use.
- Owner validation must include busy destination plus another queued destination,
  expired preparation, lost IPC reply, delayed worker storage, duplicate callbacks,
  and normal cloak/Buddies requests over the common transport.

Static diff/project-source checks only for this checkpoint; no assistant compilation
or test suite. Physical-ledger cleanup and local census/routing recovery remain open.

## Implemented boundaries

- Donation receipt, outgoing dispatch and verified overcap accounting now commit
  directly in the action path; the coordinator no longer consumes logs to perform
  these mutations. Dispatch preparation persists selected ledger IDs, and a delayed
  Central confirmation does not undo already-observed worker storage. Existing
  slot matches are reserved before assigning unmatched ledger occurrences.
  Owner confirmed the preceding candidate compiled; this accounting revision has
  only received static review, not compilation or live validation.

- Every configured banker, including Central, runs a staged full census before
  operational initialization. Process-generation paths prevent an earlier process's
  readiness files from authorizing a new process.
- Both loose inventory and loose bank items are retained with all bag contents.
  Expected worker state, observed results, and comparison findings are archived.
- Every worker must match its persisted bag identities and normalized slot/template
  multiset. Missing/unexpected bags, content differences, incomplete reads, managed
  items outside storage, and unresolved custody prevent startup release.
- Operational initialization and update/trade handlers wait for the census gate.
  Disconnect invalidates the generation. Reconnection gathers another census but
  does not resume in-flight work automatically.
- Donation, internal dispatch, withdrawal transfer, and pickup receipt processing
  use before/after normal-inventory multisets keyed by AOID/high-ID/QL. Finished
  alone does not apply accounting or begin worker storage.
- Prepared intent, validated donation offer, Finished, physical observations and
  application are retained as separate flushed JSON records under custody-transactions.
  Failure to apply verified custody closes the gate rather than replaying the action.
- Cancelled trades must restore the prior inventory multiset before another trade.
- Donor GET availability consumes a matching physical-stock occurrence once.
  Historical attribution rows are preserved, not silently deduplicated or deleted.

## Owner validation sequence (after compilation and explicit clearance)

Use a backed-up, controlled data set and owner's items. Do not test by destroying
the production baseline. Existing unresolved custody intentionally blocks release.

| Scenario | Required outcome |
|---|---|
| Cold startup | Nine full censuses before operational initialization |
| Unreadable bag or missing inventory census | No readiness or dispatch |
| Moved bag | Match stable identity; retain observed location for layout reconciliation |
| Extra/missing bag content | Preserve both snapshots; no baseline replacement |
| Donation with one item | Verified inventory gain before donation disposition |
| Identical copies; different QLs | Exact multiplicity and AOID/high-ID/QL accounting |
| Retracted offer | Final expected inventory gain must match actual gain |
| Internal dispatch | Sender decrement and receiver increment independently evidenced |
| Cancel/timeout | Verify return to prior counts; otherwise safety hold |
| Withdrawal and pickup | Verify physical gain/decrement before receipt/delivery accounting |
| Write failure after AO action | No further action; durable evidence retained |
| Disconnect | All operational gates close; recensus does not automatically resume |
| Restart with unfinished custody journal | Collect census, retain unresolved hold |

## Remaining limitations

- No assistant-side compilation or live AO test was performed, per owner policy.
- This does not implement automatic reconciliation/replay of interrupted custody
  journals. Those stop for evidence review.
- Historical duplicate ledger claims and donor attribution are not repaired here.
  Physical availability is constrained, but historical donor totals may remain inflated.
- The old Vital custody hold is retained. Its cause has not been proven.
- Live timing, repeated callbacks, plugin initialization order, and shared-directory
  failure behavior require owner validation before any public release.
