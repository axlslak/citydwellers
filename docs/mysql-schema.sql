-- Schema 3 reference; never run this as a database reset.
-- Existing completed databases upgrade through the host.
-- Import archive tables are retired. Core relational items and remaining live
-- business documents are preserved. See MYSQL_MIGRATION.md.

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
