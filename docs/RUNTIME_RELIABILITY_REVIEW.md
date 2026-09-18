# Runtime reliability review

Session 133 reviewed the owner's full current log (61,238 lines), retained
previous log (173,527 lines), and current data snapshot. The owner reported
manual in-game installation of personal bank terminals and some bag/item
movement the previous day, but no manual data-file changes. Private evidence
and credentials are not included here. The supplied snapshot remains intact.

## What the evidence establishes

| Area | Finding |
| --- | --- |
| Reconnect | All nine bankers reached InPlay after the later simultaneous disconnect. The native bank cache then contained doubled bank-bag counts. |
| Census | Seven nonempty workers rejected duplicate item addresses and repeatedly retried. An empty worker accepted duplicated bags because the old validator checked only item addresses. |
| Current accounting | Two container identities each appear at two locations, with identical contents. The latest application replaced 30 original claims with 60 found claims. This inflated stock by 30 and displaced original attribution. |
| Ledger versus stock | Both contain 2,638 matching records. Their agreement includes the duplicate-container error and is not independent physical proof. |
| Custody receipts | All 682 applied receipt endpoints conserve their recorded before/expected/observed item multisets. Six other traces are historical interrupted/recovery evidence, not current pending transfers. |
| Withdrawals | All 44 retained requests are terminal: 32 completed, seven expired, five reconciled. Completed delivery IDs are absent from active stock. |
| Tell queue | All 7,748 retained acknowledgements report success; no pending, assigned or failed jobs remain in the snapshot. The observed exceptions concern heartbeat publication. |
| Shutdown | Long-running domains fail plugin teardown through expired remoting proxies. Shutdown was already underway; the original initiating stop source was not recorded. |

These findings do not establish physical item destruction. A repeated
container observation must not be counted as two physical bags. Nor does an
unmatched claim alone prove its item disappeared.

## Changes

- Replace the native bank list before a complete local-character bank snapshot
  is appended. Preserve the Bank object and its event handlers. Unsupported
  dependency shapes block physical readiness instead of continuing silently.
- Validate bag identities and outer locations before startup staging, before
  audit movement, and before reconciliation. Empty bags are covered. Refuse
  ambiguous evidence instead of selecting or deleting an arbitrary duplicate.
- Verify both ends of bag movement and require a fresh container response
  after requesting contents. A cached open handle is not fresh evidence.
- Label failed, canceled and incomplete audits accurately.
- Sponsor client-domain proxies throughout their lifetime and dispose every
  created domain, including partial startup. Retain a direct domain-unload
  fallback, with an explicit warning if plugin cleanup could not run.
- Record console/service stop sources and distinguish shutdown errors from
  earlier component failures.
- Read snapshots with replacement sharing and publish without deleting the
  previous destination on a sharing error. Bound retries to publication only.
  Isolate heartbeat failure from tell processing; retain delivery semantics.
- Use the same durable dispatch-queue check before and during a donation.
- Recognize current owned runtime files and retained migration/temporary
  evidence in startup inventory diagnostics.
- Bound the observed HQ array allocation in the deployed dependency copy.
  `PlayfieldDynelInfo` contains five 32-bit wire fields, so its count cannot
  exceed the remaining packet bytes divided by 20. The build patch validates
  the exact type/method layout and its own repeat-build marker/body.

## Limits and next live evidence

The existing snapshot is not repaired by publishing code. The next startup
must obtain fresh game evidence. The new checks may hold an affected banker
unavailable if its cache is still ambiguous; that is preferable to applying
another false census. The initial cause of the later, fresh-process bag
aliases is not established by the retained successful-packet evidence.
Previously displaced donor attribution is not automatically restored.

The packet allocation bound prevents the demonstrated impossible allocation;
it does not decode the unknown HQ variant. It covers the direct array
serializer path, not its generated-expression path. Buddy
`SimpleCharFullUpdate` trailing-schema errors remain unresolved. The two
missing nano definitions (254847/254848) are absent from both the inspected
native index and the supplied item definitions; references to them do not
provide replacement metadata. No decoder padding or invented definitions
were introduced.

Validation was source-only: independent read-only reviews, dependency API and
event-order checks, emitted-IL reasoning, project XML/source inclusion, and
whitespace checks. No builds, test suites or live AO sessions were run. The
owner builds and validates the resulting deployment. Full rebuilt output is
needed to apply the dependency allocation guard.

Manager, banker and buffer chat connections, city-buddy game-only login,
existing tell scheduling, CRU policies and transaction verification remain
the established design. The owner-authored in-game changelog has no new entry.
