using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sefarim.Core
{
    /// <summary>הגדרות לספרייה אחת (אוצריא, זית או קובץ אחר).</summary>
    public sealed class LibraryConfig
    {
        public LibraryKind Kind { get; set; }

        /// <summary>null = איתור אוטומטי לפי ההגדרות של התוכנה עצמה.</summary>
        public string DbPath { get; set; }

        /// <summary>תיקיית הגיבוי (מומלץ כרטיס זיכרון או כונן חיצוני).</summary>
        public string BackupRoot { get; set; }

        /// <summary>האם תהליך הרקע עוקב אחרי הספרייה הזו.</summary>
        public bool Watch { get; set; } = true;

        /// <summary>מצב אחרון ידוע, כדי לדעת מה קורה גם כשהכרטיס לא מחובר.</summary>
        public LibraryStatus LastStatus { get; set; }

        public long? LastDbVersion { get; set; }

        /// <summary>עותק של רשימת הפעולות. מאפשר להחיל אותן מחדש גם אם הכרטיס אבד.</summary>
        public List<OpIntent> IntentsMirror { get; set; } = new List<OpIntent>();

        /// <summary>הודעות על עדכון לא יוצגו עד התאריך הזה.</summary>
        public DateTime? SnoozeUntil { get; set; }

        /// <summary>הגרסה האחרונה שפורסמה ושעליה כבר הודענו.</summary>
        public long? NotifiedReleaseVersion { get; set; }

        public string ResolveDbPath() => !string.IsNullOrEmpty(DbPath) && File.Exists(DbPath) ? DbPath : LibraryLocator.Find(Kind);

        public string ResolveBackupRoot() => BackupLocator.Resolve(BackupRoot);

        public string DisplayName => Kind == LibraryKind.Other && DbPath != null
            ? Path.GetFileName(Path.GetDirectoryName(DbPath)) + " (" + Path.GetFileName(DbPath) + ")"
            : LibraryLocator.DisplayName(Kind);
    }

    public sealed class AppSettings
    {
        public List<LibraryConfig> Libraries { get; set; } = new List<LibraryConfig>();

        /// <summary>תהליך הרקע עולה עם Windows.</summary>
        public bool StartWithWindows { get; set; }

        /// <summary>כל כמה שעות לבדוק אם יצאה גרסת ספרייה חדשה.</summary>
        public int CheckHours { get; set; } = 6;

        public DateTime? LastReleaseCheck { get; set; }

        /// <summary>גרסת הספרייה האחרונה שפורסמה לאוצריא, לפי הבדיקה האחרונה.</summary>
        public long? LatestOtzariaRelease { get; set; }

        public LibraryKind? LastOpened { get; set; }

        /// <summary>כבר שאלנו אם להפעיל את תהליך הרקע (שואלים פעם אחת, אחרי השינוי הראשון).</summary>
        public bool AskedAboutTray { get; set; }

        /// <summary>תיקיית ההגדרות. SEFARIM_SETTINGS_DIR מחליף אותה (לבדיקות בלבד).</summary>
        public static string Folder =>
            Environment.GetEnvironmentVariable("SEFARIM_SETTINGS_DIR") is string dir && dir.Length > 0
                ? dir
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SefarimEditor");

        public static string FilePath => Path.Combine(Folder, "settings.json");

        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            Converters = { new JsonStringEnumConverter() },
        };

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                    return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath, Encoding.UTF8), Options) ?? new AppSettings();
            }
            catch (JsonException) { }
            catch (IOException) { }
            return new AppSettings();
        }

        public void Save()
        {
            Directory.CreateDirectory(Folder);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Options), new UTF8Encoding(false));
            if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null);
            else File.Move(tmp, FilePath);
        }

        public LibraryConfig Get(LibraryKind kind, string dbPath = null)
        {
            var cfg = kind == LibraryKind.Other
                ? Libraries.FirstOrDefault(l => l.Kind == kind && LibraryLocator.SamePath(l.DbPath, dbPath))
                : Libraries.FirstOrDefault(l => l.Kind == kind);
            if (cfg == null)
            {
                cfg = new LibraryConfig { Kind = kind, DbPath = kind == LibraryKind.Other ? dbPath : null };
                // ברירת מחדל: אותה תיקיית גיבוי שכבר נבחרה לספרייה אחרת.
                cfg.BackupRoot = Libraries.Select(l => l.BackupRoot).FirstOrDefault(r => !string.IsNullOrEmpty(r));
                Libraries.Add(cfg);
            }
            return cfg;
        }

        /// <summary>מעדכן את המצב השמור של ספרייה לפי מה שנמצא במאגר הגיבוי.</summary>
        public void Mirror(LibraryConfig cfg, LibrarySession session)
        {
            var ops = session.Ops();
            cfg.LastStatus = session.Status();
            cfg.LastDbVersion = session.Version;
            cfg.IntentsMirror = ops.Select(o => o.Intent).ToList();
        }
    }

    /// <summary>
    /// מוצא את תיקיית הגיבוי גם אם אות הכונן של הכרטיס השתנתה (למשל E: הפך ל-F:).
    /// </summary>
    public static class BackupLocator
    {
        public static string Resolve(string configured)
        {
            if (string.IsNullOrEmpty(configured)) return null;
            if (Directory.Exists(configured)) return configured;
            string rest;
            try
            {
                var root = Path.GetPathRoot(configured);
                if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\")) return null;
                rest = configured.Substring(root.Length);
            }
            catch (ArgumentException) { return null; }

            foreach (var drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (!drive.IsReady) continue;
                    var candidate = Path.Combine(drive.RootDirectory.FullName, rest);
                    if (Directory.Exists(Path.Combine(candidate, BackupStore.FolderName))) return candidate;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return null;
        }
    }
}
