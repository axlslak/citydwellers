## Existing installation: relational ledger and stock upgrade (session199)

Rebuild/deploy matching host and plugin assemblies, then start CityDwellers normally.
Do **not** rerun DataMigration, reset tables, or recreate the data directory.
Before AO starts, the host takes its exclusive lease, creates `cd_ledger_items`,
`cd_stock_items` and `cd_banker_state`, and imports the existing SQL ledger/stock
once. Every represented field, item ordering, nullable value and timestamp is
read back and compared inside the import transaction before it is committed.
Source documents and immutable migration archives remain preserved. Subsequent
starts use the relational state and cannot overwrite it from those old sources.
Schema version2 prevents old version1 binaries from silently writing stale copies.
The database user needs CREATE permission for this additive upgrade.

The active ledger and stock now store one item per relational row, with typed
columns and indexes on item template, transaction, banker/location and ledger
ID/donor. Runtime writes apply changed rows and explicit removals within the
existing custody transaction. Worker recovery reads only that banker's rows via
the character index. No paths or document chunks are involved in live ledger/stock
reads or writes. Legacy DTO adapters are in memory only; no JSON item payloads
are stored in these tables. Other state types retain their current storage for
now; this is not a claim that the entire backend has been normalized.

Old event logs are no longer automatically replayed on startup. Live operation
accounting remains enabled. See `mysql-schema.sql` for all columns/indexes and
`mysql-diagnostics.sql` for authoritative live-state queries.

# MySQL is the City Dwellers backend

All mutable bot state, alts, stock, donations/history, queues, snapshots, runtime
logs, incident evidence and diagnostic dumps belong in MySQL. There is no local
`data` directory, replacement log folder, disk spool, or offline fallback after
conversion. SQL failure stops the runtime. The root `citydwellers.json` remains
the read-only administrator bootstrap; executable dependencies, GameData and
NavMeshes remain installation assets.

The offline **DataMigration.exe** replaces the retired LoginTry probe. It does
not load AO or log in characters. Build it with the solution or its project;
Debug and Release produce the utility beside CityDwellers.exe. It targets .NET
Framework 4.8/x86, as the host does. The pinned
[MySqlConnector 2.3.7 package](https://www.nuget.org/packages/MySqlConnector/2.3.7)
includes a .NET Framework 4.8 target.

## Updating an already-open Visual Studio solution

This update replaces the LoginTry project with DataMigration and adds SQL sources
and dependencies to every runtime project. After copying/pulling the updated
source, close and reopen `citydwellers.sln` so Visual Studio refreshes the project
graph and references. Restore NuGet packages before rebuilding.

If Visual Studio reports `Project unavailable` during restore, close the solution
and run this from a Visual Studio Developer Command Prompt in the repository root:

```text
msbuild citydwellers.sln /t:Restore /p:RestoreForce=true /p:Configuration=Release
```

This restores packages without compiling or starting any bot. Resolve any actual
restore error it prints before reopening and rebuilding the solution. Missing
`MySqlConnector`/`Newtonsoft` types following a failed restore are not evidence of
a SQL server problem; no database connection is needed to build. Do not delete
runtime `data` to troubleshoot a build. Only the verified migration removes it.

## Connection and first conversion

Use **MySQL 8.0 or newer** and an existing database. This schema is not a MariaDB
schema. The utility creates its own tables and indexes; the database/account
itself must already exist. The server's `max_allowed_packet` must be at least
32 MiB; connection initialization checks this before importing. Give the
configured account SELECT, INSERT, UPDATE,
DELETE, CREATE and REFERENCES on that database (INDEX/ALTER are useful for future
explicit schema upgrades). No database-server administration or file privileges
are used. Use a dedicated database for this bot installation.

Merge this section into the existing root `citydwellers.json`; keep its other
sections:

```json
"MySql": {
  "Host": "your-sql-host",
  "User": "your-sql-user",
  "Password": "your-sql-password",
  "Database": "your-sql-database",
  "Port": 3306,
  "SslMode": "Required"
}
```

The four connection values are mandatory. Port defaults to 3306 and SslMode to
Required. No credentials belong in Git or command-line arguments. SslMode may
be set to the appropriate MySqlConnector mode for your server; VerifyFull also
checks the certificate identity. The utility never prints the connection string.

1. Stop **all** bots, old executables and services that could write the original
   installation. Keep the source unchanged until conversion finishes. The SQL
   lease excludes updated runtimes and other migration utilities; an older
   executable does not know about that lease.
2. Deploy the newly built utility and dependencies beside the existing
   `citydwellers.json` and `data` folder. Keep the original source in place.
3. Run from a console:

   ```text
   DataMigration.exe
   ```

   The default source is `data` beside **the utility**, independent of the shell's
   current directory. To target a different installation explicitly:

   ```text
   DataMigration.exe --root "C:\CityDwellers\release"
   ```

4. Wait for the process to return with exit code 0. Migration and verification
   now print **only failures**; there is no success banner or per-file progress.
   In Windows Command Prompt, run `echo %ERRORLEVEL%` immediately afterward
   (PowerShell: `$LASTEXITCODE`). Zero means verification and source cleanup
   completed; start CityDwellers normally. Failure exits 1; cancellation exits
   130. A quiet process that is still running is not a completed migration.

For an intentionally empty/new installation, use `DataMigration.exe --empty`.
This is also required when the source contains only empty directories. An absent
or empty source without that flag is treated as a possible wrong installation
path, not permission to silently declare a successful empty migration. Existing
empty directory names are preserved as SQL directory metadata.

On Mono, use `mono DataMigration.exe` with the same options. Deployment symlinks
above `data` are allowed. Symbolic links/junctions **inside the source tree** are
rejected so the importer cannot silently skip data or follow paths outside it.

## What is migrated and what a retry means

Every ordinary file anywhere below `data` is included, with no extension
allowlist: unknown files, old logs, dumps, caches, alts, backups, temporary files,
malformed JSON, binary content, hidden files and zero-byte files. The original
relative name, timestamp, byte count and SHA-256 are retained. Case-insensitive
path collisions are rejected. There is no maximum file-size assumption based
on process memory: source and SQL readback are streamed in bounded chunks.

The stages are:

1. Read and hash the complete source inventory, bind the run to that installation
   and persist the immutable inventory in SQL.
2. For each file, commit its runtime document, original archive chunks and
   completion flag in **one transaction**. If a file import fails, that file's
   partial writes roll back. Completed files are reused on retry.
3. Read every original archive and initial runtime document back from SQL and
   verify SHA-256. Re-enumerate and rehash the local source. Missing, changed or
   newly appearing files stop sealing; they are never silently omitted.
4. Seal the successful import in SQL. From this point, no invocation can replay
   the old source over runtime records.
5. Verify each surviving source against its immutable SQL archive, remove only
   matching originals, then remove empty source directories. Mark cleanup
   complete in SQL. Runtime startup requires the completed conversion and no
   physical data path.

If anything fails, the console identifies the file/operation and exits nonzero.
The run keeps its manifest and last error in SQL when SQL is reachable. Run the
**same command again** after fixing the reported cause. Ctrl+C stops at a safe
operation boundary; it may wait for a large current file/hash to finish. Do not
manually remove the source to bypass a failed migration.

Before sealing, changed input requires restoring the originally inventoried
bytes or deliberately starting over in a fresh database; rerunning cannot
silently replace the first inventory. Once sealed, reruns only verify the
immutable archive and finish source cleanup. They never restore old stock,
queues, histories or balances over evolved runtime records. Already removed
source files are accepted on a sealed cleanup retry. A new file at an already
cleaned path or any unexpected file is retained and reported.

Windows cleanup hashes and marks for deletion using the same open file handle,
with writes/renames excluded. Mono/POSIX cleanup reopens and verifies the named
file again before unlinking, under the explicit offline/no-source-writers
premise. Keep administrative edits and old bot processes stopped on both
platforms. Permission/read-only/deletion failures leave the affected source for
a retry; there is no recursive forced deletion.

The immutable originals remain in `cd_migration_files`/`cd_migration_chunks` after
cleanup, independently of live documents. They are historical recovery evidence,
not a live-backend fallback. Include them in normal **server-side** MySQL backups.

## Repeating a fresh offline trial

For a retry after a failure, keep the SQL tables and rerun the same command to
resume. For a deliberate start from zero, keep all bots and migration processes
stopped, restore the **original complete `data` backup** beside the utility, then
drop all eight City Dwellers tables in the dedicated migration database. The next
normal `DataMigration.exe` run recreates tables/indexes and imports from scratch.
The utility does not automatically drop tables on an ordinary rerun.

A successful migration removed its input folder. Keep the original backup outside
the installation throughout these trials: dropping the tables also deletes the
SQL archive. Do not use `--empty` as a substitute for restoring the original data.
Drop the entire set, including `cd_meta` and migration tables, so a previous seal
cannot survive a reset. In child-before-parent order:

```sql
DROP TABLE IF EXISTS cd_migration_chunks;
DROP TABLE IF EXISTS cd_migration_files;
DROP TABLE IF EXISTS cd_migration_runs;
DROP TABLE IF EXISTS cd_event_lines;
DROP TABLE IF EXISTS cd_document_chunks;
DROP TABLE IF EXISTS cd_documents;
DROP TABLE IF EXISTS cd_directories;
DROP TABLE IF EXISTS cd_meta;
```

## Inspection and debugging

With bots stopped, these commands use the same configured database/lease:

```text
DataMigration.exe --schema
DataMigration.exe --verify
```

`--schema` prints **every actual table definition and every index column** using
SHOW CREATE TABLE and SHOW INDEX, including names, uniqueness, ordering, prefix
lengths and the server's current metadata. It can create the schema on an empty
database but does not declare migration complete. `--verify` rehashes the sealed
immutable archive without importing, deleting or comparing evolved live state
with old source. It exits nonzero on missing/corrupted archive bytes.

See [mysql-schema.sql](mysql-schema.sql) for the complete schema and
[mysql-diagnostics.sql](mysql-diagnostics.sql) for ready-to-edit read-only queries
covering every index, migration progress/errors, source hashes, latest actor
events, transaction timelines, current donor stock and withdrawal orders.

| SQL object | Purpose |
| --- | --- |
| `cd_meta` | Schema version and migration/startup gates. |
| `cd_directories` | Logical names/empty directories, stored only in SQL. |
| `cd_documents` | Current logical documents, byte counts and optional queryable JSON. |
| `cd_document_chunks` | Exact current bytes, ordered by document/chunk and indexed by byte offset. |
| `cd_event_lines` | Queryable line evidence with time, actor, event, transaction and problem indexes. |
| `cd_migration_runs` | Immutable source binding, seal, progress and failure status. |
| `cd_migration_files` | Per-source identity, size/hash, archive and deletion status. |
| `cd_migration_chunks` | Exact immutable pre-migration bytes. |

Logical paths such as `ledger.json` or `logs/...` are SQL keys, not disk files.
JSON projection is a debugging aid; exact bytes remain authoritative. Whole JSON
projections are bounded to 16 MiB and line previews to 256 KiB. Binary, malformed
or oversized documents/lines remain archived even when a complete JSON
projection is unavailable. Inspect `json_projection`/`truncated` and the original
chunk metadata rather than assuming an absent JSON projection means absent data.

SQL JSON queries are for inspection. Directly editing stock/order/accounting JSON
bypasses the bot's transaction and physical-item checks and is not an operational
workflow. SQL backup/retention is an administrator responsibility; the bot never
quietly prunes the immutable migration archive.

## Validation boundary

This change was reviewed statically. No assistant build, automated test suite,
live SQL import or AO run was performed. The owner runs the build and migration;
successful code review is not evidence that their database has already received
the source files.
