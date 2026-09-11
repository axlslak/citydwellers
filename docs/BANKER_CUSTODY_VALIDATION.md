# Banker custody safety validation

Status: implementation candidate, not approved for production. Keep the owner's
service stopped. Compile first; no data is to be deleted to bypass a safety hold.

## Implemented boundaries

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
