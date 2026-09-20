-- City Dwellers MySQL 8+ read-only diagnostics.
-- Select the configured database in your SQL client before running this file.
-- Values below are illustrative filters, not installation configuration.
SET @actor = 'Kbcentral';
SET @donor = 'Kavem';
SET @transaction = 'replace-with-request-or-transaction-id';
SET @since_utc = UTC_TIMESTAMP() - INTERVAL 1 HOUR;

-- Complete live schema: all columns, keys, engines and foreign keys.
SHOW CREATE TABLE cd_meta;
SHOW CREATE TABLE cd_directories;
SHOW CREATE TABLE cd_documents;
SHOW CREATE TABLE cd_document_chunks;
SHOW CREATE TABLE cd_event_lines;
SHOW CREATE TABLE cd_migration_runs;
SHOW CREATE TABLE cd_migration_files;
SHOW CREATE TABLE cd_migration_chunks;

-- Every index, one row per indexed column, in actual index order.
SELECT TABLE_NAME, INDEX_NAME, NON_UNIQUE, SEQ_IN_INDEX, COLUMN_NAME,
       COLLATION, SUB_PART, NULLABLE, INDEX_TYPE, IS_VISIBLE, EXPRESSION
FROM information_schema.STATISTICS
WHERE TABLE_SCHEMA = DATABASE() AND LEFT(TABLE_NAME, 3) = 'cd_'
ORDER BY TABLE_NAME, INDEX_NAME, SEQ_IN_INDEX;

-- Startup gates and exact last migration/cleanup error.
SELECT meta_key, meta_value, updated_utc FROM cd_meta ORDER BY meta_key;
SELECT * FROM cd_migration_runs ORDER BY started_utc DESC;
SELECT run_id, COUNT(*) AS source_files, COALESCE(SUM(byte_length), 0) AS source_bytes,
       SUM(archived = 1) AS archived_files, SUM(source_deleted = 1) AS deleted_source_files
FROM cd_migration_files GROUP BY run_id;
SELECT original_path, byte_length, sha256, archived, source_deleted
FROM cd_migration_files
WHERE archived = 0 OR source_deleted = 0
ORDER BY run_id, path;

-- Storage accounting. SHA-256 verification is streamed by DataMigration --verify;
-- GROUP_CONCAT is deliberately not used to reconstruct arbitrarily large files.
SELECT f.run_id, f.original_path, f.byte_length, f.sha256,
       COUNT(c.chunk_no) AS chunks, COALESCE(SUM(OCTET_LENGTH(c.content)), 0) AS archived_bytes
FROM cd_migration_files AS f
LEFT JOIN cd_migration_chunks AS c ON c.migration_file_id = f.migration_file_id
GROUP BY f.migration_file_id, f.run_id, f.original_path, f.byte_length, f.sha256
HAVING archived_bytes <> f.byte_length;

-- Logical document inventory and projections requiring raw-byte inspection.
SELECT document_id, path, byte_length, modified_utc, json_projection
FROM cd_documents ORDER BY modified_utc DESC, document_id DESC LIMIT 200;
SELECT path, byte_length, json_projection
FROM cd_documents WHERE json_projection <> 'valid'
ORDER BY path;

-- Actor/event/time indexes keep routine debugging narrow.
SELECT e.occurred_utc, e.recorded_utc, d.path, e.line_no,
       e.actor, e.event_name, e.transaction_id, e.problem, e.raw_text, e.truncated
FROM cd_event_lines AS e
JOIN cd_documents AS d ON d.document_id = e.document_id
WHERE e.actor = @actor AND e.occurred_utc >= @since_utc
ORDER BY e.occurred_utc, e.document_id, e.line_no;

SELECT e.occurred_utc, d.path, e.line_no, e.actor, e.event_name, e.raw_text
FROM cd_event_lines AS e
JOIN cd_documents AS d ON d.document_id = e.document_id
WHERE e.transaction_id = @transaction
ORDER BY e.occurred_utc, e.document_id, e.line_no;

SELECT e.occurred_utc, d.path, e.actor, e.event_name, e.raw_text
FROM cd_event_lines AS e
JOIN cd_documents AS d ON d.document_id = e.document_id
WHERE e.problem = 1 AND e.occurred_utc >= @since_utc
ORDER BY e.occurred_utc DESC LIMIT 200;

-- Lines without parseable event timestamps remain discoverable by recorded time.
SELECT e.recorded_utc, d.path, e.line_no, e.raw_text, e.truncated
FROM cd_event_lines AS e
JOIN cd_documents AS d ON d.document_id = e.document_id
WHERE e.recorded_utc >= @since_utc AND e.occurred_utc IS NULL
ORDER BY e.recorded_utc DESC LIMIT 200;

-- Current stock attributed to a donor, not an all-time donation count.
SELECT j.ledger_id, j.aoid, j.quality, j.donor, j.received_utc,
       j.banker, j.location, j.transaction_id
FROM cd_documents AS d,
JSON_TABLE(d.json_payload, '$.Items[*]' COLUMNS (
    ledger_id VARCHAR(191) PATH '$.Id' NULL ON EMPTY,
    aoid BIGINT PATH '$.AoId' NULL ON EMPTY,
    quality INT PATH '$.Ql' NULL ON EMPTY,
    donor VARCHAR(191) PATH '$.From' NULL ON EMPTY,
    received_utc VARCHAR(64) PATH '$.ReceivedUtc' NULL ON EMPTY,
    banker VARCHAR(191) PATH '$.Character' NULL ON EMPTY,
    location VARCHAR(191) PATH '$.Location' NULL ON EMPTY,
    transaction_id VARCHAR(191) PATH '$.TransactionId' NULL ON EMPTY
)) AS j
WHERE d.path = 'ledger.json' AND j.donor = @donor
ORDER BY j.received_utc DESC;

-- Current withdrawal orders, including exact failure/recipient ownership.
SELECT j.request_id, j.order_id, j.status, j.requested_by, j.recipient_main,
       j.source_character, j.donation_transaction, j.updated_utc, j.error
FROM cd_documents AS d,
JSON_TABLE(d.json_payload, '$.Withdrawals[*]' COLUMNS (
    request_id VARCHAR(191) PATH '$.Id' NULL ON EMPTY,
    order_id VARCHAR(191) PATH '$.OrderId' NULL ON EMPTY,
    status VARCHAR(191) PATH '$.Status' NULL ON EMPTY,
    requested_by VARCHAR(191) PATH '$.RequestedBy' NULL ON EMPTY,
    recipient_main VARCHAR(191) PATH '$.RecipientMain' NULL ON EMPTY,
    source_character VARCHAR(191) PATH '$.SourceCharacter' NULL ON EMPTY,
    donation_transaction VARCHAR(191) PATH '$.DonationTransactionId' NULL ON EMPTY,
    updated_utc VARCHAR(64) PATH '$.UpdatedUtc' NULL ON EMPTY,
    error TEXT PATH '$.Error' NULL ON EMPTY
)) AS j
WHERE d.path = 'withdrawal.json'
ORDER BY j.updated_utc DESC;

-- History files retain their complete original JSON fields for focused searches.
SELECT path, modified_utc, json_payload
FROM cd_documents
WHERE path LIKE 'history/%'
  AND JSON_SEARCH(json_payload, 'one', @donor) IS NOT NULL
ORDER BY modified_utc DESC LIMIT 100;
