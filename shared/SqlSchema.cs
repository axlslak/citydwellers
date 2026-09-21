using MySqlConnector;

namespace CityDwellers.Shared
{
    /// <summary>The authoritative versioned MySQL schema. See docs/mysql-schema.sql for all indexes.</summary>
    public static class SqlSchema
    {
        public const int Version = 3;
        public static readonly string[] Statements =
        {
            @"CREATE TABLE IF NOT EXISTS cd_meta (
                meta_key VARCHAR(96) CHARACTER SET ascii COLLATE ascii_bin NOT NULL PRIMARY KEY,
                meta_value LONGTEXT NOT NULL,
                updated_utc DATETIME(6) NOT NULL
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_bin",
            @"CREATE TABLE IF NOT EXISTS cd_directories (
                path VARCHAR(640) NOT NULL PRIMARY KEY,
                created_utc DATETIME(6) NOT NULL
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_bin",
            @"CREATE TABLE IF NOT EXISTS cd_documents (
                document_id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
                path VARCHAR(640) NOT NULL,
                byte_length BIGINT NOT NULL DEFAULT 0,
                next_chunk BIGINT NOT NULL DEFAULT 0,
                created_utc DATETIME(6) NOT NULL,
                modified_utc DATETIME(6) NOT NULL,
                json_payload JSON NULL,
                json_projection VARCHAR(32) NOT NULL DEFAULT 'not-json',
                line_projection VARCHAR(32) NOT NULL DEFAULT 'not-lines',
                next_line BIGINT NOT NULL DEFAULT 1,
                pending_line LONGBLOB NULL,
                pending_line_offset BIGINT NOT NULL DEFAULT 0,
                pending_line_length BIGINT NOT NULL DEFAULT 0,
                UNIQUE KEY ux_documents_path (path),
                KEY ix_documents_modified (modified_utc,document_id),
                KEY ix_documents_projection (json_projection,document_id)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_bin",
            @"CREATE TABLE IF NOT EXISTS cd_document_chunks (
                document_id BIGINT NOT NULL,
                chunk_no BIGINT NOT NULL,
                byte_offset BIGINT NOT NULL,
                content MEDIUMBLOB NOT NULL,
                PRIMARY KEY (document_id,chunk_no),
                UNIQUE KEY ux_document_chunks_offset (document_id,byte_offset),
                CONSTRAINT fk_document_chunks_document FOREIGN KEY (document_id)
                    REFERENCES cd_documents(document_id) ON DELETE CASCADE
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_bin",
            @"CREATE TABLE IF NOT EXISTS cd_event_lines (
                document_id BIGINT NOT NULL,
                line_no BIGINT NOT NULL,
                byte_offset BIGINT NOT NULL,
                byte_length BIGINT NOT NULL,
                occurred_utc DATETIME(6) NULL,
                recorded_utc DATETIME(6) NOT NULL,
                actor VARCHAR(191) NULL,
                event_name VARCHAR(191) NULL,
                transaction_id VARCHAR(191) NULL,
                problem TINYINT(1) NULL,
                raw_text MEDIUMTEXT NOT NULL,
                json_payload JSON NULL,
                truncated TINYINT(1) NOT NULL DEFAULT 0,
                PRIMARY KEY (document_id,line_no),
                KEY ix_event_lines_time (occurred_utc,document_id,line_no),
                KEY ix_event_lines_recorded (recorded_utc,document_id,line_no),
                KEY ix_event_lines_actor (actor,occurred_utc),
                KEY ix_event_lines_event (event_name,occurred_utc),
                KEY ix_event_lines_transaction (transaction_id,occurred_utc),
                KEY ix_event_lines_problem (problem,occurred_utc),
                CONSTRAINT fk_event_lines_document FOREIGN KEY (document_id)
                    REFERENCES cd_documents(document_id) ON DELETE CASCADE
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_bin"
        };

        internal static void Create(MySqlConnection connection)
        {
            foreach (string statement in Statements)
                using (var command = new MySqlCommand(statement, connection)) command.ExecuteNonQuery();
            foreach (string statement in BankerSqlStore.SchemaStatements())
                using (var command = new MySqlCommand(statement, connection)) command.ExecuteNonQuery();
        }
    }
}
