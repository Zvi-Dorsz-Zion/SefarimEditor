using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Sefarim.Core
{
    /// <summary>
    /// מאגר הגיבוי (בדרך כלל בכרטיס זיכרון): קובץ SQLite עם כל השורות המקוריות
    /// שהוסרו או שונו, לפי פעולה. מצורף לחיבור של הספרייה בשם bk, כדי שהעתקת
    /// השורות תיעשה בתוך SQLite בלי לעבור דרך הזיכרון.
    /// </summary>
    public sealed class BackupStore
    {
        public const string Alias = "bk";
        public const string FolderName = "SefarimEditor";

        private readonly SeforimDb _db;

        public string FilePath { get; }

        private BackupStore(SeforimDb db, string filePath)
        {
            _db = db;
            FilePath = filePath;
        }

        /// <summary>הנתיב לקובץ הגיבוי של ספרייה מסוימת בתוך תיקיית גיבוי.</summary>
        public static string PathFor(string backupRoot, LibraryKind kind, string dbPath)
        {
            var name = kind == LibraryKind.Other ? "Other-" + StableHash(Path.GetFullPath(dbPath).ToLowerInvariant()) : kind.ToString();
            return Path.Combine(backupRoot, FolderName, name, "originals.db");
        }

        private static string StableHash(string s)
        {
            using (var sha = System.Security.Cryptography.SHA1.Create())
            {
                var h = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(s));
                return BitConverter.ToString(h, 0, 4).Replace("-", "").ToLowerInvariant();
            }
        }

        public static BackupStore Attach(SeforimDb db, string filePath)
        {
            SqliteInit.Ensure();
            if (!db.IsReadOnly && !File.Exists(filePath))
            {
                // קובץ מצורף יורש את הרשאות החיבור, שנפתח בלי הרשאת יצירה; לכן יוצרים אותו מראש.
                Directory.CreateDirectory(Path.GetDirectoryName(filePath));
                var csb = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
                {
                    DataSource = filePath,
                    Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWriteCreate,
                    Pooling = false,
                };
                using (var c = new Microsoft.Data.Sqlite.SqliteConnection(csb.ToString()))
                {
                    c.Open();
                    using (var cmd = c.CreateCommand())
                    {
                        cmd.CommandText = "CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT)";
                        cmd.ExecuteNonQuery();
                    }
                }
            }
            db.Exec($"ATTACH DATABASE $p AS {Alias}", ("$p", filePath));
            var store = new BackupStore(db, filePath);
            // בחיבור לקריאה בלבד גם הקובץ המצורף פתוח לקריאה בלבד, ואין מה לאתחל.
            if (!db.IsReadOnly) store.Init();
            return store;
        }

        public void Detach() => _db.Exec($"DETACH DATABASE {Alias}");

        private void Init()
        {
            _db.Exec($"PRAGMA {Alias}.journal_mode=DELETE");
            _db.Exec($"PRAGMA {Alias}.synchronous=FULL");
            _db.Exec($@"CREATE TABLE IF NOT EXISTS {Alias}.ops (
                id INTEGER PRIMARY KEY,
                state TEXT NOT NULL,
                intent TEXT NOT NULL,
                db_version INTEGER,
                created TEXT NOT NULL,
                rows INTEGER NOT NULL DEFAULT 0,
                tables TEXT NOT NULL DEFAULT '[]',
                hashes TEXT NOT NULL DEFAULT '{{}}')");
            _db.Exec($"CREATE TABLE IF NOT EXISTS {Alias}.meta (key TEXT PRIMARY KEY, value TEXT)");
            if (_db.Scalar($"SELECT 1 FROM pragma_table_info('ops', '{Alias}') WHERE name='after_hashes'") == null)
                _db.Exec($"ALTER TABLE {Alias}.ops ADD COLUMN after_hashes TEXT NOT NULL DEFAULT '{{}}'");
        }

        public string GetMeta(string key) =>
            _db.Scalar($"SELECT value FROM {Alias}.meta WHERE key=$k", ("$k", key)) as string;

        public void SetMeta(string key, string value) =>
            _db.Exec($"INSERT OR REPLACE INTO {Alias}.meta(key, value) VALUES($k, $v)", ("$k", key), ("$v", value));

        public static string BeforeTable(string table) => "b:" + table;

        public static string AfterTable(string table) => "a:" + table;

        /// <summary>יוצר (או משלים עמודות ל)טבלאות הגיבוי של טבלה מהספרייה. העמודות ללא סוג, כדי ששום ערך לא יומר.</summary>
        public void EnsureTables(TableInfo t)
        {
            EnsureTable(BeforeTable(t.Name), t.ColumnNames.ToList());
            EnsureTable(AfterTable(t.Name), t.Key);
        }

        private void EnsureTable(string name, List<string> cols)
        {
            var q = $"{Alias}.{Sql.Q(name)}";
            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var cmd = _db.Command($"PRAGMA {Alias}.table_info({Sql.Q(name)})"))
            using (var r = cmd.ExecuteReader())
            {
                while (r.Read()) existing.Add(r.GetString(1));
            }
            if (existing.Count == 0)
            {
                _db.Exec($"CREATE TABLE {q} (op_id, {Sql.List(cols)})");
                _db.Exec($"CREATE INDEX {Alias}.{Sql.Q("ix:" + name)} ON {Sql.Q(name)}(op_id)");
                return;
            }
            foreach (var c in cols.Where(c => !existing.Contains(c)))
                _db.Exec($"ALTER TABLE {q} ADD COLUMN {Sql.Q(c)}");
        }

        public bool TableExists(string name) =>
            _db.Scalar($"SELECT 1 FROM {Alias}.sqlite_master WHERE type='table' AND name=$n", ("$n", name)) != null;

        public long InsertOp(OpIntent intent, long? dbVersion)
        {
            _db.Exec($"INSERT INTO {Alias}.ops(state, intent, db_version, created) VALUES('pending', $i, $v, $c)",
                ("$i", intent.ToJson()), ("$v", (object)dbVersion), ("$c", DateTime.Now.ToString("o", CultureInfo.InvariantCulture)));
            return (long)_db.Scalar("SELECT last_insert_rowid()");
        }

        public void SetAfterHashes(long id, Dictionary<string, string> hashes) =>
            _db.Exec($"UPDATE {Alias}.ops SET after_hashes=$h WHERE id=$id", ("$h", JsonSerializer.Serialize(hashes)), ("$id", id));

        public void SetOpVersion(long id, long? version) =>
            _db.Exec($"UPDATE {Alias}.ops SET db_version=$v WHERE id=$id", ("$v", (object)version), ("$id", id));

        public void UpdateOp(long id, OpState? state = null, long? rows = null, List<string> tables = null,
            Dictionary<string, string> hashes = null)
        {
            if (state.HasValue)
                _db.Exec($"UPDATE {Alias}.ops SET state=$s WHERE id=$id", ("$s", StateText(state.Value)), ("$id", id));
            if (rows.HasValue)
                _db.Exec($"UPDATE {Alias}.ops SET rows=$r WHERE id=$id", ("$r", rows.Value), ("$id", id));
            if (tables != null)
                _db.Exec($"UPDATE {Alias}.ops SET tables=$t WHERE id=$id", ("$t", JsonSerializer.Serialize(tables)), ("$id", id));
            if (hashes != null)
                _db.Exec($"UPDATE {Alias}.ops SET hashes=$h WHERE id=$id", ("$h", JsonSerializer.Serialize(hashes)), ("$id", id));
        }

        public void DeleteOp(long id) => _db.Exec($"DELETE FROM {Alias}.ops WHERE id=$id", ("$id", id));

        private const string OpsQuery = "SELECT id, state, intent, db_version, created, rows, tables, hashes, {1} FROM {0}ops ORDER BY id";

        public List<AppliedOp> LoadOps()
        {
            using (var cmd = _db.Command(string.Format(OpsQuery, Alias + ".", "after_hashes")))
                return ReadOps(cmd);
        }

        private static List<AppliedOp> ReadOps(Microsoft.Data.Sqlite.SqliteCommand cmd)
        {
            var list = new List<AppliedOp>();
            using (var r = cmd.ExecuteReader())
            {
                while (r.Read())
                {
                    list.Add(new AppliedOp
                    {
                        Id = r.GetInt64(0),
                        State = ParseState(r.GetString(1)),
                        Intent = OpIntent.FromJson(r.GetString(2)),
                        DbVersion = r.IsDBNull(3) ? (long?)null : r.GetInt64(3),
                        Created = DateTime.Parse(r.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                        Rows = r.GetInt64(5),
                        Tables = JsonSerializer.Deserialize<List<string>>(r.GetString(6)),
                        Hashes = JsonSerializer.Deserialize<Dictionary<string, string>>(r.GetString(7)),
                        AfterHashes = r.IsDBNull(8) ? new Dictionary<string, string>() : JsonSerializer.Deserialize<Dictionary<string, string>>(r.GetString(8)),
                    });
                }
            }
            return list;
        }

        /// <summary>
        /// קריאה מהירה של מאגר גיבוי בלי לפתוח את הספרייה עצמה (לתהליך הרקע).
        /// מחזיר את הפעולות ואת הגרסה שבה הקובץ הוחזר למקור, אם הוחזר.
        /// </summary>
        public static (List<AppliedOp> ops, long? restoredAt) ReadFile(string storePath)
        {
            SqliteInit.Ensure();
            var csb = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            {
                DataSource = storePath,
                Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly,
                Pooling = false,
            };
            using (var c = new Microsoft.Data.Sqlite.SqliteConnection(csb.ToString()))
            {
                c.Open();
                using (var probe = c.CreateCommand())
                {
                    probe.CommandText = "SELECT count(*) FROM sqlite_master WHERE name='ops'";
                    if (Convert.ToInt64(probe.ExecuteScalar()) == 0) return (new List<AppliedOp>(), null);
                }
                List<AppliedOp> ops;
                using (var cmd = c.CreateCommand())
                {
                    var hasAfter = false;
                    using (var probe = c.CreateCommand())
                    {
                        probe.CommandText = "SELECT 1 FROM pragma_table_info('ops') WHERE name='after_hashes'";
                        hasAfter = probe.ExecuteScalar() != null;
                    }
                    cmd.CommandText = string.Format(OpsQuery, "", hasAfter ? "after_hashes" : "NULL");
                    ops = ReadOps(cmd);
                }
                long? restoredAt = null;
                using (var cmd = c.CreateCommand())
                {
                    cmd.CommandText = "SELECT value FROM meta WHERE key='restored_at_version'";
                    if (cmd.ExecuteScalar() is string s && long.TryParse(s, out var v)) restoredAt = v;
                }
                return (ops, restoredAt);
            }
        }

        public long? RestoredAtVersion => long.TryParse(GetMeta("restored_at_version"), out var v) ? v : (long?)null;

        /// <summary>מוחק את שורות הגיבוי של פעולה (אחרי שהוחזרו לספרייה).</summary>
        public void DropOpRows(AppliedOp op)
        {
            foreach (var t in op.Tables)
            {
                if (TableExists(BeforeTable(t)))
                    _db.Exec($"DELETE FROM {Alias}.{Sql.Q(BeforeTable(t))} WHERE op_id=$op", ("$op", op.Id));
                if (TableExists(AfterTable(t)))
                    _db.Exec($"DELETE FROM {Alias}.{Sql.Q(AfterTable(t))} WHERE op_id=$op", ("$op", op.Id));
            }
        }

        private static string StateText(OpState s) => s.ToString().ToLowerInvariant();

        private static OpState ParseState(string s) =>
            (OpState)Enum.Parse(typeof(OpState), s, ignoreCase: true);
    }
}
