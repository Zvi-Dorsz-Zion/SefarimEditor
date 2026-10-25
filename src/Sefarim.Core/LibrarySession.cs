using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Sefarim.Core
{
    public enum LibraryStatus
    {
        /// <summary>אין שינויים בקובץ.</summary>
        Original,

        /// <summary>השינויים של המשתמש מוחלים בקובץ.</summary>
        Edited,

        /// <summary>השינויים הוחזרו (לקראת עדכון) ומחכים להחלה מחדש.</summary>
        WaitingReapply,
    }

    public sealed class ReapplyResult
    {
        public int Applied { get; set; }
        public List<string> Problems { get; } = new List<string>();
        public List<string> Notes { get; } = new List<string>();
    }

    /// <summary>
    /// עבודה מול ספרייה אחת: הקובץ עצמו, מאגר הגיבוי שלו ומנוע השינויים.
    /// פתיחה לכתיבה אפשרית רק כשהתוכנה (אוצריא או זית) סגורה.
    /// </summary>
    public sealed class LibrarySession : IDisposable
    {
        public LibraryKind Kind { get; }
        public string DbPath { get; }
        public SeforimDb Db { get; }
        public BackupStore Store { get; }
        public ChangeEngine Engine { get; }

        private LibrarySession(LibraryKind kind, string dbPath, SeforimDb db, BackupStore store)
        {
            Kind = kind;
            DbPath = dbPath;
            Db = db;
            Store = store;
            Version = LibraryLocator.ReadVersion(kind, dbPath, db);
            if (store != null && !db.IsReadOnly) Engine = new ChangeEngine(db, store) { LibraryVersion = Version };
        }

        /// <summary>גרסת הספרייה כפי שהתוכנה עצמה מזהה אותה.</summary>
        public long? Version { get; }

        /// <summary>תיקיית הגיבוי (רק בפתיחה לכתיבה).</summary>
        public string BackupRoot { get; private set; }

        // ───────────── שינויים. תמיד דרך כאן, כדי שגם רשימת הספרים של זית תתעדכן ─────────────

        public AppliedOp Delete(DeletePlan plan, IProgress<string> progress = null)
        {
            var op = RequireEngine().ExecuteDelete(plan, progress);
            AfterChange(progress);
            return op;
        }

        public AppliedOp EditBook(OpIntent intent, long bookId, BookChanges changes, IProgress<string> progress = null)
        {
            var op = RequireEngine().EditBook(intent, bookId, changes, progress);
            AfterChange(progress);
            return op;
        }

        public AppliedOp EditCategory(OpIntent intent, long categoryId, CategoryChanges changes, IProgress<string> progress = null)
        {
            var op = RequireEngine().EditCategory(intent, categoryId, changes);
            AfterChange(progress);
            return op;
        }

        private void AfterChange(IProgress<string> progress)
        {
            if (Kind != LibraryKind.Zayit || BackupRoot == null) return;
            progress?.Report("מעדכן את רשימת הספרים של זית…");
            ZayitCatalog.Sync(this, BackupRoot);
        }

        /// <summary>פתיחה לעיון בלבד. אם תיקיית הגיבוי זמינה, גם רשימת השינויים נקראת ממנה.</summary>
        public static LibrarySession OpenReadOnly(LibraryKind kind, string dbPath, string backupRoot)
        {
            var db = SeforimDb.Open(dbPath, readOnly: true);
            try
            {
                var storePath = backupRoot == null ? null : BackupStore.PathFor(backupRoot, kind, dbPath);
                var store = storePath != null && File.Exists(storePath) ? BackupStore.Attach(db, storePath) : null;
                return new LibrarySession(kind, dbPath, db, store);
            }
            catch
            {
                db.Dispose();
                throw;
            }
        }

        // מונע כתיבה בו-זמנית לאותו קובץ מהעורך ומתהליך הרקע. סמפור ולא Mutex, כי השחרור
        // עשוי לקרות בתהליכון אחר. השם נגזר מהנתיב, כך ששתי ספריות שונות לא חוסמות זו את זו.
        private static System.Threading.Semaphore GateFor(string dbPath)
        {
            using (var sha = System.Security.Cryptography.SHA1.Create())
            {
                var h = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(dbPath).ToLowerInvariant()));
                return new System.Threading.Semaphore(1, 1, @"Local\SefarimEditor.Write." + BitConverter.ToString(h, 0, 8).Replace("-", ""));
            }
        }

        private System.Threading.Semaphore _gate;

        /// <summary>מספר הפעולות שהתגלה שהקובץ הוחלף מתחתיהן (הורדה מלאה), ולכן סומנו להחלה מחדש.</summary>
        public int ReplacedOps { get; private set; }

        public static LibrarySession OpenForWrite(LibraryKind kind, string dbPath, string backupRoot)
        {
            if (string.IsNullOrEmpty(backupRoot))
                throw new InvalidOperationException("לא נבחרה תיקיית גיבוי. בלי גיבוי אי אפשר להחזיר את הקובץ למקור לפני עדכון.");
            if (!Directory.Exists(backupRoot))
                throw new DirectoryNotFoundException("תיקיית הגיבוי לא זמינה: " + backupRoot + ". אם היא בכרטיס זיכרון, חבר אותו.");
            if (LibraryLocator.IsRunning(kind))
                throw new InvalidOperationException(LibraryLocator.DisplayName(kind) + " פתוחה כרגע. יש לסגור אותה לפני שינוי הקובץ.");
            var gate = GateFor(dbPath);
            if (!gate.WaitOne(0))
            {
                gate.Dispose();
                throw new InvalidOperationException("פעולה אחרת על הספרייה רצה כרגע (אולי בתהליך הרקע). נסה שוב בעוד רגע.");
            }

            SeforimDb db = null;
            try
            {
                db = SeforimDb.Open(dbPath, readOnly: false);
                var store = BackupStore.Attach(db, BackupStore.PathFor(backupRoot, kind, dbPath));
                var session = new LibrarySession(kind, dbPath, db, store) { _gate = gate, BackupRoot = backupRoot };
                session.Engine.RecoverPending();
                session.ReplacedOps = session.ReconcileReplacedFile();
                return session;
            }
            catch
            {
                db?.Dispose();
                gate.Release();
                gate.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            Db.Dispose();
            if (_gate != null)
            {
                _gate.Release();
                _gate.Dispose();
                _gate = null;
            }
        }

        /// <summary>
        /// אם גרסת הקובץ שונה מהגרסה שבה בוצעו השינויים, הקובץ הוחלף בהורדה מלאה:
        /// השינויים כבר לא בו, והגיבוי שלהם שייך לגרסה הקודמת. הפעולות מסומנות להחלה מחדש.
        /// </summary>
        private int ReconcileReplacedFile()
        {
            var version = Version;
            var stale = Ops().Where(o => o.State == OpState.Applied && o.DbVersion != version).ToList();
            var replaced = 0;
            foreach (var op in stale)
            {
                // רק מספר הגרסה השתנה, והשינוי עדיין בקובץ: מעדכנים את הגרסה ושומרים על הגיבוי.
                var still = Engine.StillApplied(op);
                using (var tx = Db.Begin())
                {
                    if (still) Store.SetOpVersion(op.Id, version);
                    else
                    {
                        Store.DropOpRows(op);
                        Store.UpdateOp(op.Id, OpState.Restored);
                        replaced++;
                    }
                    tx.Commit();
                }
            }
            return replaced;
        }

        public List<AppliedOp> Ops() => Store?.LoadOps() ?? new List<AppliedOp>();

        public LibraryStatus Status() => StatusOf(Ops(), Version);

        public static LibraryStatus StatusOf(IEnumerable<AppliedOp> ops, long? currentVersion)
        {
            var list = ops.ToList();
            // פעולה "מוחלת" בגרסה אחרת פירושה שהקובץ הוחלף, והשינויים כבר לא בו.
            if (list.Any(o => (o.State == OpState.Applied || o.State == OpState.Pending) && o.DbVersion == currentVersion))
                return LibraryStatus.Edited;
            if (list.Count > 0) return LibraryStatus.WaitingReapply;
            return LibraryStatus.Original;
        }

        private ChangeEngine RequireEngine() =>
            Engine ?? throw new InvalidOperationException("הקובץ פתוח לעיון בלבד.");

        /// <summary>מחזיר את הקובץ למקור: מבטל את כל הפעולות, מהאחרונה לראשונה.</summary>
        public int RestoreAll(IProgress<string> progress = null)
        {
            var engine = RequireEngine();
            var applied = Ops().Where(o => o.State == OpState.Applied).OrderByDescending(o => o.Id).ToList();
            foreach (var op in applied) engine.Restore(op, progress);
            if (applied.Count > 0) Store.SetMeta("restored_at_version", Version?.ToString());
            AfterChange(progress);
            return applied.Count;
        }

        /// <summary>מבטל את הפעולה האחרונה בלבד וזורק את הרישום שלה.</summary>
        public AppliedOp UndoLast(IProgress<string> progress = null)
        {
            var engine = RequireEngine();
            var last = Ops().Where(o => o.State == OpState.Applied).OrderByDescending(o => o.Id).FirstOrDefault();
            if (last == null) return null;
            engine.Restore(last, progress);
            Store.DeleteOp(last.Id);
            AfterChange(progress);
            return last;
        }

        /// <summary>
        /// מחיל מחדש את כל הפעולות שהוחזרו, לפי הסדר המקורי. ספר או קטגוריה
        /// מזוהים לפי המזהה והשם, ואם המזהה השתנה בעדכון, לפי השם והמיקום.
        /// </summary>
        public ReapplyResult ReapplyRestored(IProgress<string> progress = null)
        {
            var engine = RequireEngine();
            var result = new ReapplyResult();
            var pending = Ops().Where(o => o.State == OpState.Restored).OrderBy(o => o.Id).ToList();
            foreach (var old in pending)
            {
                var catalog = Db.LoadCatalog();
                var intent = old.Intent;
                try
                {
                    progress?.Report("מחיל מחדש: " + intent.Summary);
                    var done = Reapply(engine, catalog, intent, result, progress);
                    if (done) result.Applied++;
                    Store.DeleteOp(old.Id);
                }
                catch (Exception ex)
                {
                    result.Problems.Add(intent.Summary + ": " + ex.Message);
                }
            }
            AfterChange(progress);
            return result;
        }

        private bool Reapply(ChangeEngine engine, Catalog catalog, OpIntent intent, ReapplyResult result, IProgress<string> progress)
        {
            switch (intent.Kind)
            {
                case OpIntent.DeleteBooks:
                {
                    var found = new List<BookInfo>();
                    foreach (var r in intent.Books)
                    {
                        var b = Resolver.Book(catalog, r);
                        if (b == null) result.Notes.Add($"הספר \"{r.Title}\" כבר לא נמצא בספרייה, ולכן אין צורך למחוק אותו.");
                        else found.Add(b);
                    }
                    if (found.Count == 0) return false;
                    var fresh = Intents.DeleteBooks(found);
                    var plan = engine.PlanDelete(fresh, found.Select(b => b.Id), null, progress);
                    engine.ExecuteDelete(plan, progress);
                    return true;
                }
                case OpIntent.DeleteCategories:
                {
                    var found = new List<CategoryNode>();
                    foreach (var r in intent.Categories)
                    {
                        var c = Resolver.Category(catalog, r);
                        if (c == null) result.Notes.Add($"הקטגוריה \"{r.Path}\" כבר לא נמצאת בספרייה.");
                        else found.Add(c);
                    }
                    if (found.Count == 0) return false;
                    var fresh = Intents.DeleteCategories(found);
                    var plan = engine.PlanDelete(fresh, null, found.Select(c => c.Id), progress);
                    engine.ExecuteDelete(plan, progress);
                    return true;
                }
                case OpIntent.EditBook:
                {
                    var r = intent.Books.Single();
                    var b = Resolver.Book(catalog, r) ?? throw new InvalidOperationException($"הספר \"{r.Title}\" לא נמצא.");
                    var ch = intent.BookChanges;
                    if (ch.MoveTo != null)
                    {
                        var target = Resolver.Category(catalog, ch.MoveTo) ?? throw new InvalidOperationException($"קטגוריית היעד \"{ch.MoveTo.Path}\" לא נמצאה.");
                        ch.MoveTo = CategoryRef.Of(target);
                    }
                    intent.Books[0] = BookRef.Of(b);
                    engine.EditBook(intent, b.Id, ch, progress);
                    return true;
                }
                case OpIntent.EditCategory:
                {
                    var r = intent.Categories.Single();
                    var c = Resolver.Category(catalog, r) ?? throw new InvalidOperationException($"הקטגוריה \"{r.Path}\" לא נמצאה.");
                    intent.Categories[0] = CategoryRef.Of(c);
                    engine.EditCategory(intent, c.Id, intent.CategoryChanges);
                    return true;
                }
                default:
                    throw new NotSupportedException("סוג פעולה לא מוכר: " + intent.Kind);
            }
        }

        /// <summary>מקטין את הקובץ בפועל אחרי מחיקות (VACUUM). דורש מקום פנוי זמני בגודל הקובץ.</summary>
        public void Compact(IProgress<string> progress = null)
        {
            RequireEngine();
            progress?.Report("דוחס את הקובץ (זה עשוי לקחת כמה דקות)…");
            Db.Exec("VACUUM main");
            var mode = Db.Scalar("PRAGMA main.journal_mode") as string;
            if (string.Equals(mode, "wal", StringComparison.OrdinalIgnoreCase))
                Db.Exec("PRAGMA main.wal_checkpoint(TRUNCATE)");
        }
    }

    /// <summary>איתור ספר או קטגוריה אחרי עדכון, כשהמזהים עשויים להשתנות.</summary>
    public static class Resolver
    {
        public static BookInfo Book(Catalog cat, BookRef r)
        {
            if (cat.Books.TryGetValue(r.Id, out var byId) && byId.Title == r.Title) return byId;
            var same = cat.Books.Values.Where(b => b.Title == r.Title).ToList();
            if (same.Count == 1) return same[0];
            return same.FirstOrDefault(b => b.Category?.Path == r.CategoryPath);
        }

        public static CategoryNode Category(Catalog cat, CategoryRef r)
        {
            if (cat.Categories.TryGetValue(r.Id, out var byId) && byId.Title == r.Title) return byId;
            var byPath = cat.Categories.Values.Where(c => c.Path == r.Path).ToList();
            return byPath.Count == 1 ? byPath[0] : null;
        }
    }

    /// <summary>בניית תיאורי פעולות בעברית.</summary>
    public static class Intents
    {
        public static OpIntent DeleteBooks(IList<BookInfo> books) => new OpIntent
        {
            Kind = OpIntent.DeleteBooks,
            Books = books.Select(BookRef.Of).ToList(),
            Summary = books.Count == 1 ? $"מחיקת הספר \"{books[0].Title}\"" : $"מחיקת {books.Count} ספרים",
        };

        public static OpIntent DeleteCategories(IList<CategoryNode> cats) => new OpIntent
        {
            Kind = OpIntent.DeleteCategories,
            Categories = cats.Select(CategoryRef.Of).ToList(),
            Summary = cats.Count == 1 ? $"מחיקת הקטגוריה \"{cats[0].Title}\"" : $"מחיקת {cats.Count} קטגוריות",
        };

        public static OpIntent EditBook(BookInfo book, BookChanges ch) => new OpIntent
        {
            Kind = OpIntent.EditBook,
            Books = new List<BookRef> { BookRef.Of(book) },
            BookChanges = ch,
            Summary = $"עריכת הספר \"{book.Title}\"",
        };

        public static OpIntent EditCategory(CategoryNode cat, CategoryChanges ch) => new OpIntent
        {
            Kind = OpIntent.EditCategory,
            Categories = new List<CategoryRef> { CategoryRef.Of(cat) },
            CategoryChanges = ch,
            Summary = $"עריכת הקטגוריה \"{cat.Title}\"",
        };
    }
}
