# Business storage (schema 5)

The host Manager service owns one MySQL connection with pooling disabled. Client
plugins do not contain the connector. Manager loads all durable business state at
startup and answers ordinary requests from RAM through in-process IPC. It writes
only changed business rows, in one transaction for a compound accounting change.
There is no SQL polling, advisory lock, document store or checksum gate.

## Startup conversion

Normal startup performs conversion automatically; DataMigration is retired.
Existing relational ledger and stock columns are authoritative. The importer reads
legacy documents once for transaction/item history, lost/found, cloak events and
unfinished custody. It preserves donor/taker and location metadata; it never
reconstructs ownership from inventory. Settings are exported to native config/
without overwriting existing files. The catalogue remains data/items.json.

Business rows and schema version 5 commit together before cleanup. Existing schema-4 installations are upgraded in place: the host creates the alt tables, imports legacy `config/alts.json` once, commits that state with the version change, and never uses the file as a runtime backend again. Only then does
startup drop cd_event_lines, cd_document_chunks, cd_documents, cd_directories,
cd_migration_chunks, cd_migration_files, cd_migration_runs, cd_ledger_items,
cd_stock_items, cd_banker_state and cd_meta. Interrupted cleanup can repeat without
reimporting stale data. Original disk source/backup files are never deleted.
Pending legacy tells/channels transfer into RAM during this first startup; they
are transient thereafter, as are health, availability and coordination.

## Contents and ownership

SQL retains relational ledger/stock and storage locations, transaction/item
history (including lost/found), cloak state/events, Manager alt identity state,
and the minimal state needed for unfinished transfers/withdrawals. Completed custody snapshots are removed;
meaningful history is retained. Runtime logs, requested dumps, tell queues,
positions and service control are not SQL entities.

The fixed entity mapping in executables/CityDwellers/Storage/BusinessTables.cs is
the authoritative schema definition. Every mapped business field has an ordinary
SQL column. record_id, parent_id and position identify and relate rows; there are
no serialized document payloads or hashes. Date/time columns hold UTC timestamps.
Operator edits to business columns are read on the next host startup; the running
Manager continues to own its current RAM state. Parent relationships must remain
valid when editing related rows.

Bootstrap connection settings remain in citydwellers.json under MySql (Host,
Port, Database, User, Password, SslMode). No server-brand rejection is imposed.
SQL failures during persistence stop the host; an indeterminate commit is never
replayed automatically. Reconnection occurs only when a real write needs it.

Native config/ contains administrator settings, policy, memberships, item-pair
evidence and buffer settings. data/ may contain the catalogue, normal logs and
explicit diagnostic output. Its existence does not reject startup.

## Validation boundary

The implementation received source and project-structure review only. No build,
automated tests, live SQL conversion or AO run was performed in this Work session,
following the owner boundary. mysql-diagnostics.sql contains read-only inspection
queries; mysql-schema.sql locates the runtime schema and inspects its definitions.
