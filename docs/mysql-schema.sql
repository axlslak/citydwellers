-- City Dwellers mandatory MySQL schema v2; definitions in shared/SqlSchema.cs and shared/BankerSqlStore.cs.
-- MySQL >= 8.0; InnoDB; utf8mb4; max_allowed_packet >= 33554432 (32 MiB).
-- DataMigration creates these tables. Do not hand-set migration completion keys.
-- No root data directory is used at runtime; path columns are canonical logical keys.
-- Paths use invariant lowercase and / separators, max 640 chars, no traversal.
-- All DATETIME values are UTC with microsecond precision; source-byte hashes are SHA-256.
-- SQL credentials stay in the administrator-owned root citydwellers.json.
--
-- TABLE / INDEX CATALOGUE (all explicit indexes):
-- cd_meta PRIMARY(meta_key): schema_version, active_run_id, completed_run_id,
--   cleanup_completed_run_id. The final two must agree before a runtime can start.
-- cd_directories PRIMARY(path): retains empty namespaces and parent directories.
-- cd_documents PRIMARY(document_id): stable content identity during overwrite;
--   ux_documents_path(path): unique case-normalized path lookup;
--   ix_documents_modified(modified_utc,document_id): newest state/dump selection;
--   ix_documents_projection(json_projection,document_id): projection gap discovery.
--   json_payload holds valid complete JSON <=16 MiB raw, not the original encoding.
--   json_projection is valid/invalid/oversized/not-json; raw chunks are authoritative.
--   line_projection is utf8-lines/unsupported-bom/not-lines. UTF16/32 BOM logs are
--   preserved exactly and available through OpenRead, but are not falsely indexed as UTF8.
--   pending_line* tracks at most256KiB of an unfinished line for O(1) append indexing.
-- cd_document_chunks PRIMARY(document_id,chunk_no): ordered exact-byte content;
--   ux_document_chunks_offset(document_id,byte_offset): seek and bounded streaming.
--   Imports/replacements use chunks <=256KiB. Runtime appends target16KiB chunks,
--   extending only the final chunk then allocating as needed to bound redo amplification.
-- cd_event_lines PRIMARY(document_id,line_no): per-stream ordering incl current partial line;
--   ix_event_lines_time(occurred_utc,document_id,line_no): source occurrence chronology;
--   ix_event_lines_recorded(recorded_utc,document_id,line_no): importer/runtime observation;
--   ix_event_lines_actor(actor,occurred_utc): bot/actor investigations;
--   ix_event_lines_event(event_name,occurred_utc): stage/type investigations;
--   ix_event_lines_transaction(transaction_id,occurred_utc): custody/trace linkage;
--   ix_event_lines_problem(problem,occurred_utc): explicit problem or severity warning/error/fatal.
--   raw_text is best-effort UTF8, bounded to256KiB bytes per line; truncated=1 flags longer
--   lines. Invalid UTF8 may show replacement characters here, never in content chunks.
--   json_payload is valid line JSON when complete and untruncated; invalid lines still persist.
--   actor/event/transaction are bounded191-char query hints; full values stay in JSON/raw chunks.
--   occurred_utc may be NULL when a source has no parseable timestamp: use recorded_utc.
-- cd_migration_runs PRIMARY(run_id): stable UUID retry identity and inventory/seal/error;
--   ix_migration_runs_completed(completed_utc,started_utc): run status/history.
-- cd_migration_files PRIMARY(migration_file_id): immutable inventory/archive identity;
--   ux_migration_files_run_path(run_id,path): one original file per case-normalized key;
--   ix_migration_files_progress(run_id,archived,source_deleted): resume/cleanup progress;
--   ix_migration_files_sha256(sha256): original-content lookup and dedup investigation.
--   original_path preserves source case. Staging saves full length/hash inventory before import.
-- cd_migration_chunks PRIMARY(migration_file_id,chunk_no): immutable original bytes;
--   ux_migration_chunks_offset(migration_file_id,byte_offset): archived seek/order;
--   chunks are never mutated after import, even if the live document evolves or is deleted.
--
-- FOREIGN KEYS: document chunks/event lines CASCADE on live document deletion. Archive
-- run/file relationships have no cascade: mutable live deletion cannot remove originals.
-- FK supporting indexes are already covered by the listed leftmost index columns.
--
-- TRANSACTIONS: ordinary writes and WithLock scopes use one reentrant connection and
-- READ COMMITTED transaction under a database-wide advisory writer lease. This prevents
-- lock-order inversions across custody/stock/history without InnoDB gap-locking console streams.
-- Nested failure marks the transaction rollback-only. Commit failures stop the runtime.
-- citydwellers.log and service-events/source-*.jsonl use independent per-document row
-- transactions and are append-only in runtime, preventing cross-AppDomain log deadlocks.
-- Standalone reads use a bounded256KiB repeatable-read streaming snapshot; ordinary reads
-- inside a writer scope share that transaction because all ordinary writers are serialized.
-- Runtime and migration share one exclusive process lease (nonpooled connection, 5s heartbeat).
-- Loss of persistence/lease stops runtime without a crash dump or local fallback.
-- Migration heartbeat failure is surfaced at the next operation/before commit, leaving retries safe.
--
-- The archive/live file import, chunks, indexes, and archived=1 flip commit together per file.
-- A migration seal requires every immutable inventory file archived. The utility separately
-- reads back SHA-256 for live+archive content before sealing; then verifies originals again
-- before removal, and finally writes cleanup_completed_run_id only after data is absent.
-- A sealed rerun verifies the immutable archive, never overwrites evolving live state.

CREATE TABLE IF NOT EXISTS cd_meta (
                meta_key VARCHAR(96) CHARACTER SET ascii COLLATE ascii_bin NOT NULL PRIMARY KEY,
                meta_value LONGTEXT NOT NULL,
                updated_utc DATETIME(6) NOT NULL
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_bin;

CREATE TABLE IF NOT EXISTS cd_directories (
                path VARCHAR(640) NOT NULL PRIMARY KEY,
                created_utc DATETIME(6) NOT NULL
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_bin;

CREATE TABLE IF NOT EXISTS cd_documents (
                document_id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
                path VARCHAR(640) NOT NULL,
                byte_length BIGINT NOT NULL DEFAULT 0,
                next_chunk BIGINT NOT NULL DEFAULT 0,
                created_utc DATETIME(6) NOT NULL,
                modified_utc DATETIME(6) NOT NULL,
                json_payload JSON NULL,
                json_projection VARCHAR(32) NOT NULL DEFAULT 'not-json',
                line_projection VARCHAR(32) NOT NULL DEFAULT 'not-lines',
                next_line BIGINT NOT NULL DEFAULT 1,
                pending_line LONGBLOB NULL,
                pending_line_offset BIGINT NOT NULL DEFAULT 0,
                pending_line_length BIGINT NOT NULL DEFAULT 0,
                UNIQUE KEY ux_documents_path (path),
                KEY ix_documents_modified (modified_utc,document_id),
                KEY ix_documents_projection (json_projection,document_id)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_bin;

CREATE TABLE IF NOT EXISTS cd_document_chunks (
                document_id BIGINT NOT NULL,
                chunk_no BIGINT NOT NULL,
                byte_offset BIGINT NOT NULL,
                content MEDIUMBLOB NOT NULL,
                PRIMARY KEY (document_id,chunk_no),
                UNIQUE KEY ux_document_chunks_offset (document_id,byte_offset),
                CONSTRAINT fk_document_chunks_document FOREIGN KEY (document_id)
                    REFERENCES cd_documents(document_id) ON DELETE CASCADE
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_bin;

CREATE TABLE IF NOT EXISTS cd_event_lines (
                document_id BIGINT NOT NULL,
                line_no BIGINT NOT NULL,
                byte_offset BIGINT NOT NULL,
                byte_length BIGINT NOT NULL,
                occurred_utc DATETIME(6) NULL,
                recorded_utc DATETIME(6) NOT NULL,
                actor VARCHAR(191) NULL,
                event_name VARCHAR(191) NULL,
                transaction_id VARCHAR(191) NULL,
                problem TINYINT(1) NULL,
                raw_text MEDIUMTEXT NOT NULL,
                json_payload JSON NULL,
                truncated TINYINT(1) NOT NULL DEFAULT 0,
                PRIMARY KEY (document_id,line_no),
                KEY ix_event_lines_time (occurred_utc,document_id,line_no),
                KEY ix_event_lines_recorded (recorded_utc,document_id,line_no),
                KEY ix_event_lines_actor (actor,occurred_utc),
                KEY ix_event_lines_event (event_name,occurred_utc),
                KEY ix_event_lines_transaction (transaction_id,occurred_utc),
                KEY ix_event_lines_problem (problem,occurred_utc),
                CONSTRAINT fk_event_lines_document FOREIGN KEY (document_id)
                    REFERENCES cd_documents(document_id) ON DELETE CASCADE
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_bin;

CREATE TABLE IF NOT EXISTS cd_migration_runs (
                run_id CHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL PRIMARY KEY,
                source_root TEXT NULL,
                inventory_staged TINYINT(1) NOT NULL DEFAULT 0,
                started_utc DATETIME(6) NOT NULL,
                completed_utc DATETIME(6) NULL,
                last_error TEXT NULL,
                KEY ix_migration_runs_completed (completed_utc,started_utc)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_bin;

CREATE TABLE IF NOT EXISTS cd_migration_files (
                migration_file_id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
                run_id CHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
                path VARCHAR(640) NOT NULL,
                original_path TEXT NOT NULL,
                byte_length BIGINT NOT NULL,
                modified_utc DATETIME(6) NOT NULL,
                sha256 CHAR(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
                archived TINYINT(1) NOT NULL DEFAULT 0,
                source_deleted TINYINT(1) NOT NULL DEFAULT 0,
                UNIQUE KEY ux_migration_files_run_path (run_id,path),
                KEY ix_migration_files_progress (run_id,archived,source_deleted),
                KEY ix_migration_files_sha256 (sha256),
                CONSTRAINT fk_migration_files_run FOREIGN KEY (run_id)
                    REFERENCES cd_migration_runs(run_id)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_bin;

CREATE TABLE IF NOT EXISTS cd_migration_chunks (
                migration_file_id BIGINT NOT NULL,
                chunk_no BIGINT NOT NULL,
                byte_offset BIGINT NOT NULL,
                content MEDIUMBLOB NOT NULL,
                PRIMARY KEY (migration_file_id,chunk_no),
                UNIQUE KEY ux_migration_chunks_offset (migration_file_id,byte_offset),
                CONSTRAINT fk_migration_chunks_file FOREIGN KEY (migration_file_id)
                    REFERENCES cd_migration_files(migration_file_id)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_bin;

-- Examples: inspect every installed index and locate missing projections.
SHOW TABLES LIKE 'cd_%';
SHOW INDEX FROM cd_documents;
SHOW INDEX FROM cd_event_lines;
SELECT path,byte_length,json_projection,line_projection FROM cd_documents
 WHERE json_projection IN ('invalid','oversized') OR line_projection='unsupported-bom';
SELECT d.path,e.line_no,e.occurred_utc,e.actor,e.event_name,e.transaction_id,e.problem,e.truncated,e.raw_text
 FROM cd_event_lines e JOIN cd_documents d ON d.document_id=e.document_id
 WHERE e.problem=1 ORDER BY e.occurred_utc DESC,e.recorded_utc DESC LIMIT 100;


-- Live banker state: schema v2. Created/imported by host before AO startup.

-- The old SQL documents are retained source snapshots, not the live ledger/stock.

-- All original DateTime ticks and Kind values are preserved alongside SQL DATETIME(6).

CREATE TABLE IF NOT EXISTS cd_banker_state (state_name VARCHAR(32) PRIMARY KEY, present BOOL NOT NULL, format VARCHAR(96) NULL, baseline_run_id VARCHAR(191) NULL, updated_ticks BIGINT NOT NULL, updated_kind INT NOT NULL, revision BIGINT NOT NULL) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_bin;

CREATE TABLE IF NOT EXISTS cd_ledger_items (
    row_key VARCHAR(191) NOT NULL PRIMARY KEY,
    ordinal INT NOT NULL,
    row_hash CHAR(64) CHARACTER SET ascii NOT NULL,
    ledger_id VARCHAR(191) NULL,
    aoid INT NULL,
    high_id INT NULL,
    ql INT NULL,
    transaction_id VARCHAR(191) NULL,
    donor VARCHAR(191) NULL,
    received_utc DATETIME(6) NULL,
    received_utc_ticks BIGINT NULL,
    received_utc_kind INT NULL,
    family VARCHAR(64) NULL,
    character_name VARCHAR(191) NULL,
    location VARCHAR(64) NULL,
    bag_slot INT NULL,
    item_slot INT NULL,
    KEY ix_ledger_aoid (aoid,high_id,ql),
    KEY ix_ledger_transaction (transaction_id),
    character_key VARCHAR(191) GENERATED ALWAYS AS (LOWER(character_name)) STORED,
    KEY ix_ledger_character (character_key,bag_slot,item_slot),
    UNIQUE KEY ux_ledger_id (ledger_id),
    KEY ix_ledger_donor (donor,received_utc)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_bin;

CREATE TABLE IF NOT EXISTS cd_stock_items (
    row_key VARCHAR(191) NOT NULL PRIMARY KEY,
    ordinal INT NOT NULL,
    row_hash CHAR(64) CHARACTER SET ascii NOT NULL,
    transaction_id VARCHAR(191) NULL,
    storage_role VARCHAR(64) NULL,
    physical_role VARCHAR(64) NULL,
    route_matches BOOL NULL,
    character_name VARCHAR(191) NULL,
    bag_source VARCHAR(64) NULL,
    bag_slot INT NULL,
    item_slot INT NULL,
    unique_identity VARCHAR(191) NULL,
    aoid INT NULL,
    high_id INT NULL,
    ql INT NULL,
    item_name TEXT NULL,
    observed_utc DATETIME(6) NULL,
    observed_utc_ticks BIGINT NULL,
    observed_utc_kind INT NULL,
    KEY ix_stock_aoid (aoid,high_id,ql),
    KEY ix_stock_transaction (transaction_id),
    character_key VARCHAR(191) GENERATED ALWAYS AS (LOWER(character_name)) STORED,
    KEY ix_stock_character (character_key,bag_slot,item_slot)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_bin;
