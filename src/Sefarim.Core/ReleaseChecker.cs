using System;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Sefarim.Core
{
    /// <summary>
    /// בודק ב-GitHub מה גרסת הספרייה האחרונה שפורסמה לאוצריא, באותו מקום שאוצריא
    /// עצמה בודקת (Otzaria/SeforimLibrary). זית מורידה חבילה מלאה בכל עדכון, ולכן
    /// אצלה אין צורך בבדיקה מראש.
    /// </summary>
    public static class ReleaseChecker
    {
        private const string OtzariaReleases = "https://api.github.com/repos/Otzaria/SeforimLibrary/releases?per_page=40";

        private static readonly Regex VersionTag = new Regex(@"^v(\d+)-\d+$", RegexOptions.Compiled);

        /// <summary>מחזיר את גרסת הספרייה היציבה האחרונה, או null אם הבדיקה נכשלה.</summary>
        public static long? LatestOtzaria(bool includePrerelease = false)
        {
            if (long.TryParse(Environment.GetEnvironmentVariable("SEFARIM_TEST_LATEST"), out var fake)) return fake;
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                using (var wc = new WebClient { Encoding = Encoding.UTF8 })
                {
                    wc.Headers[HttpRequestHeader.UserAgent] = "SefarimEditor";
                    wc.Headers[HttpRequestHeader.Accept] = "application/vnd.github+json";
                    var json = wc.DownloadString(OtzariaReleases);
                    using (var doc = JsonDocument.Parse(json))
                    {
                        long? best = null;
                        foreach (var rel in doc.RootElement.EnumerateArray())
                        {
                            if (rel.TryGetProperty("draft", out var d) && d.GetBoolean()) continue;
                            if (!includePrerelease && rel.TryGetProperty("prerelease", out var p) && p.GetBoolean()) continue;
                            var m = VersionTag.Match(rel.GetProperty("tag_name").GetString() ?? "");
                            if (!m.Success) continue;
                            var v = long.Parse(m.Groups[1].Value);
                            if (best == null || v > best) best = v;
                        }
                        return best;
                    }
                }
            }
            catch (WebException) { return null; }
            catch (JsonException) { return null; }
            catch (InvalidOperationException) { return null; }
        }
    }
}
