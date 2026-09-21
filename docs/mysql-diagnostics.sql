-- Read-only schema-5 diagnostics. Run in the City Dwellers database.
SELECT version FROM cd_storage_version;
SELECT table_name, table_rows, data_length, index_length
FROM information_schema.tables WHERE table_schema = DATABASE()
ORDER BY data_length + index_length DESC;
SELECT COUNT(*) AS ledger_items FROM cd_ledger_entries;
SELECT COUNT(*) AS stock_items FROM cd_stock_entries;
SELECT id, ao_id, high_id, ql, name, `from`, received_utc,
       transaction_id, `character`, location, bag, slot
FROM cd_ledger_entries ORDER BY received_utc DESC LIMIT 100;
SELECT * FROM cd_item_history ORDER BY left_utc DESC LIMIT 100;
SELECT * FROM cd_transactions ORDER BY utc DESC LIMIT 100;
SELECT * FROM cd_lost_claims LIMIT 100;
SELECT * FROM cd_cloak_events LIMIT 100;
SELECT * FROM cd_pending_custody LIMIT 100;
SELECT * FROM cd_alt_groups ORDER BY main LIMIT 100;
SELECT * FROM cd_alt_observed ORDER BY parent_id, value LIMIT 200;
-- Expected empty after successful conversion and cleanup.
SELECT table_name FROM information_schema.tables
WHERE table_schema = DATABASE() AND table_name IN
('cd_documents','cd_document_chunks','cd_directories','cd_event_lines',
 'cd_migration_chunks','cd_migration_files','cd_migration_runs',
 'cd_ledger_items','cd_stock_items','cd_banker_state','cd_meta');
-- Expected one runtime connection for the host; inspection sessions also appear.
SELECT id, user, host, db, command, time, state, info
FROM information_schema.processlist WHERE db = DATABASE();
