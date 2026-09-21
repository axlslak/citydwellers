# Existing database storage and cleanup

The database still exists. Preserve it; do not reset it or reconstruct accounting
from physical inventory. DataMigration is retired and is no longer built or used.
Do not run an old DataMigration executable against schema 3.

Build and deploy the matching host and plugins, then restart normally. Existing
completed schema 1/2 imports are admitted and upgraded to schema 3. Missing or
incomplete databases stop with an error rather than silently creating empty state.
The runtime still requires its exclusive database lease.

The host schedules cleanup ten seconds after startup, retrying hourly. It removes
obsolete SQL diagnostic documents in batches of twenty and drops the redundant
`cd_migration_chunks`, `cd_migration_files`, and `cd_migration_runs` tables.
Completed imports no longer depend on original-source deletion. There is no
replacement archive, copy, checksum pass, or replay. Original disk sources and
backups are not deleted. Partial cleanup is safe to retry.

Cleanup preserves relational ledger and stock rows, donor and recipient history,
custody transactions, lost/found, settings, pending tells and current-host census
coordination. Obsolete `ledger.json` and `current-stock.json` copies are removed
only after the relational upgrade succeeds. The explicit diagnostic allowlist
includes transaction traces, service-event copies, incident dumps, navigation
traces, old logs, old-host census documents and tell acknowledgements older than
one day. Meaningful `history/` records and cloak event history are retained.

The catalogue, runtime log and requested diagnostic dumps remain on disk as
previously authorized. A physical `data` folder is valid. Automatic incident
snapshots/exports and duplicate service-event persistence are disabled; optional
syslog forwarding remains. No new disk service-event archive replaces SQL copies.

## Remaining work and verification boundary

The generic live SQL document/chunk backend still serves other business state.
This change removes the retired import filesystem and diagnostic amplification;
it does not claim that every live document has been converted to relational rows.

`mysql-diagnostics.sql` contains read-only storage queries. Dropping archive
tables removes those tables; deleting rows in surviving InnoDB tables may leave
allocated space available for reuse rather than immediately shrink their files.
No blocking table rebuild or live SQL operation is performed by this source change.
Compilation and live runtime validation remain owner-run.
