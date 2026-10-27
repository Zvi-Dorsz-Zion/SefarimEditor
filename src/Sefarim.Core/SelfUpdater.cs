using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace Sefarim.Core
{
    /// <summary>תוצאת בדיקה: האם יש עדכון, מה המסלול, ולמה אין אם אין.</summary>
    public sealed class SelfUpdateCheck
    {
        public int LocalVersion { get; set; }
        public int LocalSchema { get; set; }
        public UpdatePlan Plan { get; set; }

        /// <summary>הסכמה שאוצריא המותקנת קוראת; null = לא ידוע (ואז לא משנים סכמה).</summary>
        public int? OtzariaReadableSchema { get; set; }

        public string OtzariaTag { get; set; }

        public bool HasUpdate => Plan != null && Plan.Steps.Count > 0;
    }

    public sealed class SelfUpdateOutcome
    {
        public int FromVersion { get; set; }
        public int ToVersion { get; set; }
        public HashSet<long> BooksTouched { get; } = new HashSet<long>();
        public List<string> Notes { get; } = new List<string>();
        public long OrphansRemoved { get; set; }

        /// <summary>האם נרשמה לאוצריא בקשה לרענן את אינדקס החיפוש.</summary>
        public bool IndexRefreshRequested { get; set; }
    }

    /// <summary>
    /// השירות שהחלון ותהליך הרקע משתמשים בו לעדכון עצמי: בדיקה, הורדה, אימות והחלה.
    /// לעולם לא מופעל בלי שהמשתמש אישר באותו רגע – זו אחריות הקורא.
    /// </summary>
    public static class SelfUpdater
    {
        public static string WorkFolder => Path.Combine(AppSettings.Folder, "updates");

        /// <summary>
        /// בודק ברשת אם יש עדכון שאפשר להחיל. שומר בהגדרות את הסכמה שאוצריא המותקנת קוראת,
        /// כדי לא לפנות לרשת על זה בכל בדיקה (הקורא שומר את ההגדרות).
        /// </summary>
        public static SelfUpdateCheck Check(AppSettings settings, string dbPath)
        {
            var check = new SelfUpdateCheck();
            using (var db = SeforimDb.Open(dbPath, readOnly: true))
            {
                check.LocalVersion = db.DbVersion ?? throw new InvalidOperationException("בקובץ אין מספר גרסה, ולכן אי אפשר לעדכן אותו חלקית.");
                check.LocalSchema = db.SchemaVersion ?? throw new InvalidOperationException("בקובץ אין מספר מבנה, ולכן אי אפשר לעדכן אותו חלקית.");
            }

            var tag = OtzariaApp.ReadReleaseTag(OtzariaApp.FindInstallDir());
            check.OtzariaTag = tag;
            if (tag != null && tag == settings.OtzariaTagChecked && settings.OtzariaReadableSchema != null)
                check.OtzariaReadableSchema = settings.OtzariaReadableSchema;
            else if (tag != null)
            {
                check.OtzariaReadableSchema = OtzariaApp.ReadableSchemaFor(tag);
                if (check.OtzariaReadableSchema != null)
                {
                    settings.OtzariaTagChecked = tag;
                    settings.OtzariaReadableSchema = check.OtzariaReadableSchema;
                }
            }

            var (edges, latest) = LibraryUpdates.Discover(includePrerelease: false);
            // בלי לדעת מה אוצריא קוראת – רק עדכונים שלא משנים את מבנה הקובץ.
            var maxSchema = check.OtzariaReadableSchema ?? check.LocalSchema;
            check.Plan = LibraryUpdates.Plan(check.LocalVersion, check.LocalSchema, latest, edges, maxSchema);
            if (check.Plan.Steps.Count == 0 && check.OtzariaReadableSchema == null && check.Plan.Reason != null && !check.Plan.UpToDate)
                check.Plan.Reason += " (לא ניתן היה לברר איזו גרסה של אוצריא מותקנת, ולכן התוכנה לא משנה את מבנה הקובץ.)";
            return check;
        }

        /// <summary>
        /// מקום פנוי נדרש: בתיקיית ההורדה – הקובץ הדחוס והפרוס של השלב הגדול; בכונן הספרייה –
        /// פעמיים הגודל הפרוס (יומן הביטול של SQLite) ועוד חצי ג'יגה.
        /// </summary>
        public static string SpaceProblem(UpdatePlan plan, string dbPath)
        {
            long Free(string path)
            {
                try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))).AvailableFreeSpace; }
                catch (Exception ex) when (ex is IOException || ex is ArgumentException || ex is UnauthorizedAccessException) { return long.MaxValue; }
            }

            var biggest = plan.Steps.Max(s => s.Manifest.CompressedSize + s.Manifest.UncompressedSize);
            var biggestApply = plan.Steps.Max(s => s.Manifest.UncompressedSize) * 2 + (512L << 20);
            Directory.CreateDirectory(WorkFolder);
            var sameDrive = string.Equals(Path.GetPathRoot(Path.GetFullPath(WorkFolder)), Path.GetPathRoot(Path.GetFullPath(dbPath)), StringComparison.OrdinalIgnoreCase);
            if (sameDrive)
            {
                var need = biggest + biggestApply;
                return Free(dbPath) < need ? $"אין מספיק מקום פנוי בכונן: נדרשים כ-{Size(need)}." : null;
            }
            if (Free(WorkFolder) < biggest) return $"אין מספיק מקום פנוי להורדה: נדרשים כ-{Size(biggest)}.";
            if (Free(dbPath) < biggestApply) return $"אין מספיק מקום פנוי בכונן של הספרייה: נדרשים כ-{Size(biggestApply)}.";
            return null;
        }

        public static string Size(long bytes) => bytes >= 1L << 30
            ? (bytes / (double)(1L << 30)).ToString("N1") + " GB"
            : Math.Max(1, bytes >> 20).ToString("N0") + " MB";

        /// <summary>
        /// מוריד ומחיל את כל שלבי העדכון על הספרייה הפתוחה. כל שלב בטרנזקציה משלו: כשל בשלב
        /// משאיר את הקובץ בגרסה של השלב הקודם, תקין. ביטול אפשרי רק בזמן הורדה.
        /// </summary>
        public static SelfUpdateOutcome Run(LibrarySession s, UpdatePlan plan, IProgress<string> progress = null,
            CancellationToken cancel = default)
        {
            if (s.Kind != LibraryKind.Otzaria) throw new InvalidOperationException("עדכון עצמי אפשרי רק בספרייה של אוצריא.");
            if (LibraryLocator.IsRunning(LibraryKind.Otzaria)) throw new InvalidOperationException("אוצריא פתוחה. יש לסגור אותה לפני העדכון.");
            var space = SpaceProblem(plan, s.DbPath);
            if (space != null) throw new InvalidOperationException(space);

            var outcome = new SelfUpdateOutcome { FromVersion = (int)(s.Version ?? 0) };
            var outside = false;
            Directory.CreateDirectory(WorkFolder);
            foreach (var step in plan.Steps)
            {
                var m = step.Manifest;
                if (s.Version != m.FromVersion)
                    throw new InvalidOperationException($"גרסת הקובץ ({s.Version}) אינה הגרסה שהעדכון מיועד לה ({m.FromVersion}).");
                var file = m.PatchFiles.Single();
                var zst = Path.Combine(WorkFolder, file.File);
                var label = $"עדכון {m.FromVersion}→{m.ToVersion}";
                var lastPct = -1;
                LibraryUpdates.Download(step.FileUrls[file.File], zst, file.Size, new SyncProgress<(long done, long total)>(p =>
                {
                    var pct = (int)(p.done * 100 / Math.Max(1, p.total));
                    if (pct == lastPct) return;
                    lastPct = pct;
                    progress?.Report($"מוריד את {label}: {pct}% מתוך {Size(p.total)}");
                }), cancel);
                cancel.ThrowIfCancellationRequested();
                var patch = PatchFiles.Prepare(zst, file, progress);
                cancel.ThrowIfCancellationRequested();

                progress?.Report($"מחיל את {label} (זה עשוי לקחת כמה דקות; אין לפתוח את אוצריא עד הסיום)…");
                var r = s.ApplyLibraryUpdate(m, patch, checkForeignKeys: true, progress: progress);
                outcome.BooksTouched.UnionWith(r.Patch.BooksTouched);
                outcome.Notes.AddRange(r.Reapply.Notes);
                outcome.OrphansRemoved += r.Patch.OrphansRemoved.Values.Sum();
                outside |= r.Patch.HasChangesOutsideBooksTouched;
                outcome.ToVersion = m.ToVersion;
                TryDelete(zst);
                TryDelete(patch);
            }
            outcome.IndexRefreshRequested = OtzariaIndex.RequestRecheck(s.DbPath, outcome.BooksTouched, outside);
            return outcome;
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        /// <summary>דיווח התקדמות סינכרוני (Progress&lt;T&gt; הרגיל שולח לתהליכון אחר ומאחר).</summary>
        private sealed class SyncProgress<T> : IProgress<T>
        {
            private readonly Action<T> _a;
            public SyncProgress(Action<T> a) => _a = a;
            public void Report(T value) => _a(value);
        }
    }

    /// <summary>
    /// בקשה לאוצריא לרענן את אינדקס החיפוש של ספרים שהעדכון שינה. אוצריא עצמה שומרת "בדיקות
    /// שטרם רצו" בקובץ otzaria_pending_reindex.json ליד האינדקס, ומריצה אותן בהפעלה הבאה
    /// (pending_reindex_store.dart). כותבים רק כשאוצריא סגורה, רק לאינדקס שזוהה בוודאות,
    /// ותמיד במיזוג עם מה שכבר כתוב שם.
    /// </summary>
    public static class OtzariaIndex
    {
        public const string PendingFile = "otzaria_pending_reindex.json";
        private const int Version = 1;

        /// <summary>תיקיית האינדקס כברירת המחדל של אוצריא (ליד תיקיית הספרייה), או null אם לא זוהתה בוודאות.</summary>
        public static string FindIndexDir(string dbPath)
        {
            try
            {
                var library = Path.GetDirectoryName(Path.GetFullPath(dbPath));
                var dir = Path.Combine(Path.GetDirectoryName(library) ?? "", "index");
                var meta = Path.Combine(dir, "otzaria_index_meta.json");
                if (!File.Exists(meta)) return null;
                using (var doc = JsonDocument.Parse(File.ReadAllText(meta)))
                    return doc.RootElement.TryGetProperty("format", out var f) && f.GetString() == "otzaria-search-index" ? dir : null;
            }
            catch (Exception ex) when (ex is IOException || ex is JsonException || ex is UnauthorizedAccessException || ex is ArgumentException)
            {
                return null;
            }
        }

        public static bool RequestRecheck(string dbPath, IEnumerable<long> bookIds, bool wholeLibrary)
        {
            var ids = bookIds.ToList();
            if (ids.Count == 0 && !wholeLibrary) return false;
            if (LibraryLocator.IsRunning(LibraryKind.Otzaria)) return false;
            var dir = FindIndexDir(dbPath);
            if (dir == null) return false;
            var path = Path.Combine(dir, PendingFile);
            try
            {
                var keys = new SortedSet<string>(StringComparer.Ordinal);
                var checkKeys = new SortedSet<string>(StringComparer.Ordinal);
                var checkLibrary = wholeLibrary;
                if (File.Exists(path))
                {
                    using (var doc = JsonDocument.Parse(File.ReadAllText(path)))
                    {
                        var r = doc.RootElement;
                        // פורמט שאנחנו לא מכירים – לא נוגעים בו.
                        if (!r.TryGetProperty("version", out var v) || !v.TryGetInt32(out var ver) || ver != Version) return false;
                        foreach (var k in r.GetProperty("keys").EnumerateArray()) keys.Add(k.GetString());
                        if (r.TryGetProperty("checkKeys", out var ck)) foreach (var k in ck.EnumerateArray()) checkKeys.Add(k.GetString());
                        checkLibrary |= r.TryGetProperty("checkLibrary", out var cl) && cl.ValueKind == JsonValueKind.True;
                    }
                }
                foreach (var id in ids) checkKeys.Add("id:" + id);

                var json = new StringBuilder("{\"version\":1,\"keys\":").Append(JsonSerializer.Serialize(keys.ToList()));
                if (checkLibrary) json.Append(",\"checkLibrary\":true");
                if (checkKeys.Count > 0) json.Append(",\"checkKeys\":").Append(JsonSerializer.Serialize(checkKeys.ToList()));
                json.Append('}');
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, json.ToString(), new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(tmp, path, null);
                else File.Move(tmp, path);
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is JsonException || ex is UnauthorizedAccessException || ex is InvalidOperationException || ex is KeyNotFoundException)
            {
                return false;
            }
        }
    }
}
