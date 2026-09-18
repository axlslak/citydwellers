# Transaction incident reports

Administrator commands:

- `#dump incidents`: list the latest 20 problem traces, with clickable export commands.
- `#dump incident-…`: export that trace with its explicitly linked transaction, item and recovery evidence.
- Bare `#dump` retains the existing full diagnostic snapshot.
- `#lost` and `#found` entries include an incident-evidence link. Old entries may have no retained trace yet; the command reports that honestly.

Manager automatically creates and refreshes problem reports in `data/incident-dumps/` every 30 seconds (first pass after 15 seconds). This works with syslog disabled. A report is available while recovery is still pending; later records update the same report. No full runtime log is copied into it. Manager must be running for automatic rendering; source evidence remains on disk while it is unavailable.

`data/transaction-traces/` holds the local structured evidence. Sources write records under a cross-AppDomain mutex; Manager alone renders dumps. This uses the existing shared runtime disk and does not depend on the optional bounded syslog IPC queue. Files are not transmitted automatically. The existing Manager authorization for dump remains in effect.

Coverage includes donation start and trade statuses; existing trade ledger events; dispatch queue changes, attempt IDs and failures; receipt preparation, offer confirmation and observed inventory evidence; storage phase transitions; retrieval requests, phases, pickup/timeout/failure states; committed ledger location/provenance changes; lost write-ahead, exclusion and confirmation records; local/global recovery start and application outcomes. Phase changes describe intent; receipt and committed-state records describe observations/results.

Related evidence is joined through explicit transaction, batch, retrieval, ledger and recovery IDs. A multi-batch donation may have several related problem IDs; their exports follow those links. Failed/cancelled standalone trades also retain their own trace. It is not a chronological slice of unrelated bots' output. A recovery report records summaries and relevant item changes rather than hundreds of bag-progress lines.

Lost/found records retain previous/new ledger locations and the reason/evidence reference. Unmatched claims also list same-template current claims as comparison candidates, **not proven matches**. Unknown donors or causes remain unknown. Equal lost/found counts are not treated as proof that the occurrences are identical. Successful reconciliation is recorded separately from establishing why the discrepancy happened.

Limitations:

- Tracing starts after this build is deployed; missing historical steps cannot be reconstructed. Successful operation evidence is retained to explain later discrepancies; only problem traces automatically produce dumps. No automatic evidence deletion/retention policy is introduced.
- Reports state when resolution is unconfirmed. Latest persisted states and explicit completion/application events are authoritative; no synthetic success is inferred from silence.
- Exports cap linked traces at 64 and events at 5,000, reporting truncation. Original trace files remain available. Related history can make a report larger, but it never falls back to the big log.
- Evidence I/O is best effort and cannot cause a physical retry or an audit. Writer contention is bounded at 100 ms per record. Failures attempt to leave `evidence-warning.txt`; an unwritable disk may prevent even that marker. Partial lines and missing linked history are identified in exports.
- Structured evidence includes player names, item details and transaction history. Keep it with other private runtime data; do not commit it to the public repository.

Owner compiles and performs live validation. This implementation was reviewed statically, not live-tested.
