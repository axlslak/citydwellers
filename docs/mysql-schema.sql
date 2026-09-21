-- Schema 5 reference / inspection, not a second schema installer.
-- Authoritative fixed relational definitions:
-- executables/CityDwellers/Storage/BusinessTables.cs
-- Normal host startup creates/imports them and records version 5 atomically
-- with imported business rows before removing the retired SQL filesystem.
-- Do not recreate legacy documents, chunks, directories or migration archives.
SELECT version FROM cd_storage_version;
SHOW CREATE TABLE cd_ledger_entries;
SHOW CREATE TABLE cd_stock_entries;
SHOW CREATE TABLE cd_transactions;
SHOW CREATE TABLE cd_item_history;
SHOW CREATE TABLE cd_lost_claims;
SHOW CREATE TABLE cd_cloak_events;
SHOW CREATE TABLE cd_pending_custody;
SHOW CREATE TABLE cd_alt_groups;
SHOW CREATE TABLE cd_alt_observed;
-- Full table/column inventory, including related items and pending operations.
SELECT table_name, column_name, column_type, is_nullable, column_key
FROM information_schema.columns
WHERE table_schema = DATABASE() AND table_name LIKE 'cd\_%'
ORDER BY table_name, ordinal_position;
