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

            // --logical-hash <מניפסט> from|to: חתימת התוכן של אוצריא מול המניפסט, טבלה אחר טבלה
            var logicalHash = Array.IndexOf(args, "--logical-hash");
            if (logicalHash >= 0) return CompareLogicalHash(dbPath, args[logicalHash + 1], args[logicalHash + 2]);

            // --apply-patch <מניפסט> <קובץ zst> [--no-fk]: החלת עדכון על עותק לא ערוך
            var applyPatch = Array.IndexOf(args, "--apply-patch");
            if (applyPatch >= 0) return ApplyPatch(dbPath, args[applyPatch + 1], args[applyPatch + 2], !args.Contains("--no-fk"), progress);

            // --otzaria-info: איתור אוצריא, הסכמה שהיא קוראת והגדרות העדכון שלה (קריאה בלבד)
            if (args.Contains("--otzaria-info"))
            {
                var dir = OtzariaApp.FindInstallDir();
                var tag = OtzariaApp.ReadReleaseTag(dir);
                Log($"התקנה: {dir ?? "לא נמצאה"} · תגית: {tag ?? "לא ידועה"} · קוראת עד סכמה: {OtzariaApp.ReadableSchemaFor(tag)?.ToString() ?? "לא ידוע"}");
                SqliteInit.Ensure();
                Log("SQLite: " + SQLitePCL.raw.sqlite3_libversion().utf8_to_string());
                var st = OtzariaApp.ReadUpdateSettings();
                Log(st == null ? "הגדרות העדכון: לא ידועות" : $"עדכונים: {st.UpdatesEnabled} · לא מקוון: {st.OfflineMode} · בודקת: {st.ChecksUpdates}");
                return 0;
            }

            // --e2e <קובץ zst מוכן>: בדיקה מקצה לקצה של השירות המשותף (SEFARIM_SETTINGS_DIR חובה)
            if (args.Contains("--e2e")) return EndToEnd(dbPath, backupRoot, args[Array.IndexOf(args, "--e2e") + 1], progress);

            // --plan <תיקיית מניפסטים>: בדיקת בחירת המסלול על מניפסטים אמיתיים, בלי רשת (dbPath ו-backupRoot לא בשימוש)
            if (args.Contains("--plan")) return PlanTest(args[Array.IndexOf(args, "--plan") + 1]);

            // --self-update <מניפסט> <קובץ zst>: עריכה של עותק, עדכון עצמי, ובדיקה שהכול נכון
            var selfUpdate = Array.IndexOf(args, "--self-update");
            if (selfUpdate >= 0) return SelfUpdateTest(dbPath, backupRoot, args[selfUpdate + 1], args[selfUpdate + 2], progress);

            // --add-book: הוספת ספרים לעותק של ספריית זית, ביטול, החזרה למקור, החלה מחדש והחלפת קובץ מדומה
            if (args.Contains("--add-book")) return AddBookTest(dbPath, backupRoot, quick, progress);

            // --install-addon <zip>: פריסת רכיב החיפוש מקובץ zip לתיקיית ההגדרות (SEFARIM_SETTINGS_DIR) ובדיקה שהוא רץ
            var installAddon = Array.IndexOf(args, "--install-addon");
            if (installAddon >= 0)
            {
                SearchAddon.InstallFromZip(args[installAddon + 1], progress);
                Log($"  ✔ הותקן ב-{SearchAddon.Find()}, ניתן להסרה: {SearchAddon.IsRemovable}");
                return 0;
            }

            // --download-addon: הורדה אמיתית של רכיב החיפוש מגיטהאב, אימות ופריסה לתיקיית ההגדרות (SEFARIM_SETTINGS_DIR)
            if (args.Contains("--download-addon"))
            {
                SearchAddon.Install(new SyncProgress2(d => { }), progress);
                Log($"  ✔ הורד, אומת והותקן ב-{SearchAddon.Find()}");
                return 0;
            }

            // --search-probe N: הנרמול שלנו מול האינדקס הרשמי, על N שורות רשמיות (קריאה בלבד; SEFARIM_ADDON_DIR חובה)
            var probe = Array.IndexOf(args, "--search-probe");
            if (probe >= 0) return SearchProbe(dbPath, int.Parse(args[probe + 1]));

            // --search-test: רכיב החיפוש על עותק של זית (כולל האינדקס): הוספה, מחיקה, ביטול, החלפה (SEFARIM_ADDON_DIR חובה)
            if (args.Contains("--search-test")) return SearchTest(dbPath, backupRoot, progress);

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

        private sealed class SyncProgress2 : IProgress<(long done, long total)>
        {
            private readonly Action<(long, long)> _a;
            public SyncProgress2(Action<(long, long)> a) => _a = a;
            public void Report((long done, long total) value) => _a(value);
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

        // ───────────────────────── רכיב החיפוש (זית) ─────────────────────────

        private static (int code, string output) Addon(string command, Action<StreamWriter> input = null)
        {
            var (code, output, error) = SearchAddon.Run(command, input == null ? (Action<Stream>)null : s =>
            {
                using (var w = new StreamWriter(s, new UTF8Encoding(false)) { NewLine = "\n" }) input(w);
            }, TimeSpan.FromMinutes(60));
            if (error.Length > 0) Log("  stderr: " + error.Split('\n').FirstOrDefault());
            return (code, output);
        }

        private static string Quote(string path) => "\"" + Path.GetFullPath(path) + "\"";

        private static int SearchProbe(string dbPath, int count)
        {
            using (var db = SeforimDb.Open(dbPath, readOnly: true))
            {
                var max = Convert.ToInt64(db.Scalar("SELECT max(id) FROM line WHERE id < 900000000"));
                var rnd = new Random(12345);
                var picked = new List<(long id, int index, string text)>();
                // שורות מפוזרות בכל הספרייה, ועוד שורות עם תגיות, ישויות וניקוד.
                for (var i = 0; i < count; i++)
                {
                    using (var cmd = db.Command("SELECT id, lineIndex, content FROM line WHERE id >= $x ORDER BY id LIMIT 1", ("$x", (long)(rnd.NextDouble() * max))))
                    using (var r = cmd.ExecuteReader())
                        if (r.Read()) picked.Add((r.GetInt64(0), r.GetInt32(1), r.GetString(2)));
                }
                foreach (var like in new[] { "%&%;%", "%<span%", "%<br%", "%־%", "%״%", "%<%<%>%" })
                    using (var cmd = db.Command("SELECT id, lineIndex, content FROM line WHERE content LIKE $l AND id < 900000000 LIMIT 40", ("$l", like)))
                    using (var r = cmd.ExecuteReader())
                        while (r.Read()) picked.Add((r.GetInt64(0), r.GetInt32(1), r.GetString(2)));
                Log($"בודק {picked.Count:N0} שורות רשמיות מול האינדקס…");
                var (code, output) = Addon("probe", w =>
                {
                    w.WriteLine("INDEX\t" + Path.GetFullPath(ZayitSearch.IndexDir(dbPath)));
                    foreach (var p in picked) w.WriteLine($"LINE\t{p.id}\t{p.index}\t{ZayitSearch.NormalizeForIndex(p.text).Replace('\t', ' ')}");
                });
                foreach (var l in output.Split('\n').Where(l => l.Trim().Length > 0).Take(40)) Log("  " + l.Trim());
                Log(code == 0 ? "  ✔ הנרמול זהה לאינדקס הרשמי" : "  ✘ יש הבדלים");
                return code;
            }
        }

        private static Dictionary<string, int> Counts(string dbPath, params long[] books)
        {
            var (code, output) = Addon("count --index " + Quote(ZayitSearch.IndexDir(dbPath)) + " --lookup " + Quote(ZayitSearch.LookupDir(dbPath)) +
                                       string.Concat(books.Select(b => " --book " + b)));
            if (code != 0) throw new Exception("count נכשל: " + output);
            var result = new Dictionary<string, int>();
            foreach (var line in output.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("book ")))
            {
                var parts = line.Split(' ');
                foreach (var kv in parts.Skip(2).Select(p => p.Split('=')))
                    result[parts[1] + ":" + kv[0]] = int.Parse(kv[1]);
            }
            return result;
        }

        private static int Hits(string dbPath, string term, long? book = null)
        {
            var (code, output) = Addon("search --index " + Quote(ZayitSearch.IndexDir(dbPath)) + " --term " + term + (book.HasValue ? " --book " + book : ""));
            if (code != 0) throw new Exception("search נכשל: " + output);
            var line = output.Split('\n').First(l => l.StartsWith("hits="));
            return int.Parse(new string(line.Substring(5).TakeWhile(char.IsDigit).ToArray()));
        }

        private static int SearchTest(string dbPath, string backupRoot, IProgress<string> progress)
        {
            try
            {
                var sw = Stopwatch.StartNew();
                Assert(SearchAddon.IsInstalled, "רכיב החיפוש נמצא");
                var (vcode, vout) = Addon("version");
                Log("  " + vout.Trim());
                var lines = new[]
                {
                    "<h1>ספר בדיקת חיפוש</h1>",
                    "<h2>פרק א</h2>",
                    "המילה המיוחדת זזזקקקווו נמצאת כאן בְּנִקּוּד",
                    "ועוד שורה עם מילה ייחודית שנייה ששששטטטם",
                };
                var bytes = Encoding.UTF8.GetBytes(string.Join("\n", lines));
                long added, official;
                CategoryNode target;
                using (var s = LibrarySession.OpenForWrite(LibraryKind.Zayit, dbPath, backupRoot))
                {
                    var cat = s.Db.LoadCatalog();
                    target = cat.Categories.Values.Where(c => c.Books.Count >= 3 && c.Parent != null).OrderBy(c => c.Id).First();
                    var spec = new AddBookSpec { Title = "ספר בדיקת חיפוש", Category = CategoryRef.Of(target) };
                    Timed("הוספת ספר + עדכון החיפוש", () => s.AddBook(spec, bytes, progress));
                    added = spec.BookId.Value;
                    Log($"  סנכרון: נוספו {s.LastSearchSync?.Added}, הוסרו {s.LastSearchSync?.Removed}, אזהרה: {s.LastSearchSync?.Warning ?? "אין"}");
                    Assert(s.LastSearchSync != null && s.LastSearchSync.Warning == null && s.LastSearchSync.Added == 1, "הספר נשלח לחיפוש");
                }
                var c1 = Counts(dbPath, added);
                Assert(c1[added + ":lines"] == lines.Length && c1[added + ":titles"] == 1 && c1[added + ":lookup"] == 1, $"באינדקס: {lines.Length} שורות, שם ומסמך בחיפוש השמות");
                Assert(Hits(dbPath, "זזזקקקווו") == 1, "מילה מהספר נמצאת בחיפוש");
                Assert(Hits(dbPath, "ששששטטטמ") == 1, "מילה שנייה נמצאת (אות סופית מנורמלת לרגילה, כמו בזית)");
                Assert(Hits(dbPath, "בנקוד", added) == 1, "מילה עם ניקוד נמצאת בלי ניקוד");

                // מחיקת ספר רשמי – יוצא מהחיפוש; ביטול – חוזר.
                using (var s = LibrarySession.OpenForWrite(LibraryKind.Zayit, dbPath, backupRoot))
                {
                    var b = s.Db.LoadCatalog().Books.Values.Where(x => x.TotalLines > 20 && x.TotalLines < 200).OrderBy(x => x.Id).Skip(5).First();
                    official = b.Id;
                    var before = Counts(dbPath, official);
                    Log($"  ספר רשמי \"{b.Title}\" ({b.TotalLines} שורות): באינדקס {before[official + ":lines"]} שורות");
                    Assert(before[official + ":lines"] == b.TotalLines, "הספר הרשמי באינדקס לפני המחיקה");
                    s.Delete(s.Engine.PlanDelete(Intents.DeleteBooks(new[] { b }), new[] { official }, null, progress), progress);
                    Assert(s.LastSearchSync?.Warning == null && s.LastSearchSync?.Removed == 1, "המחיקה נשלחה לחיפוש");
                }
                var c2 = Counts(dbPath, official, added);
                Assert(c2[official + ":lines"] == 0 && c2[official + ":titles"] == 0 && c2[official + ":lookup"] == 0, "הספר שנמחק יצא מהחיפוש");
                Assert(c2[added + ":lines"] == lines.Length, "הספר שנוסף עדיין בחיפוש");

                using (var s = LibrarySession.OpenForWrite(LibraryKind.Zayit, dbPath, backupRoot))
                {
                    s.UndoLast(progress);
                    Assert(s.LastSearchSync?.Warning == null && s.LastSearchSync?.Added == 1, "ביטול המחיקה נשלח לחיפוש");
                    var total = s.Db.LoadCatalog().Books[official].TotalLines;
                    var c3 = Counts(dbPath, official);
                    Assert(c3[official + ":lines"] == total && c3[official + ":lookup"] == 1, "הספר הרשמי חזר לחיפוש");
                }

                // אין שינוי – אין כתיבה.
                using (var s = LibrarySession.OpenForWrite(LibraryKind.Zayit, dbPath, backupRoot))
                {
                    var r = s.SyncSearch(progress);
                    Assert(r.Warning == null && !r.Changed, "סנכרון חוזר בלי שינויים לא כותב כלום");
                    // שינוי שם – הספר נכתב מחדש עם השם החדש.
                    var book = s.Db.LoadCatalog().Books[added];
                    var ch = new BookChanges { Title = "ספר בדיקת חיפוש (נערך)" };
                    s.EditBook(Intents.EditBook(book, ch), added, ch, progress);
                    Assert(s.LastSearchSync?.Added == 1, "שינוי שם נשלח לחיפוש");
                }
                Assert(Counts(dbPath, added)[added + ":lines"] == lines.Length, "אחרי שינוי השם – אותו מספר שורות (בלי כפילויות)");

                // ביטול הכול: הספר שנוסף יוצא מהחיפוש.
                using (var s = LibrarySession.OpenForWrite(LibraryKind.Zayit, dbPath, backupRoot))
                {
                    s.RestoreAll(progress);
                    Assert(s.LastSearchSync?.Warning == null && s.LastSearchSync?.Removed == 1, "החזרה למקור נשלחה לחיפוש");
                }
                var c4 = Counts(dbPath, added);
                Assert(c4[added + ":lines"] == 0 && c4[added + ":lookup"] == 0, "הספר שנוסף יצא מהחיפוש");
                Assert(Hits(dbPath, "זזזקקקווו") == 0, "המילה כבר לא נמצאת");

                // החלה מחדש – הספר חוזר (וגם השם הערוך).
                using (var s = LibrarySession.OpenForWrite(LibraryKind.Zayit, dbPath, backupRoot))
                {
                    var res = s.ReapplyRestored(progress);
                    Assert(res.Problems.Count == 0 && s.LastSearchSync?.Warning == null, "החלה מחדש + חיפוש");
                }
                Assert(Counts(dbPath, added)[added + ":lines"] == lines.Length && Hits(dbPath, "זזזקקקווו") == 1, "הספר חזר לחיפוש");

                // שלמות האינדקס (הקטעים החדשים בלבד; הקטע הגדול לא נכתב).
                var (scode, segs) = Addon("segments --index " + Quote(ZayitSearch.IndexDir(dbPath)));
                foreach (var l in segs.Split('\n').Where(l => l.Trim().Length > 0)) Log("  " + l.Trim());
                var small = segs.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("_") && !l.StartsWith("_39 ")).Select(l => l.Split(' ')[0]).ToList();
                var (ccode, cout) = Addon("check --index " + Quote(ZayitSearch.IndexDir(dbPath)) + string.Concat(small.Select(x => " --segment " + x)));
                Assert(ccode == 0 && cout.Contains("CLEAN"), $"CheckIndex על {small.Count} הקטעים החדשים");

                using (var s = LibrarySession.OpenForWrite(LibraryKind.Zayit, dbPath, backupRoot))
                {
                    s.RestoreAll(progress);
                    foreach (var op in s.Ops()) s.Store.DeleteOp(op.Id);
                    s.SyncSearch(progress);
                }
                Assert(Counts(dbPath, added)[added + ":lines"] == 0, "בסוף: הספר שנוסף לא בחיפוש");
                Log($"הכול עבר. זמן כולל: {sw.Elapsed.TotalMinutes:N1} דקות");
                return 0;
            }
            catch (Exception ex)
            {
                Log("נכשל: " + ex);
                return 1;
            }
        }

        // ───────────────────────── הוספת ספרים (זית) ─────────────────────────

        private static readonly string[] AddedTables =
            { "book", "line", "tocEntry", "tocText", "line_toc", "category", "category_closure", "book_has_links", "book_author", "author" };

        private static int AddBookTest(string dbPath, string backupRoot, bool quick, IProgress<string> progress)
        {
            try
            {
                var sw = Stopwatch.StartNew();
                var dir = Path.GetDirectoryName(dbPath);
                var catalogPath = Path.Combine(dir, ZayitCatalog.FileName);
                var releasePath = Path.Combine(dir, "release_info.txt");
                var catalogBefore = File.ReadAllBytes(catalogPath);
                var releaseBefore = File.ReadAllText(releasePath);
                string h0 = null;
                if (!quick)
                {
                    Log("מחשב טביעת אצבע מלאה של הקובץ המקורי…");
                    h0 = FullFingerprint(dbPath);
                    Log($"  {h0}  ({sw.Elapsed.TotalMinutes:N1} דק')");
                }
                var fkBefore = ForeignKeyViolations(dbPath);
                Log($"הפרות קשרים בקובץ המקורי (בטבלאות ההוספה): {fkBefore}");

                // ספר א: כותרות בכמה רמות, ניקוד וטעמים, מקף, ישות, כותרת ריקה, קפיצת רמה, שורות ריקות.
                var linesA = new[]
                {
                    "<h1>ספר בדיקה להוספה</h1>",
                    "הקדמה קצרה &amp; ראשונה",
                    "",
                    "<h2>פרק א</h2>",
                    "(א) בְּרֵאשִׁ֖ית בָּרָ֣א אֱלֹהִ֑ים",
                    "שורה שנייה בפרק",
                    "<h3>סִימָן רִאשׁוֹן</h3>",
                    "<b>מודגש</b> וטקסט",
                    "<h2></h2>",
                    "שורה אחרי כותרת ריקה",
                    "<h2>פרק ב</h2>",
                    "<h4>קפיצה מ-2 ל-4 עַל־פִּי</h4>",
                    "טקסט בקפיצה",
                    "<h2>כותרת ייחודית לבדיקה 7f3a</h2>",
                    "אחרון",
                    "",
                    "",
                };
                var bytesA = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(string.Join("\r\n", linesA))).ToArray();
                // ספר ב: בלי כותרות, בקידוד Windows-1255.
                var bytesB = Encoding.GetEncoding(1255).GetBytes("שורה ראשונה בלי כותרות\nשורה שנייה\n\nשורה רביעית");

                long idA, idB, newCat;
                CategoryNode target, parentOfNew;
                using (var s = LibrarySession.OpenForWrite(LibraryKind.Zayit, dbPath, backupRoot))
                {
                    Assert(ChangeEngine.AddBookUnsupportedReason(s.Db) == null, "מבנה הקובץ מתאים להוספה");
                    var cat = s.Db.LoadCatalog();
                    target = cat.Categories.Values.Where(c => c.Books.Count >= 3 && c.Parent != null).OrderBy(c => c.Id).First();
                    parentOfNew = cat.Categories.Values.Where(c => c.Parent != null && c.Parent.Parent != null && c.Children.Count > 0).OrderBy(c => c.Id).First();
                    var existingAuthor = s.Db.AllAuthors().First();
                    Log($"יעד ספר א: \"{target.Path}\"; קטגוריה חדשה בתוך \"{parentOfNew.Path}\"; מחבר קיים \"{existingAuthor}\"");

                    var specA = new AddBookSpec
                    {
                        Title = "ספר בדיקה להוספה",
                        Authors = new List<string> { existingAuthor, "מחבר חדש לבדיקה 7f3a" },
                        ShortDesc = "תיאור קצר לבדיקה",
                        Category = CategoryRef.Of(target),
                        OriginalFileName = "ספר בדיקה.txt",
                    };
                    var opA = Timed2("הוספת ספר א", () => s.AddBook(specA, bytesA, progress));
                    idA = specA.BookId.Value;
                    var specB = new AddBookSpec
                    {
                        Title = "ספר ב בלי כותרות",
                        Category = CategoryRef.Of(parentOfNew),
                        NewCategoryTitle = "קטגוריה חדשה לבדיקה",
                        OriginalFileName = "ב.txt",
                    };
                    Timed2("הוספת ספר ב (קטגוריה חדשה)", () => s.AddBook(specB, bytesB, progress));
                    idB = specB.BookId.Value;
                    newCat = specB.NewCategoryId.Value;
                    Log($"  מזהים: ספר א {idA}, ספר ב {idB}, קטגוריה {newCat}; טבלאות: {string.Join(",", opA.Tables)}; {opA.Rows:N0} מפתחות");
                    Assert(idA >= BookImport.ReservedIdBase && idB >= BookImport.ReservedIdBase && newCat >= BookImport.ReservedIdBase, "המזהים בטווח השמור");

                    VerifyAddedBook(s.Db, idA, specA.Title, BookImport.Read(bytesA, specA.Title), target.Id);
                    VerifyAddedBook(s.Db, idB, specB.Title, BookImport.Read(bytesB, specB.Title), newCat);
                    Assert(Convert.ToInt64(s.Db.Scalar("SELECT hasTeamim + hasNekudot FROM book WHERE id=$b", ("$b", idA))) == 2, "דגלי ניקוד וטעמים בספר א");
                    Assert(Convert.ToInt64(s.Db.Scalar("SELECT hasTeamim + hasNekudot FROM book WHERE id=$b", ("$b", idB))) == 0, "בלי דגלים בספר ב");
                    Assert(s.Db.Scalar("SELECT heShortDesc FROM book WHERE id=$b", ("$b", idA)) as string == "תיאור קצר לבדיקה", "התיאור נשמר");
                    var reused = Convert.ToInt64(s.Db.Scalar("SELECT t.textId FROM tocEntry t JOIN tocText x ON x.id=t.textId WHERE t.bookId=$b AND x.text='פרק א'", ("$b", idA)));
                    Assert(reused < BookImport.ReservedIdBase, "טקסט כותרת קיים (\"פרק א\") משותף ולא נוסף שוב");
                    Assert(s.Db.Scalar("SELECT 1 FROM tocText WHERE text='קפיצה מ-2 ל-4 על פי'") != null, "כותרת: בלי ניקוד, מקף הפך לרווח");
                    var authors = s.Db.LoadDetails(s.Db.LoadCatalog().Books[idA]).Authors;
                    Assert(authors.Count == 2 && authors.Contains(existingAuthor) && authors.Contains("מחבר חדש לבדיקה 7f3a"), "המחברים");
                    var parentLevel = Convert.ToInt32(s.Db.Scalar("SELECT level FROM category WHERE id=$c", ("$c", parentOfNew.Id)));
                    Assert(Convert.ToInt32(s.Db.Scalar("SELECT level FROM category WHERE id=$c", ("$c", newCat))) == parentLevel + 1, "רמת הקטגוריה החדשה");
                    var closure = Convert.ToInt64(s.Db.Scalar("SELECT count(*) FROM category_closure WHERE descendantId=$c", ("$c", newCat)));
                    var parentClosure = Convert.ToInt64(s.Db.Scalar("SELECT count(*) FROM category_closure WHERE descendantId=$c", ("$c", parentOfNew.Id)));
                    Assert(closure == parentClosure + 1, $"category_closure של הקטגוריה החדשה ({closure} שורות)");
                    Assert(s.Db.LoadCatalog().Categories[newCat].Path == parentOfNew.Path + "/קטגוריה חדשה לבדיקה", "נתיב הקטגוריה החדשה");

                    var ch = new BookChanges { Title = "ספר בדיקה להוספה (נערך)" };
                    var bookA = s.Db.LoadCatalog().Books[idA];
                    s.EditBook(Intents.EditBook(bookA, ch), idA, ch, progress);
                    Assert(s.Status() == LibraryStatus.Edited, "מצב: ערוך");
                    Assert(File.Exists(Path.Combine(s.AddedBooksFolder, specA.SourceFile)) && File.Exists(Path.Combine(s.AddedBooksFolder, specB.SourceFile)), "עותקי הספרים נשמרו בתיקיית הגיבוי");
                }
                Assert(ForeignKeyViolations(dbPath) == fkBefore, "אין הפרות קשרים חדשות");
                VerifyCatalog(catalogPath, catalogBefore, idA, "ספר בדיקה להוספה (נערך)", target.Id, idB, newCat, parentOfNew.Id);

                using (var s = LibrarySession.OpenForWrite(LibraryKind.Zayit, dbPath, backupRoot))
                {
                    Timed("ביטול העריכה", () => s.UndoLast(progress));
                    Assert(s.Db.LoadCatalog().Books[idA].Title == "ספר בדיקה להוספה", "העריכה בוטלה");
                    Timed("החזרה למקור", () => Log($"  הוחזרו {s.RestoreAll(progress)} פעולות"));
                    Assert(s.Status() == LibraryStatus.WaitingReapply, "מצב: ממתין להחלה מחדש");
                    Assert(NoReservedRows(s.Db), "אין שורות בטווח השמור");
                    Assert(File.ReadAllBytes(catalogPath).SequenceEqual(catalogBefore), "קטלוג זית זהה בדיוק למקור");
                }
                if (!quick)
                {
                    Log("מחשב טביעת אצבע מלאה אחרי ההחזרה…");
                    Assert(FullFingerprint(dbPath) == h0, "הקובץ זהה למקור אחרי ההחזרה");
                }

                using (var s = LibrarySession.OpenForWrite(LibraryKind.Zayit, dbPath, backupRoot))
                {
                    ReapplyResult res = null;
                    Timed("החלה מחדש", () => res = s.ReapplyRestored(progress));
                    foreach (var p in res.Problems) Log("  בעיה: " + p);
                    Assert(res.Problems.Count == 0 && res.Applied == 2, $"שתי ההוספות הוחלו מחדש ({res.Applied})");
                    var cat = s.Db.LoadCatalog();
                    Assert(cat.Books.ContainsKey(idA) && cat.Books.ContainsKey(idB) && cat.Categories.ContainsKey(newCat), "אותם מזהים בהוספה החוזרת");
                    var ch = new BookChanges { Title = "ספר בדיקה להוספה (נערך)" };
                    s.EditBook(Intents.EditBook(cat.Books[idA], ch), idA, ch, progress);
                }
                VerifyCatalog(catalogPath, catalogBefore, idA, "ספר בדיקה להוספה (נערך)", target.Id, idB, newCat, parentOfNew.Id);

                // זית עדכנה רק את מספר הגרסה, והספרים עדיין בקובץ: הפעולות נשארות מוחלות.
                File.WriteAllText(releasePath, "29990101000000");
                using (var s = LibrarySession.OpenForWrite(LibraryKind.Zayit, dbPath, backupRoot))
                {
                    Assert(s.ReplacedOps == 0 && s.Status() == LibraryStatus.Edited, "שינוי גרסה בלבד: השינויים זוהו כקיימים");
                }

                // זית הורידה קובץ חדש (מדמים: השורות שלנו נעלמו, הקטלוג חזר למקור, הגרסה התקדמה).
                using (var db = SeforimDb.Open(dbPath, readOnly: false)) DeleteReservedRows(db);
                File.WriteAllBytes(catalogPath, catalogBefore);
                File.WriteAllText(releasePath, "29990202000000");
                using (var s = LibrarySession.OpenForWrite(LibraryKind.Zayit, dbPath, backupRoot))
                {
                    Assert(s.ReplacedOps == 3, $"זוהתה החלפת קובץ ({s.ReplacedOps} פעולות)");
                    Assert(s.Status() == LibraryStatus.WaitingReapply, "מצב: ממתין להחלה מחדש");
                    var res = s.ReapplyRestored(progress);
                    foreach (var p in res.Problems) Log("  בעיה: " + p);
                    Assert(res.Problems.Count == 0 && res.Applied == 3, $"הוחלו שוב 3 פעולות ({res.Applied})");
                    var cat = s.Db.LoadCatalog();
                    Assert(cat.Books[idA].Title == "ספר בדיקה להוספה (נערך)" && cat.Books.ContainsKey(idB), "הספרים והעריכה חזרו");
                    VerifyAddedBook(s.Db, idA, "ספר בדיקה להוספה", BookImport.Read(bytesA, "ספר בדיקה להוספה"), target.Id, editedTitle: "ספר בדיקה להוספה (נערך)");
                }
                VerifyCatalog(catalogPath, catalogBefore, idA, "ספר בדיקה להוספה (נערך)", target.Id, idB, newCat, parentOfNew.Id);
                Assert(ForeignKeyViolations(dbPath) == fkBefore, "אין הפרות קשרים חדשות אחרי ההחלה מחדש");

                // מחיקה (לא ביטול) של ספר שהוספנו, ואחריה החלפת קובץ: גם המחיקה צריכה לחזור.
                using (var s = LibrarySession.OpenForWrite(LibraryKind.Zayit, dbPath, backupRoot))
                {
                    var b = s.Db.LoadCatalog().Books[idB];
                    s.Delete(s.Engine.PlanDelete(Intents.DeleteBooks(new[] { b }), new[] { idB }, null, progress), progress);
                    Assert(!s.Db.LoadCatalog().Books.ContainsKey(idB), "ספר ב נמחק במחיקה רגילה");
                }
                File.WriteAllText(releasePath, "29990303000000");
                using (var s = LibrarySession.OpenForWrite(LibraryKind.Zayit, dbPath, backupRoot))
                    Assert(s.ReplacedOps == 0 && s.Status() == LibraryStatus.Edited, "שינוי גרסה בלבד אחרי מחיקת ספר שנוסף: הכול נשאר מוחל");
                using (var db = SeforimDb.Open(dbPath, readOnly: false)) DeleteReservedRows(db);
                File.WriteAllBytes(catalogPath, catalogBefore);
                File.WriteAllText(releasePath, "29990404000000");
                using (var s = LibrarySession.OpenForWrite(LibraryKind.Zayit, dbPath, backupRoot))
                {
                    Assert(s.ReplacedOps == 4, $"זוהתה החלפת קובץ, כולל המחיקה של ספר שנוסף ({s.ReplacedOps} פעולות)");
                    var res = s.ReapplyRestored(progress);
                    foreach (var p in res.Problems) Log("  בעיה: " + p);
                    Assert(res.Problems.Count == 0 && res.Applied == 4, $"הוחלו שוב 4 פעולות ({res.Applied})");
                    var cat = s.Db.LoadCatalog();
                    Assert(cat.Books.ContainsKey(idA) && !cat.Books.ContainsKey(idB) && cat.Categories.ContainsKey(newCat), "ספר א קיים, ספר ב נמחק שוב");
                    var books = ZayitCatalog.ReadBooks(File.ReadAllBytes(catalogPath));
                    Assert(books.ContainsKey(idA) && !books.ContainsKey(idB), "בקטלוג: ספר א בלבד");
                }

                using (var s = LibrarySession.OpenForWrite(LibraryKind.Zayit, dbPath, backupRoot))
                {
                    var sourceB = s.Ops().First(o => o.Intent.Kind == OpIntent.AddBook && o.Intent.Books.Any(b => b.Id == idB)).Intent.AddBookSpec.SourceFile;
                    s.UndoLast(progress);
                    Assert(s.Db.LoadCatalog().Books.ContainsKey(idB), "ביטול המחיקה החזיר את ספר ב");
                    s.UndoLast(progress);
                    s.UndoLast(progress);
                    Assert(!s.Db.LoadCatalog().Books.ContainsKey(idB) && !s.Db.LoadCatalog().Categories.ContainsKey(newCat), "ביטול הוספת ספר ב");
                    Assert(!File.Exists(Path.Combine(s.AddedBooksFolder, sourceB)), "העותק של ספר ב נמחק אחרי הביטול");
                    s.RestoreAll(progress);
                    foreach (var op in s.Ops()) s.Store.DeleteOp(op.Id);
                    Assert(NoReservedRows(s.Db), "אין שורות בטווח השמור בסוף");
                }
                File.WriteAllText(releasePath, releaseBefore);
                using (var s = LibrarySession.OpenForWrite(LibraryKind.Zayit, dbPath, backupRoot)) { }
                Assert(File.ReadAllBytes(catalogPath).SequenceEqual(catalogBefore), "קטלוג זית זהה למקור בסוף");
                if (!quick)
                {
                    Log("מחשב טביעת אצבע מלאה בסוף…");
                    Assert(FullFingerprint(dbPath) == h0, "הקובץ זהה למקור בסוף");
                    using (var db = SeforimDb.Open(dbPath, readOnly: true))
                        Assert(db.Scalar("PRAGMA quick_check") as string == "ok", "quick_check תקין");
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

        private static T Timed2<T>(string what, Func<T> f)
        {
            var result = default(T);
            Timed(what, () => result = f());
            return result;
        }

        /// <summary>בודק ספר שנוסף מול הטקסט, בלי להשתמש בקוד ההוספה עצמו לחישוב המבנה.</summary>
        private static void VerifyAddedBook(SeforimDb db, long bookId, string title, BookText text, long categoryId, string editedTitle = null)
        {
            var row = new List<object>();
            using (var cmd = db.Command("SELECT title, heRef, categoryId, totalLines, orderIndex FROM book WHERE id=$b", ("$b", bookId)))
            using (var r = cmd.ExecuteReader())
            {
                Assert(r.Read(), $"הספר {bookId} קיים");
                for (var i = 0; i < 5; i++) row.Add(r.GetValue(i));
            }
            Assert((string)row[0] == (editedTitle ?? title) && (string)row[1] == title && (long)row[2] == categoryId &&
                   (long)row[3] == text.Lines.Count && (long)row[4] == 999, "שורת הספר");

            var lines = new List<(long id, long index, string content, string heRef, long? toc, long chars)>();
            using (var cmd = db.Command("SELECT id, lineIndex, content, heRef, tocEntryId, charCount FROM line WHERE bookId=$b ORDER BY lineIndex", ("$b", bookId)))
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                    lines.Add((r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? (long?)null : r.GetInt64(4), r.GetInt64(5)));
            Assert(lines.Count == text.Lines.Count && lines.Select((l, i) => l.index == i && l.content == text.Lines[i]).All(x => x), "השורות זהות לטקסט ורצופות");

            var toc = new Dictionary<long, (long? parent, long textId, long level, long? lineId, long last, long kids)>();
            using (var cmd = db.Command("SELECT id, parentId, textId, level, lineId, isLastChild, hasChildren FROM tocEntry WHERE bookId=$b ORDER BY id", ("$b", bookId)))
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                    toc[r.GetInt64(0)] = (r.IsDBNull(1) ? (long?)null : r.GetInt64(1), r.GetInt64(2), r.GetInt64(3), r.IsDBNull(4) ? (long?)null : r.GetInt64(4), r.GetInt64(5), r.GetInt64(6));
            var expectedHeadings = text.Lines.Where(l => BookImport.HeadingLevel(l) > 0 && SeforimDb.StripHtml(l).Length > 0).Count();
            Assert(toc.Count == expectedHeadings && toc.Count == text.Headings, $"מספר הכותרות ({toc.Count})");

            var lineToc = new Dictionary<long, long>();
            using (var cmd = db.Command("SELECT lt.lineId, lt.tocEntryId FROM line_toc lt JOIN line l ON l.id=lt.lineId WHERE l.bookId=$b", ("$b", bookId)))
            using (var r = cmd.ExecuteReader())
                while (r.Read()) lineToc[r.GetInt64(0)] = r.GetInt64(1);

            // מבנה צפוי, מחושב כאן בנפרד: לכל כותרת – האב הוא הכותרת האחרונה ברמה נמוכה יותר שעדיין "פתוחה".
            var open = new List<(int level, long id)>();
            long? owner = null;
            var ok = true;
            var problems = new List<string>();
            for (var i = 0; i < lines.Count; i++)
            {
                var l = lines[i];
                var level = BookImport.HeadingLevel(l.content);
                var isHeading = level > 0 && SeforimDb.StripHtml(l.content).Length > 0;
                if (l.chars != BookImport.VisibleChars(l.content)) { ok = false; problems.Add($"charCount בשורה {i}"); }
                if (isHeading)
                {
                    var entry = l.toc ?? -1;
                    if (!toc.TryGetValue(entry, out var e)) { ok = false; problems.Add($"אין כותרת לשורה {i}"); continue; }
                    open.RemoveAll(o => o.level >= level);
                    var parent = open.Count == 0 ? (long?)null : open[open.Count - 1].id;
                    if (e.parent != parent || e.level != level || e.lineId != l.id || l.heRef != null) { ok = false; problems.Add($"מבנה הכותרת בשורה {i}"); }
                    var tocText = db.Scalar("SELECT text FROM tocText WHERE id=$x", ("$x", e.textId)) as string;
                    if (tocText != BookImport.HeadingText(l.content)) { ok = false; problems.Add($"טקסט הכותרת בשורה {i}"); }
                    open.Add((level, entry));
                    owner = entry;
                }
                else if (l.toc != null || l.heRef != title + " " + (i + 1)) { ok = false; problems.Add($"שורה רגילה {i}"); }
                var mapped = lineToc.TryGetValue(l.id, out var m) ? m : (long?)null;
                if (mapped != owner) { ok = false; problems.Add($"line_toc בשורה {i}"); }
            }
            foreach (var kv in toc)
            {
                var kids = toc.Any(x => x.Value.parent == kv.Key);
                var siblings = toc.Where(x => x.Value.parent == kv.Value.parent).Select(x => x.Key).ToList();
                if ((kv.Value.kids == 1) != kids || (kv.Value.last == 1) != (siblings.Max() == kv.Key)) { ok = false; problems.Add($"דגלי כותרת {kv.Key}"); }
            }
            foreach (var p in problems.Take(10)) Log("    ✘ " + p);
            Assert(ok, $"מבנה השורות, תוכן העניינים ו-line_toc של \"{title}\"");
            Assert(db.Scalar("SELECT 1 FROM book_has_links WHERE bookId=$b AND hasSourceLinks=0 AND hasTargetLinks=0", ("$b", bookId)) != null, "book_has_links");
        }

        private static void VerifyCatalog(string catalogPath, byte[] original, long idA, string titleA, long catA, long idB, long newCat, long parentOfNew)
        {
            var bytes = File.ReadAllBytes(catalogPath);
            var books = ZayitCatalog.ReadBooks(bytes);
            var before = ZayitCatalog.ReadBooks(original);
            Assert(books.Count == before.Count + 2 && before.All(kv => books.ContainsKey(kv.Key)), "בקטלוג: כל הספרים המקוריים ועוד שניים");
            Assert(books.TryGetValue(idA, out var t) && t == titleA && books.ContainsKey(idB), "בקטלוג: הספרים שנוספו");

            // מבנה: ספר א בקטגוריה שלו, ספר ב בקטגוריה החדשה שבתוך האב הנכון, עם רמה ו-parentId.
            var found = new Dictionary<long, (long cat, long? parentCat)>();
            (long id, long? parent, long level, bool hasLevel) newInfo = (0, null, 0, false);
            void Walk(byte[] cat, long? parent)
            {
                var fields = ParseProto(cat);
                var id = (long)fields.First(f => f.num == 1).value;
                if (id == newCat)
                    newInfo = (id, fields.Where(f => f.num == 4).Select(f => (long?)f.value).FirstOrDefault(), (long)fields.First(f => f.num == 3).value, fields.Any(f => f.num == 3));
                foreach (var f in fields.Where(f => f.num == 5))
                    found[(long)ParseProto(f.bytes).First(x => x.num == 1).value] = (id, parent);
                foreach (var f in fields.Where(f => f.num == 6)) Walk(f.bytes, id);
            }
            var root = ParseProto(bytes);
            foreach (var f in root.Where(f => f.num == 1)) Walk(f.bytes, null);
            Assert(found[idA].cat == catA, "בקטלוג: ספר א בקטגוריה הנכונה");
            Assert(found[idB].cat == newCat && found[idB].parentCat == parentOfNew, "בקטלוג: ספר ב בקטגוריה החדשה, בתוך האב הנכון");
            Assert(newInfo.hasLevel && newInfo.parent == parentOfNew, "בקטלוג: הקטגוריה החדשה עם רמה ואב");
            Assert((long)root.First(f => f.num == 3).value == books.Count, "בקטלוג: מספר הספרים הכולל עודכן");
        }

        private static List<(int num, ulong value, byte[] bytes)> ParseProto(byte[] data)
        {
            var list = new List<(int, ulong, byte[])>();
            var pos = 0;
            ulong Varint()
            {
                ulong v = 0;
                for (var shift = 0; ; shift += 7)
                {
                    var b = data[pos++];
                    v |= (ulong)(b & 0x7F) << shift;
                    if (b < 0x80) return v;
                }
            }
            while (pos < data.Length)
            {
                var key = Varint();
                var num = (int)(key >> 3);
                switch (key & 7)
                {
                    case 0: list.Add((num, Varint(), null)); break;
                    case 2:
                        var len = (int)Varint();
                        list.Add((num, 0, data.Skip(pos).Take(len).ToArray()));
                        pos += len;
                        break;
                    case 5: list.Add((num, 0, data.Skip(pos).Take(4).ToArray())); pos += 4; break;
                    case 1: list.Add((num, 0, data.Skip(pos).Take(8).ToArray())); pos += 8; break;
                    default: throw new InvalidDataException("protobuf");
                }
            }
            return list;
        }

        private static string ReservedWhere(string table)
        {
            switch (table)
            {
                case "line_toc": return "lineId >= 900000000";
                case "line":
                case "tocEntry":
                case "book_has_links":
                case "book_author": return "bookId >= 900000000";
                case "category_closure": return "descendantId >= 900000000";
                default: return "id >= 900000000";
            }
        }

        private static bool NoReservedRows(SeforimDb db) =>
            AddedTables.All(t => db.Scalar($"SELECT 1 FROM {t} WHERE {ReservedWhere(t)} LIMIT 1") == null) &&
            db.Scalar("SELECT 1 FROM line WHERE id >= 900000000 LIMIT 1") == null &&
            db.Scalar("SELECT 1 FROM tocEntry WHERE id >= 900000000 LIMIT 1") == null;

        private static void DeleteReservedRows(SeforimDb db)
        {
            using (var tx = db.Begin())
            {
                foreach (var t in AddedTables) db.Exec($"DELETE FROM {t} WHERE {ReservedWhere(t)}");
                tx.Commit();
            }
        }

        private static long ForeignKeyViolations(string dbPath)
        {
            using (var db = SeforimDb.Open(dbPath, readOnly: true))
            {
                long n = 0;
                foreach (var t in AddedTables)
                    n += Convert.ToInt64(db.Scalar($"SELECT count(*) FROM pragma_foreign_key_check('{t}')"));
                return n;
            }
        }

        private static int ApplyPatch(string dbPath, string manifestPath, string zstPath, bool checkFk, IProgress<string> progress)
        {
            var m = PatchManifest.Parse(File.ReadAllText(manifestPath));
            var sw = Stopwatch.StartNew();
            var patch = PatchFiles.Prepare(zstPath, m.PatchFiles.Single(), progress);
            Log($"קובץ העדכון מוכן ({sw.Elapsed:mm\\:ss})");
            sw.Restart();
            using (var db = SeforimDb.Open(dbPath, readOnly: false))
            {
                var r = PatchApplier.Apply(db, patch, m, new PatchApplyOptions { CheckForeignKeys = checkFk, Progress = progress });
                Log($"הוחל בהצלחה ({sw.Elapsed:mm\\:ss}). migrations: {r.Migrations}, ספרים שהשתנו: {r.BooksTouched.Count}, " +
                    $"שינוי מחוץ לספרים: {r.HasChangesOutsideBooksTouched}");
                Log("  upserts: " + string.Join(", ", r.Upserts.Where(kv => kv.Value > 0).Select(kv => $"{kv.Key}={kv.Value:N0}")));
                Log("  deletes: " + string.Join(", ", r.Deletes.Where(kv => kv.Value > 0).Select(kv => $"{kv.Key}={kv.Value:N0}")));
                Log($"  אומתו: {r.VerifiedTables.Count}, נדחו: {r.DeferredTables.Count}, לא אומתו: {r.UnverifiedTables.Count}");
                Log($"  גרסה בקובץ: {db.DbVersion}, מבנה: {db.SchemaVersion}");
            }
            return 0;
        }

        private static int EndToEnd(string dbPath, string backupRoot, string readyZst, IProgress<string> progress)
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SEFARIM_SETTINGS_DIR")))
            {
                Log("חובה להגדיר SEFARIM_SETTINGS_DIR לתיקיית בדיקה.");
                return 2;
            }
            var failures = 0;
            void Check(bool ok, string what)
            {
                Log((ok ? "  ✓ " : "  ✗ נכשל: ") + what);
                if (!ok) failures++;
            }

            // 1. בדיקה מול אוצריא המותקנת
            var settings = AppSettings.Load();
            var check = SelfUpdater.Check(settings, dbPath);
            Log($"בדיקה: מקומי {check.LocalVersion}/מבנה {check.LocalSchema}, אוצריא {check.OtzariaTag} קוראת עד {check.OtzariaReadableSchema}, " +
                $"אחרונה {check.Plan.LatestVersion}, שלבים {check.Plan.Steps.Count}, סיבה: {check.Plan.Reason}");
            Check(check.OtzariaReadableSchema != null && check.OtzariaReadableSchema >= 6, "זוהתה הסכמה שאוצריא קוראת");
            Check(check.OtzariaReadableSchema >= 7 ? check.HasUpdate : !check.HasUpdate && check.Plan.Reason.Contains("לעדכן את אוצריא"),
                "התוצאה מתאימה לגרסת אוצריא המותקנת");

            // 2. מסלול בלי מגבלת סכמה (לבדיקה בלבד), וקובץ העדכון כבר בתיקיית העבודה כדי לא להוריד שוב
            var (edges, latest) = LibraryUpdates.Discover(false);
            var plan = LibraryUpdates.Plan(check.LocalVersion, check.LocalSchema, latest, edges, 7);
            Check(plan.Steps.Count == 1, "נמצא מסלול כשהסכמה מותרת");
            Directory.CreateDirectory(SelfUpdater.WorkFolder);
            var target = Path.Combine(SelfUpdater.WorkFolder, Path.GetFileName(readyZst));
            if (!File.Exists(target)) File.Copy(readyZst, target);

            using (var s = LibrarySession.OpenForWrite(LibraryKind.Otzaria, dbPath, backupRoot))
            {
                var cat = s.Db.LoadCatalog();
                var books = cat.Books.Values.Where(b => b.TotalLines > 50 && b.TotalLines < 2000).OrderBy(b => b.Id).Take(2).ToList();
                Log($"מוחק \"{books[0].Title}\" ומפנה את הגיבוי");
                s.Delete(s.Engine.PlanDelete(Intents.DeleteBooks(new[] { books[0] }), new[] { books[0].Id }, null, progress), progress);
                var bkBefore = new FileInfo(s.Store.FilePath).Length;
                Check(s.ReleaseDeleteBackups(progress) == 1, "פונה גיבוי של מחיקה אחת");
                Check(s.Ops().Single().State == OpState.Permanent, "המחיקה קבועה");
                Check(Convert.ToInt64(s.Db.Scalar($"SELECT count(*) FROM {BackupStore.Alias}.\"b:line\"")) == 0, "אין שורות בגיבוי");
                Log($"  גודל הגיבוי: {bkBefore:N0} ← {new FileInfo(s.Store.FilePath).Length:N0}");
                try
                {
                    s.UndoLast();
                    Check(false, "ביטול מחיקה קבועה נחסם");
                }
                catch (InvalidOperationException) { Check(true, "ביטול מחיקה קבועה נחסם"); }
                try
                {
                    s.RestoreAll();
                    Check(false, "החזרה למקור נחסמה");
                }
                catch (InvalidOperationException) { Check(true, "החזרה למקור נחסמה"); }

                Log($"מוחק \"{books[1].Title}\" (עם גיבוי), ומעדכן דרך השירות המשותף");
                s.Delete(s.Engine.PlanDelete(Intents.DeleteBooks(new[] { books[1] }), new[] { books[1].Id }, null, progress), progress);
                Check(s.UndoableDeletes().Count == 1, "מחיקה אחת ניתנת לביטול לפני העדכון");
                var sw = Stopwatch.StartNew();
                var o = SelfUpdater.Run(s, plan, progress);
                Log($"עודכן {o.FromVersion}→{o.ToVersion} ({sw.Elapsed:mm\\:ss}), ספרים שהשתנו {o.BooksTouched.Count:N0}, יתומות {o.OrphansRemoved:N0}, אינדקס {o.IndexRefreshRequested}");
                Check(s.Version == plan.LatestVersion && s.Db.DbVersion == plan.LatestVersion, "הגרסה עודכנה");
                Check(s.Ops().Count == 2 && s.Ops().All(x => x.State == OpState.Permanent), "שתי המחיקות קבועות");
                Check(books.All(b => s.Db.Scalar("SELECT 1 FROM book WHERE id=$i", ("$i", b.Id)) == null), "שני הספרים לא קיימים");
                Check(!File.Exists(target) && !File.Exists(Path.ChangeExtension(target, null)), "קובצי העדכון נמחקו");
                Check(!o.IndexRefreshRequested, "לא נכתבה בקשה לאינדקס (אין אינדקס ליד עותק הבדיקה)");
            }

            // 3. בקשת רענון לאינדקס – על תיקייה מדומה בלבד
            var fake = Path.Combine(AppSettings.Folder, "fake-otzaria");
            if (Directory.Exists(fake)) Directory.Delete(fake, true);
            Directory.CreateDirectory(Path.Combine(fake, "lib"));
            Directory.CreateDirectory(Path.Combine(fake, "index"));
            File.WriteAllText(Path.Combine(fake, "lib", "seforim.db"), "");
            Check(!OtzariaIndex.RequestRecheck(Path.Combine(fake, "lib", "seforim.db"), new long[] { 5 }, false), "בלי קובץ זיהוי של אינדקס – לא כותבים");
            File.WriteAllText(Path.Combine(fake, "index", "otzaria_index_meta.json"), "{\"format\":\"otzaria-search-index\",\"schema_version\":5}");
            var pending = Path.Combine(fake, "index", OtzariaIndex.PendingFile);
            File.WriteAllText(pending, "{\"version\":1,\"keys\":[\"id:9\"],\"checkKeys\":[\"id:3\"]}");
            Check(OtzariaIndex.RequestRecheck(Path.Combine(fake, "lib", "seforim.db"), new long[] { 5, 3 }, false), "נכתבה בקשה");
            var written = File.ReadAllText(pending);
            Log("  " + written);
            Check(written == "{\"version\":1,\"keys\":[\"id:9\"],\"checkKeys\":[\"id:3\",\"id:5\"]}", "מוזג נכון עם הקיים");
            Check(OtzariaIndex.RequestRecheck(Path.Combine(fake, "lib", "seforim.db"), new long[0], true) &&
                  File.ReadAllText(pending).Contains("\"checkLibrary\":true"), "בקשה לבדיקת כל הספרייה");
            File.WriteAllText(pending, "{\"version\":2,\"keys\":[]}");
            Check(!OtzariaIndex.RequestRecheck(Path.Combine(fake, "lib", "seforim.db"), new long[] { 1 }, false) &&
                  File.ReadAllText(pending) == "{\"version\":2,\"keys\":[]}", "פורמט לא מוכר – לא נוגעים");
            Directory.Delete(fake, true);

            // 4. הורדה אמיתית קטנה עם אימות
            var small = edges.FirstOrDefault(e => e.Manifest.FromVersion == 33 && e.Manifest.ToVersion == 34);
            if (small != null)
            {
                var f = small.Manifest.PatchFiles.Single();
                var dl = Path.Combine(SelfUpdater.WorkFolder, f.File);
                var sw = Stopwatch.StartNew();
                LibraryUpdates.Download(small.FileUrls[f.File], dl, f.Size);
                Log($"  הורד {f.File} ({f.Size:N0} בתים) ב-{sw.Elapsed:mm\\:ss}");
                try
                {
                    PatchFiles.Verify(dl, f.Size, f.Sha256, "קובץ העדכון");
                    Check(true, "ההורדה תקינה לפי המניפסט");
                }
                catch (InvalidDataException ex) { Check(false, "ההורדה תקינה: " + ex.Message); }
                File.Delete(dl);
            }

            Log(failures == 0 ? "כל הבדיקות עברו." : $"{failures} בדיקות נכשלו!");
            return failures == 0 ? 0 : 1;
        }

        private static int PlanTest(string manifestDir)
        {
            var edges = Directory.GetFiles(manifestDir, "*.manifest.json").Select(f => new PatchEdge
            {
                Manifest = PatchManifest.Parse(File.ReadAllText(f)),
                ManifestUrl = f,
            }).ToList();
            var failures = 0;
            void Expect(int local, int schema, int latest, int maxSchema, string expected)
            {
                var p = LibraryUpdates.Plan(local, schema, latest, edges, maxSchema);
                var got = p.UpToDate ? "מעודכן" : p.Steps.Count > 0
                    ? string.Join(" ", p.Steps.Select(s => $"{s.Manifest.FromVersion}→{s.Manifest.ToVersion}"))
                    : "אין: " + p.Reason;
                var ok = got.StartsWith(expected);
                if (!ok) failures++;
                Log($"  {(ok ? "✓" : "✗")} v{local}/סכמה {schema}, אחרונה {latest}, אוצריא קוראת עד {maxSchema}: {got}");
            }
            Expect(32, 6, 34, 7, "32→34");
            Expect(32, 6, 34, 6, "אין: כדי לקבל את העדכון צריך קודם לעדכן את אוצריא");
            Expect(33, 7, 34, 7, "33→34");
            Expect(31, 6, 34, 7, "31→32 32→34");
            Expect(31, 6, 32, 6, "31→32");
            Expect(29, 5, 34, 7, "אין: אין עדכון חלקי");
            Expect(34, 7, 34, 7, "מעודכן");
            Expect(27, 5, 29, 7, "אין: אין עדכון חלקי");
            Log(failures == 0 ? "כל בדיקות המסלול עברו." : $"{failures} בדיקות מסלול נכשלו!");
            return failures == 0 ? 0 : 1;
        }

        private static int SelfUpdateTest(string dbPath, string backupRoot, string manifestPath, string zstPath, IProgress<string> progress)
        {
            var m = PatchManifest.Parse(File.ReadAllText(manifestPath));
            var patch = PatchFiles.Prepare(zstPath, m.PatchFiles.Single(), progress);

            // ספרים שהעדכון משנה, מתוך קובץ העדכון עצמו.
            var patched = new HashSet<long>();
            SqliteInit.Ensure();
            using (var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = patch, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
            {
                c.Open();
                using (var cmd = c.CreateCommand())
                {
                    cmd.CommandText = "SELECT id FROM upsert_book";
                    using (var r = cmd.ExecuteReader())
                        while (r.Read()) patched.Add(r.GetInt64(0));
                }
            }
            Log($"העדכון משנה {patched.Count:N0} ספרים");

            long touchedId, untouchedId, renamedId, catId;
            string newTitle;
            var failures = 0;
            void Check(bool ok, string what)
            {
                Log((ok ? "  ✓ " : "  ✗ נכשל: ") + what);
                if (!ok) failures++;
            }

            using (var s = LibrarySession.OpenForWrite(LibraryKind.Otzaria, dbPath, backupRoot))
            {
                var cat = s.Db.LoadCatalog();
                var mid = cat.Books.Values.Where(b => b.TotalLines > 50 && b.TotalLines < 3000).OrderBy(b => b.Id).ToList();
                var touched = mid.First(b => patched.Contains(b.Id));
                var untouched = mid.First(b => !patched.Contains(b.Id));
                var renamed = mid.Where(b => patched.Contains(b.Id)).Skip(1).First();
                var smallCat = cat.Categories.Values
                    .Where(c => c.Books.Count >= 2 && c.Books.Count <= 6 && c.Children.Count == 0 && !c.Books.Any(b => b.Id == touched.Id || b.Id == untouched.Id || b.Id == renamed.Id))
                    .OrderBy(c => c.Id).First();
                (touchedId, untouchedId, renamedId, catId) = (touched.Id, untouched.Id, renamed.Id, smallCat.Id);
                newTitle = renamed.Title + " (נערך)";

                Log($"מוחק ספר שהעדכון משנה: \"{touched.Title}\" ({touched.Id}), ספר שלא: \"{untouched.Title}\" ({untouched.Id})");
                var books = new[] { touched, untouched };
                s.Delete(s.Engine.PlanDelete(Intents.DeleteBooks(books), books.Select(b => b.Id), null, progress), progress);
                Log($"מוחק קטגוריה: \"{smallCat.Path}\" ({smallCat.Books.Count} ספרים)");
                s.Delete(s.Engine.PlanDelete(Intents.DeleteCategories(new[] { smallCat }), null, new[] { smallCat.Id }, progress), progress);
                Log($"משנה שם: \"{renamed.Title}\" ← \"{newTitle}\"");
                var ch = new BookChanges { Title = newTitle };
                s.EditBook(Intents.EditBook(renamed, ch), renamed.Id, ch, progress);

                // טבלאות שהשינויים המקוריים (לפני העדכון) שינו – גם הן שונות מהרשמיות בכוונה.
                var tablesBefore = s.Ops().SelectMany(o => o.Tables).ToList();
                var sw = Stopwatch.StartNew();
                var r = s.ApplyLibraryUpdate(m, patch, checkForeignKeys: true, progress: progress);
                Log($"העדכון העצמי הושלם ({sw.Elapsed:mm\\:ss}). הוחלו שוב: {r.Reapply.Applied}, ספרים שהשתנו: {r.Patch.BooksTouched.Count}");
                foreach (var n in r.Reapply.Notes) Log("  הערה: " + n);
                Log("  שורות יתומות שנוקו: " + (r.Patch.OrphansRemoved.Count == 0 ? "אין"
                    : string.Join(", ", r.Patch.OrphansRemoved.Select(kv => $"{kv.Key}={kv.Value:N0}"))));
                Log($"  אומתו מול החתימה הרשמית: {r.Patch.VerifiedTables.Count}; לא ניתנות לאימות (נערכו): {string.Join(", ", r.Patch.UnverifiedTables)}");

                Check(s.Db.DbVersion == m.ToVersion && s.Version == m.ToVersion, $"גרסת הקובץ {s.Db.DbVersion}");
                Check(s.Db.SchemaVersion == m.ToSchemaVersion, $"מבנה הקובץ {s.Db.SchemaVersion}");
                foreach (var id in new[] { touchedId, untouchedId })
                {
                    Check(s.Db.Scalar("SELECT 1 FROM book WHERE id=$i", ("$i", id)) == null, $"הספר {id} לא קיים");
                    foreach (var t in s.Db.Schema.Tables.Values.Where(t => t.HasColumn("bookId")))
                        Check(Convert.ToInt64(s.Db.Scalar($"SELECT count(*) FROM \"{t.Name}\" WHERE bookId=$i", ("$i", id))) == 0, $"אין שורות של {id} ב-{t.Name}");
                }
                Check(s.Db.Scalar("SELECT 1 FROM category WHERE id=$i", ("$i", catId)) == null, "הקטגוריה לא קיימת");
                Check(Convert.ToInt64(s.Db.Scalar("SELECT count(*) FROM book WHERE categoryId=$i", ("$i", catId))) == 0, "אין ספרים בקטגוריה שנמחקה");
                Check((s.Db.Scalar("SELECT title FROM book WHERE id=$i", ("$i", renamedId)) as string) == newTitle, "שינוי השם נשמר");
                Check((s.Db.Scalar("PRAGMA quick_check") as string) == "ok", "בדיקת תקינות הקובץ");
                Check(Convert.ToInt64(s.Db.Scalar("SELECT count(*) FROM pragma_foreign_key_check")) == 0, "אין הפרות של קשרים");
                var ops = s.Ops();
                foreach (var op in ops) Log($"  פעולה #{op.Id} {op.State} v{op.DbVersion}: {op.Intent.Summary} ({op.Rows:N0} שורות)");
                Check(ops.Count == 3, "שלוש פעולות רשומות");
                Check(s.Status() == LibraryStatus.Edited, "מצב: ערוך");

                // כל טבלה שלא נערכה חייבת להיות זהה לגרסה הרשמית, גם אלה שהעדכון לא נגע בהן.
                var edited = new HashSet<string>(ops.SelectMany(o => o.Tables).Concat(r.Patch.UnverifiedTables).Concat(tablesBefore)
                    .Concat(r.Patch.OrphansRemoved.Keys));
                var report = LogicalHasher.Compute(s.Db, m.ToSchemaVersion);
                foreach (var kv in report.TableHashes.Where(kv => !edited.Contains(kv.Key)))
                    Check(m.ToTableContentHashes[kv.Key] == kv.Value, $"הטבלה {kv.Key} זהה לרשמית");
            }
            Log(failures == 0 ? "כל הבדיקות עברו." : $"{failures} בדיקות נכשלו!");
            return failures == 0 ? 0 : 1;
        }

        private static int CompareLogicalHash(string dbPath, string manifestPath, string side)
        {
            using (var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(manifestPath)))
            using (var db = SeforimDb.Open(dbPath, readOnly: true))
            {
                var root = doc.RootElement;
                var schema = root.GetProperty(side + "SchemaVersion").GetInt32();
                var expectedWhole = root.GetProperty(side + "ContentHash").GetString();
                var expected = root.GetProperty(side + "TableContentHashes").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString());
                var sw = Stopwatch.StartNew();
                var report = LogicalHasher.Compute(db, schema, progress: b => Log($"    · {b / (1 << 20):N0}MB"));
                var bad = 0;
                foreach (var kv in report.TableHashes)
                {
                    var ok = expected.TryGetValue(kv.Key, out var e) && e == kv.Value;
                    if (!ok) bad++;
                    Log($"  {(ok ? "תואם " : "שונה!")} {kv.Key} ({report.TableBytes[kv.Key]:N0} בתים)");
                }
                var wholeOk = report.WholeHash == expectedWhole;
                Log($"חתימה כוללת: {report.WholeHash} – {(wholeOk ? "תואמת" : "לא תואמת! צפוי " + expectedWhole)}");
                Log($"טבלאות שונות: {bad}. זמן: {sw.Elapsed:mm\\:ss}");
                return wholeOk && bad == 0 ? 0 : 1;
            }
        }

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
