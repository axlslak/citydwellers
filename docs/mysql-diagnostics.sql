-- Read-only checks. No reset, reimport, archive reconstruction or table rebuild.
SELECT table_name, table_rows, data_length, index_length,
       ROUND((data_length + index_length) / 1048576, 2) AS allocated_mib
FROM information_schema.tables
WHERE table_schema = DATABASE() AND table_name LIKE 'cd_%'
ORDER BY data_length + index_length DESC;

SELECT meta_key, meta_value FROM cd_meta
WHERE meta_key IN ('schema_version', 'banker_relational_version');

SELECT 'ledger' AS entity, COUNT(*) AS item_count FROM cd_ledger_items
UNION ALL SELECT 'stock', COUNT(*) FROM cd_stock_items;

-- Completed background cleanup leaves no migration archive tables.
SELECT table_name FROM information_schema.tables
WHERE table_schema = DATABASE()
  AND table_name IN ('cd_migration_runs','cd_migration_files','cd_migration_chunks');

-- Remaining document storage by top-level namespace (business documents remain).
SELECT SUBSTRING_INDEX(path, '/', 1) AS namespace,
       COUNT(*) AS documents, SUM(byte_length) AS content_bytes
FROM cd_documents GROUP BY namespace ORDER BY content_bytes DESC;

SELECT donor, COUNT(*) AS items FROM cd_ledger_items
GROUP BY donor ORDER BY items DESC;
