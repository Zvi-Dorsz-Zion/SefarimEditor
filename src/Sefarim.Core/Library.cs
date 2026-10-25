using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Sefarim.Core
{
    public enum LibraryKind
    {
        Otzaria,
        Zayit,
        Other,
    }

    /// <summary>מאתר את קובצי הספרייה של אוצריא ושל זית אצל כל משתמש, בלי תלות בקיצורי דרך.</summary>
    public static class LibraryLocator
    {
        public static string DisplayName(LibraryKind kind)
        {
            switch (kind)
            {
                case LibraryKind.Otzaria: return "אוצריא";
                case LibraryKind.Zayit: return "זית";
                default: return "קובץ אחר";
            }
        }

        public static string[] ProcessNames(LibraryKind kind)
        {
            // לבדיקות בלבד: שם תהליך חלופי, כדי לבדוק בלי לסגור את התוכנה האמיתית.
            if (Environment.GetEnvironmentVariable("SEFARIM_TEST_PROCESS") is string test && test.Length > 0 && kind != LibraryKind.Other)
                return new[] { test };
            switch (kind)
            {
                case LibraryKind.Otzaria: return new[] { "otzaria" };
                case LibraryKind.Zayit: return new[] { "zayit" };
                default: return new string[0];
            }
        }

        public static bool IsRunning(LibraryKind kind)
        {
            foreach (var name in ProcessNames(kind))
            {
                var procs = Process.GetProcessesByName(name);
                var running = procs.Length > 0;
                foreach (var p in procs) p.Dispose();
                if (running) return true;
            }
            return false;
        }

        /// <summary>מחזיר את הנתיב ל-seforim.db או null אם לא נמצא.</summary>
        public static string Find(LibraryKind kind)
        {
            switch (kind)
            {
                case LibraryKind.Otzaria: return FindOtzaria();
                case LibraryKind.Zayit: return FindZayit();
                default: return null;
            }
        }

        public static string FindOtzaria()
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var otzariaData = Path.Combine(appData, "Otzaria");

            // אוצריא שומרת את נתיב הספרייה בקובץ טקסט, ולגיבוי גם בהעדפות.
            var fromTxt = ReadTrimmed(Path.Combine(otzariaData, "library_path.txt"));
            var candidate = DbIn(fromTxt);
            if (candidate != null) return candidate;

            var prefs = ReadTrimmed(Path.Combine(otzariaData, "shared_preferences.json"));
            if (!string.IsNullOrEmpty(prefs))
            {
                try
                {
                    using (var doc = JsonDocument.Parse(prefs))
                    {
                        if (doc.RootElement.TryGetProperty("flutter.key-library-path", out var el))
                        {
                            candidate = DbIn(el.GetString());
                            if (candidate != null) return candidate;
                        }
                    }
                }
                catch (JsonException) { }
            }

            var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            return DbIn(Path.Combine(programData, "otzaria", "ספריית אוצריא"));
        }

        public static string FindZayit()
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var path = Path.Combine(appData, "io.github.kdroidfilter.seforimapp", "databases", "seforim.db");
            return File.Exists(path) ? path : null;
        }

        /// <summary>
        /// גרסת הספרייה. באוצריא זו schema_meta.db_version; זית מחליפה את כל הקובץ
        /// בכל עדכון ושומרת את הגרסה בקובץ release_info.txt שליד הקובץ.
        /// </summary>
        public static long? ReadVersion(LibraryKind kind, string dbPath, SeforimDb db)
        {
            if (kind == LibraryKind.Zayit)
            {
                var info = ReadTrimmed(Path.Combine(Path.GetDirectoryName(dbPath) ?? "", "release_info.txt"));
                if (long.TryParse(info, out var v)) return v;
            }
            return db.DbVersion;
        }

        /// <summary>קריאת גרסה בלי לטעון את מבנה הקובץ (לתהליך הרקע, שבודק לעתים קרובות).</summary>
        public static long? ReadVersionQuick(LibraryKind kind, string dbPath)
        {
            SqliteInit.Ensure();
            if (kind == LibraryKind.Zayit)
            {
                var info = ReadTrimmed(Path.Combine(Path.GetDirectoryName(dbPath) ?? "", "release_info.txt"));
                if (long.TryParse(info, out var v)) return v;
            }
            try
            {
                var csb = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
                {
                    DataSource = dbPath,
                    Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly,
                    Pooling = false,
                };
                using (var c = new Microsoft.Data.Sqlite.SqliteConnection(csb.ToString()))
                {
                    c.Open();
                    using (var cmd = c.CreateCommand())
                    {
                        cmd.CommandText = "SELECT value FROM schema_meta WHERE key='db_version'";
                        return long.TryParse(cmd.ExecuteScalar() as string, out var v) ? v : (long?)null;
                    }
                }
            }
            catch (Microsoft.Data.Sqlite.SqliteException)
            {
                return null;
            }
        }

        /// <summary>מזהה לפי הנתיב לאיזו תוכנה שייך הקובץ.</summary>
        public static LibraryKind GuessKind(string dbPath)
        {
            if (string.IsNullOrEmpty(dbPath)) return LibraryKind.Other;
            var full = Path.GetFullPath(dbPath);
            if (SamePath(full, FindOtzaria())) return LibraryKind.Otzaria;
            if (SamePath(full, FindZayit())) return LibraryKind.Zayit;
            return LibraryKind.Other;
        }

        public static bool SamePath(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            return string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'),
                StringComparison.OrdinalIgnoreCase);
        }

        private static string DbIn(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder)) return null;
            try
            {
                var path = Path.Combine(folder.Trim(), "seforim.db");
                return File.Exists(path) ? path : null;
            }
            catch (ArgumentException) { return null; }
        }

        private static string ReadTrimmed(string path)
        {
            try
            {
                return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8).Trim().Trim('﻿') : null;
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }
    }
}
