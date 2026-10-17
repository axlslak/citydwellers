using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using CityDwellers.Shared;
using MySqlConnector;

namespace CityDwellers.Host
{
    internal sealed partial class ManagerDatabase : IPlayerProfilePersistence
    {
        private void CreatePlayerProfileTable()
        {
            using (var command = new MySqlCommand("CREATE TABLE IF NOT EXISTS cd_player_profiles (" +
                "name VARCHAR(30) NOT NULL PRIMARY KEY,level INT NOT NULL,alien_level INT NOT NULL," +
                "profession INT NOT NULL,updated_utc DATETIME(6) NOT NULL) " +
                "ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_bin", _connection)) command.ExecuteNonQuery();
        }

        private PlayerProfile[] LoadPlayerProfiles()
        {
            var result = new List<PlayerProfile>();
            using (var command = new MySqlCommand("SELECT name,level,alien_level,profession,updated_utc FROM cd_player_profiles", _connection))
            using (var reader = command.ExecuteReader())
                while (reader.Read()) result.Add(new PlayerProfile { Name = reader.GetString(0), Level = reader.GetInt32(1),
                    AlienLevel = reader.GetInt32(2), Profession = reader.GetInt32(3), UpdatedUtc = reader.GetDateTime(4) });
            return result.ToArray();
        }

        public void SavePlayerProfiles(PlayerProfile[] profiles)
        {
            try
            {
                if (_disposed) throw new ObjectDisposedException(nameof(ManagerDatabase));
                if (_connection.State != ConnectionState.Open || !_connection.Ping())
                { _connection.Close(); _connection.Open(); }
                using (var transaction = _connection.BeginTransaction())
                {
                    WriteSeenRows(transaction, "INSERT INTO cd_player_profiles (name,level,alien_level,profession,updated_utc) VALUES ",
                        "ON DUPLICATE KEY UPDATE level=VALUES(level),alien_level=VALUES(alien_level)," +
                        "profession=VALUES(profession),updated_utc=VALUES(updated_utc)", profiles.Select(p => new object[] {
                            p.Name, p.Level, p.AlienLevel, p.Profession, p.UpdatedUtc }).ToArray());
                    transaction.Commit();
                }
            }
            catch (Exception error) { _failed(error); throw; }
        }
    }
}
