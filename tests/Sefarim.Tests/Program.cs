using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Data.Sqlite;
using Sefarim.Core;

namespace Sefarim.Tests
{
    /// <summary>
    /// בדיקת מנוע השינויים על עותק של ספרייה אמיתית:
    /// טביעת אצבע מלאה → מחיקות ועריכות → החזרה → טביעת אצבע זהה → החלה מחדש → החזרה.
    /// שימוש: Sefarim.Tests &lt;עותק של seforim.db&gt; &lt;תיקיית גיבוי&gt; [--quick]
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            if (args.Length < 2)
            {
                Console.WriteLine("שימוש: Sefarim.Tests <עותק של seforim.db> <תיקיית גיבוי> [--quick]");
                return 2;
            }
            var dbPath = args[0];
            var backupRoot = args[1];
            var quick = args.Contains("--quick");
            var kind = args.Contains("--zayit") ? LibraryKind.Zayit : LibraryKind.Other;
            var catalogPath = Path.Combine(Path.GetDirectoryName(dbPath), "catalog.pb");
            var catalogBefore = kind == LibraryKind.Zayit && File.Exists(catalogPath) ? File.ReadAllBytes(catalogPath) : null;
            IProgress<string> progress = new SyncProgress(s => Log("    · " + s));

            // פקודות קטנות לבדיקת תהליך הרקע: --status, --make-edit, --restore-all, --set-version N
            var cmdKind = args.Contains("--otzaria") ? LibraryKind.Otzaria : kind;
            if (args.Contains("--status"))
            {
                using (var s = LibrarySession.OpenReadOnly(cmdKind, dbPath, backupRoot))
                {
                    Log($"מצב: {s.Status()}, גרסה: {s.Version}, הוחזר בגרסה: {s.Store?.RestoredAtVersion}");
                    foreach (var op in s.Ops()) Log($"  #{op.Id} {op.State} v{op.DbVersion}: {op.Intent.Summary}");
                }
                return 0;
            }
            if (args.Contains("--make-edit"))
            {
                using (var s = LibrarySession.OpenForWrite(cmdKind, dbPath, backupRoot))
                {
                    var cat = s.Db.LoadCatalog();
                    var b = cat.Books.Values.Where(x => x.TotalLines > 20 && x.TotalLines < 200).OrderBy(x => x.Id).Skip(3).First();
                    var plan = s.Engine.PlanDelete(Intents.DeleteBooks(new[] { b }), new[] { b.Id }, null);
                    s.Delete(plan);
                    Log($"נמחק \"{b.Title}\" (מזהה {b.Id}). מצב: {s.Status()}");
                }
                return 0;
            }
            if (args.Contains("--restore-all"))
            {
                using (var s = LibrarySession.OpenForWrite(cmdKind, dbPath, backupRoot))
                {
                    Log($"הוחזרו {s.RestoreAll()} פעולות");
                    foreach (var op in s.Ops()) s.Store.DeleteOp(op.Id);
                }
                return 0;
            }
            var setVersion = Array.IndexOf(args, "--set-version");
            if (setVersion >= 0)
            {
                // מדמה עדכון ספרייה: משנה רק את מספר הגרסה בקובץ הבדיקה.
                using (var db = SeforimDb.Open(dbPath, readOnly: false))
                    db.Exec("UPDATE schema_meta SET value=$v WHERE key='db_version'", ("$v", args[setVersion + 1]));
                Log("הגרסה בקובץ הבדיקה: " + args[setVersion + 1]);
                return 0;
            }

            try
            {
                var sw = Stopwatch.StartNew();
                string h0 = null;
                if (!quick)
                {
                    Log("מחשב טביעת אצבע מלאה של הקובץ המקורי…");
                    h0 = FullFingerprint(dbPath);
                    Log($"  {h0}  ({sw.Elapsed.TotalSeconds:N0} שנ')");
                }

                using (var s = LibrarySession.OpenForWrite(kind, dbPath, backupRoot))
                {
                    Log($"גרסת קובץ: {s.Db.DbVersion}, סכמה: {s.Db.SchemaVersion}, טקסט דחוס: {s.Db.CompressedText}");
                    Log("קשרים זרים שאינם CASCADE:");
                    foreach (var fk in s.Db.Schema.ForeignKeys.Where(f => f.OnDelete != DeleteAction.Cascade))
                        Log($"  {fk.Table}.{fk.Column} → {fk.RefTable}.{fk.RefColumn} [{fk.OnDelete}]");
                    var cat = s.Db.LoadCatalog();
                    Log($"קטגוריות: {cat.Categories.Count}, ספרים: {cat.Books.Count}");

                    // ספר מרכזי עם הרבה קישורים, ספר קטן, וקטגוריה קטנה שלמה.
                    var big = cat.Books.Values.FirstOrDefault(b => b.Title == "בראשית") ?? cat.Books.Values.OrderByDescending(b => b.TotalLines).First();
                    var small = cat.Books.Values.Where(b => b.TotalLines > 20 && b.TotalLines < 200).OrderBy(b => b.Id).First();
                    var smallCat = cat.Categories.Values.Where(c => c.TotalBooks >= 2 && c.TotalBooks <= 6 && c.Children.Count > 0)
                        .OrderBy(c => c.Id).FirstOrDefault()
                        ?? cat.Categories.Values.Where(c => c.TotalBooks >= 2 && c.TotalBooks <= 6).OrderBy(c => c.Id).First();
                    Log($"נבחרו: \"{big.Title}\" ({big.TotalLines:N0} שורות), \"{small.Title}\", קטגוריה \"{smallCat.Path}\" ({smallCat.TotalBooks} ספרים)");

                    var preview = s.Db.ReadLines(small.Id, 0, 3);
                    Log("שורות ראשונות: " + string.Join(" | ", preview.Select(l => l.Length > 40 ? l.Substring(0, 40) : l)));

                    Timed("מחיקת ספר גדול", () =>
                    {
                        var plan = s.Engine.PlanDelete(Intents.DeleteBooks(new[] { big }), new[] { big.Id }, null, progress);
                        PrintPlan(plan);
                        s.Delete(plan, progress);
                    });
                    Timed("מחיקת קטגוריה", () =>
                    {
                        var plan = s.Engine.PlanDelete(Intents.DeleteCategories(new[] { smallCat }), null, new[] { smallCat.Id }, progress);
                        PrintPlan(plan);
                        s.Delete(plan, progress);
                    });
                    Timed("עריכת ספר", () =>
                    {
                        var ch = new BookChanges
                        {
                            Title = small.Title + " (נערך)",
                            ShortDesc = "תיאור בדיקה",
                            OrderIndex = 5,
                            Authors = new List<string> { "מחבר בדיקה חדש", "רש\"י" },
                        };
                        s.EditBook(Intents.EditBook(small, ch), small.Id, ch, progress);
                    });
                    var someCat = cat.Categories.Values.First(c => c.Id != smallCat.Id && !smallCat.SelfAndDescendants().Contains(c));
                    Timed("עריכת קטגוריה", () =>
                    {
                        var ch = new CategoryChanges { Title = someCat.Title + " (נערך)", OrderIndex = 1 };
                        s.EditCategory(Intents.EditCategory(someCat, ch), someCat.Id, ch, progress);
                    });

                    var after = s.Db.LoadCatalog();
                    Assert(!after.Books.ContainsKey(big.Id), "הספר הגדול נמחק");
                    Assert(!after.Categories.ContainsKey(smallCat.Id), "הקטגוריה נמחקה");
                    Assert(after.Books[small.Id].Title.EndsWith("(נערך)"), "שם הספר שונה");
                    Assert(after.Books[small.Id].Authors.Contains("מחבר בדיקה חדש"), "המחבר נוסף");
                    Assert(s.Status() == LibraryStatus.Edited, "מצב: ערוך");
                    if (catalogBefore != null)
                    {
                        var books = ZayitCatalog.ReadBooks(File.ReadAllBytes(catalogPath));
                        var original = ZayitCatalog.ReadBooks(catalogBefore);
                        Log($"  קטלוג זית: {original.Count:N0} ספרים לפני, {books.Count:N0} אחרי");
                        Assert(!books.ContainsKey(big.Id), "הספר הגדול הוסר מקטלוג זית");
                        Assert(smallCat.AllBooks().All(b => !books.ContainsKey(b.Id)), "ספרי הקטגוריה הוסרו מקטלוג זית");
                        Assert(books.TryGetValue(small.Id, out var title) && title.EndsWith("(נערך)"), "השם החדש מופיע בקטלוג זית");
                        var removedIds = new HashSet<long>(smallCat.AllBooks().Select(b => b.Id)) { big.Id };
                        Assert(original.Count - books.Count == original.Keys.Count(removedIds.Contains), "שאר הספרים בקטלוג לא נפגעו");
                    }

                    Timed("ביטול הפעולה האחרונה", () => s.UndoLast(progress));
                    Assert(s.Db.LoadCatalog().Categories[someCat.Id].Title == someCat.Title, "עריכת הקטגוריה בוטלה");

                    Timed("החזרה למקור", () => Log($"  הוחזרו {s.RestoreAll(progress)} פעולות"));
                    Assert(s.Status() == LibraryStatus.WaitingReapply, "מצב: ממתין להחלה מחדש");
                    var restored = s.Db.LoadCatalog();
                    Assert(restored.Books.ContainsKey(big.Id) && restored.Categories.ContainsKey(smallCat.Id), "הכול חזר");
                    if (catalogBefore != null)
                        Assert(File.ReadAllBytes(catalogPath).SequenceEqual(catalogBefore), "קטלוג זית זהה בדיוק למקור אחרי ההחזרה");
                }

                if (!quick)
                {
                    Log("מחשב טביעת אצבע מלאה אחרי ההחזרה…");
                    var h1 = FullFingerprint(dbPath);
                    Assert(h1 == h0, "הקובץ זהה למקור אחרי ההחזרה");
                }

                using (var s = LibrarySession.OpenForWrite(kind, dbPath, backupRoot))
                {
                    ReapplyResult res = null;
                    Timed("החלה מחדש", () => res = s.ReapplyRestored(progress));
                    Log($"  הוחלו {res.Applied}, בעיות: {res.Problems.Count}, הערות: {res.Notes.Count}");
                    foreach (var p in res.Problems) Log("  בעיה: " + p);
                    Assert(res.Problems.Count == 0, "החלה מחדש בלי בעיות");
                    Assert(s.Status() == LibraryStatus.Edited, "מצב: ערוך שוב");
                    Timed("החזרה למקור שוב", () => s.RestoreAll(progress));
                    foreach (var op in s.Ops()) s.Store.DeleteOp(op.Id);
                    if (catalogBefore != null)
                        Assert(File.ReadAllBytes(catalogPath).SequenceEqual(catalogBefore), "קטלוג זית זהה למקור גם אחרי סבב שני");
                }

                if (!quick)
                {
                    var h2 = FullFingerprint(dbPath);
                    Assert(h2 == h0, "הקובץ זהה למקור אחרי סבב שני");
                }
                Log($"הכול עבר. זמן כולל: {sw.Elapsed.TotalMinutes:N1} דקות");
                return 0;
            }
            catch (Exception ex)
            {
                Log("נכשל: " + ex);
                return 1;
            }
        }

        private sealed class SyncProgress : IProgress<string>
        {
            private readonly Action<string> _a;
            public SyncProgress(Action<string> a) => _a = a;
            public void Report(string value) => _a(value);
        }

        private static void PrintPlan(DeletePlan plan)
        {
            Log($"  תוכנית: {plan.TotalRows:N0} שורות, חוסמים: {plan.Blockers.Count}");
            foreach (var t in plan.Tables) Log($"    {t.Table}: {t.DeleteRows:N0} למחיקה" + (t.NullifyRows > 0 ? $", {t.NullifyRows:N0} לאיפוס" : ""));
            foreach (var b in plan.Blockers) Log("    חוסם: " + b);
        }

        private static void Timed(string what, Action a)
        {
            var sw = Stopwatch.StartNew();
            Log(what + "…");
            a();
            Log($"  {what}: {sw.Elapsed.TotalSeconds:N1} שנ'");
        }

        private static void Assert(bool ok, string what)
        {
            Log((ok ? "  ✔ " : "  ✘ ") + what);
            if (!ok) throw new Exception("בדיקה נכשלה: " + what);
        }

        private static void Log(string s) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {s}");

        /// <summary>טביעת אצבע של כל התוכן הלוגי: כל טבלה, השורות לפי המפתח.</summary>
        private static string FullFingerprint(string dbPath)
        {
            using (var db = SeforimDb.Open(dbPath, readOnly: true))
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                var parts = new StringBuilder();
                foreach (var t in db.Schema.Tables.Values.OrderBy(t => t.Name, StringComparer.Ordinal))
                {
                    var sw = Stopwatch.StartNew();
                    var order = string.Join(",", t.Key.Select(k => "\"" + k + "\""));
                    var cols = string.Join(",", t.Columns.Select(c => "\"" + c.Name + "\""));
                    using (var cmd = db.Command($"SELECT {cols} FROM \"{t.Name}\" ORDER BY {order}"))
                    {
                        var h = HashRows(cmd);
                        parts.Append(t.Name).Append('=').Append(h).Append(';');
                    }
                    if (sw.Elapsed.TotalSeconds > 5) Log($"    {t.Name}: {sw.Elapsed.TotalSeconds:N0} שנ'");
                }
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(parts.ToString()))).Replace("-", "");
            }
        }

        private static string HashRows(SqliteCommand cmd)
        {
            // אותו קידוד כמו במנוע, משוכפל כאן כדי שהבדיקה תהיה בלתי תלויה.
            // נצבר במאגר ומוזן ל-SHA במנות גדולות; קריאה ל-TransformBlock על כל תא איטית מאוד.
            using (var sha = System.Security.Cryptography.SHA256.Create())
            using (var r = cmd.ExecuteReader())
            {
                var buf = new System.IO.MemoryStream(1 << 20);
                void Flush()
                {
                    sha.TransformBlock(buf.GetBuffer(), 0, (int)buf.Length, null, 0);
                    buf.SetLength(0);
                }
                while (r.Read())
                {
                    for (var i = 0; i < r.FieldCount; i++)
                    {
                        if (r.IsDBNull(i)) { buf.WriteByte(0); }
                        else
                        {
                            var v = r.GetValue(i);
                            switch (v)
                            {
                                case long l: buf.WriteByte(2); buf.Write(BitConverter.GetBytes(l), 0, 8); break;
                                case double d: buf.WriteByte(4); buf.Write(BitConverter.GetBytes(d), 0, 8); break;
                                case byte[] b: buf.WriteByte(1); buf.Write(BitConverter.GetBytes(b.Length), 0, 4); buf.Write(b, 0, b.Length); break;
                                default:
                                    var s = Encoding.UTF8.GetBytes(Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture));
                                    buf.WriteByte(3); buf.Write(BitConverter.GetBytes(s.Length), 0, 4); buf.Write(s, 0, s.Length);
                                    break;
                            }
                        }
                    }
                    buf.WriteByte(0x1E);
                    if (buf.Length > (1 << 20)) Flush();
                }
                Flush();
                sha.TransformFinalBlock(new byte[0], 0, 0);
                return BitConverter.ToString(sha.Hash).Replace("-", "");
            }
        }
    }
}
