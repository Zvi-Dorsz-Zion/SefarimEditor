using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Data.Sqlite;

namespace Sefarim.Core
{
    /// <summary>כשל בהחלת עדכון. הקובץ לא שונה (הטרנזקציה בוטלה).</summary>
    public sealed class PatchApplyException : Exception
    {
        /// <summary>החתימה לא התאימה: הקובץ המקומי שונה מהצפוי, או שהעדכון פגום.</summary>
        public bool IsContentMismatch { get; }

        public IReadOnlyList<string> MismatchedTables { get; }

        public PatchApplyException(string message, bool contentMismatch = false, IReadOnlyList<string> tables = null, Exception inner = null)
            : base(message, inner)
        {
            IsContentMismatch = contentMismatch;
            MismatchedTables = tables;
        }
    }

    public sealed class PatchApplyOptions
    {
        /// <summary>בדיקה שהעדכון לא הוסיף הפרות של מפתחות זרים (סריקה של כל הקובץ, כחצי דקה).</summary>
        public bool CheckForeignKeys { get; set; } = true;

        /// <summary>
        /// לקובץ ערוך: ניקוי שורות שהעדכון הכניס ומפנות לשורות שהמשתמש מחק, לפי כלל הקשר (CASCADE / SET NULL).
        /// </summary>
        public bool RemoveOrphans { get; set; }

        /// <summary>
        /// טבלאות שהמשתמש שינה בכוונה, ולכן אי אפשר לאמת אותן מול החתימה הרשמית.
        /// ריק = קובץ לא ערוך, והאימות זהה לזה של אוצריא.
        /// </summary>
        public ISet<string> UnverifiableTables { get; set; } = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>
        /// רץ אחרי העדכון ולפני האימות, באותה טרנזקציה: כאן מוחלים שוב השינויים של המשתמש.
        /// מחזיר את הטבלאות שהשתנו בהחלה, והן מצטרפות ל-UnverifiableTables.
        /// </summary>
        public Func<PatchApplyResult, IEnumerable<string>> BeforeVerify { get; set; }

        public IProgress<string> Progress { get; set; }
    }

    public sealed class PatchApplyResult
    {
        public int Migrations { get; set; }
        public Dictionary<string, long> Upserts { get; } = new Dictionary<string, long>(StringComparer.Ordinal);
        public Dictionary<string, long> Deletes { get; } = new Dictionary<string, long>(StringComparer.Ordinal);

        /// <summary>ספרים שתוכן החיפוש שלהם השתנה (כמו booksTouched באוצריא).</summary>
        public HashSet<long> BooksTouched { get; } = new HashSet<long>();

        /// <summary>שורות יתומות שנוקו אחרי החלה מחדש של מחיקות, לפי טבלה.</summary>
        public Dictionary<string, long> OrphansRemoved { get; } = new Dictionary<string, long>(StringComparer.Ordinal);

        public List<string> VerifiedTables { get; } = new List<string>();

        /// <summary>טבלאות שהעדכון שינה ולא אומתו, כי המשתמש שינה אותן.</summary>
        public List<string> UnverifiedTables { get; } = new List<string>();

        /// <summary>טבלאות שהעדכון לא נגע בהן ולא אומתו (אוצריא מאמתת אותן אחרי השמירה).</summary>
        public List<string> DeferredTables { get; } = new List<string>();

        /// <summary>שינויים בטבלאות שלא ממופות לספרים: אוצריא מרעננת אז את כל אינדקס החיפוש.</summary>
        public bool HasChangesOutsideBooksTouched
        {
            get
            {
                var ignored = new HashSet<string>(StringComparer.Ordinal)
                {
                    "schema_meta", "line_ref", "line_dh", "banner", "book_banner", "book_protection",
                    "line_divine_name", "line_note_divine_name",
                };
                bool Changed(KeyValuePair<string, long> e) =>
                    e.Value > 0 && !ignored.Contains(e.Key) && !PatchApplier.BooksTouchedTables.Contains(e.Key);
                return Upserts.Any(Changed) || Deletes.Any(Changed);
            }
        }
    }

    /// <summary>
    /// מחיל קובץ הפרשים של אוצריא על seforim.db, בדיוק כמו patch_applier.dart
    /// (otzaria_library_updater): migrations → upserts בסדר מפתחות זרים → deletes בסדר הפוך
    /// → סטטיסטיקות → אימות חתימה → שמירה. הכול בטרנזקציה אחת; כל כשל מבטל הכול.
    /// </summary>
    public static class PatchApplier
    {
        /// <summary>טבלאות ששינוי בהן ממופה למזהי ספרים (kBooksTouchedTables במקור).</summary>
        public static readonly HashSet<string> BooksTouchedTables = new HashSet<string>(StringComparer.Ordinal)
        {
            "book", "line", "line_content", "line_note", "tocEntry", "line_toc", "tocText", "alt_toc_structure",
            "alt_toc_entry", "line_alt_toc", "book_author", "book_base_text", "book_topic", "book_acronym",
        };

        private const string Stat1Snapshot = "stat1_snapshot";
        private const int SqliteConstraint = 19;

        public static PatchApplyResult Apply(SeforimDb db, string patchPath, PatchManifest m, PatchApplyOptions options = null)
        {
            options = options ?? new PatchApplyOptions();
            var progress = options.Progress;
            if (db.IsReadOnly) throw new InvalidOperationException("הקובץ נפתח לקריאה בלבד.");
            if (db.InTransaction) throw new InvalidOperationException("לא ניתן להחיל עדכון בתוך טרנזקציה פתוחה.");
            if (m.FullRebase) throw new PatchApplyException("העדכון הזה מחייב הורדה מלאה של הספרייה.");
            var fromOrder = PatchTables.HashTableOrder(m.FromSchemaVersion);
            var toOrder = PatchTables.HashTableOrder(m.ToSchemaVersion);

            progress?.Report("בודק את הקובץ לפני העדכון…");
            var localVersion = db.DbVersion;
            var localSchema = db.SchemaVersion;
            if (localVersion != m.FromVersion)
                throw new PatchApplyException($"גרסת הספרייה בקובץ ({localVersion}) אינה הגרסה שהעדכון מיועד לה ({m.FromVersion}).");
            if (localSchema != null && localSchema != m.FromSchemaVersion)
                throw new PatchApplyException($"מבנה הקובץ ({localSchema}) אינו המבנה שהעדכון מיועד לו ({m.FromSchemaVersion}).");

            var result = new PatchApplyResult();
            var attached = false;
            try
            {
                // כמו באוצריא: מפתחות זרים פעילים (קובע מחיקות בשרשרת), מחוץ לטרנזקציה.
                db.Exec("PRAGMA foreign_keys=ON");
                db.Exec("PRAGMA cache_size=-262144");
                db.Exec("PRAGMA temp_store=FILE");

                try
                {
                    db.Exec("ATTACH DATABASE $p AS patch", ("$p", patchPath));
                    attached = true;
                    db.Scalar("SELECT 1 FROM patch.sqlite_master LIMIT 1");
                }
                catch (SqliteException e)
                {
                    throw new PatchApplyException("לא ניתן לפתוח את קובץ העדכון: " + e.Message, inner: e);
                }
                AssertCompatible(db, m);

                var preFk = options.CheckForeignKeys || options.RemoveOrphans
                    ? FkViolations(db, progress)
                    : new HashSet<FkViolation>();

                using (var tx = db.Begin())
                {
                    db.Exec("PRAGMA defer_foreign_keys=ON");

                    progress?.Report("מעדכן את מבנה הקובץ…");
                    foreach (var sql in Strings(db, "SELECT sql FROM patch.migrations ORDER BY version ASC"))
                    {
                        db.Exec(sql);
                        result.Migrations++;
                    }
                    db.InvalidateCaches();

                    RunUpserts(db, result, progress);
                    // אחרי ה-upserts (שורות חדשות כבר בקובץ) ולפני ה-deletes (שורות שיימחקו עדיין בו).
                    CollectBooksTouched(db, result.BooksTouched);
                    RunDeletes(db, result, progress);
                    ApplyStat1Snapshot(db);
                    db.InvalidateCaches();

                    var unverifiable = new HashSet<string>(options.UnverifiableTables ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
                    if (options.BeforeVerify != null)
                    {
                        unverifiable.UnionWith(options.BeforeVerify(result) ?? Enumerable.Empty<string>());
                        db.InvalidateCaches();
                    }

                    if (options.RemoveOrphans)
                        unverifiable.UnionWith(RemoveNewOrphans(db, preFk, result, progress));

                    if (options.CheckForeignKeys)
                    {
                        var added = FkViolations(db, progress).Where(v => !preFk.Contains(v)).ToList();
                        if (added.Count > 0)
                            throw new PatchApplyException($"אחרי העדכון נמצאו {added.Count:N0} הפרות חדשות של קשרים בין טבלאות: " + Describe(added));
                    }

                    Verify(db, m, fromOrder, toOrder, unverifiable, result, progress);

                    progress?.Report("שומר…");
                    tx.Commit();
                }
                return result;
            }
            finally
            {
                db.InvalidateCaches();
                if (attached)
                {
                    try { db.Exec("DETACH DATABASE patch"); }
                    catch (SqliteException) { }
                }
                db.Exec("PRAGMA foreign_keys=OFF");
            }
        }

        private static void AssertCompatible(SeforimDb db, PatchManifest m)
        {
            var format = PatchMetaInt(db, "schema_version");
            if (format == null) throw new PatchApplyException("בקובץ העדכון חסרה גרסת הפורמט.");
            if (format < 1 || format > PatchTables.SupportedPatchFormatVersion)
                throw new PatchApplyException($"פורמט העדכון ({format}) חדש מדי לתוכנה.");
            if (m.ToSchemaVersion >= 4 && m.PatchFormatVersion == null)
                throw new PatchApplyException("במניפסט חסרה גרסת הפורמט.");
            if (m.PatchFormatVersion != null && format != m.PatchFormatVersion)
                throw new PatchApplyException("גרסת הפורמט בקובץ העדכון אינה תואמת למניפסט.");
            var from = PatchMetaInt(db, "from_version");
            var to = PatchMetaInt(db, "to_version");
            if (from != m.FromVersion || to != m.ToVersion)
                throw new PatchApplyException($"קובץ העדכון ({from}→{to}) אינו תואם למניפסט ({m.FromVersion}→{m.ToVersion}).");
        }

        private static int? PatchMetaInt(SeforimDb db, string key)
        {
            try
            {
                var v = db.Scalar("SELECT value FROM patch.patch_meta WHERE key=$k LIMIT 1", ("$k", key));
                return int.TryParse(Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture), out var i) ? i : (int?)null;
            }
            catch (SqliteException) { return null; }
        }

        private static void RunUpserts(SeforimDb db, PatchApplyResult result, IProgress<string> progress)
        {
            foreach (var table in PatchTables.InFkOrder)
            {
                var patchTable = "upsert_" + table.Name;
                if (!HasTable(db, "patch", patchTable)) continue;
                var cols = PatchColumns(db, patchTable);
                if (cols.Count == 0) continue;
                progress?.Report($"מעדכן את {table.Name}…");

                var colsCsv = string.Join(",", cols.Select(c => $"\"{c}\""));
                var pkCsv = string.Join(",", table.PrimaryKey.Select(c => $"\"{c}\""));
                var nonPk = cols.Where(c => !table.PrimaryKey.Contains(c)).ToList();
                string conflict;
                if (!table.Updatable || nonPk.Count == 0) conflict = $"ON CONFLICT({pkCsv}) DO NOTHING";
                else
                {
                    var set = string.Join(",", nonPk.Select(c => $"\"{c}\" = excluded.\"{c}\""));
                    // שורה זהה לא נכתבת. ההשוואה כמו בחתימה: typeof מבדיל 2 מ-2.0, ו-BINARY עוקף NOCASE.
                    var changed = string.Join(" OR ", nonPk.Select(c =>
                        $"\"{table.Name}\".\"{c}\" IS NOT excluded.\"{c}\" COLLATE BINARY OR typeof(\"{table.Name}\".\"{c}\") IS NOT typeof(excluded.\"{c}\")"));
                    conflict = $"ON CONFLICT({pkCsv}) DO UPDATE SET {set} WHERE {changed}";
                }
                try
                {
                    // ה-WHERE true נדרש: בלעדיו ה-ON CONFLICT משויך ל-SELECT ולא ל-INSERT.
                    result.Upserts[table.Name] = db.Exec(
                        $"INSERT INTO \"{table.Name}\" ({colsCsv}) SELECT {colsCsv} FROM patch.\"{patchTable}\" WHERE true {conflict}");
                }
                catch (SqliteException e) when (e.SqliteErrorCode == SqliteConstraint)
                {
                    throw new PatchApplyException($"העדכון אינו מתאים לקובץ הזה (התנגשות בטבלה {table.Name}): {e.Message}", inner: e);
                }
            }
        }

        private static void RunDeletes(SeforimDb db, PatchApplyResult result, IProgress<string> progress)
        {
            foreach (var table in PatchTables.InFkOrder.Reverse())
            {
                var patchTable = "delete_" + table.Name;
                if (!HasTable(db, "patch", patchTable) || table.PrimaryKey.Count == 0) continue;
                progress?.Report($"מוחק מ-{table.Name}…");
                var pkCsv = string.Join(",", table.PrimaryKey.Select(c => $"\"{c}\""));
                var keys = table.PrimaryKey.Count == 1 ? $"\"{table.PrimaryKey[0]}\"" : pkCsv;
                var target = table.PrimaryKey.Count == 1 ? keys : $"({pkCsv})";
                result.Deletes[table.Name] = db.Exec($"DELETE FROM \"{table.Name}\" WHERE {target} IN (SELECT {keys} FROM patch.\"{patchTable}\")");
            }
        }

        /// <summary>מעתיק את סטטיסטיקות המתכנן של הקובץ המלא, כמו _applyStat1Snapshot.</summary>
        private static void ApplyStat1Snapshot(SeforimDb db)
        {
            if (!HasTable(db, "patch", Stat1Snapshot)) return;
            if (!HasTable(db, "main", "sqlite_stat1")) db.Exec("ANALYZE main.sqlite_schema");
            db.Exec("DELETE FROM main.sqlite_stat1");
            db.Exec($"INSERT INTO main.sqlite_stat1 (tbl, idx, stat) SELECT tbl, idx, stat FROM patch.\"{Stat1Snapshot}\"");
            if (HasTable(db, "main", "sqlite_stat4")) db.Exec("DELETE FROM main.sqlite_stat4");
        }

        /// <summary>מזהי הספרים שתוכן החיפוש שלהם הושפע, בדיוק כמו _collectBooksTouched.</summary>
        private static void CollectBooksTouched(SeforimDb db, HashSet<long> touched)
        {
            void Collect(string patchTable, string sql, params string[] joins)
            {
                if (!HasTable(db, "patch", patchTable)) return;
                if (joins.Any(t => !HasTable(db, "main", t))) return;
                using (var cmd = db.Command(sql))
                using (var r = cmd.ExecuteReader())
                    while (r.Read())
                        if (!r.IsDBNull(0) && r.GetValue(0) is long id) touched.Add(id);
            }

            Collect("upsert_book", "SELECT DISTINCT id FROM patch.upsert_book");
            Collect("delete_book", "SELECT DISTINCT id FROM patch.delete_book");
            foreach (var op in new[] { "upsert", "delete" })
            {
                Collect($"{op}_line", $"SELECT DISTINCT l.bookId FROM patch.{op}_line p JOIN main.line l ON l.id = p.id", "line");
                Collect($"{op}_line_content", $"SELECT DISTINCT l.bookId FROM patch.{op}_line_content p JOIN main.line l ON l.id = p.id", "line");
                Collect($"{op}_tocEntry", $"SELECT DISTINCT t.bookId FROM patch.{op}_tocEntry p JOIN main.tocEntry t ON t.id = p.id", "tocEntry");
                Collect($"{op}_line_note", $"SELECT DISTINCT l.bookId FROM patch.{op}_line_note p JOIN main.line l ON l.id = p.lineId", "line");
                Collect($"{op}_line_toc", $"SELECT DISTINCT l.bookId FROM patch.{op}_line_toc p JOIN main.line l ON l.id = p.lineId", "line");
                Collect($"{op}_tocText", $"SELECT DISTINCT t.bookId FROM patch.{op}_tocText p JOIN main.tocEntry t ON t.textId = p.id", "tocEntry");
                Collect($"{op}_tocText", $"SELECT DISTINCT s.bookId FROM patch.{op}_tocText p JOIN main.alt_toc_entry a ON a.textId = p.id " +
                                         "JOIN main.alt_toc_structure s ON s.id = a.structureId", "alt_toc_entry", "alt_toc_structure");
                Collect($"{op}_alt_toc_structure", $"SELECT DISTINCT s.bookId FROM patch.{op}_alt_toc_structure p JOIN main.alt_toc_structure s ON s.id = p.id", "alt_toc_structure");
                Collect($"{op}_alt_toc_entry", $"SELECT DISTINCT s.bookId FROM patch.{op}_alt_toc_entry p JOIN main.alt_toc_entry a ON a.id = p.id " +
                                               "JOIN main.alt_toc_structure s ON s.id = a.structureId", "alt_toc_entry", "alt_toc_structure");
                Collect($"{op}_line_alt_toc", $"SELECT DISTINCT l.bookId FROM patch.{op}_line_alt_toc p JOIN main.line l ON l.id = p.lineId", "line");
                foreach (var t in new[] { "book_author", "book_topic", "book_acronym" })
                    Collect($"{op}_{t}", $"SELECT DISTINCT bookId FROM patch.{op}_{t}");
                Collect($"{op}_book_base_text", $"SELECT DISTINCT bookId FROM patch.{op}_book_base_text");
                Collect($"{op}_book_base_text", $"SELECT DISTINCT baseBookId FROM patch.{op}_book_base_text");
            }
        }

        /// <summary>
        /// אימות לפני השמירה. כמו האימות החלקי של אוצריא: כל טבלה שהעדכון שינה (או שחתימתה
        /// משתנה בין הגרסאות) חייבת להתאים בדיוק. טבלאות שהמשתמש שינה בכוונה לא נבדקות ונרשמות.
        /// </summary>
        private static void Verify(SeforimDb db, PatchManifest m, IReadOnlyList<string> fromOrder, IReadOnlyList<string> toOrder,
            ISet<string> unverifiable, PatchApplyResult result, IProgress<string> progress)
        {
            var from = m.FromTableContentHashes;
            var to = m.ToTableContentHashes;
            var consistent = from != null && to != null && SameKeys(from.Keys, fromOrder) && SameKeys(to.Keys, toOrder);
            if (!consistent)
            {
                if (unverifiable.Count > 0)
                    throw new PatchApplyException("במניפסט אין חתימה לכל טבלה, ולכן אי אפשר לאמת עדכון של קובץ ערוך.");
                progress?.Report("מאמת את כל הקובץ…");
                var whole = LogicalHasher.Compute(db, m.ToSchemaVersion).WholeHash;
                if (whole != m.ToContentHash)
                    throw new PatchApplyException("הקובץ אחרי העדכון אינו זהה לגרסה הרשמית.", contentMismatch: true);
                result.VerifiedTables.AddRange(toOrder);
                return;
            }

            var changedByPatch = toOrder.Where(t => t == "schema_meta" || !from.TryGetValue(t, out var f) || f != to[t] ||
                                                    PatchHasRows(db, "upsert_" + t) || PatchHasRows(db, "delete_" + t)).ToList();
            var toVerify = changedByPatch.Where(t => !unverifiable.Contains(t)).ToList();
            result.UnverifiedTables.AddRange(changedByPatch.Where(unverifiable.Contains));
            result.DeferredTables.AddRange(toOrder.Where(t => !changedByPatch.Contains(t)));

            progress?.Report($"מאמת {toVerify.Count} טבלאות מול החתימה הרשמית…");
            var report = LogicalHasher.Compute(db, m.ToSchemaVersion, new HashSet<string>(toVerify, StringComparer.Ordinal));
            var bad = toVerify.Where(t => report.TableHashes[t] != to[t]).ToList();
            if (bad.Count > 0)
                throw new PatchApplyException("אחרי העדכון התוכן אינו זהה לגרסה הרשמית בטבלאות: " + string.Join(", ", bad),
                    contentMismatch: true, tables: bad);
            result.VerifiedTables.AddRange(toVerify);
        }

        private static bool SameKeys(IEnumerable<string> keys, IReadOnlyList<string> order)
        {
            var set = new HashSet<string>(keys, StringComparer.Ordinal);
            return set.Count == order.Count && order.All(set.Contains);
        }

        private static bool PatchHasRows(SeforimDb db, string name) =>
            HasTable(db, "patch", name) && db.Scalar($"SELECT 1 FROM patch.\"{name}\" LIMIT 1") != null;

        internal static bool HasTable(SeforimDb db, string schema, string name) =>
            db.Scalar($"SELECT 1 FROM {schema}.sqlite_master WHERE type='table' AND name=$n LIMIT 1", ("$n", name)) != null;

        private static List<string> PatchColumns(SeforimDb db, string name)
        {
            var cols = new List<string>();
            using (var cmd = db.Command($"PRAGMA patch.table_info(\"{name}\")"))
            using (var r = cmd.ExecuteReader())
                while (r.Read()) cols.Add(r.GetString(1));
            return cols;
        }

        private static List<string> Strings(SeforimDb db, string sql)
        {
            var list = new List<string>();
            using (var cmd = db.Command(sql))
            using (var r = cmd.ExecuteReader())
                while (r.Read()) list.Add(r.GetString(0));
            return list;
        }

        /// <summary>שורה שמפנה לשורה שאינה קיימת (פלט PRAGMA foreign_key_check).</summary>
        private struct FkViolation : IEquatable<FkViolation>
        {
            public string Table;
            public long? RowId;
            public string Parent;
            public long FkId;

            public bool Equals(FkViolation o) => Table == o.Table && RowId == o.RowId && Parent == o.Parent && FkId == o.FkId;
            public override bool Equals(object obj) => obj is FkViolation o && Equals(o);
            public override int GetHashCode() => (Table, RowId, Parent, FkId).GetHashCode();
        }

        private static HashSet<FkViolation> FkViolations(SeforimDb db, IProgress<string> progress)
        {
            progress?.Report("בודק קשרים בין הטבלאות…");
            var set = new HashSet<FkViolation>();
            using (var cmd = db.Command("PRAGMA main.foreign_key_check"))
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                    set.Add(new FkViolation
                    {
                        Table = r.GetString(0),
                        RowId = r.IsDBNull(1) ? (long?)null : r.GetInt64(1),
                        Parent = r.GetString(2),
                        FkId = r.GetInt64(3),
                    });
            return set;
        }

        private static string Describe(IEnumerable<FkViolation> list) =>
            string.Join(", ", list.GroupBy(v => v.Table + "→" + v.Parent).Select(g => $"{g.Key} ({g.Count():N0})"));

        /// <summary>
        /// אחרי החלה מחדש של מחיקות: העדכון עלול להכניס שורות שמפנות לשורות שהמשתמש מחק לפני העדכון
        /// (למשל קישור מפירוש חדש לשורה בספר שנמחק). שורות כאלה מטופלות לפי הכלל של הקשר עצמו –
        /// CASCADE נמחקות, SET NULL מתאפסות – בדיוק כפי שהיה קורה אילו נמחקו יחד עם הספר.
        /// רק הפרות חדשות; כל קשר מסוג אחר, או שורה בלי rowid – העדכון מתבטל.
        /// מחזיר את הטבלאות שהשתנו (כולל מחיקות בשרשרת).
        /// </summary>
        private static IEnumerable<string> RemoveNewOrphans(SeforimDb db, HashSet<FkViolation> pre, PatchApplyResult result, IProgress<string> progress)
        {
            var touched = new HashSet<string>(StringComparer.Ordinal);
            var lastCount = int.MaxValue;
            for (var pass = 0; ; pass++)
            {
                var added = FkViolations(db, progress).Where(v => !pre.Contains(v)).ToList();
                if (added.Count == 0) break;
                if (pass >= 20 || added.Count >= lastCount)
                    throw new PatchApplyException("ניקוי השורות היתומות לא התקדם: " + Describe(added));
                lastCount = added.Count;
                foreach (var g in added.GroupBy(v => (v.Table, v.FkId)))
                {
                    var fk = ForeignKeyOf(db, g.Key.Table, g.Key.FkId);
                    if (fk.action != "CASCADE" && fk.action != "SET NULL")
                        throw new PatchApplyException($"בטבלה {g.Key.Table} נוצרו הפרות של קשר מסוג {fk.action}, ולכן העדכון בוטל.");
                    progress?.Report($"מנקה {g.Count():N0} שורות יתומות מ-{g.Key.Table}…");
                    var preSame = pre.Any(v => v.Table == g.Key.Table && v.FkId == g.Key.FkId);
                    long n = 0;
                    if (!preSame && fk.to.All(c => c != null))
                    {
                        // לקשר הזה לא היו הפרות לפני העדכון, ולכן כל שורה שמפנה לשורה חסרה היא חדשה.
                        var orphan = $"{string.Join(" AND ", fk.from.Select(c => $"t.\"{c}\" IS NOT NULL"))} AND NOT EXISTS " +
                                     $"(SELECT 1 FROM main.\"{fk.parent}\" AS p WHERE {string.Join(" AND ", fk.from.Zip(fk.to, (f, t) => $"p.\"{t}\" = t.\"{f}\""))})";
                        n = fk.action == "CASCADE"
                            ? db.Exec($"DELETE FROM main.\"{g.Key.Table}\" AS t WHERE {orphan}")
                            : db.Exec($"UPDATE main.\"{g.Key.Table}\" AS t SET {string.Join(",", fk.from.Select(c => $"\"{c}\" = NULL"))} WHERE {orphan}");
                    }
                    else
                    {
                        // היו הפרות קודמות באותו קשר: מטפלים רק בשורות החדשות, לפי rowid.
                        if (g.Any(v => v.RowId == null))
                            throw new PatchApplyException($"לא ניתן להבחין בין הפרות ישנות לחדשות בטבלה {g.Key.Table}, ולכן העדכון בוטל.");
                        foreach (var chunk in g.Select(v => v.RowId.Value).Distinct().Select((id, i) => (id, i)).GroupBy(x => x.i / 500))
                        {
                            var ids = string.Join(",", chunk.Select(x => x.id));
                            n += fk.action == "CASCADE"
                                ? db.Exec($"DELETE FROM main.\"{g.Key.Table}\" WHERE rowid IN ({ids})")
                                : db.Exec($"UPDATE main.\"{g.Key.Table}\" SET {string.Join(",", fk.from.Select(c => $"\"{c}\" = NULL"))} WHERE rowid IN ({ids})");
                        }
                    }
                    result.OrphansRemoved[g.Key.Table] = (result.OrphansRemoved.TryGetValue(g.Key.Table, out var c0) ? c0 : 0) + n;
                    touched.Add(g.Key.Table);
                }
            }
            // מחיקה גוררת מחיקות בשרשרת בטבלאות הבנות, ולכן גם הן אינן ניתנות לאימות.
            var queue = new Queue<string>(touched);
            while (queue.Count > 0)
            {
                var t = queue.Dequeue();
                foreach (var fk in db.Schema.ForeignKeys.Where(f => f.RefTable.Equals(t, StringComparison.OrdinalIgnoreCase) && f.OnDelete != DeleteAction.Block))
                    if (touched.Add(fk.Table)) queue.Enqueue(fk.Table);
            }
            return touched;
        }

        /// <summary>קשר אחד מ-PRAGMA foreign_key_list: הפעולה במחיקה, עמודות הבן, טבלת האב ועמודות האב (null = המפתח שלה).</summary>
        private static (string action, List<string> from, string parent, List<string> to) ForeignKeyOf(SeforimDb db, string table, long fkId)
        {
            string action = null, parent = null;
            var rows = new List<(long seq, string from, string to)>();
            using (var cmd = db.Command($"PRAGMA main.foreign_key_list(\"{table}\")"))
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                {
                    if (r.GetInt64(0) != fkId) continue;
                    rows.Add((r.GetInt64(1), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4)));
                    parent = r.GetString(2);
                    action = r.GetString(6).ToUpperInvariant();
                }
            if (action == null) throw new PatchApplyException($"הקשר {fkId} בטבלה {table} לא נמצא.");
            rows.Sort((a, b) => a.seq.CompareTo(b.seq));
            return (action, rows.Select(x => x.from).ToList(), parent, rows.Select(x => x.to).ToList());
        }
    }
}
