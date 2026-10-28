using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Data.Sqlite;

namespace Sefarim.Core
{
    /// <summary>
    /// הוספת ספר (זית בלבד). הספר נבנה בדיוק כמו שהמחולל של זית בונה ספר מקובץ טקסט: שורה לכל שורה,
    /// כותרות &lt;h1&gt;…&lt;h6&gt; הופכות לתוכן עניינים. כל השורות שנוספו נרשמות בגיבוי (כמפתחות בלבד),
    /// כך שביטול מוחק אותן בדיוק, ואחרי עדכון של זית אפשר לזהות שהספר כבר לא בקובץ ולהוסיף אותו שוב.
    /// </summary>
    public sealed partial class ChangeEngine
    {
        /// <summary>העמודות שההוספה ממלאת בכל טבלה. עמודת חובה שאינה כאן – ההוספה מסרבת (מבנה קובץ לא מוכר).</summary>
        private static readonly Dictionary<string, string[]> AddColumns = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["book"] = new[]
            {
                "id", "categoryId", "sourceId", "title", "heRef", "heShortDesc", "notesContent", "orderIndex", "totalLines", "isBaseBook",
                "hasTargumConnection", "hasReferenceConnection", "hasSourceConnection", "hasCommentaryConnection", "hasOtherConnection",
                "hasAltStructures", "hasTeamim", "hasNekudot",
            },
            ["line"] = new[] { "id", "bookId", "lineIndex", "content", "heRef", "tocEntryId", "charCount" },
            ["tocEntry"] = new[] { "id", "bookId", "parentId", "textId", "level", "lineId", "isLastChild", "hasChildren" },
            ["tocText"] = new[] { "id", "text" },
            ["line_toc"] = new[] { "lineId", "tocEntryId" },
            ["category"] = new[] { "id", "parentId", "title", "level", "orderIndex" },
            ["category_closure"] = new[] { "ancestorId", "descendantId" },
            ["book_has_links"] = new[] { "bookId", "hasSourceLinks", "hasTargetLinks" },
            ["source"] = new[] { "id", "name" },
            ["author"] = new[] { "id", "name" },
            ["book_author"] = new[] { "bookId", "authorId" },
        };

        private static readonly string[] OptionalAddTables = { "author", "book_author" };

        /// <summary>null = אפשר להוסיף ספרים לקובץ הזה; אחרת – ההסבר למה לא.</summary>
        public static string AddBookUnsupportedReason(SeforimDb db)
        {
            if (db.CompressedText) return "הוספת ספרים אפשרית רק בספרייה של זית.";
            foreach (var kv in AddColumns)
            {
                if (!db.Schema.Has(kv.Key))
                {
                    if (OptionalAddTables.Contains(kv.Key)) continue;
                    return $"מבנה הקובץ לא מוכר (אין טבלה {kv.Key}), ולכן אי אפשר להוסיף אליו ספרים.";
                }
                var t = db.Schema[kv.Key];
                var missing = kv.Value.FirstOrDefault(c => !t.HasColumn(c));
                if (missing != null) return $"מבנה הקובץ לא מוכר (אין עמודה {kv.Key}.{missing}), ולכן אי אפשר להוסיף אליו ספרים.";
                using (var cmd = db.Command($"PRAGMA main.table_info({Sql.Q(t.Name)})"))
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        var name = r.GetString(1);
                        var required = r.GetInt64(3) != 0 && r.IsDBNull(4);
                        if (required && !kv.Value.Contains(name, StringComparer.OrdinalIgnoreCase))
                            return $"בקובץ יש שדה חובה שהתוכנה לא מכירה ({kv.Key}.{name}), ולכן אי אפשר להוסיף אליו ספרים.";
                    }
                }
            }
            if (db.Scalar("SELECT 1 FROM source LIMIT 1") == null) return "מבנה הקובץ לא מוכר (טבלת המקורות ריקה).";
            return null;
        }

        public AppliedOp AddBook(OpIntent intent, BookText text, IProgress<string> progress = null)
        {
            var spec = intent.AddBookSpec ?? throw new ArgumentException("חסרים פרטי הספר.");
            var reason = AddBookUnsupportedReason(_db);
            if (reason != null) throw new NotSupportedException(reason);
            var title = (spec.Title ?? "").Trim();
            if (title.Length == 0) throw new InvalidOperationException("לא ניתן שם לספר.");
            var authors = (spec.Authors ?? new List<string>()).Select(a => a.Trim()).Where(a => a.Length > 0).Distinct().ToList();
            if (authors.Count > 0 && (!S.Has("author") || !S.Has("book_author")))
                throw new InvalidOperationException("בקובץ הזה אין טבלת מחברים.");
            if (text?.Lines == null || text.Lines.Count == 0) throw new InvalidOperationException("אין בספר שורות.");

            long opId;
            using (var tx = _db.Begin())
            {
                // ── הקטגוריה ──
                long categoryId;
                long? parentId = null;
                var categoryLevel = 0;
                string newCategoryTitle = null;
                string categoryPath;
                if (spec.NewCategoryTitle != null)
                {
                    newCategoryTitle = spec.NewCategoryTitle.Trim();
                    if (newCategoryTitle.Length == 0) throw new InvalidOperationException("לא ניתן שם לקטגוריה החדשה.");
                    if (spec.Category != null)
                    {
                        var lv = _db.Scalar("SELECT level FROM category WHERE id=$c", ("$c", spec.Category.Id));
                        if (lv == null) throw new InvalidOperationException($"קטגוריית האב \"{spec.Category.Path}\" לא נמצאה.");
                        parentId = spec.Category.Id;
                        categoryLevel = Convert.ToInt32(lv) + 1;
                    }
                    categoryId = PickId("category", spec.NewCategoryId);
                    spec.NewCategoryId = categoryId;
                    categoryPath = (parentId == null ? "" : CategoryPathOf(parentId.Value) + "/") + newCategoryTitle;
                }
                else
                {
                    if (spec.Category == null) throw new InvalidOperationException("לא נבחרה קטגוריה.");
                    if (_db.Scalar("SELECT 1 FROM category WHERE id=$c", ("$c", spec.Category.Id)) == null)
                        throw new InvalidOperationException($"הקטגוריה \"{spec.Category.Path}\" לא נמצאה.");
                    categoryId = spec.Category.Id;
                    categoryPath = CategoryPathOf(categoryId);
                }

                // מקור הספר: "Unknown" כשקיים (כמו ספרים שמקורם לא ידוע), אחרת המקור הראשון.
                var sourceId = Convert.ToInt64(_db.Scalar("SELECT id FROM source ORDER BY (name='Unknown') DESC, id LIMIT 1"));
                var bookId = PickId("book", spec.BookId);
                spec.BookId = bookId;
                spec.Title = title;
                spec.Authors = authors;
                intent.Books = new List<BookRef> { new BookRef { Id = bookId, Title = title, CategoryPath = categoryPath } };
                opId = _store.InsertOp(intent, LibraryVersion);

                progress?.Report("מוסיף את הספר…");
                if (newCategoryTitle != null)
                {
                    _db.Exec("INSERT INTO category(id, parentId, title, level, orderIndex) VALUES($c, $p, $t, $l, 999)",
                        ("$c", categoryId), ("$p", (object)parentId), ("$t", newCategoryTitle), ("$l", categoryLevel));
                    _db.Exec("INSERT INTO category_closure(ancestorId, descendantId) VALUES($c, $c)", ("$c", categoryId));
                    if (parentId != null)
                        _db.Exec("INSERT INTO category_closure(ancestorId, descendantId) SELECT ancestorId, $c FROM category_closure WHERE descendantId=$p",
                            ("$c", categoryId), ("$p", parentId.Value));
                }

                long? firstNewAuthor = null, lastNewAuthor = null;
                var authorIds = new List<long>();
                foreach (var name in authors)
                {
                    var existing = _db.Scalar("SELECT id FROM author WHERE name=$n ORDER BY id LIMIT 1", ("$n", name));
                    if (existing != null)
                    {
                        authorIds.Add(Convert.ToInt64(existing));
                        continue;
                    }
                    var id = PickId("author", null);
                    _db.Exec("INSERT INTO author(id, name) VALUES($id, $n)", ("$id", id), ("$n", name));
                    firstNewAuthor = firstNewAuthor ?? id;
                    lastNewAuthor = id;
                    authorIds.Add(id);
                }

                _db.Exec("INSERT INTO book(id, categoryId, sourceId, title, heRef, heShortDesc, notesContent, orderIndex, totalLines, isBaseBook, " +
                         "hasTargumConnection, hasReferenceConnection, hasSourceConnection, hasCommentaryConnection, hasOtherConnection, " +
                         "hasAltStructures, hasTeamim, hasNekudot) " +
                         "VALUES($id, $c, $s, $t, $t, $d, NULL, 999, $n, 0, 0, 0, 0, 0, 0, 0, $te, $ne)",
                    ("$id", bookId), ("$c", categoryId), ("$s", sourceId), ("$t", title),
                    ("$d", string.IsNullOrWhiteSpace(spec.ShortDesc) ? null : spec.ShortDesc.Trim()),
                    ("$n", text.Lines.Count), ("$te", text.HasTeamim ? 1 : 0), ("$ne", text.HasNekudot ? 1 : 0));
                _db.Exec("INSERT INTO book_has_links(bookId, hasSourceLinks, hasTargetLinks) VALUES($b, 0, 0)", ("$b", bookId));
                foreach (var a in authorIds.Distinct())
                    _db.Exec("INSERT INTO book_author(bookId, authorId) VALUES($b, $a)", ("$b", bookId), ("$a", a));

                var (firstText, lastText) = InsertBookText(bookId, title, text, progress);

                // ── רישום המפתחות של כל מה שנוסף ──
                progress?.Report("רושם את ההוספה בגיבוי…");
                var tables = new List<TableInfo>();
                long rows = 0;
                void After(string table, string where, params (string, object)[] args)
                {
                    var t = S[table];
                    _store.EnsureTables(t);
                    var all = new List<(string, object)> { ("$op", opId) };
                    all.AddRange(args);
                    var n = _db.Exec($"INSERT INTO {BackupStore.Alias}.{Q(BackupStore.AfterTable(t.Name))}(op_id,{KeyCols(t)}) " +
                                     $"SELECT $op,{KeyCols(t)} FROM {MainT(t)} WHERE {where}", all.ToArray());
                    if (n == 0) return;
                    tables.Add(t);
                    rows += n;
                }

                if (newCategoryTitle != null)
                {
                    After("category", "id=$c", ("$c", categoryId));
                    After("category_closure", "descendantId=$c", ("$c", categoryId));
                }
                if (firstNewAuthor != null) After("author", "id BETWEEN $a0 AND $a1", ("$a0", firstNewAuthor.Value), ("$a1", lastNewAuthor.Value));
                After("book", "id=$b", ("$b", bookId));
                After("book_has_links", "bookId=$b", ("$b", bookId));
                if (authorIds.Count > 0) After("book_author", "bookId=$b", ("$b", bookId));
                if (lastText >= firstText) After("tocText", "id BETWEEN $x0 AND $x1", ("$x0", firstText), ("$x1", lastText));
                After("tocEntry", "bookId=$b", ("$b", bookId));
                After("line", "bookId=$b", ("$b", bookId));
                After("line_toc", "lineId IN (SELECT id FROM line WHERE bookId=$b)", ("$b", bookId));

                progress?.Report("מחשב טביעת אצבע…");
                // אין שורות "לפני": טביעת האצבע של הגיבוי היא של קבוצה ריקה, ובביטול נבדק שהשורות שנוספו נעלמו.
                var hashes = tables.ToDictionary(t => t.Name, t => HashBackup(opId, t));
                _store.UpdateOp(opId, OpState.Applied, rows: rows, tables: tables.Select(t => t.Name).ToList(), hashes: hashes);
                _store.SetAfterHashes(opId, tables.ToDictionary(t => t.Name, t => HashAdded(opId, t)));
                tx.Commit();
            }
            return _store.LoadOps().First(o => o.Id == opId);
        }

        /// <summary>
        /// מכניס את השורות ואת תוכן העניינים, כמו processLinesWithTocEntries בזית. מחזיר את טווח
        /// המזהים של טקסטי כותרות חדשים (טקסט כותרת שכבר קיים בקובץ משותף לכל הספרים ואינו נוסף שוב).
        /// </summary>
        private (long firstText, long lastText) InsertBookText(long bookId, string title, BookText text, IProgress<string> progress)
        {
            var nextLine = PickId("line", null);
            var nextToc = PickId("tocEntry", null);
            var firstText = PickId("tocText", null);
            var nextText = firstText;
            var textIds = new Dictionary<string, long>(StringComparer.Ordinal);
            var stack = new SortedDictionary<int, long>();
            var entries = new List<(long id, long? parent)>();
            long? owner = null;

            using (var insLine = Prepared("INSERT INTO line(id, bookId, lineIndex, content, heRef, tocEntryId, charCount) VALUES($id, $b, $i, $c, $r, $t, $n)",
                       "$id", "$b", "$i", "$c", "$r", "$t", "$n"))
            using (var insToc = Prepared("INSERT INTO tocEntry(id, bookId, parentId, textId, level, lineId, isLastChild, hasChildren) VALUES($id, $b, $p, $x, $l, $line, 0, 0)",
                       "$id", "$b", "$p", "$x", "$l", "$line"))
            using (var insLineToc = Prepared("INSERT INTO line_toc(lineId, tocEntryId) VALUES($line, $t)", "$line", "$t"))
            using (var findText = Prepared("SELECT id FROM tocText WHERE text=$x", "$x"))
            using (var insText = Prepared("INSERT INTO tocText(id, text) VALUES($id, $x)", "$id", "$x"))
            {
                for (var i = 0; i < text.Lines.Count; i++)
                {
                    if (i % 5000 == 0) progress?.Report($"מוסיף שורות ({i:N0} מתוך {text.Lines.Count:N0})…");
                    var line = text.Lines[i];
                    var lineId = nextLine++;
                    var level = BookImport.HeadingLevel(line);
                    var heading = level > 0 ? BookImport.HeadingText(line) : "";
                    // כותרת ריקה נשמרת כשורה רגילה, כדי שמספרי השורות יישארו רצופים.
                    if (level > 0 && heading.Length > 0)
                    {
                        long? parent = null;
                        for (var l = level - 1; l >= 1 && parent == null; l--)
                            if (stack.TryGetValue(l, out var p)) parent = p;
                        foreach (var deeper in stack.Keys.Where(k => k >= level).ToList()) stack.Remove(deeper);

                        if (!textIds.TryGetValue(heading, out var textId))
                        {
                            findText.Parameters["$x"].Value = heading;
                            var found = findText.ExecuteScalar();
                            if (found != null && !(found is DBNull)) textId = Convert.ToInt64(found);
                            else
                            {
                                textId = nextText++;
                                Run(insText, textId, heading);
                            }
                            textIds[heading] = textId;
                        }

                        var tocId = nextToc++;
                        Run(insToc, tocId, bookId, parent, textId, level, lineId);
                        Run(insLine, lineId, bookId, i, line, null, tocId, BookImport.VisibleChars(line));
                        Run(insLineToc, lineId, tocId);
                        stack[level] = tocId;
                        owner = tocId;
                        entries.Add((tocId, parent));
                    }
                    else
                    {
                        Run(insLine, lineId, bookId, i, line, title + " " + (i + 1), null, BookImport.VisibleChars(line));
                        if (owner != null) Run(insLineToc, lineId, owner.Value);
                    }
                }
            }

            // מעבר שני, כמו בזית: למי יש ילדים, ומי הילד האחרון של אביו.
            var parents = new HashSet<long>(entries.Where(e => e.parent != null).Select(e => e.parent.Value));
            var lastChildren = entries.GroupBy(e => e.parent).Select(g => g.Last().id).ToList();
            using (var setParent = Prepared("UPDATE tocEntry SET hasChildren=1 WHERE id=$id", "$id"))
                foreach (var id in parents) Run(setParent, id);
            using (var setLast = Prepared("UPDATE tocEntry SET isLastChild=1 WHERE id=$id", "$id"))
                foreach (var id in lastChildren) Run(setLast, id);
            return (firstText, nextText - 1);
        }

        private SqliteCommand Prepared(string sql, params string[] names)
        {
            var cmd = _db.Command(sql);
            foreach (var n in names) cmd.Parameters.Add(new SqliteParameter(n, DBNull.Value));
            return cmd;
        }

        private static void Run(SqliteCommand cmd, params object[] values)
        {
            for (var i = 0; i < values.Length; i++) cmd.Parameters[i].Value = values[i] ?? DBNull.Value;
            cmd.ExecuteNonQuery();
        }

        /// <summary>מזהה פנוי בטווח השמור; המזהה המועדף (מההוספה הקודמת של אותו ספר) – אם הוא עדיין פנוי.</summary>
        private long PickId(string table, long? preferred)
        {
            if (preferred.HasValue && preferred.Value >= BookImport.ReservedIdBase &&
                _db.Scalar($"SELECT 1 FROM {Q(table)} WHERE id=$i", ("$i", preferred.Value)) == null)
                return preferred.Value;
            var max = Convert.ToInt64(_db.Scalar($"SELECT coalesce(max(id), 0) FROM {Q(table)}"));
            return Math.Max(BookImport.ReservedIdBase, max + 1);
        }

        private string CategoryPathOf(long id)
        {
            var parts = new List<string>();
            long? current = id;
            for (var guard = 0; current.HasValue && guard < 100; guard++)
            {
                using (var cmd = _db.Command("SELECT parentId, title FROM category WHERE id=$c", ("$c", current.Value)))
                using (var r = cmd.ExecuteReader())
                {
                    if (!r.Read()) break;
                    parts.Insert(0, r.IsDBNull(1) ? "" : r.GetString(1));
                    current = r.IsDBNull(0) ? (long?)null : r.GetInt64(0);
                }
            }
            return string.Join("/", parts);
        }

        /// <summary>
        /// האם ספר שנוסף עדיין בקובץ (לזיהוי קובץ שזית החליפה). לא לפי טביעת אצבע, כי עריכה מאוחרת
        /// של הספר (שם, מחברים) משנה את השורות ואינה אומרת שהקובץ הוחלף. הספר בקובץ – מוחל. הספר לא
        /// בקובץ – מוחל רק אם מחיקה מאוחרת יותר שלנו מחקה אותו (והיא שומרת אותו בגיבוי); אחרת הקובץ הוחלף.
        /// </summary>
        private bool AddedStillApplied(AppliedOp op)
        {
            var bookId = op.Intent.AddBookSpec?.BookId ?? op.Intent.Books.FirstOrDefault()?.Id;
            if (bookId == null) return false;
            // טקסטי כותרות ומחברים שההוספה יצרה לא נמחקים בשום פעולה (רק בביטול ההוספה עצמה).
            // אם אחד מהם חסר – הקובץ הוחלף, גם אם הספר עצמו נמחק בינתיים במחיקה שלנו.
            foreach (var name in new[] { "tocText", "author" }.Where(n => op.Tables.Contains(n) && S.Has(n)))
            {
                var t = S[name];
                var keys = Convert.ToInt64(_db.Scalar($"SELECT count(*) FROM {AddedKeys(op.Id, t)}"));
                if (CountAdded(op.Id, t) != keys) return false;
            }
            if (_db.Scalar("SELECT 1 FROM book WHERE id=$b", ("$b", bookId.Value)) != null) return true;
            if (!_store.TableExists(BackupStore.BeforeTable("book"))) return false;
            return _store.LoadOps().Any(o => o.Id > op.Id && o.State == OpState.Applied && o.Tables.Contains("book") &&
                                             _db.Scalar($"SELECT 1 FROM {BackupStore.Alias}.{Q(BackupStore.BeforeTable("book"))} WHERE op_id=$o AND id=$b",
                                                 ("$o", o.Id), ("$b", bookId.Value)) != null);
        }

        private string AddedKeys(long opId, TableInfo t) =>
            $"(SELECT DISTINCT {KeyCols(t)} FROM {BackupStore.Alias}.{Q(BackupStore.AfterTable(t.Name))} WHERE op_id={opId})";

        /// <summary>טביעת אצבע של השורות שהוספה רשמה (לפי המפתחות ב-a:), כפי שהן עכשיו בקובץ.</summary>
        private string HashAdded(long opId, TableInfo t)
        {
            if (!_store.TableExists(BackupStore.AfterTable(t.Name))) return null;
            using (var cmd = _db.Command($"SELECT {Sql.List(t.ColumnNames, "m")} FROM {AddedKeys(opId, t)} AS s " +
                                         $"JOIN {MainT(t)} AS m ON {KeyJoin(t, "m", "s")} ORDER BY {OrderBy(t, "m")}"))
                return RowHasher.Hash(cmd).hash;
        }

        private long CountAdded(long opId, TableInfo t)
        {
            if (!_store.TableExists(BackupStore.AfterTable(t.Name))) return 0;
            return Convert.ToInt64(_db.Scalar($"SELECT count(*) FROM {AddedKeys(opId, t)} AS s JOIN {MainT(t)} AS m ON {KeyJoin(t, "m", "s")}"));
        }
    }
}
