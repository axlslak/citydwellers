-- Schema 7 reference / inspection, not a second schema installer.
-- Authoritative fixed relational definitions:
-- executables/CityDwellers/Storage/BusinessTables.cs
-- Normal host startup creates an empty schema or upgrades supported versions.
-- Personal buff opt-outs live in cd_learned_buff_exclusions; startup upgrades 6 to 7.
-- Do not recreate legacy documents, chunks, directories or migration archives.
SELECT version FROM cd_storage_version;
SHOW CREATE TABLE cd_ledger_entries;
SHOW CREATE TABLE cd_stock_entries;
SHOW CREATE TABLE cd_transactions;
SHOW CREATE TABLE cd_item_history;
SHOW CREATE TABLE cd_lost_claims;
SHOW CREATE TABLE cd_cloak_events;
SHOW CREATE TABLE cd_learned_buffs;
SHOW CREATE TABLE cd_learned_buff_nanos;
SHOW CREATE TABLE cd_learned_buff_exclusions;
SHOW CREATE TABLE cd_pending_custody;
SHOW CREATE TABLE cd_alt_groups;
SHOW CREATE TABLE cd_alt_observed;
-- Full table/column inventory, including related items and pending operations.
SELECT table_name, column_name, column_type, is_nullable, column_key
FROM information_schema.columns
WHERE table_schema = DATABASE() AND table_name LIKE 'cd\_%'
ORDER BY table_name, ordinal_position;
