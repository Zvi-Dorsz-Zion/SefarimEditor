using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;

namespace Sefarim.Core
{
    /// <summary>עדכון אחד שזמין ב-GitHub: המניפסט וכתובות ההורדה של הקבצים שלו.</summary>
    public sealed class PatchEdge
    {
        public PatchManifest Manifest { get; set; }
        public string ManifestUrl { get; set; }
        public Dictionary<string, string> FileUrls { get; } = new Dictionary<string, string>(StringComparer.Ordinal);
        public bool Prerelease { get; set; }
    }

    public sealed class UpdatePlan
    {
        public int LocalVersion { get; set; }
        public int LatestVersion { get; set; }

        /// <summary>השלבים לביצוע, לפי הסדר. ריק = אין עדכון אפשרי (ראו Reason).</summary>
        public List<PatchEdge> Steps { get; } = new List<PatchEdge>();

        /// <summary>למה אין מסלול, בעברית. null כשיש מסלול או כשהספרייה מעודכנת.</summary>
        public string Reason { get; set; }

        public bool UpToDate => LocalVersion >= LatestVersion;
        public long DownloadBytes => Steps.Sum(s => s.Manifest.CompressedSize);
        public long UncompressedBytes => Steps.Sum(s => s.Manifest.UncompressedSize);
        public bool ChangesSchema => Steps.Any(s => s.Manifest.FromSchemaVersion != s.Manifest.ToSchemaVersion);
    }

    /// <summary>
    /// גילוי ותכנון של עדכוני הספרייה של אוצריא, כמו library_update_discovery.dart ו-
    /// library_update_planner.dart: כל מניפסט של עדכון חלקי הוא "קשת" בין גרסאות, ומחפשים
    /// מסלול עם הכי מעט שלבים (ובשוויון – הכי קטן להורדה).
    /// </summary>
    public static class LibraryUpdates
    {
        private const string ReleasesUrl = "https://api.github.com/repos/Otzaria/SeforimLibrary/releases?per_page=40";
        private static readonly Regex ManifestName = new Regex(@"^patch-v(\d+)-v(\d+)\.db\.zst\.manifest\.json$", RegexOptions.Compiled);
        private static readonly Regex VersionTag = new Regex(@"^v(\d+)-\d+$", RegexOptions.Compiled);

        private static WebClient Client()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var wc = new WebClient { Encoding = Encoding.UTF8 };
            wc.Headers[HttpRequestHeader.UserAgent] = "SefarimEditor";
            return wc;
        }

        /// <summary>קורא את כל ה-releases ובונה את רשימת העדכונים. מניפסט פגום – מתעלמים ממנו, כמו באוצריא.</summary>
        public static (List<PatchEdge> edges, int latest) Discover(bool includePrerelease)
        {
            var edges = new List<PatchEdge>();
            var latest = 0;
            string json;
            using (var wc = Client())
            {
                wc.Headers[HttpRequestHeader.Accept] = "application/vnd.github+json";
                json = wc.DownloadString(ReleasesUrl);
            }
            using (var doc = JsonDocument.Parse(json))
            {
                foreach (var rel in doc.RootElement.EnumerateArray())
                {
                    if (rel.TryGetProperty("draft", out var d) && d.GetBoolean()) continue;
                    var pre = rel.TryGetProperty("prerelease", out var p) && p.GetBoolean();
                    if (pre && !includePrerelease) continue;
                    var assets = rel.GetProperty("assets").EnumerateArray()
                        .ToDictionary(a => a.GetProperty("name").GetString(), a => a.GetProperty("browser_download_url").GetString());
                    var tag = VersionTag.Match(rel.GetProperty("tag_name").GetString() ?? "");
                    var hasManifest = false;
                    foreach (var kv in assets.Where(a => ManifestName.IsMatch(a.Key)))
                    {
                        hasManifest = true;
                        latest = Math.Max(latest, int.Parse(ManifestName.Match(kv.Key).Groups[2].Value));
                        try
                        {
                            string text;
                            using (var wc = Client()) text = wc.DownloadString(kv.Value);
                            var m = PatchManifest.Parse(text);
                            var edge = new PatchEdge { Manifest = m, ManifestUrl = kv.Value, Prerelease = pre };
                            if (m.PatchFiles.All(f => assets.ContainsKey(f.File)))
                            {
                                foreach (var f in m.PatchFiles) edge.FileUrls[f.File] = assets[f.File];
                                edges.Add(edge);
                            }
                        }
                        catch (WebException) { }
                        catch (FormatException) { }
                        catch (JsonException) { }
                    }
                    if (!hasManifest && tag.Success) latest = Math.Max(latest, int.Parse(tag.Groups[1].Value));
                }
            }
            return (edges, latest);
        }

        /// <summary>
        /// בוחר מסלול מהגרסה המקומית לאחרונה. maxSchema = הסכמה הגבוהה ביותר שאוצריא המותקנת
        /// יודעת לקרוא; שלב שעובר אותה לא נבחר לעולם.
        /// </summary>
        public static UpdatePlan Plan(int localVersion, int localSchema, int latest, IEnumerable<PatchEdge> edges, int maxSchema)
        {
            var plan = new UpdatePlan { LocalVersion = localVersion, LatestVersion = latest };
            if (localVersion >= latest) return plan;

            bool Usable(PatchEdge e)
            {
                var m = e.Manifest;
                return !m.FullRebase && m.ToVersion > m.FromVersion && m.ToSchemaVersion >= m.FromSchemaVersion &&
                       PatchTables.IsSupportedSchema(m.FromSchemaVersion) && PatchTables.IsSupportedSchema(m.ToSchemaVersion) &&
                       m.ToSchemaVersion <= maxSchema &&
                       (m.PatchFormatVersion == null || m.PatchFormatVersion <= PatchTables.SupportedPatchFormatVersion) &&
                       (m.ToSchemaVersion < 4 || m.PatchFormatVersion != null) && m.PatchFiles.Count == 1;
            }

            var all = edges.ToList();
            var path = BestPath(all.Where(Usable).ToList(), localVersion, localSchema, latest);
            if (path != null)
            {
                plan.Steps.AddRange(path);
                return plan;
            }
            var withoutSchemaLimit = BestPath(all.Where(e => !e.Manifest.FullRebase).ToList(), localVersion, localSchema, latest);
            plan.Reason = withoutSchemaLimit != null && withoutSchemaLimit.Any(e => e.Manifest.ToSchemaVersion > maxSchema)
                ? "כדי לקבל את העדכון צריך קודם לעדכן את אוצריא: גרסת אוצריא המותקנת לא יודעת לקרוא את מבנה הספרייה החדש."
                : $"אין עדכון חלקי מגרסה {localVersion} לגרסה {latest}. צריך לעדכן דרך אוצריא (הורדה מלאה); אחר כך השינויים יוחלו שוב.";
            return plan;
        }

        /// <summary>Dijkstra לפי (מספר שלבים, גודל הורדה), עם התאמת סכמה בין שלבים – כמו _findBestPath.</summary>
        private static List<PatchEdge> BestPath(List<PatchEdge> edges, int from, int fromSchema, int to)
        {
            var best = new Dictionary<(int v, int s), (int hops, long size, List<PatchEdge> path)> { [(from, fromSchema)] = (0, 0, new List<PatchEdge>()) };
            var visited = new HashSet<(int, int)>();
            while (true)
            {
                var open = best.Where(kv => !visited.Contains(kv.Key)).ToList();
                if (open.Count == 0) return null;
                var cur = open.OrderBy(kv => kv.Value.hops).ThenBy(kv => kv.Value.size).First();
                if (cur.Key.v == to) return cur.Value.path;
                visited.Add(cur.Key);
                foreach (var e in edges.Where(e => e.Manifest.FromVersion == cur.Key.v && e.Manifest.FromSchemaVersion == cur.Key.s))
                {
                    var next = (e.Manifest.ToVersion, e.Manifest.ToSchemaVersion);
                    if (visited.Contains(next)) continue;
                    var cand = (cur.Value.hops + 1, cur.Value.size + e.Manifest.CompressedSize, cur.Value.path.Concat(new[] { e }).ToList());
                    if (!best.TryGetValue(next, out var ex) || cand.Item1 < ex.hops || (cand.Item1 == ex.hops && cand.Item2 < ex.size))
                        best[next] = cand;
                }
            }
        }

        /// <summary>
        /// מוריד קובץ עם המשך מנקודת העצירה (Range). הקובץ החלקי נשמר בשם ‎.download ומקבל את שמו
        /// הסופי רק בסוף; האימות מול המניפסט נעשה אחר כך (PatchFiles).
        /// </summary>
        public static void Download(string url, string target, long expectedSize, IProgress<(long done, long total)> progress = null,
            CancellationToken cancel = default)
        {
            if (File.Exists(target) && new FileInfo(target).Length == expectedSize) return;
            var part = target + ".download";
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            for (var attempt = 0; ; attempt++)
            {
                var have = File.Exists(part) ? new FileInfo(part).Length : 0;
                if (have > expectedSize)
                {
                    File.Delete(part);
                    have = 0;
                }
                try
                {
                    if (have < expectedSize)
                    {
                        var req = (HttpWebRequest)WebRequest.Create(url);
                        req.UserAgent = "SefarimEditor";
                        req.AllowAutoRedirect = true;
                        req.ReadWriteTimeout = 60000;
                        if (have > 0) req.AddRange(have);
                        using (var resp = (HttpWebResponse)req.GetResponse())
                        {
                            // השרת התעלם מה-Range: מתחילים מהתחלה.
                            var append = have > 0 && resp.StatusCode == HttpStatusCode.PartialContent;
                            using (var input = resp.GetResponseStream())
                            using (var output = new FileStream(part, append ? FileMode.Append : FileMode.Create, FileAccess.Write))
                            {
                                var done = append ? have : 0;
                                var buf = new byte[1 << 16];
                                int n;
                                while ((n = input.Read(buf, 0, buf.Length)) > 0)
                                {
                                    cancel.ThrowIfCancellationRequested();
                                    output.Write(buf, 0, n);
                                    done += n;
                                    progress?.Report((done, expectedSize));
                                }
                            }
                        }
                    }
                    if (new FileInfo(part).Length != expectedSize)
                        throw new IOException("ההורדה הסתיימה בגודל לא צפוי.");
                    if (File.Exists(target)) File.Delete(target);
                    File.Move(part, target);
                    return;
                }
                catch (Exception ex) when ((ex is WebException || ex is IOException) && attempt < 5 && !cancel.IsCancellationRequested)
                {
                    Thread.Sleep(2000 * (attempt + 1));
                }
            }
        }
    }

    /// <summary>
    /// איתור אוצריא המותקנת ובירור הסכמה הגבוהה ביותר שהיא יודעת לקרוא. נקרא מגיטהאב את
    /// database_constants.dart בתגית של הגרסה המותקנת בדיוק (readableDbSchemaVersion).
    /// </summary>
    public static class OtzariaApp
    {
        public static string FindInstallDir()
        {
            foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using (var root = RegistryKey.OpenBaseKey(hive, view))
                    using (var un = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall"))
                    {
                        if (un == null) continue;
                        foreach (var name in un.GetSubKeyNames())
                            using (var k = un.OpenSubKey(name))
                            {
                                var display = k?.GetValue("DisplayName") as string ?? "";
                                var loc = k?.GetValue("InstallLocation") as string;
                                if ((display.Contains("אוצריא") || display.IndexOf("otzaria", StringComparison.OrdinalIgnoreCase) >= 0) &&
                                    !string.IsNullOrEmpty(loc) && File.Exists(Path.Combine(loc, "otzaria.exe")))
                                    return loc;
                            }
                    }
                }
                catch (Exception ex) when (ex is System.Security.SecurityException || ex is UnauthorizedAccessException || ex is IOException) { }
            }
            return null;
        }

        /// <summary>התגית של הגרסה המותקנת (למשל 0.9.98+801) מתוך otzaria-release.json.</summary>
        public static string ReadReleaseTag(string installDir)
        {
            var path = installDir == null ? null : Path.Combine(installDir, "otzaria-release.json");
            if (path == null || !File.Exists(path)) return null;
            try
            {
                using (var doc = JsonDocument.Parse(File.ReadAllText(path)))
                    return doc.RootElement.TryGetProperty("releaseTag", out var t) ? t.GetString() : null;
            }
            catch (JsonException) { return null; }
            catch (IOException) { return null; }
        }

        /// <summary>הגדרות העדכון של אוצריא, כפי שנקראו מהקובץ שלה. null = לא ידוע.</summary>
        public sealed class UpdateSettings
        {
            public bool UpdatesEnabled { get; set; } = true;
            public bool OfflineMode { get; set; }

            /// <summary>האם אוצריא עצמה הייתה בודקת עדכונים.</summary>
            public bool ChecksUpdates => UpdatesEnabled && !OfflineMode;
        }

        public static string SettingsPath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "otzaria", "app_preferences.hive");

        /// <summary>
        /// קורא (לעולם לא כותב) את הגדרות העדכון מ-app_preferences.hive של אוצריא. הפורמט: יומן של רשומות
        /// [אורך u32 כולל עצמו][סוג מפתח 1 + אורך + מפתח][סוג ערך][ערך][crc32]; הרשומה האחרונה של מפתח קובעת,
        /// ורשומה בלי ערך היא מחיקה. כל דבר לא צפוי – מחזיר null, ואז התוכנה נוהגת לפי ההגדרה שלה עצמה.
        /// </summary>
        public static UpdateSettings ReadUpdateSettings(string path = null)
        {
            path = path ?? SettingsPath;
            try
            {
                byte[] b;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    b = new byte[fs.Length];
                    var read = 0;
                    while (read < b.Length)
                    {
                        var n = fs.Read(b, read, b.Length - read);
                        if (n == 0) return null;
                        read += n;
                    }
                }
                var values = new Dictionary<string, bool?>(StringComparer.Ordinal);
                var pos = 0;
                while (pos < b.Length)
                {
                    if (b.Length - pos < 4) return null;
                    var len = BitConverter.ToInt32(b, pos);
                    if (len < 10 || len > b.Length - pos) return null;
                    var end = pos + len;
                    var p = pos + 4;
                    string key = null;
                    if (b[p] == 1)
                    {
                        var kl = b[p + 1];
                        if (p + 2 + kl > end - 4) return null;
                        key = Encoding.ASCII.GetString(b, p + 2, kl);
                        p += 2 + kl;
                    }
                    else if (b[p] == 0) p += 5;
                    else return null;
                    if (key != null)
                    {
                        if (p == end - 4) values[key] = null; // מחיקה
                        else if (b[p] == 3 && p + 2 == end - 4) values[key] = b[p + 1] != 0;
                        else values.Remove(key); // ערך שאינו bool: לא רלוונטי לנו, ולא מנחשים
                    }
                    pos = end;
                }
                var s = new UpdateSettings();
                if (values.TryGetValue("key-software-and-book-updates-enabled", out var u) && u.HasValue) s.UpdatesEnabled = u.Value;
                if (values.TryGetValue("key-offline-mode", out var o) && o.HasValue) s.OfflineMode = o.Value;
                return s;
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            catch (ArgumentException) { return null; }
            catch (IndexOutOfRangeException) { return null; }
        }

        private static readonly Regex ReadableSchema =new Regex(@"readableDbSchemaVersion\s*=\s*(\d+)\s*;", RegexOptions.Compiled);

        /// <summary>הסכמה הגבוהה ביותר שהגרסה הזו של אוצריא קוראת, או null אם אי אפשר לברר (ואז לא משנים סכמה).</summary>
        public static int? ReadableSchemaFor(string releaseTag)
        {
            if (string.IsNullOrEmpty(releaseTag)) return null;
            try
            {
                using (var wc = new WebClient { Encoding = Encoding.UTF8 })
                {
                    ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                    wc.Headers[HttpRequestHeader.UserAgent] = "SefarimEditor";
                    var text = wc.DownloadString("https://raw.githubusercontent.com/Otzaria/otzaria/" + Uri.EscapeDataString(releaseTag) +
                                                 "/lib/data/constants/database_constants.dart");
                    var m = ReadableSchema.Match(text);
                    return m.Success ? int.Parse(m.Groups[1].Value) : (int?)null;
                }
            }
            catch (WebException) { return null; }
        }
    }
}
