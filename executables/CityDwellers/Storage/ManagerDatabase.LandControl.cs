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
                "category INT NULL,instance INT NULL,event_sequence BIGINT NOT NULL AUTO_INCREMENT UNIQUE," +
                "KEY ix_observed(observed_utc),KEY ix_kind_time(kind,observed_utc)) " +
                "ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_bin", _connection)) command.ExecuteNonQuery();
        }

        private LandControlEvent[] LoadLandControl()
        {
            var result = new List<LandControlEvent>();
            using (var command = new MySqlCommand("SELECT " + LandControlColumns + ",event_sequence" +
                " FROM cd_land_control_events ORDER BY observed_utc,id", _connection))
            using (var reader = command.ExecuteReader())
                while (reader.Read()) result.Add(new LandControlEvent {
                    Id = reader.GetString(0), ObservedUtc = reader.GetDateTime(1), Kind = reader.GetString(2),
                    Message = reader.GetString(3), RawMessage = reader.GetString(4), Source = reader.GetString(5), Observer = reader.GetString(6),
                    AttackerSide = LcString(reader, 7), AttackerOrg = LcString(reader, 8), Attacker = LcString(reader, 9),
                    DefenderSide = LcString(reader, 10), DefenderOrg = LcString(reader, 11), Playfield = LcString(reader, 12), Tower = LcString(reader, 13),
                    X = LcInt(reader, 14), Y = LcInt(reader, 15), HealthPercent = LcInt(reader, 16),
                    GroupType = LcInt(reader, 17), ChannelId = LcInt(reader, 18), Category = LcInt(reader, 19), Instance = LcInt(reader, 20), Sequence = reader.GetInt64(21)
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
                    entry.Sequence = command.LastInsertedId;
                }
            }
            catch (Exception error) { _failed(error); throw; }
        }

        private void UpgradeLandControl()
        {
            using (var check = new MySqlCommand("SELECT COUNT(*) FROM information_schema.columns WHERE table_schema=DATABASE() " +
                "AND table_name='cd_land_control_events' AND column_name='event_sequence'", _connection))
                if (Convert.ToInt32(check.ExecuteScalar()) == 0)
                    using (var command = new MySqlCommand("ALTER TABLE cd_land_control_events ADD COLUMN event_sequence BIGINT NOT NULL AUTO_INCREMENT UNIQUE", _connection))
                        command.ExecuteNonQuery();
            using (var command = new MySqlCommand("CREATE TABLE IF NOT EXISTS cd_land_control_reports (" +
                "id CHAR(32) NOT NULL PRIMARY KEY,observed_utc DATETIME(6) NOT NULL,action VARCHAR(16) NOT NULL,reporter VARCHAR(30) NOT NULL," +
                "playfield_id INT NOT NULL,site_number INT NOT NULL,organization TEXT NULL,faction VARCHAR(10) NULL,controller_ql INT NULL," +
                "org_id INT NULL,x_coord INT NULL,y_coord INT NULL,conductors INT NULL,turrets INT NULL,close_seconds INT NULL," +
                "planted_utc DATETIME(6) NULL,penalty_until_utc DATETIME(6) NULL,details LONGTEXT NOT NULL," +
                "KEY ix_site_time(playfield_id,site_number,observed_utc)) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_bin", _connection)) command.ExecuteNonQuery();
        }

        private const string LandControlReportColumns = "id,observed_utc,action,reporter,playfield_id,site_number,organization,faction," +
            "controller_ql,org_id,x_coord,y_coord,conductors,turrets,close_seconds,planted_utc,penalty_until_utc,details";

        private LandControlReport[] LoadLandControlReports()
        {
            var rows = new List<LandControlReport>();
            using (var command = new MySqlCommand("SELECT " + LandControlReportColumns + " FROM cd_land_control_reports ORDER BY observed_utc,id", _connection))
            using (var reader = command.ExecuteReader())
                while (reader.Read()) rows.Add(new LandControlReport {
                    Id = reader.GetString(0), ObservedUtc = reader.GetDateTime(1), Action = reader.GetString(2), Reporter = reader.GetString(3),
                    PlayfieldId = reader.GetInt32(4), SiteNumber = reader.GetInt32(5), Organization = LcString(reader, 6), Faction = LcString(reader, 7),
                    ControllerQl = LcInt(reader, 8), OrgId = LcInt(reader, 9), X = LcInt(reader, 10), Y = LcInt(reader, 11),
                    Conductors = LcInt(reader, 12), Turrets = LcInt(reader, 13), CloseSeconds = LcInt(reader, 14),
                    PlantedUtc = reader.IsDBNull(15) ? (DateTime?)null : reader.GetDateTime(15),
                    PenaltyUntilUtc = reader.IsDBNull(16) ? (DateTime?)null : reader.GetDateTime(16), Details = reader.GetString(17)
                });
            return rows.ToArray();
        }

        public void AppendLandControlReport(LandControlReport report)
        {
            try
            {
                if (_disposed) throw new ObjectDisposedException(nameof(ManagerDatabase));
                if (_connection.State != ConnectionState.Open || !_connection.Ping()) { _connection.Close(); _connection.Open(); }
                using (var command = new MySqlCommand("INSERT INTO cd_land_control_reports (" + LandControlReportColumns +
                    ") VALUES (@id,@time,@action,@reporter,@pf,@site,@org,@faction,@ql,@orgid,@x,@y,@conductors,@turrets,@close,@plant,@penalty,@details)", _connection))
                {
                    string[] names = { "id", "time", "action", "reporter", "pf", "site", "org", "faction", "ql", "orgid", "x", "y", "conductors", "turrets", "close", "plant", "penalty", "details" };
                    object[] values = { report.Id, report.ObservedUtc, report.Action, report.Reporter, report.PlayfieldId, report.SiteNumber,
                        report.Organization, report.Faction, report.ControllerQl, report.OrgId, report.X, report.Y, report.Conductors,
                        report.Turrets, report.CloseSeconds, report.PlantedUtc, report.PenaltyUntilUtc, report.Details ?? "" };
                    for (int i = 0; i < names.Length; i++) command.Parameters.AddWithValue("@" + names[i], values[i] ?? DBNull.Value);
                    command.ExecuteNonQuery();
                }
            }
            catch (Exception error) { _failed(error); throw; }
        }
    }
}
