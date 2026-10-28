using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;

namespace Sefarim.Core
{
    /// <summary>
    /// רכיב החיפוש לזית: תוכנית קטנה בג'אווה (עם סביבת הרצה משלה) שכותבת לאינדקס החיפוש של זית (Lucene 10).
    /// לא חלק מקובץ התוכנה: יורד פעם אחת מדף הגרסה בגיטהאב, נבדק מול SHA-256 שכתוב כאן, ונפרס לתיקיית ההגדרות.
    /// אפשר גם להניח אותו בתיקייה addons שליד התוכנה (החבילה המלאה).
    /// </summary>
    public static class SearchAddon
    {
        public const string Version = "1.0.0";
        public const string FolderName = "SefarimSearch-" + Version;
        public const string AssetName = FolderName + ".zip";
        public const string DownloadUrl = "https://github.com/Zvi-Dorsz-Zion/SefarimEditor/releases/download/search-addon-" + Version + "/" + AssetName;

        // נקבעים אחרי בניית הרכיב (addon\build.ps1), וחייבים להתאים בדיוק לקובץ שבגיטהאב.
        public const long ZipSize = 29711734;
        public const string ZipSha256 = "286b9e8eb89073fa19346a698719b30c09630734b709742b69f2403f5ee721b7";

        private const string Marker = "sefarim-search.ok";

        /// <summary>תיקיית ההתקנה של הרכיב שהתוכנה מורידה.</summary>
        public static string InstallRoot => Path.Combine(AppSettings.Folder, "addons");

        /// <summary>תיקיית הרכיב המותקן, או null. SEFARIM_ADDON_DIR מחליף אותה (לבדיקות בלבד).</summary>
        public static string Find()
        {
            var candidates = new[]
            {
                Environment.GetEnvironmentVariable("SEFARIM_ADDON_DIR"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "addons", FolderName),
                Path.Combine(InstallRoot, FolderName),
            };
            return candidates.FirstOrDefault(IsValid);
        }

        private static bool IsValid(string dir) =>
            !string.IsNullOrEmpty(dir) &&
            File.Exists(Path.Combine(dir, "runtime", "bin", "java.exe")) &&
            File.Exists(Path.Combine(dir, "sefarim-search.jar")) &&
            File.Exists(Path.Combine(dir, Marker)) &&
            File.ReadAllText(Path.Combine(dir, Marker)).Trim() == Version;

        public static bool IsInstalled => Find() != null;

        /// <summary>מותקן בתיקיית ההגדרות (ולא בחבילה שליד התוכנה), ולכן אפשר להסיר אותו.</summary>
        public static bool IsRemovable
        {
            get
            {
                var dir = Find();
                return dir != null && LibraryLocator.SamePath(dir, Path.Combine(InstallRoot, FolderName));
            }
        }

        /// <summary>מוריד, מאמת ופורס את הרכיב. כל כשל משאיר את המצב כמו שהיה.</summary>
        public static void Install(IProgress<(long done, long total)> download = null, IProgress<string> progress = null,
            CancellationToken cancel = default)
        {
            if (ZipSize <= 0 || string.IsNullOrEmpty(ZipSha256))
                throw new InvalidOperationException("בגרסה הזו של התוכנה אין הורדה של רכיב החיפוש.");
            Directory.CreateDirectory(InstallRoot);
            var zip = Path.Combine(InstallRoot, AssetName);
            progress?.Report("מוריד את רכיב החיפוש…");
            LibraryUpdates.Download(DownloadUrl, zip, ZipSize, download, cancel);
            progress?.Report("מאמת את הקובץ…");
            try
            {
                PatchFiles.Verify(zip, ZipSize, ZipSha256, "רכיב החיפוש");
            }
            catch
            {
                File.Delete(zip);
                throw;
            }
            InstallFromZip(zip, progress);
            File.Delete(zip);
        }

        /// <summary>פורס קובץ zip של הרכיב (שכבר אומת) לתיקיית ההתקנה, ובודק שהוא רץ.</summary>
        public static void InstallFromZip(string zip, IProgress<string> progress = null)
        {
            var final = Path.Combine(InstallRoot, FolderName);
            var tmp = final + ".tmp";
            if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
            progress?.Report("פורס את רכיב החיפוש…");
            var root = Path.GetFullPath(tmp) + Path.DirectorySeparatorChar;
            using (var archive = ZipFile.OpenRead(zip))
            {
                foreach (var entry in archive.Entries)
                {
                    // יש כלים שכותבים ב-zip '\' במקום '/' (למשל Compress-Archive של PowerShell 5.1).
                    var name = entry.FullName.Replace('\\', '/');
                    var target = Path.GetFullPath(Path.Combine(tmp, name.Replace('/', Path.DirectorySeparatorChar)));
                    // הגנה מפני קובץ zip שמנסה לכתוב מחוץ לתיקייה.
                    if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("קובץ הרכיב פגום.");
                    if (name.EndsWith("/")) Directory.CreateDirectory(target);
                    else
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(target));
                        entry.ExtractToFile(target);
                    }
                }
            }
            if (!IsValid(tmp)) throw new InvalidDataException("קובץ הרכיב אינו שלם.");
            var (code, output, error) = RunIn(tmp, "version", null, TimeSpan.FromMinutes(2));
            if (code != 0 || !output.Contains("sefarim-search " + Version))
                throw new InvalidOperationException("רכיב החיפוש לא הצליח לפעול במחשב הזה: " + (error.Length > 0 ? error : output));
            if (Directory.Exists(final)) Directory.Delete(final, true);
            Directory.Move(tmp, final);
        }

        public static void Uninstall()
        {
            var dir = Path.Combine(InstallRoot, FolderName);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }

        /// <summary>מריץ את הרכיב. הקלט (אם יש) נשלח ב-UTF-8 דרך stdin, כדי שנתיבים בעברית לא ייפגעו.</summary>
        public static (int code, string output, string error) Run(string command, Action<Stream> input, TimeSpan timeout)
        {
            var dir = Find() ?? throw new InvalidOperationException("רכיב החיפוש אינו מותקן.");
            return RunIn(dir, command, input, timeout);
        }

        private static (int code, string output, string error) RunIn(string dir, string command, Action<Stream> input, TimeSpan timeout)
        {
            var psi = new ProcessStartInfo
            {
                FileName = Path.Combine(dir, "runtime", "bin", "java.exe"),
                // נתיב יחסי (התיקייה הנוכחית היא תיקיית הרכיב), כי ג'אווה מפענחת ארגומנטים בקידוד של Windows.
                Arguments = "-Xmx1g --enable-native-access=ALL-UNNAMED -jar sefarim-search.jar " + command,
                WorkingDirectory = dir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            using (var p = Process.Start(psi))
            {
                var output = new StringBuilder();
                var error = new StringBuilder();
                p.OutputDataReceived += (s, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
                p.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (error) error.AppendLine(e.Data); };
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                try
                {
                    using (var stdin = p.StandardInput.BaseStream) input?.Invoke(stdin);
                }
                catch (IOException) { /* התהליך נסגר מוקדם; השגיאה שלו תופיע למטה */ }
                if (!p.WaitForExit((int)timeout.TotalMilliseconds))
                {
                    try { p.Kill(); } catch (InvalidOperationException) { }
                    throw new TimeoutException("רכיב החיפוש לא סיים בזמן.");
                }
                p.WaitForExit();
                return (p.ExitCode, output.ToString(), error.ToString());
            }
        }
    }
}
