using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using MySqlConnector;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CityDwellers.Shared
{
    /// <summary>File-shaped API over MySQL documents. Data paths are logical identifiers, never disk files.</summary>
    public static class SqlFile
    {
        private static readonly Encoding Utf8 = new UTF8Encoding(false);

        public static bool Exists(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string key; if (!SqlStore.TryKey(path, out key)) return File.Exists(path);
            return SqlStore.ReadStatement(context => FindId(context, key).HasValue);
        }

        public static string ReadAllText(string path) { return ReadAllText(path, Utf8); }
        public static string ReadAllText(string path, Encoding encoding)
        {
            using (Stream stream = OpenTextRead(path))
            using (var reader = new StreamReader(stream, encoding, true)) return reader.ReadToEnd();
        }
        private static Stream OpenTextRead(string path)
        {
            string key;
            if (SqlStore.TryKey(path, out key))
            {
                var snapshot = ReadSmallDocuments(new[] { path });
                byte[] bytes;
                if (!snapshot.TryGetValue(path, out bytes)) throw Missing(key);
                if (bytes != null) return new MemoryStream(bytes, false);
            }
            return OpenRead(path);
        }
        public static string ReadAllTextOrNull(string path)
        {
            try { return ReadAllText(path); } catch (FileNotFoundException) { return null; }
        }
        public static byte[] ReadAllBytesOrNull(string path)
        {
            try { return ReadAllBytes(path); } catch (FileNotFoundException) { return null; }
        }
        public static byte[] ReadAllBytes(string path)
        {
            string key;
            if (SqlStore.TryKey(path, out key))
            {
                var snapshot = ReadSmallDocuments(new[] { path });
                byte[] bytes;
                if (!snapshot.TryGetValue(path, out bytes)) throw Missing(key);
                if (bytes != null) return bytes;
            }
            using (Stream stream = OpenRead(path))
            {
                if (stream.Length > int.MaxValue) throw new IOException("The SQL document is too large to materialize; use OpenRead.");
                using (var output = new MemoryStream((int)stream.Length)) { stream.CopyTo(output); return output.ToArray(); }
            }
        }

        // One statement reads exact bytes and existence from the same revision.
        // Missing paths are absent; null means present but over the 1 MiB bound.
        // Large archives retain the bounded streaming path above.
        public static Dictionary<string, byte[]> ReadSmallDocuments(IEnumerable<string> paths)
        {
            var requested = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (requested.Length > 64) throw new ArgumentException("At most 64 documents per snapshot.");
            var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            if (requested.Length == 0) return result;
            var keys = requested.Select(FileKey).ToArray();
            return SqlStore.ReadStatement(context =>
            {
                var names = Enumerable.Range(0, keys.Length).Select(i => "@p" + i).ToArray();
                using (var command = SqlStore.Command(context,
                    "SELECT d.path,d.byte_length,c.byte_offset,c.content FROM cd_documents d " +
                    "LEFT JOIN cd_document_chunks c ON c.document_id=d.document_id AND d.byte_length<=1048576 " +
                    "WHERE d.path IN (" + string.Join(",", names) + ") ORDER BY d.path,c.byte_offset"))
                {
                    for (int i = 0; i < keys.Length; i++) command.Parameters.AddWithValue(names[i], keys[i]);
                    using (var reader = command.ExecuteReader())
                    {
                        string previous = null; byte[] bytes = null; long copied = 0;
                        while (reader.Read())
                        {
                            string key = reader.GetString(0); long length = reader.GetInt64(1);
                            if (length < 0) throw new SqlStore.DatabaseUnavailableException("Invalid SQL document length.");
                            if (previous != key)
                            {
                                if (bytes != null && copied != bytes.Length) throw new SqlStore.DatabaseUnavailableException("Incomplete SQL document snapshot.");
                                previous = key; copied = 0;
                                bytes = length <= 1048576 ? new byte[checked((int)length)] : null;
                                for (int i = 0; i < keys.Length; i++)
                                    if (string.Equals(keys[i], key, StringComparison.OrdinalIgnoreCase)) result[requested[i]] = bytes;
                            }
                            if (bytes == null || reader.IsDBNull(2)) continue;
                            var chunk = (byte[])reader.GetValue(3);
                            if (reader.GetInt64(2) != copied || chunk.Length > SqlStore.ChunkSize || chunk.Length > bytes.Length - copied)
                                throw new SqlStore.DatabaseUnavailableException("Invalid SQL document snapshot offsets.");
                            Buffer.BlockCopy(chunk, 0, bytes, (int)copied, chunk.Length); copied += chunk.Length;
                        }
                        if (bytes != null && copied != bytes.Length) throw new SqlStore.DatabaseUnavailableException("Incomplete SQL document snapshot.");
                    }
                }
                return result;
            }, keys.Any(SqlStore.IsIndependentLog));
        }
        public static IEnumerable<string> ReadLines(string path) { return ReadLines(path, Utf8); }
        public static IEnumerable<string> ReadLines(string path, Encoding encoding)
        {
            using (Stream stream = OpenRead(path))
            using (var reader = new StreamReader(stream, encoding, true))
            {
                string line; while ((line = reader.ReadLine()) != null) yield return line;
            }
        }

        public static string[] ReadAllLines(string path) { return ReadLines(path).ToArray(); }
        public static string[] ReadAllLines(string path, Encoding encoding) { return ReadLines(path, encoding).ToArray(); }

        public static string[] ReadTailLines(string path, int limit)
        {
            if (limit < 0) throw new ArgumentOutOfRangeException(nameof(limit));
            if (limit == 0) return new string[0];
            using (Stream stream = OpenRead(path))
            {
                // Diagnostic tails are bounded even if a malformed file contains a multi-gigabyte line.
                const int maximumBytes = 8 * 1024 * 1024;
                var parts = new List<byte[]>(); int bytes = 0, lines = 0; long cursor = stream.Length;
                while (cursor > 0 && bytes < maximumBytes && lines <= limit)
                {
                    int size = (int)Math.Min(Math.Min(cursor, SqlStore.ChunkSize), maximumBytes - bytes);
                    cursor -= size; stream.Position = cursor;
                    var block = new byte[size]; int count = 0;
                    while (count < size) { int read = stream.Read(block, count, size - count); if (read == 0) throw new InvalidDataException("SQL tail content ended before its recorded length."); count += read; }
                    lines += block.Count(b => b == 10); parts.Add(block); bytes += size;
                }
                parts.Reverse();
                using (var all = new MemoryStream(bytes))
                {
                    foreach (byte[] part in parts) all.Write(part, 0, part.Length);
                    all.Position = 0;
                    var tail = new Queue<string>();
                    using (var reader = new StreamReader(all, Utf8, true))
                    {
                        string line; while ((line = reader.ReadLine()) != null) { tail.Enqueue(line); if (tail.Count > limit) tail.Dequeue(); }
                    }
                    if (cursor > 0 && lines <= limit && tail.Count > 0)
                    {
                        var result = tail.ToArray(); result[0] = "[earlier bytes omitted from oversized tail] " + result[0]; return result;
                    }
                    return tail.ToArray();
                }
            }
        }

        public static Stream OpenRead(string path)
        {
            string key; if (!SqlStore.TryKey(path, out key)) return File.OpenRead(path);
            return new DocumentStream(key);
        }

        public static long GetLength(string path)
        {
            string key; if (!SqlStore.TryKey(path, out key)) return new FileInfo(path).Length;
            return SqlStore.Execute(false, context =>
            {
                using (var command = SqlStore.Command(context, "SELECT byte_length FROM cd_documents WHERE path=@path", "@path", key))
                { object result = command.ExecuteScalar(); if (result == null) throw Missing(key); return Convert.ToInt64(result); }
            });
        }
        public static DateTime GetLastWriteTimeUtc(string path)
        {
            string key; if (!SqlStore.TryKey(path, out key)) return File.GetLastWriteTimeUtc(path);
            return SqlStore.Execute(false, context =>
            {
                using (var command = SqlStore.Command(context, "SELECT modified_utc FROM cd_documents WHERE path=@path", "@path", key))
                { object result = command.ExecuteScalar(); return result == null ? DateTime.FromFileTimeUtc(0) : DateTime.SpecifyKind((DateTime)result, DateTimeKind.Utc); }
            });
        }

        public static void WriteAllText(string path, string contents) { WriteAllText(path, contents, Utf8); }
        public static void WriteAllText(string path, string contents, Encoding encoding)
        {
            if (encoding == null) throw new ArgumentNullException(nameof(encoding));
            WriteAllBytes(path, Encode(contents ?? "", encoding, true));
        }
        public static void WriteAllLines(string path, IEnumerable<string> lines) { WriteAllLines(path, lines, Utf8); }
        public static void WriteAllLines(string path, IEnumerable<string> lines, Encoding encoding)
        { if (lines == null) throw new ArgumentNullException(nameof(lines)); WriteAllText(path, string.Concat(lines.Select(line => (line ?? "") + Environment.NewLine)), encoding); }

        public static void WriteAllBytes(string path, byte[] bytes)
        {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            string key = FileKey(path);
            SqlStore.RequireMutableDocument(key);
            SqlStore.Execute(true, context => { long id = CreateEmpty(context, key, DateTime.UtcNow, true); WriteBytes(context, id, key, bytes, DateTime.UtcNow); return 0; });
        }

        public static void CreateNew(string path, byte[] bytes)
        {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes)); string key = FileKey(path);
            SqlStore.RequireMutableDocument(key);
            SqlStore.Execute(true, context =>
            {
                if (FindId(context, key).HasValue) throw new IOException("The SQL document already exists: mysql:" + key);
                long id = CreateEmpty(context, key, DateTime.UtcNow, false); WriteBytes(context, id, key, bytes, DateTime.UtcNow); return 0;
            });
        }

        public static bool TryCreateNew(string path, string text)
        {
            string key = FileKey(path);
            SqlStore.RequireMutableDocument(key);
            return SqlStore.Execute(true, context =>
            {
                if (FindId(context, key).HasValue) return false;
                long id = CreateEmpty(context, key, DateTime.UtcNow, false); WriteBytes(context, id, key, Utf8.GetBytes(text ?? ""), DateTime.UtcNow); return true;
            });
        }

        public static void AppendAllText(string path, string text) { AppendAllText(path, text, Utf8); }
        public static void AppendAllText(string path, string text, Encoding encoding)
        {
            if (encoding == null) throw new ArgumentNullException(nameof(encoding)); string key = FileKey(path);
            if (SqlStore.IsIndependentLog(key))
            {
                if (encoding.CodePage != Encoding.UTF8.CodePage || encoding.GetPreamble().Length != 0)
                    throw new ArgumentException("Runtime append-only logs use UTF-8 without a BOM.", nameof(encoding));
                SqlStore.AppendRuntimeLog(path, text ?? ""); return;
            }
            SqlStore.Execute(true, context => { Append(context, key, text ?? "", encoding); return 0; });
        }
        public static void AppendAllLines(string path, IEnumerable<string> lines) { AppendAllLines(path, lines, Utf8); }
        public static void AppendAllLines(string path, IEnumerable<string> lines, Encoding encoding)
        { if (lines == null) throw new ArgumentNullException(nameof(lines)); AppendAllText(path, string.Concat(lines.Select(line => (line ?? "") + Environment.NewLine)), encoding); }

        internal static void Append(SqlStore.DbContext context, string key, string text, Encoding encoding)
        {
            // An upsert also obtains the row lock for the independent host logger.
            using (var command = SqlStore.Command(context,
                "INSERT INTO cd_documents(path,created_utc,modified_utc) VALUES(@path,UTC_TIMESTAMP(6),UTC_TIMESTAMP(6)) ON DUPLICATE KEY UPDATE document_id=LAST_INSERT_ID(document_id)", "@path", key)) command.ExecuteNonQuery();
            long id, length, chunk;
            using (var command = SqlStore.Command(context, "SELECT document_id,byte_length,next_chunk FROM cd_documents WHERE path=@path FOR UPDATE", "@path", key))
            using (var reader = command.ExecuteReader()) { if (!reader.Read()) throw new SqlStore.DatabaseUnavailableException("SQL append document disappeared."); id = reader.GetInt64(0); length = reader.GetInt64(1); chunk = reader.GetInt64(2); }
            byte[] bytes = Encode(text, encoding, length == 0);
            using (var projection = new ProjectionWriter(context, id, key, true))
            {
                int offset = 0;
                if (chunk > 0 && bytes.Length > 0)
                {
                    int tailLength;
                    using (var command = SqlStore.Command(context, "SELECT OCTET_LENGTH(content) FROM cd_document_chunks WHERE document_id=@id AND chunk_no=@chunk", "@id", id, "@chunk", chunk - 1))
                    {
                        object value = command.ExecuteScalar(); if (value == null) throw new SqlStore.DatabaseUnavailableException("SQL append tail chunk is missing."); tailLength = Convert.ToInt32(value);
                    }
                    if (tailLength > SqlStore.ChunkSize) throw new SqlStore.DatabaseUnavailableException("SQL append tail exceeds the chunk size.");
                    int size = Math.Min(Math.Max(0, SqlStore.AppendChunkSize - tailLength), bytes.Length);
                    if (size > 0)
                    {
                        var part = new byte[size]; Buffer.BlockCopy(bytes, 0, part, 0, size);
                        using (var command = SqlStore.Command(context, "UPDATE cd_document_chunks SET content=CONCAT(content,@bytes) WHERE document_id=@id AND chunk_no=@chunk", "@id", id, "@chunk", chunk - 1, "@bytes", part)) command.ExecuteNonQuery();
                        projection.Feed(part, 0, part.Length); length += size; offset += size;
                    }
                }
                while (offset < bytes.Length)
                {
                    int size = Math.Min(SqlStore.AppendChunkSize, bytes.Length - offset);
                    var part = new byte[size]; Buffer.BlockCopy(bytes, offset, part, 0, size);
                    InsertChunk(context, id, chunk++, length, part); projection.Feed(part, 0, part.Length);
                    length += size; offset += size;
                }
                projection.Complete(length, chunk, DateTime.UtcNow);
            }
        }

        public static void Delete(string path)
        {
            string key = FileKey(path);
            SqlStore.RequireMutableDocument(key);
            SqlStore.Execute(true, context => { using (var command = SqlStore.Command(context, "DELETE FROM cd_documents WHERE path=@path", "@path", key)) command.ExecuteNonQuery(); return 0; });
        }

        public static void Move(string source, string destination)
        {
            string from = FileKey(source), to = FileKey(destination);
            SqlStore.RequireMutableDocument(from); SqlStore.RequireMutableDocument(to);
            if (from == to) throw new IOException("Source and destination are the same SQL document.");
            SqlStore.Execute(true, context =>
            {
                long? id = FindId(context, from); if (!id.HasValue) throw Missing(from);
                if (FindId(context, to).HasValue) throw new IOException("The SQL destination already exists: mysql:" + to);
                SqlDirectory.EnsureParents(context, to);
                using (var command = SqlStore.Command(context, "UPDATE cd_documents SET path=@to WHERE document_id=@id", "@to", to, "@id", id.Value)) command.ExecuteNonQuery();
                ReprojectOnNameChange(context, id.Value, from, to);
                return 0;
            });
        }

        public static void Copy(string source, string destination, bool overwrite = false)
        {
            string from = FileKey(source), to = FileKey(destination);
            SqlStore.RequireMutableDocument(to);
            if (from == to) throw new IOException("Source and destination are the same SQL document.");
            SqlStore.Execute(true, context =>
            {
                long? sourceId = FindId(context, from); if (!sourceId.HasValue) throw Missing(from);
                if (!overwrite && FindId(context, to).HasValue) throw new IOException("The SQL destination already exists: mysql:" + to);
                CopyDocument(context, sourceId.Value, to);
                return 0;
            });
        }

        public static void Replace(string source, string destination, string backup)
        {
            string from = FileKey(source), to = FileKey(destination), saved = backup == null ? null : FileKey(backup);
            SqlStore.RequireMutableDocument(from); SqlStore.RequireMutableDocument(to); if (saved != null) SqlStore.RequireMutableDocument(saved);
            if (from == to || saved == from || saved == to) throw new IOException("Replacement source, destination, and backup must be distinct SQL keys.");
            SqlStore.Execute(true, context =>
            {
                long? sourceId = FindId(context, from), targetId = FindId(context, to);
                if (!sourceId.HasValue) throw Missing(from); if (!targetId.HasValue) throw Missing(to);
                if (saved != null) CopyDocument(context, targetId.Value, saved);
                using (var command = SqlStore.Command(context, "DELETE FROM cd_documents WHERE document_id=@id", "@id", targetId.Value)) command.ExecuteNonQuery();
                using (var command = SqlStore.Command(context, "UPDATE cd_documents SET path=@path WHERE document_id=@id", "@path", to, "@id", sourceId.Value)) command.ExecuteNonQuery();
                ReprojectOnNameChange(context, sourceId.Value, from, to);
                return 0;
            });
        }

        private static void CopyDocument(SqlStore.DbContext context, long sourceId, string to)
        {
            long id = CreateEmpty(context, to, DateTime.UtcNow, true);
            using (var command = SqlStore.Command(context,
                "UPDATE cd_documents target JOIN cd_documents source ON source.document_id=@source SET target.byte_length=source.byte_length,target.next_chunk=source.next_chunk,target.modified_utc=source.modified_utc,target.json_payload=source.json_payload,target.json_projection=source.json_projection,target.line_projection=source.line_projection,target.next_line=source.next_line,target.pending_line=source.pending_line,target.pending_line_offset=source.pending_line_offset,target.pending_line_length=source.pending_line_length WHERE target.document_id=@target",
                "@source", sourceId, "@target", id)) command.ExecuteNonQuery();
            using (var command = SqlStore.Command(context, "INSERT INTO cd_document_chunks(document_id,chunk_no,byte_offset,content) SELECT @target,chunk_no,byte_offset,content FROM cd_document_chunks WHERE document_id=@source", "@source", sourceId, "@target", id)) command.ExecuteNonQuery();
            using (var command = SqlStore.Command(context,
                "INSERT INTO cd_event_lines(document_id,line_no,byte_offset,byte_length,occurred_utc,recorded_utc,actor,event_name,transaction_id,problem,raw_text,json_payload,truncated) SELECT @target,line_no,byte_offset,byte_length,occurred_utc,recorded_utc,actor,event_name,transaction_id,problem,raw_text,json_payload,truncated FROM cd_event_lines WHERE document_id=@source",
                "@source", sourceId, "@target", id)) command.ExecuteNonQuery();
        }

        private static void ReprojectOnNameChange(SqlStore.DbContext context, long id, string oldPath, string newPath)
        {
            // Temporary JSON writes commonly end in .tmp-<guid>; project the final name after atomic rename.
            if (IsJson(oldPath) == IsJson(newPath) && IsLines(oldPath) == IsLines(newPath)) return;
            long length, chunk;
            DateTime modified;
            using (var command = SqlStore.Command(context, "SELECT byte_length,next_chunk,modified_utc FROM cd_documents WHERE document_id=@id", "@id", id))
            using (var reader = command.ExecuteReader()) { reader.Read(); length = reader.GetInt64(0); chunk = reader.GetInt64(1); modified = reader.GetDateTime(2); }
            using (var command = SqlStore.Command(context, "DELETE FROM cd_event_lines WHERE document_id=@id", "@id", id)) command.ExecuteNonQuery();
            using (var projection = new ProjectionWriter(context, id, newPath))
            {
                long index = 0;
                while (true)
                {
                    byte[] bytes;
                    using (var command = SqlStore.Command(context, "SELECT content FROM cd_document_chunks WHERE document_id=@id AND chunk_no=@chunk", "@id", id, "@chunk", index++)) bytes = command.ExecuteScalar() as byte[];
                    if (bytes == null) break; projection.Feed(bytes, 0, bytes.Length);
                }
                projection.Complete(length, chunk, modified);
            }
        }

        private static void WriteBytes(SqlStore.DbContext context, long id, string key, byte[] bytes, DateTime modified)
        {
            long chunk = 0;
            using (var projection = new ProjectionWriter(context, id, key))
            {
                for (int offset = 0; offset < bytes.Length;)
                {
                    int size = Math.Min(SqlStore.ChunkSize, bytes.Length - offset); var part = new byte[size]; Buffer.BlockCopy(bytes, offset, part, 0, size);
                    InsertChunk(context, id, chunk++, offset, part); projection.Feed(part, 0, size); offset += size;
                }
                projection.Complete(bytes.LongLength, chunk, modified);
            }
        }

        internal static long CreateEmpty(SqlStore.DbContext context, string key, DateTime modifiedUtc, bool overwrite)
        {
            if (key.Length == 0) throw new ArgumentException("A document requires a nonempty relative path.", nameof(key));
            long? previous = FindId(context, key);
            if (previous.HasValue)
            {
                if (!overwrite) throw new IOException("The SQL document already exists: mysql:" + key);
                using (var command = SqlStore.Command(context, "DELETE FROM cd_document_chunks WHERE document_id=@id", "@id", previous.Value)) command.ExecuteNonQuery();
                using (var command = SqlStore.Command(context, "DELETE FROM cd_event_lines WHERE document_id=@id", "@id", previous.Value)) command.ExecuteNonQuery();
                using (var command = SqlStore.Command(context, "UPDATE cd_documents SET byte_length=0,next_chunk=0,modified_utc=@modified,json_payload=NULL,json_projection='not-json',line_projection='not-lines',next_line=1,pending_line=NULL,pending_line_offset=0,pending_line_length=0 WHERE document_id=@id", "@id", previous.Value, "@modified", modifiedUtc)) command.ExecuteNonQuery();
                return previous.Value;
            }
            SqlDirectory.EnsureParents(context, key);
            using (var command = SqlStore.Command(context, "INSERT INTO cd_documents(path,created_utc,modified_utc) VALUES(@path,UTC_TIMESTAMP(6),@modified)", "@path", key, "@modified", modifiedUtc)) { command.ExecuteNonQuery(); return command.LastInsertedId; }
        }

        internal static void InsertChunk(SqlStore.DbContext context, long id, long chunk, long offset, byte[] bytes)
        {
            using (var command = SqlStore.Command(context, "INSERT INTO cd_document_chunks(document_id,chunk_no,byte_offset,content) VALUES(@id,@chunk,@offset,@bytes)", "@id", id, "@chunk", chunk, "@offset", offset, "@bytes", bytes)) command.ExecuteNonQuery();
        }

        private static string FileKey(string path)
        { string key = SqlStore.Key(path); if (key.Length == 0) throw new ArgumentException("A document requires a file name.", nameof(path)); return key; }
        internal static long? FindId(SqlStore.DbContext context, string key)
        { using (var command = SqlStore.Command(context, "SELECT document_id FROM cd_documents WHERE path=@path", "@path", key)) { object value = command.ExecuteScalar(); return value == null ? (long?)null : Convert.ToInt64(value); } }
        private static FileNotFoundException Missing(string key) { return new FileNotFoundException("SQL document not found: mysql:" + key, "mysql:" + key); }
        private static byte[] Encode(string text, Encoding encoding, bool preamble)
        {
            byte[] bytes = encoding.GetBytes(text), prefix = preamble ? encoding.GetPreamble() : new byte[0];
            if (prefix.Length == 0) return bytes;
            var result = new byte[prefix.Length + bytes.Length]; Buffer.BlockCopy(prefix, 0, result, 0, prefix.Length); Buffer.BlockCopy(bytes, 0, result, prefix.Length, bytes.Length); return result;
        }
        private static bool IsJson(string key) { return key.EndsWith(".json", StringComparison.OrdinalIgnoreCase); }
        private static bool IsLines(string key)
        {
            string extension = System.IO.Path.GetExtension(key);
            return extension == ".jsonl" || extension == ".log" || extension == ".txt" || extension == ".csv" || extension == ".err" || extension == ".out";
        }

        internal sealed class ProjectionWriter : IDisposable
        {
            private readonly SqlStore.DbContext _context;
            private readonly long _id;
            private readonly bool _json, _append;
            private bool _lines;
            private string _lineProjection;
            private readonly MemoryStream _jsonBuffer = new MemoryStream();
            private readonly MemoryStream _pending = new MemoryStream();
            private long _line = 1, _lineOffset, _lineLength, _position;
            private bool _jsonOversized;
            private readonly List<object[]> _eventBatch = new List<object[]>();
            private int _eventBatchBytes;

            internal ProjectionWriter(SqlStore.DbContext context, long id, string key, bool append = false)
            {
                _context = context; _id = id; _json = IsJson(key); _lines = IsLines(key); _append = append;
                _lineProjection = _lines ? "utf8-lines" : "not-lines";
                if (append)
                {
                    using (var command = SqlStore.Command(context, "SELECT next_line,pending_line,pending_line_offset,pending_line_length,byte_length,line_projection FROM cd_documents WHERE document_id=@id", "@id", id))
                    using (var reader = command.ExecuteReader())
                    {
                        if (!reader.Read()) throw new SqlStore.DatabaseUnavailableException("SQL projection document disappeared.");
                        _line = reader.GetInt64(0); if (!reader.IsDBNull(1)) { byte[] pending = (byte[])reader.GetValue(1); _pending.Write(pending, 0, pending.Length); }
                        _lineOffset = reader.GetInt64(2); _lineLength = reader.GetInt64(3); _position = reader.GetInt64(4);
                        if (reader.GetString(5) == "unsupported-bom") { _lines = false; _lineProjection = "unsupported-bom"; }
                    }
                    // Appending to a JSON document is rare; bounded projection is reconstructed at completion.
                }
            }

            internal void Feed(byte[] bytes, int offset, int count)
            {
                if (_lines && _position == 0 && count >= 2 &&
                    (bytes[offset] == 255 && bytes[offset + 1] == 254 || bytes[offset] == 254 && bytes[offset + 1] == 255 ||
                    count >= 4 && bytes[offset] == 0 && bytes[offset + 1] == 0 && bytes[offset + 2] == 254 && bytes[offset + 3] == 255))
                { _lines = false; _lineProjection = "unsupported-bom"; }
                if (_json && !_append && !_jsonOversized)
                {
                    if (_jsonBuffer.Length + count <= SqlStore.ProjectionLimit) _jsonBuffer.Write(bytes, offset, count);
                    else { _jsonOversized = true; _jsonBuffer.SetLength(0); }
                }
                if (!_lines) { _position += count; return; }
                int end = offset + count, start = offset;
                for (int i = offset; i < end; i++) if (bytes[i] == 10)
                {
                    AddPending(bytes, start, i - start); _lineLength += i - start;
                    QueueLine(true); _position += i - start + 1; _line++; _lineOffset = _position; _lineLength = 0; _pending.SetLength(0); start = i + 1;
                }
                AddPending(bytes, start, end - start); _lineLength += end - start; _position += end - start;
            }

            private void AddPending(byte[] bytes, int offset, int count)
            {
                int retain = (int)Math.Min(count, SqlStore.ChunkSize - _pending.Length);
                if (retain > 0) _pending.Write(bytes, offset, retain);
            }

            private void QueueLine(bool newline)
            {
                string raw = Utf8.GetString(_pending.ToArray()).TrimEnd('\r');
                bool truncated = _lineLength > _pending.Length;
                JToken json = null;
                if (!truncated) { try { json = JToken.Parse(raw.TrimStart('\uFEFF')); } catch (JsonException) { } }
                JObject row = json as JObject;
                DateTime? occurred = Timestamp(row, raw);
                string actor = Field(row, "Actor", "Source", "Character", "Bot", "CharacterName");
                string stage = Field(row, "Event", "Stage", "Kind", "Category", "Type");
                string transaction = Field(row, "Trace", "TransactionId", "DonationTransactionId", "BatchId", "RequestId", "LedgerId") ??
                    Field(row == null ? null : row.GetValue("Data", StringComparison.OrdinalIgnoreCase) as JObject, "Trace", "TransactionId", "DonationTransactionId", "BatchId", "RequestId", "LedgerId");
                object problem = null;
                JToken flag = row == null ? null : row.GetValue("Problem", StringComparison.OrdinalIgnoreCase);
                if (flag != null && flag.Type == JTokenType.Boolean) problem = (bool)flag;
                else
                {
                    string severity = Field(row, "Severity", "Level");
                    if (severity != null) problem = severity.Equals("error", StringComparison.OrdinalIgnoreCase) || severity.Equals("warning", StringComparison.OrdinalIgnoreCase) || severity.Equals("fatal", StringComparison.OrdinalIgnoreCase);
                    else if (raw.IndexOf(" WRN]", StringComparison.Ordinal) >= 0 || raw.IndexOf(" ERR]", StringComparison.Ordinal) >= 0 || raw.IndexOf(" FTL]", StringComparison.Ordinal) >= 0) problem = true;
                }
                _eventBatch.Add(new object[] { _id, _line, _lineOffset, _lineLength + (newline ? 1 : 0), occurred, DateTime.UtcNow, actor, stage, transaction, problem, raw, json == null ? null : json.ToString(Formatting.None), truncated });
                _eventBatchBytes += raw.Length * 4 + 512;
                if (_eventBatch.Count >= 64 || _eventBatchBytes >= SqlStore.ChunkSize) FlushLines();
            }

            private void FlushLines()
            {
                if (_eventBatch.Count == 0) return;
                var query = new StringBuilder("INSERT INTO cd_event_lines(document_id,line_no,byte_offset,byte_length,occurred_utc,recorded_utc,actor,event_name,transaction_id,problem,raw_text,json_payload,truncated) VALUES ");
                using (var command = SqlStore.Command(_context, ""))
                {
                    for (int row = 0; row < _eventBatch.Count; row++)
                    {
                        if (row != 0) query.Append(','); query.Append('(');
                        for (int col = 0; col < _eventBatch[row].Length; col++)
                        {
                            if (col != 0) query.Append(','); string name = "@v" + row + "_" + col;
                            if (col == 11) query.Append("IF(JSON_VALID(").Append(name).Append("),").Append(name).Append(",NULL)");
                            else query.Append(name);
                            command.Parameters.AddWithValue(name, _eventBatch[row][col] ?? DBNull.Value);
                        }
                        query.Append(')');
                    }
                    query.Append(" ON DUPLICATE KEY UPDATE byte_length=VALUES(byte_length),occurred_utc=VALUES(occurred_utc),recorded_utc=VALUES(recorded_utc),actor=VALUES(actor),event_name=VALUES(event_name),transaction_id=VALUES(transaction_id),problem=VALUES(problem),raw_text=VALUES(raw_text),json_payload=VALUES(json_payload),truncated=VALUES(truncated)");
                    command.CommandText = query.ToString(); command.ExecuteNonQuery();
                }
                _eventBatch.Clear(); _eventBatchBytes = 0;
            }

            internal void Complete(long length, long chunks, DateTime modifiedUtc)
            {
                if (_lines && _lineLength > 0) QueueLine(false);
                FlushLines();
                string json = null, status = _json ? "invalid" : "not-json";
                if (_json)
                {
                    if (length > SqlStore.ProjectionLimit) status = "oversized";
                    else
                    {
                        if (_append)
                        {
                            using (var command = SqlStore.Command(_context, "SELECT content FROM cd_document_chunks WHERE document_id=@id ORDER BY chunk_no", "@id", _id))
                            using (var reader = command.ExecuteReader()) while (reader.Read()) { byte[] bytes = (byte[])reader.GetValue(0); _jsonBuffer.Write(bytes, 0, bytes.Length); }
                        }
                        try
                        {
                            _jsonBuffer.Position = 0;
                            using (var reader = new StreamReader(_jsonBuffer, Utf8, true, 1024, true)) json = JToken.Parse(reader.ReadToEnd()).ToString(Formatting.None);
                            status = "valid";
                        }
                        catch (JsonException) { status = "invalid"; }
                    }
                }
                using (var command = SqlStore.Command(_context,
                    "UPDATE cd_documents SET byte_length=@length,next_chunk=@chunks,modified_utc=@modified,json_payload=IF(JSON_VALID(@json),@json,NULL),json_projection=IF(@status='valid' AND NOT JSON_VALID(@json),'invalid',@status),line_projection=@lineProjection,next_line=@line,pending_line=@pending,pending_line_offset=@offset,pending_line_length=@pendingLength WHERE document_id=@id",
                    "@id", _id, "@length", length, "@chunks", chunks, "@modified", modifiedUtc.ToUniversalTime(), "@json", json, "@status", status, "@line", _line,
                    "@pending", _pending.Length == 0 ? null : _pending.ToArray(), "@offset", _lineOffset, "@pendingLength", _lineLength, "@lineProjection", _lineProjection)) command.ExecuteNonQuery();
            }

            private static string Field(JObject row, params string[] fields)
            {
                if (row == null) return null;
                foreach (string name in fields)
                {
                    JToken value = row.GetValue(name, StringComparison.OrdinalIgnoreCase);
                    if (value == null || value.Type == JTokenType.Null || value is JContainer) continue;
                    string result;
                    if (value.Type == JTokenType.Date)
                    {
                        DateTime date = (DateTime)value;
                        result = (date.Kind == DateTimeKind.Local ? date.ToUniversalTime() : DateTime.SpecifyKind(date, DateTimeKind.Utc)).ToString("O", CultureInfo.InvariantCulture);
                    }
                    else result = value.ToString();
                    return result.Length > 191 ? result.Substring(0, 191) : result;
                }
                return null;
            }
            private static DateTime? Timestamp(JObject row, string raw)
            {
                string value = Field(row, "Utc", "TimeUtc", "Timestamp", "TimestampUtc", "Time", "CreatedUtc", "OccurredUtc");
                DateTime parsed;
                if (value != null && DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out parsed) && parsed.Year >= 1000) return parsed;
                string prefix = raw.StartsWith("[HOST ", StringComparison.Ordinal) ? raw.Substring(6) : raw.TrimStart('[', '\uFEFF');
                int end = prefix.IndexOfAny(new[] { ' ', ']' });
                if (end > 0 && end <= 40 && DateTime.TryParse(prefix.Substring(0, end), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out parsed) && parsed.Year >= 1000) return parsed;
                return null;
            }
            public void Dispose() { _pending.Dispose(); _jsonBuffer.Dispose(); }
        }

        /// <summary>Bounded-memory repeatable-read snapshot; replacement/deletion cannot mix document revisions.</summary>
        private sealed class DocumentStream : Stream
        {
            private readonly SqlStore.DbContext _context;
            private readonly bool _owns;
            private readonly long _id, _length;
            private long _position, _chunkOffset;
            private byte[] _chunk;
            private bool _disposed;

            internal DocumentStream(string key)
            {
                _context = SqlStore.CurrentContext;
                if (_context != null && (!_context.WriterHeld || SqlStore.IsIndependentLog(key))) _context = null;
                _owns = _context == null;
                try
                {
                    if (_owns)
                    {
                        _context = new SqlStore.DbContext { Connection = SqlStore.OpenConnection() };
                        _context.Transaction = _context.Connection.BeginTransaction(System.Data.IsolationLevel.RepeatableRead);
                    }
                    using (var command = SqlStore.Command(_context, "SELECT document_id,byte_length FROM cd_documents WHERE path=@path", "@path", key))
                    using (var reader = command.ExecuteReader())
                    { if (!reader.Read()) throw Missing(key); _id = reader.GetInt64(0); _length = reader.GetInt64(1); }
                }
                catch (Exception ex)
                {
                    if (_owns && _context != null) { if (_context.Transaction != null) _context.Transaction.Dispose(); _context.Connection.Dispose(); }
                    SqlStore.HandleDatabaseFailure(ex); throw;
                }
            }
            public override bool CanRead { get { return !_disposed; } }
            public override bool CanSeek { get { return !_disposed; } }
            public override bool CanWrite { get { return false; } }
            public override long Length { get { CheckDisposed(); return _length; } }
            public override long Position { get { CheckDisposed(); return _position; } set { Seek(value, SeekOrigin.Begin); } }
            public override void Flush() { CheckDisposed(); }
            public override long Seek(long offset, SeekOrigin origin)
            {
                CheckDisposed(); long value;
                checked { value = origin == SeekOrigin.Begin ? offset : origin == SeekOrigin.Current ? _position + offset : origin == SeekOrigin.End ? _length + offset : throw new ArgumentOutOfRangeException(nameof(origin)); }
                if (value < 0) throw new IOException("Cannot seek before the beginning of a SQL document.");
                _position = value; return value;
            }
            public override int Read(byte[] buffer, int offset, int count)
            {
                CheckDisposed(); if (buffer == null) throw new ArgumentNullException(nameof(buffer));
                if (offset < 0 || count < 0 || offset > buffer.Length - count) throw new ArgumentOutOfRangeException();
                try
                {
                    int copied = 0;
                    while (copied < count && _position < _length)
                    {
                        if (_chunk == null || _position < _chunkOffset || _position >= _chunkOffset + _chunk.Length)
                        {
                            using (var command = SqlStore.Command(_context, "SELECT byte_offset,content FROM cd_document_chunks WHERE document_id=@id AND byte_offset<=@offset ORDER BY byte_offset DESC LIMIT 1", "@id", _id, "@offset", _position))
                            using (var reader = command.ExecuteReader())
                            {
                                if (!reader.Read()) throw new SqlStore.DatabaseUnavailableException("A SQL document chunk is missing.");
                                _chunkOffset = reader.GetInt64(0); _chunk = (byte[])reader.GetValue(1);
                            }
                            if (_position >= _chunkOffset + _chunk.Length || _chunk.Length > SqlStore.ChunkSize)
                                throw new SqlStore.DatabaseUnavailableException("A SQL document has invalid chunk offsets or sizes.");
                        }
                        int available = (int)Math.Min(Math.Min(count - copied, _chunkOffset + _chunk.Length - _position), _length - _position);
                        Buffer.BlockCopy(_chunk, (int)(_position - _chunkOffset), buffer, offset + copied, available);
                        copied += available; _position += available;
                    }
                    return copied;
                }
                catch (Exception ex) { SqlStore.HandleDatabaseFailure(ex); throw; }
            }
            public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
            public override void SetLength(long value) { throw new NotSupportedException(); }
            private void CheckDisposed() { if (_disposed) throw new ObjectDisposedException(nameof(DocumentStream)); }
            protected override void Dispose(bool disposing)
            {
                if (!_disposed && disposing && _owns)
                {
                    try { _context.Transaction.Rollback(); }
                    catch (Exception ex) { SqlStore.HandleDatabaseFailure(ex); }
                    finally { _context.Transaction.Dispose(); _context.Connection.Dispose(); }
                }
                _disposed = true; _chunk = null; base.Dispose(disposing);
            }
        }
    }
}
