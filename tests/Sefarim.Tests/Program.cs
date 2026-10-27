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
