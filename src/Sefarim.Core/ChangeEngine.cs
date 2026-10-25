using System;
using System.Collections.Generic;
using System.Linq;

namespace Sefarim.Core
{
    public sealed class TablePlan
    {
        public string Table { get; set; }
        public long DeleteRows { get; set; }
        public long NullifyRows { get; set; }
    }

    /// <summary>תוצאת התכנון של מחיקה: כמה שורות יוסרו מכל טבלה, לפני שמשהו נכתב.</summary>
    public sealed class DeletePlan
    {
        internal int Token { get; set; }
        internal Dictionary<string, List<ForeignKeyInfo>> Nullify { get; } =
            new Dictionary<string, List<ForeignKeyInfo>>(StringComparer.OrdinalIgnoreCase);

        public OpIntent Intent { get; set; }
        public List<TablePlan> Tables { get; } = new List<TablePlan>();
        public List<string> Blockers { get; } = new List<string>();

        public long Count(string table) => Tables.FirstOrDefault(t => t.Table.Equals(table, StringComparison.OrdinalIgnoreCase))?.DeleteRows ?? 0;

        public long TotalRows => Tables.Sum(t => t.DeleteRows + t.NullifyRows);
    }

    /// <summary>
    /// מבצע שינויים בספרייה כך שאפשר להחזיר אותם בדיוק: לפני כל שינוי השורות
    /// המקוריות מועתקות למאגר הגיבוי עם טביעת אצבע, ובהחזרה נבדק שהתוכן זהה.
    /// </summary>
    public sealed class ChangeEngine
    {
        private readonly SeforimDb _db;
        private readonly BackupStore _store;
        private int _planToken;
        private readonly HashSet<string> _temps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public ChangeEngine(SeforimDb db, BackupStore store)
        {
            if (db.IsReadOnly) throw new InvalidOperationException("הקובץ נפתח לקריאה בלבד.");
            _db = db;
            _store = store;
            if (db.Schema.Triggers.Count > 0)
                throw new NotSupportedException("בקובץ יש טריגרים (" + string.Join(", ", db.Schema.Triggers) + ") והתוכנה לא יודעת לטפל בהם בבטחה.");
        }

        /// <summary>גרסת הספרייה שנרשמת עם כל פעולה, כדי לזהות אחר כך אם הקובץ הוחלף.</summary>
        public long? LibraryVersion { get; set; }

        private SchemaInfo S => _db.Schema;

        private static string Q(string s) => Sql.Q(s);

        private static string SelName(string t) => "temp." + Q("sel:" + t);

        private static string NulName(string t) => "temp." + Q("nul:" + t);

        private static string RefName(string t, string c) => "temp." + Q("ref:" + t + ":" + c);

        private static string MainT(TableInfo t) => "main." + Q(t.Name);

        private static string KeyCols(TableInfo t, string alias = null) => Sql.List(t.Key, alias);

        private static string KeyJoin(TableInfo t, string a, string b) =>
            string.Join(" AND ", t.Key.Select(k => $"{a}.{Q(k)} IS {b}.{Q(k)}"));

        private static string OrderBy(TableInfo t, string alias) =>
            string.Join(",", t.Key.Select(k => (alias == null ? "" : alias + ".") + Q(k) + " COLLATE BINARY"));

        /// <summary>תנאי ששורה בטבלה הראשית (בשמה המלא) נמצאת ברשימת המפתחות שב-source.</summary>
        private static string KeyIn(TableInfo t, string source)
        {
            var target = MainT(t);
            if (t.SimpleKey)
                return $"{target}.{Q(t.Key[0])} IN (SELECT {Q(t.Key[0])} FROM {source})";
            if (t.WithoutRowid)
                return $"({Sql.List(t.Key, target)}) IN (SELECT {KeyCols(t)} FROM {source})";
            return $"{target}.rowid IN (SELECT m2.rowid FROM {source} AS s2 JOIN {target} AS m2 ON {KeyJoin(t, "m2", "s2")})";
        }

        private void CreateKeyTemp(string name, TableInfo t)
        {
            _db.Exec($"DROP TABLE IF EXISTS {name}");
            _db.Exec($"CREATE TEMP TABLE {name.Substring(5)} ({KeyCols(t)}, PRIMARY KEY({KeyCols(t)}))");
            _temps.Add(name);
        }

        public void DropTemps()
        {
            foreach (var t in _temps) _db.Exec($"DROP TABLE IF EXISTS {t}");
            _temps.Clear();
        }

        // ───────────────────────── תכנון מחיקה ─────────────────────────

        private sealed class Sets
        {
            public readonly Dictionary<string, int> Version = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, int> RefVersion = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        }

        public DeletePlan PlanDelete(OpIntent intent, IEnumerable<long> bookIds, IEnumerable<long> categoryIds,
            IProgress<string> progress = null)
        {
            DropTemps();
            var plan = new DeletePlan { Token = ++_planToken, Intent = intent };
            var sets = new Sets();

            void EnsureSel(TableInfo t)
            {
                if (sets.Version.ContainsKey(t.Name)) return;
                CreateKeyTemp(SelName(t.Name), t);
                sets.Version[t.Name] = 0;
            }

            void AddRoots(string table, IEnumerable<long> ids)
            {
                var list = ids.Distinct().ToList();
                if (list.Count == 0) return;
                var t = S[table];
                EnsureSel(t);
                foreach (var id in list)
                    _db.Exec($"INSERT OR IGNORE INTO {SelName(t.Name)}({Q(t.Key[0])}) VALUES($id)", ("$id", id));
                sets.Version[t.Name]++;
            }

            // מקור המפתחות של טבלת האב: ה-sel עצמו כשהקשר הוא למפתח שלה, אחרת סט עזר של העמודה.
            string ParentSource(ForeignKeyInfo fk)
            {
                var p = S[fk.RefTable];
                if (p.Key.Count == 1 && p.Key[0].Equals(fk.RefColumn, StringComparison.OrdinalIgnoreCase))
                    return $"SELECT {Q(p.Key[0])} FROM {SelName(p.Name)}";
                var refName = RefName(p.Name, fk.RefColumn);
                var refKey = p.Name + ":" + fk.RefColumn;
                if (!sets.RefVersion.ContainsKey(refKey))
                {
                    _db.Exec($"CREATE TEMP TABLE {refName.Substring(5)} (k PRIMARY KEY)");
                    _temps.Add(refName);
                    sets.RefVersion[refKey] = 0;
                }
                if (sets.RefVersion[refKey] != sets.Version[p.Name])
                {
                    _db.Exec($"INSERT OR IGNORE INTO {refName}(k) SELECT m.{Q(fk.RefColumn)} FROM {SelName(p.Name)} AS s " +
                             $"JOIN {MainT(p)} AS m ON {KeyJoin(p, "m", "s")} WHERE m.{Q(fk.RefColumn)} IS NOT NULL");
                    sets.RefVersion[refKey] = sets.Version[p.Name];
                }
                return $"SELECT k FROM {refName}";
            }

            using (var tx = _db.Begin())
            {
                AddRoots("book", bookIds ?? Enumerable.Empty<long>());
                AddRoots("category", categoryIds ?? Enumerable.Empty<long>());

                var cascades = S.ForeignKeys.Where(f => f.OnDelete == DeleteAction.Cascade && S.Has(f.Table) && S.Has(f.RefTable)).ToList();
                var seen = cascades.ToDictionary(f => f, f => 0);
                var changed = true;
                var pass = 0;
                while (changed)
                {
                    changed = false;
                    pass++;
                    foreach (var fk in cascades)
                    {
                        if (!sets.Version.TryGetValue(fk.RefTable, out var pv) || pv == 0 || seen[fk] == pv) continue;
                        seen[fk] = pv;
                        var child = S[fk.Table];
                        EnsureSel(child);
                        progress?.Report($"מחשב מה יימחק ({child.Name})…");
                        var n = _db.Exec($"INSERT OR IGNORE INTO {SelName(child.Name)}({KeyCols(child)}) " +
                                         $"SELECT {KeyCols(child)} FROM {MainT(child)} WHERE {Q(fk.Column)} IN ({ParentSource(fk)})");
                        if (n > 0)
                        {
                            sets.Version[child.Name]++;
                            changed = true;
                        }
                    }
                    if (pass > 200) throw new InvalidOperationException("חישוב המחיקה לא התכנס.");
                }

                foreach (var fk in S.ForeignKeys.Where(f => f.OnDelete != DeleteAction.Cascade && S.Has(f.Table) && S.Has(f.RefTable)))
                {
                    if (!sets.Version.TryGetValue(fk.RefTable, out var pv) || pv == 0) continue;
                    var child = S[fk.Table];
                    var notDeleted = sets.Version.ContainsKey(child.Name)
                        ? $" AND NOT EXISTS (SELECT 1 FROM {SelName(child.Name)} AS s WHERE {KeyJoin(child, "s", "m")})"
                        : "";
                    var where = $"m.{Q(fk.Column)} IN ({ParentSource(fk)}){notDeleted}";
                    if (fk.OnDelete == DeleteAction.SetNull)
                    {
                        if (!_temps.Contains(NulName(child.Name))) CreateKeyTemp(NulName(child.Name), child);
                        var n = _db.Exec($"INSERT OR IGNORE INTO {NulName(child.Name)}({KeyCols(child)}) " +
                                         $"SELECT {KeyCols(child, "m")} FROM {MainT(child)} AS m WHERE {where}");
                        if (n > 0)
                        {
                            if (!plan.Nullify.TryGetValue(child.Name, out var list)) plan.Nullify[child.Name] = list = new List<ForeignKeyInfo>();
                            list.Add(fk);
                        }
                    }
                    else
                    {
                        var n = Convert.ToInt64(_db.Scalar($"SELECT count(*) FROM {MainT(child)} AS m WHERE {where}"));
                        if (n > 0) plan.Blockers.Add($"{n:N0} שורות בטבלה {child.Name} מפנות לשורות שיימחקו מ-{fk.RefTable}");
                    }
                }

                foreach (var name in sets.Version.Keys.Union(plan.Nullify.Keys, StringComparer.OrdinalIgnoreCase))
                {
                    var tp = new TablePlan { Table = S[name].Name };
                    if (sets.Version.ContainsKey(name)) tp.DeleteRows = Convert.ToInt64(_db.Scalar($"SELECT count(*) FROM {SelName(name)}"));
                    if (plan.Nullify.ContainsKey(name)) tp.NullifyRows = Convert.ToInt64(_db.Scalar($"SELECT count(*) FROM {NulName(name)}"));
                    if (tp.DeleteRows + tp.NullifyRows > 0) plan.Tables.Add(tp);
                }
                plan.Tables.Sort((a, b) => b.DeleteRows.CompareTo(a.DeleteRows));
                tx.Commit();
            }
            return plan;
        }

        // ───────────────────────── ביצוע מחיקה ─────────────────────────

        public AppliedOp ExecuteDelete(DeletePlan plan, IProgress<string> progress = null)
        {
            if (plan.Token != _planToken) throw new InvalidOperationException("התכנון אינו עדכני. יש לתכנן מחדש.");
            if (plan.Blockers.Count > 0) throw new InvalidOperationException("לא ניתן למחוק: " + string.Join("; ", plan.Blockers));
            if (plan.Tables.Count == 0) throw new InvalidOperationException("אין מה למחוק.");

            var tables = plan.Tables.Select(t => S[t.Table]).ToList();
            long opId;

            // שלב א: גיבוי לשורות המקוריות במאגר. הספרייה עוד לא נגעה.
            using (var tx = _db.Begin())
            {
                opId = _store.InsertOp(plan.Intent, LibraryVersion);
                long rows = 0;
                foreach (var t in tables)
                {
                    progress?.Report($"מגבה את {t.Name}…");
                    _store.EnsureTables(t);
                    var b = $"{BackupStore.Alias}.{Q(BackupStore.BeforeTable(t.Name))}";
                    var cols = Sql.List(t.ColumnNames);
                    if (_temps.Contains(SelName(t.Name)))
                        rows += _db.Exec($"INSERT INTO {b}(op_id,{cols}) SELECT $op,{Sql.List(t.ColumnNames, "m")} FROM {SelName(t.Name)} AS s " +
                                         $"JOIN {MainT(t)} AS m ON {KeyJoin(t, "m", "s")}", ("$op", opId));
                    if (_temps.Contains(NulName(t.Name)))
                    {
                        rows += _db.Exec($"INSERT INTO {b}(op_id,{cols}) SELECT $op,{Sql.List(t.ColumnNames, "m")} FROM {NulName(t.Name)} AS s " +
                                         $"JOIN {MainT(t)} AS m ON {KeyJoin(t, "m", "s")}", ("$op", opId));
                        _db.Exec($"INSERT INTO {BackupStore.Alias}.{Q(BackupStore.AfterTable(t.Name))}(op_id,{KeyCols(t)}) " +
                                 $"SELECT $op,{KeyCols(t)} FROM {NulName(t.Name)}", ("$op", opId));
                    }
                }
                progress?.Report("מחשב טביעת אצבע לגיבוי…");
                var hashes = tables.ToDictionary(t => t.Name, t => HashBackup(opId, t));
                _store.UpdateOp(opId, rows: rows, tables: tables.Select(t => t.Name).ToList(), hashes: hashes);
                tx.Commit();
            }

            // שלב ב: השינוי עצמו בספרייה.
            using (var tx = _db.Begin())
            {
                foreach (var kv in plan.Nullify)
                {
                    var t = S[kv.Key];
                    foreach (var fk in kv.Value)
                    {
                        progress?.Report($"מעדכן את {t.Name}…");
                        _db.Exec($"UPDATE {MainT(t)} SET {Q(fk.Column)} = NULL WHERE {KeyIn(t, NulName(t.Name))} " +
                                 $"AND {Q(fk.Column)} IN (SELECT {Q(S[fk.RefTable].Key[0])} FROM {SelName(fk.RefTable)})");
                    }
                }
                foreach (var t in tables.Where(t => _temps.Contains(SelName(t.Name))))
                {
                    progress?.Report($"מוחק מ-{t.Name}…");
                    _db.Exec($"DELETE FROM {MainT(t)} WHERE {KeyIn(t, SelName(t.Name))}");
                }
                _store.UpdateOp(opId, OpState.Applied);
                tx.Commit();
            }
            RecordAfter(opId, tables);
            DropTemps();
            _planToken++;
            return _store.LoadOps().First(o => o.Id == opId);
        }

        private string HashBackup(long opId, TableInfo t)
        {
            var b = $"{BackupStore.Alias}.{Q(BackupStore.BeforeTable(t.Name))}";
            using (var cmd = _db.Command($"SELECT {Sql.List(t.ColumnNames)} FROM {b} WHERE op_id=$op ORDER BY {OrderBy(t, null)}", ("$op", opId)))
                return RowHasher.Hash(cmd).hash;
        }

        /// <summary>שומר טביעת אצבע של השורות מיד אחרי השינוי.</summary>
        private void RecordAfter(long opId, IEnumerable<TableInfo> tables)
        {
            using (var tx = _db.Begin())
            {
                _store.SetAfterHashes(opId, tables.ToDictionary(t => t.Name, t => HashCurrent(opId, t)));
                tx.Commit();
            }
        }

        /// <summary>
        /// האם השינוי של הפעולה עדיין נמצא בקובץ, כלומר השורות נראות בדיוק כמו מיד אחרי
        /// השינוי. משמש לזהות אם הקובץ באמת הוחלף, או שרק מספר הגרסה השתנה.
        /// </summary>
        public bool StillApplied(AppliedOp op)
        {
            if (op.AfterHashes == null || op.AfterHashes.Count == 0) return false;
            using (var tx = _db.Begin())
            {
                var same = op.Tables.All(n => S.Has(n) && op.AfterHashes.TryGetValue(n, out var h) && HashCurrent(op.Id, S[n]) == h);
                tx.Commit();
                return same;
            }
        }

        private string HashCurrent(long opId, TableInfo t)
        {
            var b = $"{BackupStore.Alias}.{Q(BackupStore.BeforeTable(t.Name))}";
            using (var cmd = _db.Command(
                       $"SELECT {Sql.List(t.ColumnNames, "m")} FROM (SELECT DISTINCT {KeyCols(t)} FROM {b} WHERE op_id=$op) AS s " +
                       $"JOIN {MainT(t)} AS m ON {KeyJoin(t, "m", "s")} ORDER BY {OrderBy(t, "m")}", ("$op", opId)))
                return RowHasher.Hash(cmd).hash;
        }

        // ───────────────────────── עריכה ─────────────────────────

        public AppliedOp EditBook(OpIntent intent, long bookId, BookChanges ch, IProgress<string> progress = null)
        {
            if (ch == null || ch.IsEmpty) throw new InvalidOperationException("לא נבחר שום שינוי.");
            var book = S["book"];
            if (_db.Scalar("SELECT 1 FROM book WHERE id=$b", ("$b", bookId)) == null)
                throw new InvalidOperationException("הספר לא נמצא בקובץ.");
            if (ch.LongDesc != null && !book.HasColumn("heDesc"))
                throw new InvalidOperationException("בקובץ הזה אין שדה תיאור מלא.");
            if (ch.MoveTo != null && _db.Scalar("SELECT 1 FROM category WHERE id=$c", ("$c", ch.MoveTo.Id)) == null)
                throw new InvalidOperationException("קטגוריית היעד לא נמצאה.");

            var tables = new List<TableInfo> { book };
            List<(long id, string name, bool isNew)> authors = null;
            if (ch.Authors != null)
            {
                if (!S.Has("book_author") || !S.Has("author")) throw new InvalidOperationException("בקובץ הזה אין טבלת מחברים.");
                tables.Add(S["book_author"]);
                tables.Add(S["author"]);
                authors = new List<(long, string, bool)>();
                var next = Convert.ToInt64(_db.Scalar("SELECT coalesce(max(id),0)+1 FROM author"));
                foreach (var name in ch.Authors.Select(a => a.Trim()).Where(a => a.Length > 0).Distinct())
                {
                    var existing = _db.Scalar("SELECT id FROM author WHERE name=$n", ("$n", name));
                    authors.Add(existing != null ? (Convert.ToInt64(existing), name, false) : (next++, name, true));
                }
            }

            long opId;
            using (var tx = _db.Begin())
            {
                opId = _store.InsertOp(intent, LibraryVersion);
                foreach (var t in tables) _store.EnsureTables(t);
                BackupWhere(opId, book, "id=$k", bookId);
                AddAfterKey(opId, book, bookId);
                if (authors != null)
                {
                    BackupWhere(opId, S["book_author"], "bookId=$k", bookId);
                    foreach (var a in authors)
                        _db.Exec($"INSERT INTO {BackupStore.Alias}.{Q(BackupStore.AfterTable("book_author"))}(op_id,{Q("bookId")},{Q("authorId")}) VALUES($op,$b,$a)",
                            ("$op", opId), ("$b", bookId), ("$a", a.id));
                    foreach (var a in authors.Where(a => a.isNew)) AddAfterKey(opId, S["author"], a.id);
                }
                var hashes = tables.ToDictionary(t => t.Name, t => HashBackup(opId, t));
                _store.UpdateOp(opId, rows: tables.Count, tables: tables.Select(t => t.Name).ToList(), hashes: hashes);
                tx.Commit();
            }

            using (var tx = _db.Begin())
            {
                progress?.Report("שומר את השינויים…");
                var sets = new List<string>();
                var args = new List<(string, object)> { ("$id", bookId) };
                if (ch.Title != null) { sets.Add("title=$title"); args.Add(("$title", ch.Title)); }
                if (ch.ShortDesc != null) { sets.Add("heShortDesc=$sd"); args.Add(("$sd", ch.ShortDesc.Length == 0 ? null : ch.ShortDesc)); }
                if (ch.LongDesc != null) { sets.Add("heDesc=$ld"); args.Add(("$ld", ch.LongDesc.Length == 0 ? null : ch.LongDesc)); }
                if (ch.OrderIndex != null) { sets.Add("orderIndex=$oi"); args.Add(("$oi", ch.OrderIndex.Value)); }
                if (ch.MoveTo != null) { sets.Add("categoryId=$cat"); args.Add(("$cat", ch.MoveTo.Id)); }
                if (sets.Count > 0) _db.Exec($"UPDATE book SET {string.Join(",", sets)} WHERE id=$id", args.ToArray());
                if (authors != null)
                {
                    foreach (var a in authors.Where(a => a.isNew))
                        _db.Exec("INSERT INTO author(id, name) VALUES($id, $n)", ("$id", a.id), ("$n", a.name));
                    _db.Exec("DELETE FROM book_author WHERE bookId=$b", ("$b", bookId));
                    foreach (var a in authors)
                        _db.Exec("INSERT INTO book_author(bookId, authorId) VALUES($b, $a)", ("$b", bookId), ("$a", a.id));
                }
                _store.UpdateOp(opId, OpState.Applied);
                tx.Commit();
            }
            RecordAfter(opId, tables);
            return _store.LoadOps().First(o => o.Id == opId);
        }

        public AppliedOp EditCategory(OpIntent intent, long categoryId, CategoryChanges ch)
        {
            if (ch == null || ch.IsEmpty) throw new InvalidOperationException("לא נבחר שום שינוי.");
            var cat = S["category"];
            if (_db.Scalar("SELECT 1 FROM category WHERE id=$c", ("$c", categoryId)) == null)
                throw new InvalidOperationException("הקטגוריה לא נמצאה בקובץ.");
            long opId;
            using (var tx = _db.Begin())
            {
                opId = _store.InsertOp(intent, LibraryVersion);
                _store.EnsureTables(cat);
                BackupWhere(opId, cat, "id=$k", categoryId);
                AddAfterKey(opId, cat, categoryId);
                _store.UpdateOp(opId, rows: 1, tables: new List<string> { cat.Name },
                    hashes: new Dictionary<string, string> { [cat.Name] = HashBackup(opId, cat) });
                tx.Commit();
            }
            using (var tx = _db.Begin())
            {
                if (ch.Title != null) _db.Exec("UPDATE category SET title=$t WHERE id=$id", ("$t", ch.Title), ("$id", categoryId));
                if (ch.OrderIndex != null) _db.Exec("UPDATE category SET orderIndex=$o WHERE id=$id", ("$o", ch.OrderIndex.Value), ("$id", categoryId));
                _store.UpdateOp(opId, OpState.Applied);
                tx.Commit();
            }
            RecordAfter(opId, new[] { cat });
            return _store.LoadOps().First(o => o.Id == opId);
        }

        private void BackupWhere(long opId, TableInfo t, string where, object key)
        {
            var b = $"{BackupStore.Alias}.{Q(BackupStore.BeforeTable(t.Name))}";
            _db.Exec($"INSERT INTO {b}(op_id,{Sql.List(t.ColumnNames)}) SELECT $op,{Sql.List(t.ColumnNames)} FROM {MainT(t)} WHERE {where}",
                ("$op", opId), ("$k", key));
        }

        private void AddAfterKey(long opId, TableInfo t, object key)
        {
            if (t.Key.Count != 1) throw new InvalidOperationException($"לטבלה {t.Name} מפתח מורכב.");
            _db.Exec($"INSERT INTO {BackupStore.Alias}.{Q(BackupStore.AfterTable(t.Name))}(op_id,{Q(t.Key[0])}) VALUES($op,$k)",
                ("$op", opId), ("$k", key));
        }

        // ───────────────────────── החזרה ─────────────────────────

        /// <summary>
        /// מחזיר פעולה אחת: מסיר את מה שהיא הוסיפה ומחזיר את השורות המקוריות, ואז
        /// מוודא שטביעת האצבע של התוכן זהה לזו של הגיבוי. אם לא, הכול מתבטל.
        /// </summary>
        public void Restore(AppliedOp op, IProgress<string> progress = null)
        {
            if (op.State == OpState.Restored) return;
            using (var tx = _db.Begin())
            {
                RestoreRows(op, progress);
                _store.DropOpRows(op);
                _store.UpdateOp(op.Id, OpState.Restored);
                tx.Commit();
            }
            op.State = OpState.Restored;
        }

        private void RestoreRows(AppliedOp op, IProgress<string> progress)
        {
            foreach (var name in op.Tables)
            {
                var t = S[name] ?? throw new InvalidOperationException($"הטבלה {name} כבר לא קיימת בקובץ, ולכן אי אפשר להחזיר.");
                var after = BackupStore.AfterTable(name);
                if (_store.TableExists(after))
                    _db.Exec($"DELETE FROM {MainT(t)} WHERE {KeyIn(t, $"(SELECT * FROM {BackupStore.Alias}.{Q(after)} WHERE op_id={op.Id})")}");
                progress?.Report($"מחזיר את {name}…");
                var cols = Sql.List(t.ColumnNames);
                _db.Exec($"INSERT INTO {MainT(t)}({cols}) SELECT {cols} FROM {BackupStore.Alias}.{Q(BackupStore.BeforeTable(name))} WHERE op_id=$op",
                    ("$op", op.Id));
            }
            foreach (var name in op.Tables)
            {
                progress?.Report($"מוודא את {name}…");
                var actual = HashCurrent(op.Id, S[name]);
                if (!op.Hashes.TryGetValue(name, out var expected) || actual != expected)
                    throw new InvalidOperationException($"הבדיקה נכשלה: התוכן שהוחזר לטבלה {name} אינו זהה למקור. שום דבר לא שונה.");
            }
        }

        /// <summary>
        /// טיפול בפעולה שנקטעה באמצע (למשל בגלל הפסקת חשמל): אם הספרייה לא השתנתה,
        /// הפעולה נמחקת; אם השתנתה, היא מסומנת כמבוצעת.
        /// </summary>
        public void RecoverPending()
        {
            foreach (var op in _store.LoadOps().Where(o => o.State == OpState.Pending))
            {
                var untouched = op.Tables.All(n => S.Has(n) && op.Hashes.TryGetValue(n, out var h) && HashCurrent(op.Id, S[n]) == h);
                using (var tx = _db.Begin())
                {
                    if (untouched)
                    {
                        _store.DropOpRows(op);
                        _store.DeleteOp(op.Id);
                    }
                    else _store.UpdateOp(op.Id, OpState.Applied);
                    tx.Commit();
                }
            }
        }
    }
}
