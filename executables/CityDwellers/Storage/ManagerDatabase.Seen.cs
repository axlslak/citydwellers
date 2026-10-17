using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using CityDwellers.Shared;
using MySqlConnector;

namespace CityDwellers.Host
{
    internal sealed partial class ManagerDatabase : IPlayerSeenPersistence
    {
        private void CreateSeenTables()
        {
            foreach (string sql in new[] {
                "CREATE TABLE IF NOT EXISTS cd_seen (name VARCHAR(30) NOT NULL PRIMARY KEY," +
                "character_id BIGINT UNSIGNED NOT NULL DEFAULT 0,tracked_utc DATETIME(6) NOT NULL," +
                "reported_utc DATETIME(6) NULL,reported_online TINYINT NULL,online_utc DATETIME(6) NULL," +
                "offline_utc DATETIME(6) NULL,reporter VARCHAR(256) NULL,KEY ix_character(character_id)) " +
                "ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_bin",
                "CREATE TABLE IF NOT EXISTS cd_seen_events (id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT PRIMARY KEY," +
                "name VARCHAR(30) NOT NULL,character_id BIGINT UNSIGNED NOT NULL,online TINYINT NOT NULL," +
                "observed_utc DATETIME(6) NOT NULL,reporter VARCHAR(256) NULL,KEY ix_player_time(name,observed_utc)) " +
                "ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_bin" })
                using (var command = new MySqlCommand(sql, _connection)) command.ExecuteNonQuery();
        }

        private PlayerSeenRecord[] LoadSeen()
        {
            var rows = new List<PlayerSeenRecord>();
            using (var command = new MySqlCommand("SELECT name,character_id,tracked_utc,reported_utc," +
                "reported_online,online_utc,offline_utc,reporter FROM cd_seen", _connection))
            using (var reader = command.ExecuteReader())
                while (reader.Read())
                    rows.Add(new PlayerSeenRecord {
                        Name = reader.GetString(0), CharacterId = reader.GetUInt32(1), TrackedUtc = reader.GetDateTime(2),
                        ReportedUtc = reader.IsDBNull(3) ? (DateTime?)null : reader.GetDateTime(3),
                        ReportedOnline = reader.IsDBNull(4) ? (bool?)null : reader.GetBoolean(4),
                        OnlineUtc = reader.IsDBNull(5) ? (DateTime?)null : reader.GetDateTime(5),
                        OfflineUtc = reader.IsDBNull(6) ? (DateTime?)null : reader.GetDateTime(6),
                        Reporter = reader.IsDBNull(7) ? null : reader.GetString(7)
                    });
            return rows.ToArray();
        }

        // Called under ManagerMemory's accounting lock, using its existing sole
        // connection. Presence never rewrites/serializes unrelated business rows.
        public void SaveSeen(PlayerSeenRecord[] records, PlayerPresenceReport[] reports)
        {
            try
            {
                if (_disposed) throw new ObjectDisposedException(nameof(ManagerDatabase));
                if (_connection.State != ConnectionState.Open || !_connection.Ping())
                { _connection.Close(); _connection.Open(); }
                using (var transaction = _connection.BeginTransaction())
                {
                    WriteSeenRows(transaction, "INSERT INTO cd_seen " +
                        "(name,character_id,tracked_utc,reported_utc,reported_online,online_utc,offline_utc,reporter) " +
                        "VALUES ",
                        "ON DUPLICATE KEY UPDATE character_id=VALUES(character_id),reported_utc=VALUES(reported_utc)," +
                        "reported_online=VALUES(reported_online),online_utc=VALUES(online_utc)," +
                        "offline_utc=VALUES(offline_utc),reporter=VALUES(reporter)", records.Select(row => new object[] {
                            row.Name, row.CharacterId, row.TrackedUtc, row.ReportedUtc, row.ReportedOnline,
                            row.OnlineUtc, row.OfflineUtc, row.Reporter }).ToArray());
                    WriteSeenRows(transaction, "INSERT INTO cd_seen_events " +
                        "(name,character_id,online,observed_utc,reporter) VALUES ", "", reports.Select(report => new object[] {
                            report.Name, report.CharacterId, report.Online, report.ObservedUtc, report.Reporter }).ToArray());
                    transaction.Commit();
                }
            }
            catch (Exception error) { _failed(error); throw; }
        }

        private void WriteSeenRows(MySqlTransaction transaction, string prefix, string suffix, object[][] rows)
        {
            // Initial tracking can contain many thousands of names. Batch writes
            // instead of making one round trip per player or keeping history in RAM.
            for (int offset = 0; offset < rows.Length; offset += 100)
                using (var command = new MySqlCommand("", _connection, transaction))
                {
                    var groups = new List<string>();
                    int parameter = 0;
                    foreach (var row in rows.Skip(offset).Take(100))
                    {
                        var names = new List<string>();
                        foreach (object value in row)
                        {
                            string name = "@v" + parameter++;
                            names.Add(name);
                            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
                        }
                        groups.Add("(" + string.Join(",", names) + ")");
                    }
                    command.CommandText = prefix + string.Join(",", groups) + " " + suffix;
                    command.ExecuteNonQuery();
                }
        }
    }
}
