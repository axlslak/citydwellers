using System;
using System.Collections.Generic;
using System.Data;
using CityDwellers.Shared;
using MySqlConnector;

namespace CityDwellers.Host
{
    internal sealed partial class ManagerDatabase : ILandControlPersistence
    {
        private const string LandControlColumns = "id,observed_utc,kind,message,raw_message,source,observer," +
            "attacker_side,attacker_org,attacker,defender_side,defender_org,playfield,tower," +
            "x_coord,y_coord,health_percent,group_type,channel_id,category,instance";

        private void CreateLandControlTable()
        {
            using (var command = new MySqlCommand("CREATE TABLE IF NOT EXISTS cd_land_control_events (" +
                "id CHAR(32) NOT NULL PRIMARY KEY,observed_utc DATETIME(6) NOT NULL,kind VARCHAR(24) NOT NULL," +
                "message LONGTEXT NOT NULL,raw_message LONGTEXT NOT NULL,source VARCHAR(40) NOT NULL,observer VARCHAR(30) NOT NULL," +
                "attacker_side TEXT NULL,attacker_org TEXT NULL,attacker TEXT NULL," +
                "defender_side TEXT NULL,defender_org TEXT NULL,playfield TEXT NULL,tower TEXT NULL," +
                "x_coord INT NULL,y_coord INT NULL,health_percent INT NULL,group_type INT NULL,channel_id INT NULL," +
                "category INT NULL,instance INT NULL,KEY ix_observed(observed_utc),KEY ix_kind_time(kind,observed_utc)) " +
                "ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_bin", _connection)) command.ExecuteNonQuery();
        }

        private LandControlEvent[] LoadLandControl()
        {
            var result = new List<LandControlEvent>();
            using (var command = new MySqlCommand("SELECT " + LandControlColumns +
                " FROM cd_land_control_events ORDER BY observed_utc,id", _connection))
            using (var reader = command.ExecuteReader())
                while (reader.Read()) result.Add(new LandControlEvent {
                    Id = reader.GetString(0), ObservedUtc = reader.GetDateTime(1), Kind = reader.GetString(2),
                    Message = reader.GetString(3), RawMessage = reader.GetString(4), Source = reader.GetString(5), Observer = reader.GetString(6),
                    AttackerSide = LcString(reader, 7), AttackerOrg = LcString(reader, 8), Attacker = LcString(reader, 9),
                    DefenderSide = LcString(reader, 10), DefenderOrg = LcString(reader, 11), Playfield = LcString(reader, 12), Tower = LcString(reader, 13),
                    X = LcInt(reader, 14), Y = LcInt(reader, 15), HealthPercent = LcInt(reader, 16),
                    GroupType = LcInt(reader, 17), ChannelId = LcInt(reader, 18), Category = LcInt(reader, 19), Instance = LcInt(reader, 20)
                });
            return result.ToArray();
        }

        private static string LcString(MySqlDataReader reader, int column) => reader.IsDBNull(column) ? null : reader.GetString(column);
        private static int? LcInt(MySqlDataReader reader, int column) => reader.IsDBNull(column) ? (int?)null : reader.GetInt32(column);

        // ManagerMemory serializes this on the existing sole connection before
        // publishing to RAM. A sink retry keeps the same observation ID.
        public void AppendLandControlEvent(LandControlEvent entry)
        {
            try
            {
                if (_disposed) throw new ObjectDisposedException(nameof(ManagerDatabase));
                if (_connection.State != ConnectionState.Open || !_connection.Ping())
                { _connection.Close(); _connection.Open(); }
                using (var command = new MySqlCommand("INSERT INTO cd_land_control_events (" + LandControlColumns +
                    ") VALUES (@id,@time,@kind,@message,@raw,@source,@observer,@as,@ao,@an,@ds,@do,@pf,@tower," +
                    "@x,@y,@health,@group,@channel,@category,@instance)", _connection))
                {
                    string[] names = { "id", "time", "kind", "message", "raw", "source", "observer", "as", "ao", "an", "ds", "do", "pf", "tower", "x", "y", "health", "group", "channel", "category", "instance" };
                    object[] values = { entry.Id, entry.ObservedUtc, entry.Kind, entry.Message, entry.RawMessage, entry.Source, entry.Observer,
                        entry.AttackerSide, entry.AttackerOrg, entry.Attacker, entry.DefenderSide, entry.DefenderOrg, entry.Playfield, entry.Tower,
                        entry.X, entry.Y, entry.HealthPercent, entry.GroupType, entry.ChannelId, entry.Category, entry.Instance };
                    for (int i = 0; i < names.Length; i++) command.Parameters.AddWithValue("@" + names[i], values[i] ?? DBNull.Value);
                    command.ExecuteNonQuery();
                }
            }
            catch (Exception error) { _failed(error); throw; }
        }
    }
}
