using System;
using System.Collections.Generic;
using System.Data;
using CityDwellers.Shared;
using MySqlConnector;

namespace CityDwellers.Host
{
    internal sealed partial class ManagerDatabase : IBuffHistoryPersistence
    {
        private void CreateBuffHistoryTable()
        {
            using (var command = new MySqlCommand("CREATE TABLE IF NOT EXISTS cd_buff_history (" +
                "id CHAR(32) NOT NULL PRIMARY KEY,completed_utc DATETIME(6) NOT NULL," +
                "recipient_id INT NOT NULL,recipient VARCHAR(30) NOT NULL,caster VARCHAR(30) NOT NULL," +
                "nano_id INT NOT NULL,nano_name TEXT NOT NULL,paid BOOLEAN NOT NULL," +
                "buff_profession INT NOT NULL,caster_profession INT NOT NULL," +
                "KEY ix_time(completed_utc),KEY ix_recipient_time(recipient,completed_utc),KEY ix_nano(nano_id)) " +
                "ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_bin", _connection)) command.ExecuteNonQuery();
        }

        private BuffHistoryEntry[] LoadBuffHistory()
        {
            var result = new List<BuffHistoryEntry>();
            using (var command = new MySqlCommand("SELECT id,completed_utc,recipient_id,recipient,caster," +
                "nano_id,nano_name,paid,buff_profession,caster_profession FROM cd_buff_history ORDER BY completed_utc,id", _connection))
            using (var reader = command.ExecuteReader())
                while (reader.Read()) result.Add(new BuffHistoryEntry {
                    Id = reader.GetString(0), CompletedUtc = reader.GetDateTime(1), RecipientId = reader.GetInt32(2),
                    Recipient = reader.GetString(3), Caster = reader.GetString(4), NanoId = reader.GetInt32(5),
                    NanoName = reader.GetString(6), Paid = reader.GetBoolean(7),
                    BuffProfession = reader.GetInt32(8), CasterProfession = reader.GetInt32(9) });
            return result.ToArray();
        }

        // Serialized by ManagerMemory's accounting lock on the existing sole SQL
        // connection. No banking-state flatten, polling, file fallback or pruning.
        public void AppendBuffHistory(BuffHistoryEntry entry)
        {
            try
            {
                if (_disposed) throw new ObjectDisposedException(nameof(ManagerDatabase));
                if (_connection.State != ConnectionState.Open || !_connection.Ping())
                { _connection.Close(); _connection.Open(); }
                using (var command = new MySqlCommand("INSERT INTO cd_buff_history " +
                    "(id,completed_utc,recipient_id,recipient,caster,nano_id,nano_name,paid,buff_profession,caster_profession) " +
                    "VALUES (@id,@time,@recipientId,@recipient,@caster,@nano,@name,@paid,@buffProfession,@casterProfession)", _connection))
                {
                    command.Parameters.AddWithValue("@id", entry.Id);
                    command.Parameters.AddWithValue("@time", entry.CompletedUtc);
                    command.Parameters.AddWithValue("@recipientId", entry.RecipientId);
                    command.Parameters.AddWithValue("@recipient", entry.Recipient);
                    command.Parameters.AddWithValue("@caster", entry.Caster);
                    command.Parameters.AddWithValue("@nano", entry.NanoId);
                    command.Parameters.AddWithValue("@name", entry.NanoName ?? "AOID " + entry.NanoId);
                    command.Parameters.AddWithValue("@paid", entry.Paid);
                    command.Parameters.AddWithValue("@buffProfession", entry.BuffProfession);
                    command.Parameters.AddWithValue("@casterProfession", entry.CasterProfession);
                    command.ExecuteNonQuery();
                }
            }
            catch (Exception error) { _failed(error); throw; }
        }
    }
}
