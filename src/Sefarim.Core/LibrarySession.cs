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

    public sealed class SelfUpdateResult
    {
        public PatchApplyResult Patch { get; set; }
        public ReapplyResult Reapply { get; set; }
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
        public long? Version { get; private set; }

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

        /// <summary>
        /// מוסיף ספר מקובץ טקסט (זית בלבד). עותק של הקובץ נשמר בתיקיית הגיבוי, כדי שאפשר יהיה
        /// להוסיף את הספר שוב אחרי עדכון של זית (שמחליף את כל הקובץ).
        /// </summary>
        public AppliedOp AddBook(AddBookSpec spec, byte[] fileBytes, IProgress<string> progress = null)
        {
            var engine = RequireEngine();
            if (Kind != LibraryKind.Zayit) throw new InvalidOperationException("הוספת ספרים אפשרית רק בספרייה של זית.");
            var reason = ChangeEngine.AddBookUnsupportedReason(Db);
            if (reason != null) throw new NotSupportedException(reason);
            var text = BookImport.Read(fileBytes, (spec.Title ?? "").Trim());
            spec.Sha256 = BookImport.Sha256(fileBytes);
            // שם קצר (נתיבים ארוכים מ-260 תווים נכשלים ב-.NET Framework); התוכן נבדק מול ה-SHA-256 המלא בכל קריאה.
            spec.SourceFile = spec.Sha256.Substring(0, 16) + ".txt";
            var stored = Path.Combine(AddedBooksFolder, spec.SourceFile);
            var created = !File.Exists(stored);
            if (created)
            {
                Directory.CreateDirectory(AddedBooksFolder);
                var tmp = stored + ".tmp";
                File.WriteAllBytes(tmp, fileBytes);
                if (BookImport.Sha256(File.ReadAllBytes(tmp)) != spec.Sha256)
                {
                    File.Delete(tmp);
                    throw new IOException("שמירת עותק הספר בתיקיית הגיבוי נכשלה (התוכן שנשמר שונה).");
                }
                File.Move(tmp, stored);
            }
            AppliedOp op;
            try
            {
                op = engine.AddBook(Intents.AddBook(spec), text, progress);
            }
            catch
            {
                if (created) DeleteUnusedSource(spec.SourceFile);
                throw;
            }
            AfterChange(progress);
            return op;
        }

        /// <summary>תיקיית העותקים של ספרים שנוספו, ליד מאגר הגיבוי.</summary>
        public string AddedBooksFolder => Store == null ? null : Path.Combine(Path.GetDirectoryName(Store.FilePath) ?? "", "added-books");

        /// <summary>קורא את העותק השמור של ספר שנוסף, ומוודא שהוא זהה למה שנשמר.</summary>
        private byte[] ReadStoredSource(AddBookSpec spec)
        {
            var path = Path.Combine(AddedBooksFolder ?? "", spec.SourceFile ?? "");
            if (string.IsNullOrEmpty(spec.SourceFile) || !File.Exists(path))
                throw new FileNotFoundException($"העותק של הספר \"{spec.Title}\" לא נמצא בתיקיית הגיבוי ({path}).");
            var bytes = File.ReadAllBytes(path);
            if (BookImport.Sha256(bytes) != spec.Sha256)
                throw new InvalidDataException($"העותק של הספר \"{spec.Title}\" בתיקיית הגיבוי השתנה, ולכן לא ניתן להוסיף אותו שוב.");
            return bytes;
        }

        /// <summary>מוחק עותק של ספר שנוסף, אם אף פעולה רשומה כבר לא צריכה אותו.</summary>
        private void DeleteUnusedSource(string sourceFile)
        {
            if (string.IsNullOrEmpty(sourceFile) || AddedBooksFolder == null) return;
            if (Ops().Any(o => o.Intent.AddBookSpec?.SourceFile == sourceFile)) return;
            try
            {
                var path = Path.Combine(AddedBooksFolder, sourceFile);
                if (File.Exists(path)) File.Delete(path);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private void AfterChange(IProgress<string> progress)
        {
            if (Kind != LibraryKind.Zayit || BackupRoot == null) return;
            progress?.Report("מעדכן את רשימת הספרים של זית…");
            ZayitCatalog.Sync(this, BackupRoot);
            if (AppSettings.Load().ZayitSearchSync && SearchAddon.IsInstalled) SyncSearch(progress);
        }

        /// <summary>תוצאת סנכרון החיפוש האחרון בחיבור הזה (null = לא רץ).</summary>
        public SearchSyncResult LastSearchSync { get; private set; }

        /// <summary>
        /// מעדכן את אינדקס החיפוש של זית (רכיב החיפוש). כשל כאן לא מבטל את השינוי בקובץ: הוא נרשם כאזהרה,
        /// והסנכרון הבא ישלים את מה שחסר.
        /// </summary>
        public SearchSyncResult SyncSearch(IProgress<string> progress = null)
        {
            try
            {
                LastSearchSync = ZayitSearch.Sync(this, progress);
            }
            catch (Exception ex)
            {
                LastSearchSync = new SearchSyncResult { Warning = "עדכון החיפוש של זית נכשל: " + ex.Message };
            }
            return LastSearchSync;
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
            var stale = Ops().Where(o => (o.State == OpState.Applied || o.State == OpState.Permanent) && o.DbVersion != version).ToList();
            var stillApplied = stale.ToDictionary(o => o.Id, o => o.State == OpState.Applied && Engine.StillApplied(o));
            // זית מחליפה את כל הקובץ. אם התגלה שפעולה כלשהי כבר לא בקובץ, גם שינויים בספרים שהוספנו
            // נעלמו – כולל מחיקה של ספר כזה, שנראית "עדיין מוחלת" רק כי הספר ממילא לא בקובץ החדש.
            var zayitReplaced = Kind == LibraryKind.Zayit && stillApplied.Values.Any(v => !v);
            var replaced = 0;
            foreach (var op in stale)
            {
                // רק מספר הגרסה השתנה, והשינוי עדיין בקובץ: מעדכנים את הגרסה ושומרים על הגיבוי.
                // מחיקה קבועה אי אפשר לבדוק; גרסה שהשתנתה בלי התוכנה פירושה שאוצריא הורידה קובץ חדש.
                var still = stillApplied[op.Id] && !(zayitReplaced && TouchesAddedContent(op));
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

        /// <summary>פעולה על ספר או קטגוריה שהוספנו (מזהה בטווח השמור), או ההוספה עצמה.</summary>
        private static bool TouchesAddedContent(AppliedOp op) =>
            op.Intent.Kind == OpIntent.AddBook ||
            op.Intent.Books.Any(b => b.Id >= BookImport.ReservedIdBase) ||
            op.Intent.Categories.Any(c => c.Id >= BookImport.ReservedIdBase);

        public List<AppliedOp> Ops() => Store?.LoadOps() ?? new List<AppliedOp>();

        public LibraryStatus Status() => StatusOf(Ops(), Version);

        public static LibraryStatus StatusOf(IEnumerable<AppliedOp> ops, long? currentVersion)
        {
            var list = ops.ToList();
            // פעולה "מוחלת" בגרסה אחרת פירושה שהקובץ הוחלף, והשינויים כבר לא בו.
            if (list.Any(o => (o.State == OpState.Applied || o.State == OpState.Pending || o.State == OpState.Permanent) &&
                              o.DbVersion == currentVersion))
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
            if (Ops().Any(o => o.State == OpState.Permanent))
                throw new InvalidOperationException("חלק מהמחיקות נעשו בלי גיבוי (מצב עדכון עצמי), ולכן אי אפשר להחזיר את הקובץ למקור. " +
                                                    "להחזרה מלאה צריך הורדה מלאה של הספרייה דרך אוצריא.");
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
            // לא מדלגים על מחיקה קבועה: החזרה של פעולה מוקדמת ממנה עלולה להחזיר ספר לקטגוריה שנמחקה.
            var last = Ops().Where(o => o.State == OpState.Applied || o.State == OpState.Permanent).OrderByDescending(o => o.Id).FirstOrDefault();
            if (last == null) return null;
            if (last.State == OpState.Permanent)
                throw new InvalidOperationException($"השינוי האחרון (\"{last.Intent.Summary}\") נעשה בלי גיבוי, ולכן אי אפשר לבטל אותו.");
            engine.Restore(last, progress);
            Store.DeleteOp(last.Id);
            if (last.Intent.Kind == OpIntent.AddBook) DeleteUnusedSource(last.Intent.AddBookSpec?.SourceFile);
            AfterChange(progress);
            return last;
        }

        /// <summary>
        /// מחיל מחדש את כל הפעולות שהוחזרו, לפי הסדר המקורי. ספר או קטגוריה
        /// מזוהים לפי המזהה והשם, ואם המזהה השתנה בעדכון, לפי השם והמיקום.
        /// </summary>
        /// <param name="permanentDeletes">
        /// מצב "עדכון עצמי": מחיקות מוחלות בלי גיבוי שורות, עם האיתור הקפדני, ונרשמות גם כשאין מה למחוק.
        /// </param>
        public ReapplyResult ReapplyRestored(IProgress<string> progress = null, bool permanentDeletes = false)
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
                    var isDelete = intent.Kind == OpIntent.DeleteBooks || intent.Kind == OpIntent.DeleteCategories;
                    var done = permanentDeletes && isDelete
                        ? ReapplyAfterUpdate(engine, intent, result, progress) != null
                        : Reapply(engine, catalog, intent, result, progress);
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
                case OpIntent.AddBook:
                {
                    var spec = intent.AddBookSpec ?? throw new InvalidOperationException("חסרים פרטי הספר שנוסף.");
                    var bytes = ReadStoredSource(spec);
                    if (spec.Category != null)
                    {
                        var c = Resolver.Category(catalog, spec.Category) ??
                                throw new InvalidOperationException($"הקטגוריה \"{spec.Category.Path}\" של הספר \"{spec.Title}\" לא נמצאה.");
                        spec.Category = CategoryRef.Of(c);
                    }
                    engine.AddBook(Intents.AddBook(spec), BookImport.Read(bytes, spec.Title), progress);
                    return true;
                }
                default:
                    throw new NotSupportedException("סוג פעולה לא מוכר: " + intent.Kind);
            }
        }

        /// <summary>מחיקות שעדיין יש להן גיבוי שורות (אפשר לבטל אותן).</summary>
        public List<AppliedOp> UndoableDeletes() => Ops()
            .Where(o => o.State == OpState.Applied &&
                        (o.Intent.Kind == OpIntent.DeleteBooks || o.Intent.Kind == OpIntent.DeleteCategories))
            .ToList();

        /// <summary>
        /// מצב עדכון עצמי: מפנה את גיבוי השורות של המחיקות. המחיקות נשארות בקובץ ונרשמות כקבועות
        /// (אי אפשר לבטל אותן יותר). עריכות נשארות עם הגיבוי הקטן שלהן. מחזיר כמה מחיקות פונו.
        /// </summary>
        public int ReleaseDeleteBackups(IProgress<string> progress = null)
        {
            RequireEngine();
            var deletes = UndoableDeletes();
            if (deletes.Count == 0) return 0;
            progress?.Report("מפנה את הגיבוי…");
            using (var tx = Db.Begin())
            {
                foreach (var op in deletes)
                {
                    Store.DropOpRows(op);
                    Store.UpdateOp(op.Id, OpState.Permanent, hashes: new Dictionary<string, string>());
                }
                tx.Commit();
            }
            // הקובץ של הגיבוי לא קטן מעצמו אחרי מחיקת שורות.
            progress?.Report("מקטין את קובץ הגיבוי…");
            Db.Exec($"VACUUM {BackupStore.Alias}");
            return deletes.Count;
        }

        // ───────────── עדכון עצמי (אוצריא בלבד) ─────────────

        /// <summary>
        /// מחיל עדכון ספרייה של אוצריא על הקובץ הערוך, ובאותה טרנזקציה מחיל שוב את כל השינויים
        /// של המשתמש. כל כשל – כולל שינוי שלא ניתן להחיל שוב – מבטל הכול, והקובץ נשאר כמו שהיה.
        /// מחיקות הופכות לקבועות (בלי גיבוי שורות), כי השורות שבגיבוי שייכות לגרסה הקודמת.
        /// </summary>
        public SelfUpdateResult ApplyLibraryUpdate(PatchManifest m, string patchPath, bool checkForeignKeys = true,
            IProgress<string> progress = null)
        {
            var engine = RequireEngine();
            if (Kind != LibraryKind.Otzaria) throw new InvalidOperationException("עדכון עצמי אפשרי רק בספרייה של אוצריא.");
            var ops = Ops().OrderBy(o => o.Id).ToList();
            if (ops.Any(o => o.State == OpState.Restored || o.State == OpState.Pending))
                throw new InvalidOperationException("יש שינויים שממתינים להחלה מחדש. יש להחיל אותם לפני העדכון.");

            var reapply = new ReapplyResult();
            var patch = PatchApplier.Apply(Db, patchPath, m, new PatchApplyOptions
            {
                CheckForeignKeys = checkForeignKeys,
                RemoveOrphans = ops.Count > 0,
                Progress = progress,
                // הטבלאות שהמשתמש שינה לפני העדכון שונות מהרשמיות בכוונה.
                UnverifiableTables = new HashSet<string>(ops.SelectMany(o => o.Tables), StringComparer.Ordinal),
                BeforeVerify = _ =>
                {
                    engine.LibraryVersion = m.ToVersion;
                    var touched = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var old in ops)
                    {
                        progress?.Report("מחיל מחדש: " + old.Intent.Summary);
                        Store.DropOpRows(old);
                        Store.DeleteOp(old.Id);
                        var op = ReapplyAfterUpdate(engine, old.Intent, reapply, progress);
                        touched.UnionWith(op.Tables);
                        reapply.Applied++;
                    }
                    return touched;
                },
            });
            Version = m.ToVersion;
            return new SelfUpdateResult { Patch = patch, Reapply = reapply };
        }

        private AppliedOp ReapplyAfterUpdate(ChangeEngine engine, OpIntent intent, ReapplyResult result, IProgress<string> progress)
        {
            var catalog = Db.LoadCatalog();
            switch (intent.Kind)
            {
                case OpIntent.DeleteBooks:
                {
                    var ids = new List<long>();
                    var refs = new List<BookRef>();
                    foreach (var r in intent.Books)
                    {
                        var (target, sweepOldId) = ResolveForDelete(catalog, r, result);
                        if (target != null)
                        {
                            ids.Add(target.Id);
                            refs.Add(BookRef.Of(target));
                        }
                        else refs.Add(r);
                        // שורות שנשארו עם המזהה הישן בלי ספר (העדכון החזיר חלק מהספר) נמחקות גם הן.
                        if (sweepOldId) ids.Add(r.Id);
                    }
                    var fresh = new OpIntent { Kind = intent.Kind, Summary = intent.Summary, Books = refs };
                    return DeletePermanently(engine, fresh, ids, null, progress);
                }
                case OpIntent.DeleteCategories:
                {
                    var ids = new List<long>();
                    var refs = new List<CategoryRef>();
                    foreach (var r in intent.Categories)
                    {
                        var c = Resolver.Category(catalog, r);
                        if (c != null)
                        {
                            ids.Add(c.Id);
                            refs.Add(CategoryRef.Of(c));
                            continue;
                        }
                        refs.Add(r);
                        if (!catalog.Categories.ContainsKey(r.Id)) ids.Add(r.Id);
                        else result.Notes.Add($"המזהה של הקטגוריה \"{r.Path}\" שייך עכשיו לקטגוריה אחרת, ולכן היא לא נמחקה.");
                    }
                    var fresh = new OpIntent { Kind = intent.Kind, Summary = intent.Summary, Categories = refs };
                    return DeletePermanently(engine, fresh, null, ids, progress);
                }
                case OpIntent.EditBook:
                case OpIntent.EditCategory:
                {
                    Reapply(engine, catalog, intent, result, progress);
                    return Ops().OrderByDescending(o => o.Id).First();
                }
                default:
                    throw new NotSupportedException("סוג פעולה לא מוכר: " + intent.Kind);
            }
        }

        /// <summary>
        /// איתור ספר למחיקה אחרי עדכון, בזהירות: לפי מזהה ושם; ואם לא – רק ספר אחד בדיוק עם
        /// אותו שם ואותו נתיב קטגוריה. sweepOldId = אין בקובץ ספר עם המזהה הישן, ולכן בטוח
        /// למחוק שורות שנשארו איתו.
        /// </summary>
        private static (BookInfo target, bool sweepOldId) ResolveForDelete(Catalog catalog, BookRef r, ReapplyResult result)
        {
            var exists = catalog.Books.TryGetValue(r.Id, out var byId);
            if (exists && byId.Title == r.Title) return (byId, false);
            var same = catalog.Books.Values.Where(b => b.Title == r.Title && b.Category?.Path == r.CategoryPath).ToList();
            var target = same.Count == 1 ? same[0] : null;
            if (exists && target == null)
                result.Notes.Add($"המזהה של הספר \"{r.Title}\" שייך עכשיו לספר אחר (\"{byId.Title}\"), ולכן הוא לא נמחק.");
            return (target, !exists);
        }

        private static AppliedOp DeletePermanently(ChangeEngine engine, OpIntent intent, IEnumerable<long> bookIds,
            IEnumerable<long> categoryIds, IProgress<string> progress)
        {
            var plan = engine.PlanDelete(intent, bookIds, categoryIds, progress);
            if (plan.Blockers.Count > 0) throw new InvalidOperationException($"{intent.Summary}: " + string.Join("; ", plan.Blockers));
            if (plan.Tables.Count == 0)
            {
                engine.DropTemps();
                return engine.RecordPermanent(intent);
            }
            return engine.ExecuteDelete(plan, progress, keepBackup: false);
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

        public static OpIntent AddBook(AddBookSpec spec) => new OpIntent
        {
            Kind = OpIntent.AddBook,
            AddBookSpec = spec,
            Summary = $"הוספת הספר \"{spec.Title?.Trim()}\"",
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
